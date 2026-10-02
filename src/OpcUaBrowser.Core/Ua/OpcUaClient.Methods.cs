using Opc.Ua;
using Opc.Ua.Client;

namespace OpcUaBrowser.Core.Ua;

public sealed partial class OpcUaClient : IMethodCaller
{
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

        var arguments = new VariantCollection();
        for (var i = 0; i < inputs.Count; i++)
        {
            var argument = signature.Inputs[i];
            try
            {
                arguments.Add(new Variant(ValueParser.Parse(inputs[i], argument.BuiltInType, argument.IsArray)));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{argument.Name} ({argument.DataType}): {ex.Message}", ex);
            }
        }

        var request = new CallMethodRequestCollection
        {
            new CallMethodRequest { ObjectId = objectId, MethodId = methodId, InputArguments = arguments },
        };
        var response = await session.CallAsync(null, request, cancellationToken).ConfigureAwait(false);
        var result = response.Results[0];
        if (StatusCode.IsBad(result.StatusCode))
        {
            // A rejected argument says more than the overall status.
            for (var i = 0; i < (result.InputArgumentResults?.Count ?? 0); i++)
            {
                if (StatusCode.IsBad(result.InputArgumentResults![i]))
                {
                    throw new ServiceResultException(new ServiceResult(result.InputArgumentResults[i],
                        new LocalizedText($"Argument {signature.Inputs[i].Name} was rejected")));
                }
            }

            throw new ServiceResultException(result.StatusCode);
        }

        return [.. result.OutputArguments.Select(ValueFormatter.Format)];
    }

    private async Task<IReadOnlyList<MethodArgument>> ReadArgumentsAsync(ISession session, NodeId methodId, string browseName, CancellationToken cancellationToken)
    {
        // Browse rather than TranslateBrowsePaths: not every server resolves the path, all of them list the property.
        var children = await BrowseCoreAsync(methodId, probe: false, cancellationToken).ConfigureAwait(false);
        if (children.FirstOrDefault(c => c.BrowseName == browseName || c.BrowseName.EndsWith(":" + browseName, StringComparison.Ordinal)) is not { } property)
        {
            return []; // no such property: the method has no arguments of this kind
        }

        var propertyId = property.NodeId;
        var read = await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
            new ReadValueIdCollection { new ReadValueId { NodeId = propertyId, AttributeId = Attributes.Value } }, cancellationToken).ConfigureAwait(false);
        // The SDK may hand back ExtensionObject[] or already decoded Argument[].
        if (read.Results[0].Value is not Array values)
        {
            return [];
        }

        var arguments = new List<MethodArgument>(values.Length);
        foreach (var argument in values.Cast<object?>().Select(v => v is ExtensionObject e ? e.Body : v).OfType<Argument>())
        {
            var builtIn = TypeInfo.GetBuiltInType(argument.DataType, session.TypeTree);
            var typeName = await session.NodeCache.GetDisplayTextAsync(argument.DataType, cancellationToken).ConfigureAwait(false);
            var isArray = argument.ValueRank >= ValueRanks.OneDimension;
            arguments.Add(new MethodArgument(
                argument.Name,
                (string.IsNullOrEmpty(typeName) ? argument.DataType.ToString() : typeName) + (isArray ? "[]" : string.Empty),
                builtIn,
                isArray,
                argument.Description?.Text ?? string.Empty));
        }

        return arguments;
    }
}
