using Opc.Ua;
using Opc.Ua.Client;

namespace MachineDataBrowser.Core.Ua;

public sealed partial class OpcUaClient : IHistorySource
{
    private const uint HistoryPageSize = 1000;

    public async Task<HistoryResult> ReadHistoryAsync(NodeId nodeId, DateTime startTime, DateTime endTime, int maxValues, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var details = new ReadRawModifiedDetails
        {
            StartTime = startTime.ToUniversalTime(),
            EndTime = endTime.ToUniversalTime(),
            NumValuesPerNode = HistoryPageSize,
            IsReadModified = false,
            ReturnBounds = false,
        };

        var values = new List<ValueUpdate>();
        byte[]? continuation = null;
        var truncated = false;
        do
        {
            var toRead = new HistoryReadValueIdCollection { new HistoryReadValueId { NodeId = nodeId, ContinuationPoint = continuation } };
            var response = await session.HistoryReadAsync(null, new ExtensionObject(details), TimestampsToReturn.Both, false, toRead, cancellationToken)
                .ConfigureAwait(false);
            var result = response.Results[0];
            if (StatusCode.IsBad(result.StatusCode))
            {
                throw new ServiceResultException(result.StatusCode);
            }

            continuation = result.ContinuationPoint is { Length: > 0 } cp ? cp : null;
            if (ExtensionObject.ToEncodeable(result.HistoryData) is HistoryData data)
            {
                foreach (var dv in data.DataValues)
                {
                    values.Add(new ValueUpdate(
                        nodeId,
                        ValueFormatter.Format(dv.WrappedValue),
                        dv.StatusCode,
                        dv.SourceTimestamp,
                        dv.ServerTimestamp,
                        ValueFormatter.ToNumeric(dv.WrappedValue),
                        dv.WrappedValue.Value));
                }
            }

            if (values.Count >= maxValues && continuation is not null)
            {
                truncated = true;
                break;
            }
        }
        while (continuation is not null);

        if (continuation is not null)
        {
            // Release the server-side continuation point.
            var release = new HistoryReadValueIdCollection { new HistoryReadValueId { NodeId = nodeId, ContinuationPoint = continuation } };
            await session.HistoryReadAsync(null, new ExtensionObject(details), TimestampsToReturn.Neither, true, release, cancellationToken).ConfigureAwait(false);
        }

        if (values.Count > maxValues)
        {
            values.RemoveRange(maxValues, values.Count - maxValues);
            truncated = true;
        }

        return new HistoryResult(values, truncated);
    }
}
