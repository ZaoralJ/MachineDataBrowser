using System.Collections.Concurrent;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.ComplexTypes;

namespace MachineDataBrowser.Core.Ua;

public sealed partial class OpcUaClient
{
    private StructureTypes? _structureTypes;

    /// <summary>The structure types of the current session; a reconnect may bring a new session, which starts over.</summary>
    private StructureTypes? Structures
    {
        get
        {
            if (_session is not { } session)
            {
                return null;
            }

            var current = _structureTypes;
            if (current?.Session != session)
            {
                current = new StructureTypes(session);
                _structureTypes = current;
            }

            return current;
        }
    }

    /// <summary>Decodes the server-specific structures in a value, loading their types first if needed.</summary>
    private async Task<object?> DecodeStructuresAsync(object? value, CancellationToken cancellationToken)
    {
        if (Structures is not { } types || !StructureTypes.HasUndecoded(value))
        {
            return value;
        }

        await types.LoadAsync(value, cancellationToken).ConfigureAwait(false);
        return types.Decode(value);
    }

    private async Task<DataValue> DecodeStructuresAsync(DataValue dv, CancellationToken cancellationToken)
    {
        if (!StructureTypes.HasUndecoded(dv.WrappedValue.AsBoxedObject()))
        {
            return dv;
        }

        var decoded = await DecodeStructuresAsync(dv.WrappedValue.AsBoxedObject(), cancellationToken).ConfigureAwait(false);
        return new DataValue(ValueFormatter.ToVariant(decoded), dv.StatusCode, dv.SourceTimestamp, dv.ServerTimestamp);
    }

    /// <summary>
    /// Server-specific structure types, loaded one at a time when a value of that type first shows up. Loading them
    /// all at connect (<see cref="ComplexTypeSystem.LoadAsync"/>) took ~40 s on a FactoryTalk Linx server with a few
    /// hundred controller UDTs, most of which the user never looks at.
    /// </summary>
    private sealed class StructureTypes(ISession session) : IDisposable
    {
        private readonly ComplexTypeSystem _system = ComplexTypeSystem.Create(session, Telemetry);
        private readonly ConcurrentDictionary<ExpandedNodeId, Lazy<Task>> _loads = new();

        // ComplexTypeSystem isn't safe for concurrent loads (they share its caches), so they run one after another.
        private readonly Lock _lock = new();
        private Task _lastLoad = Task.CompletedTask;

        public ISession Session => session;

        public void Dispose() => _system.Dispose();

        public static bool HasUndecoded(object? value) => value switch
        {
            ExtensionObject eo => IsUndecoded(eo),
            ExtensionObject[] array => Array.Exists(array, IsUndecoded),
            ArrayOf<ExtensionObject> array => array.ToList().Exists(IsUndecoded),
            _ => false,
        };

        private static bool IsUndecoded(ExtensionObject eo) =>
            eo.TryGetAsBinary(out ByteString bytes) && !bytes.IsNull && !eo.TypeId.IsNull;

        public async Task LoadAsync(object? value, CancellationToken cancellationToken)
        {
            List<ExtensionObject> encodings = value switch
            {
                ExtensionObject eo => [eo],
                ExtensionObject[] array => [.. array],
                ArrayOf<ExtensionObject> array => array.ToList(),
                _ => [],
            };

            // Shared between callers, so it runs to completion even if this caller gives up.
            foreach (var encodingId in encodings.Where(e => IsUndecoded(e)).Select(e => e.TypeId).Distinct())
            {
                if (session.Factory.GetSystemType(encodingId) is null)
                {
                    await _loads.GetOrAdd(encodingId, id => new Lazy<Task>(() => Enqueue(id))).Value
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private Task Enqueue(ExpandedNodeId encodingId)
        {
            lock (_lock)
            {
                _lastLoad = LoadAfterAsync(_lastLoad, encodingId);
                return _lastLoad;
            }
        }

        private async Task LoadAfterAsync(Task previous, ExpandedNodeId encodingId)
        {
            await previous.ConfigureAwait(false);
            try
            {
                // Values carry the encoding id; the type system wants the data type it encodes.
                var browse = new List<BrowseDescription>
                {
                    new BrowseDescription
                    {
                        NodeId = ExpandedNodeId.ToNodeId(encodingId, session.NamespaceUris),
                        BrowseDirection = BrowseDirection.Inverse,
                        ReferenceTypeId = ReferenceTypeIds.HasEncoding,
                        IncludeSubtypes = false,
                        NodeClassMask = (uint)NodeClass.DataType,
                        ResultMask = (uint)BrowseResultMask.None,
                    },
                };
                var response = await session.BrowseAsync(null, null, 0, browse, CancellationToken.None).ConfigureAwait(false);
                if (response.Results is [{ References: [var dataType, ..] }, ..])
                {
                    await LoadWithFieldTypesAsync(ExpandedNodeId.ToNodeId(dataType.NodeId, session.NamespaceUris), []).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (Errors.IsRecoverable(ex))
            {
                // Server without type info: the value still shows, just undecoded.
                System.Diagnostics.Trace.TraceWarning($"Loading structure type {encodingId} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the server-specific field types before the structure. Loading the structure alone resolves nested
        /// structures but fails on enumeration fields (SDK 1.5.378 treats the enum as a missing structure).
        /// </summary>
        private async Task LoadWithFieldTypesAsync(NodeId dataTypeId, HashSet<NodeId> visited)
        {
            if (!visited.Add(dataTypeId))
            {
                return;
            }

            var read = new List<ReadValueId> { new ReadValueId { NodeId = dataTypeId, AttributeId = Attributes.DataTypeDefinition } };
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, read, CancellationToken.None).ConfigureAwait(false);

            // Servers without DataTypeDefinition (pre-1.04) describe types in dictionaries, which LoadTypeAsync reads itself.
            if (response.Results is [{ } result]
                && result.WrappedValue.AsBoxedObject() is ExtensionObject encoded
                && encoded.TryGetValue(out IEncodeable? body)
                && body is StructureDefinition definition)
            {
                foreach (var field in definition.Fields.ToList().Where(f => f.DataType.NamespaceIndex != 0).DistinctBy(f => f.DataType))
                {
                    await LoadWithFieldTypesAsync(field.DataType, visited).ConfigureAwait(false);
                }
            }

            await _system.LoadTypeAsync(dataTypeId, false, false, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Decodes the bodies the session received before their type was known; later reads decode on their own.</summary>
        public object? Decode(object? value) => value switch
        {
            ExtensionObject eo => Decode(eo),
            ExtensionObject[] array => Array.ConvertAll(array, Decode),
            ArrayOf<ExtensionObject> array => array.ToList().ConvertAll(Decode).ToArrayOf(),
            _ => value,
        };

        private ExtensionObject Decode(ExtensionObject eo)
        {
            if (!IsUndecoded(eo) || !eo.TryGetValue(out IEncodeable? body, session.MessageContext))
            {
                return eo;
            }

            return new ExtensionObject(eo.TypeId, body);
        }
    }
}
