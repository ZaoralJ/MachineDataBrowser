using Opc.Ua;
using Opc.Ua.Client;

namespace MachineDataBrowser.Core.Ua;

public sealed partial class OpcUaClient : IHistorySource
{
    Task<HistoryResult> IHistorySource.ReadHistoryAsync(DeviceNodeId nodeId, DateTime startTime, DateTime endTime, int maxValues, CancellationToken cancellationToken) =>
        UaStatusAdapter.TranslateAsync(() => ReadHistoryAsync(UaNodeIdAdapter.ToUa(nodeId), startTime, endTime, maxValues, cancellationToken));

    /// <summary>Values asked for per HistoryRead call; adjustable for tests that exercise paging.</summary>
    internal static uint HistoryPageSize { get; set; } = 1000;

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
        DateTime? pagedAfter = null;
        ByteString continuation = default;
        var truncated = false;
        do
        {
            var toRead = new List<HistoryReadValueId> { new HistoryReadValueId { NodeId = nodeId, ContinuationPoint = continuation } };
            var response = await session.HistoryReadAsync(null, new ExtensionObject(details), TimestampsToReturn.Both, false, toRead, cancellationToken)
                .ConfigureAwait(false);
            var result = response.Results[0];
            if (StatusCode.IsBad(result.StatusCode))
            {
                throw new ServiceResultException(result.StatusCode);
            }

            continuation = result.ContinuationPoint;
            var page = 0;
            var before = values.Count;
            if (ExtensionObject.ToEncodeable(result.HistoryData) is HistoryData data)
            {
                page = data.DataValues.Count;
                foreach (var dv in data.DataValues.ToList())
                {
                    // Paging by time: servers may round the start (e.g. to microseconds) and return the last value again.
                    if (pagedAfter is { } after && dv.SourceTimestamp <= after)
                    {
                        continue;
                    }

                    var decoded = await DecodeStructuresAsync(dv, cancellationToken).ConfigureAwait(false);
                    values.Add(new ValueUpdate(
                        UaNodeIdAdapter.ToDevice(nodeId),
                        UaValueFormatter.Format(decoded.WrappedValue),
                        UaStatusAdapter.ToDevice(dv.StatusCode),
                        dv.SourceTimestamp.ToDateTime(),
                        dv.ServerTimestamp.ToDateTime(),
                        UaValueFormatter.ToNumeric(decoded.WrappedValue),
                        decoded.WrappedValue.AsBoxedObject()));
                }
            }

            if (values.Count >= maxValues && !continuation.IsNull && !continuation.IsEmpty)
            {
                truncated = true;
                break;
            }

            if (!continuation.IsNull && !continuation.IsEmpty)
            {
                continue;
            }

            // Some servers (e.g. asyncua) return a full page without a continuation point: page on by time instead,
            // from just after the last value, until the range is covered.
            // A page that added nothing new (every value at the timestamp already read) would repeat forever: stop.
            if (page >= HistoryPageSize && values.Count < maxValues && values.Count > before
                && values[^1].SourceTimestamp is var last && last != DateTime.MinValue && last < details.EndTime)
            {
                details.StartTime = last.AddTicks(1);
                pagedAfter = last;
                continue;
            }

            truncated |= page >= HistoryPageSize && values.Count >= maxValues;
            break;
        }
        while (true);

        if (!continuation.IsNull && !continuation.IsEmpty)
        {
            // Release the server-side continuation point.
            var release = new List<HistoryReadValueId> { new HistoryReadValueId { NodeId = nodeId, ContinuationPoint = continuation } };
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
