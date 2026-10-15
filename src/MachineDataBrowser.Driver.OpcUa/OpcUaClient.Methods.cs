using Opc.Ua;
using Opc.Ua.Client;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

public sealed partial class OpcUaClient : IMethodCaller
{
    Task<MethodSignature> IMethodCaller.GetMethodSignatureAsync(DeviceNodeId methodId, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => GetMethodSignatureAsync(UaNodeIdAdapter.ToUa(methodId), cancellationToken));

    Task<IReadOnlyList<string>> IMethodCaller.CallMethodAsync(DeviceNodeId objectId, DeviceNodeId methodId, IReadOnlyList<string> inputs, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => CallMethodAsync(UaNodeIdAdapter.ToUa(objectId), UaNodeIdAdapter.ToUa(methodId), inputs, cancellationToken));

    public async Task<MethodSignature> GetMethodSignatureAsync(NodeId methodId, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var inputs = await ReadArgumentsAsync(session, methodId, BrowseNames.InputArguments, cancellationToken).ConfigureAwait(false);
        var outputs = await ReadArgumentsAsync(session, methodId, BrowseNames.OutputArguments, cancellationToken).ConfigureAwait(false);
        return new MethodSignature(inputs, outputs);
    }

    public async Task<IReadOnlyList<string>> CallMethodAsync(NodeId objectId, NodeId methodId, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var session = RequireSession();
        var signature = await GetMethodSignatureAsync(methodId, cancellationToken).ConfigureAwait(false);
        if (inputs.Count != signature.Inputs.Count)
        {
            throw new ArgumentException($"The method takes {signature.Inputs.Count} argument(s), {inputs.Count} given.", nameof(inputs));
        }

        var arguments = new List<Variant>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var argument = signature.Inputs[i];
            try
            {
                arguments.Add(UaValueFormatter.ToVariant(UaValueParser.Parse(inputs[i], argument.ValueType, argument.IsArray)));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{argument.Name} ({argument.DataType}): {ex.Message}", ex);
            }
        }

        var request = new List<CallMethodRequest>
        {
            new CallMethodRequest { ObjectId = objectId, MethodId = methodId, InputArguments = arguments },
        };
        var response = await session.CallAsync(null, request, cancellationToken).ConfigureAwait(false);
        var result = response.Results[0];
        if (StatusCode.IsBad(result.StatusCode))
        {
            // A rejected argument says more than the overall status.
            for (var i = 0; i < result.InputArgumentResults.Count; i++)
            {
                if (StatusCode.IsBad(result.InputArgumentResults![i]))
                {
                    throw new ServiceResultException(ServiceResult.Create(result.InputArgumentResults[i],
                        $"Argument {signature.Inputs[i].Name} was rejected"));
                }
            }

            throw new ServiceResultException(result.StatusCode);
        }

        var outputs = new List<string>(result.OutputArguments.Count);
        foreach (var output in result.OutputArguments.ToList())
        {
            outputs.Add(UaValueFormatter.Format(UaValueFormatter.ToVariant(
                await DecodeStructuresAsync(output.AsBoxedObject(), cancellationToken).ConfigureAwait(false))));
        }

        return outputs;
    }

    private async Task<IReadOnlyList<MethodArgument>> ReadArgumentsAsync(ISession session, NodeId methodId, string browseName, CancellationToken cancellationToken)
    {
        // Browse rather than TranslateBrowsePaths: not every server resolves the path, all of them list the property.
        var children = await BrowseCoreAsync(methodId, probe: false, cancellationToken).ConfigureAwait(false);
        if (children.FirstOrDefault(c => c.BrowseName == browseName || c.BrowseName.EndsWith(":" + browseName, StringComparison.Ordinal)) is not { } property)
        {
            return []; // no such property: the method has no arguments of this kind
        }

        var propertyId = UaNodeIdAdapter.ToUa(property.NodeId);
        var read = await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
            new List<ReadValueId> { new ReadValueId { NodeId = propertyId, AttributeId = Attributes.Value } }, cancellationToken).ConfigureAwait(false);
        var rawArguments = read.Results[0].WrappedValue.AsBoxedObject() switch
        {
            ArrayOf<Argument> encodedArguments => encodedArguments.ToList(),
            Argument[] decodedArguments => decodedArguments.ToList(),
            ArrayOf<ExtensionObject> encoded => encoded.ToList()
                .Select(e => e.TryGetValue(out IEncodeable? body, session.MessageContext) ? body : null)
                .OfType<Argument>()
                .ToList(),
            ExtensionObject[] encoded => encoded
                .Select(e => e.TryGetValue(out IEncodeable? body, session.MessageContext) ? body : null)
                .OfType<Argument>()
                .ToList(),
            _ => [],
        };

        var arguments = new List<MethodArgument>(rawArguments.Count);
        foreach (var argument in rawArguments)
        {
            var builtIn = TypeInfo.GetBuiltInType(argument.DataType, session.TypeTree);
            var typeName = await session.NodeCache.GetDisplayTextAsync(argument.DataType, cancellationToken).ConfigureAwait(false);
            var isArray = argument.ValueRank >= ValueRanks.OneDimension;
            arguments.Add(new MethodArgument(
                argument.Name ?? string.Empty,
                (string.IsNullOrEmpty(typeName) ? argument.DataType.ToString() : typeName) + (isArray ? "[]" : string.Empty),
                UaValueParser.ToDeviceDataType(builtIn),
                isArray,
                argument.Description.Text ?? string.Empty));
        }

        return arguments;
    }
}
