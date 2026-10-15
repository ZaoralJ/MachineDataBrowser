using System.Globalization;
using System.Text.Json;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Recording.Json;

public static class JsonRecordingExporter
{
    public static async Task ExportAsync(RecordingSnapshot snapshot, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("name", snapshot.Name);
        if (snapshot.StartedAt is { } started)
        {
            json.WriteString("startedAt", started);
        }

        if (snapshot.StoppedAt is { } stopped)
        {
            json.WriteString("stoppedAt", stopped);
        }

        json.WriteNumber("samplingIntervalMs", snapshot.SamplingIntervalMs);
        json.WriteStartArray("items");
        foreach (var entry in snapshot.Items)
        {
            json.WriteStartObject();
            json.WriteString("nodeId", entry.Item.PortableId);
            json.WriteString("displayName", entry.Item.DisplayName);
            json.WriteStartArray("samples");
            foreach (var sample in entry.Samples)
            {
                json.WriteStartObject();
                json.WriteString("receivedAt", sample.ReceivedAt);
                json.WriteString("sourceTimestamp", sample.SourceTimestamp);
                json.WriteString("value", sample.Value);
                if (sample.Numeric is { } numeric)
                {
                    if (double.IsFinite(numeric))
                    {
                        json.WriteNumber("numeric", numeric);
                    }
                    else
                    {
                        json.WriteString("numeric", numeric.ToString(CultureInfo.InvariantCulture));
                    }
                }

                json.WriteString("status", MachineDataBrowser.Core.StatusText.Of(sample.Status));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
