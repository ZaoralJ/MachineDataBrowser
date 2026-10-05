using Opc.Ua;
using Opc.Ua.Client;

namespace MachineDataBrowser.Core.Ua;

public sealed partial class OpcUaClient : IHistorySource
{
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
            var page = 0;
            var before = values.Count;
            if (ExtensionObject.ToEncodeable(result.HistoryData) is HistoryData data)
            {
                page = data.DataValues.Count;
                foreach (var dv in data.DataValues)
                {
                    // Paging by time: servers may round the start (e.g. to microseconds) and return the last value again.
                    if (pagedAfter is { } after && dv.SourceTimestamp <= after)
                    {
                        continue;
                    }

                    var decoded = await DecodeStructuresAsync(dv, cancellationToken).ConfigureAwait(false);
                    values.Add(new ValueUpdate(
                        nodeId,
                        ValueFormatter.Format(decoded.WrappedValue),
                        dv.StatusCode,
                        dv.SourceTimestamp,
                        dv.ServerTimestamp,
                        ValueFormatter.ToNumeric(decoded.WrappedValue),
                        decoded.WrappedValue.Value));
                }
            }

            if (values.Count >= maxValues && continuation is not null)
            {
                truncated = true;
                break;
            }

            if (continuation is not null)
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
