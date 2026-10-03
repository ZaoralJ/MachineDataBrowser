using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// Recordings the agent starts in the background (only with <c>--allow-recording &lt;folder&gt;</c>): they write SQLite
/// files into that folder only, stop by themselves after a limit, and are stopped when the server exits. The recording
/// tools can read them while they run.
/// </summary>
internal sealed class RecordingControlTools(EndpointPool pool, string folder) : IAsyncDisposable
{
    public const int MaxActive = 5;
    public const int MaxItems = 500;
    public const int MaxMinutes = 24 * 60;

    private readonly Dictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _next;

    [McpServerTool(Name = "start_recording", Destructive = false, OpenWorld = false)]
    [Description("Starts recording variables in the background into a SQLite file in the server's recording folder, for later "
        + "analysis with recording_items and recording_samples. Stops by itself after durationMinutes (max 24 h) or with stop_recording.")]
    public Task<string> StartRecordingAsync(
        [Description("Recording name; also the file name (letters, digits, - and _)")] string name,
        [Description("Variables or folders: paths like /Objects/Line1 or ids")] string[] nodes,
        [Description("How long to record, 1-1440 minutes")] int durationMinutes = 60,
        [Description("Expand folders and structures to every variable below them")] bool recursive = false,
        [Description("Refresh time in ms (default 1000)")] int refreshMs = 1000,
        [Description("Endpoint URL from list_endpoints; may be omitted when only one is configured")] string? endpoint = null,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        var file = Path.Combine(folder, $"{SafeName(name)}.db");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active.Values.Count(a => a.Recording.State is RecordingState.Recording or RecordingState.Paused) >= MaxActive)
            {
                throw new McpException($"At most {MaxActive} recordings run at a time; stop one first.");
            }

            var target = pool.Find(endpoint);
            var client = await pool.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var resolved = recursive
                ? await NodeQueries.ExpandAsync(client, nodes, NodeQueries.DefaultRecursiveDepth, MaxItems, _ => Task.CompletedTask, cancellationToken).ConfigureAwait(false)
                : await NodeQueries.ResolveAllAsync(client, nodes, cancellationToken).ConfigureAwait(false);
            if (resolved.Count is 0 or > MaxItems)
            {
                throw new McpException(resolved.Count == 0 ? "Nothing to record." : $"At most {MaxItems} items per recording.");
            }

            var minutes = Math.Clamp(durationMinutes, 1, MaxMinutes);
            var recording = new Recording(client, new RecordingOptions
            {
                Name = name.Trim(),
                SamplingIntervalMs = Math.Max(0, refreshMs),
                MaxPointsPerItem = 1,
                LiveFilePath = file,
                Endpoint = target.Url,
                StopAfter = TimeSpan.FromMinutes(minutes),
            }, [.. resolved.DistinctBy(n => n.Id).Select(n => new RecordedItem(n.Id, n.DisplayName ?? n.Name, client.ToPortableId(n.Id), n.ParentPath))]);
            await recording.StartAsync(cancellationToken).ConfigureAwait(false);

            var id = $"r{Interlocked.Increment(ref _next)}";
            _active[id] = new Active(recording, file, target.Url);
            return new JsonObject
            {
                ["id"] = id,
                ["file"] = Path.GetFileName(file),
                ["items"] = recording.Items.Count,
                ["stopsAt"] = recording.PlannedStopAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
        }
        finally
        {
            _gate.Release();
        }
    });

    [McpServerTool(Name = "stop_recording", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stops a background recording started with start_recording; its file stays for recording_items and recording_samples.")]
    public Task<string> StopRecordingAsync(
        [Description("Id returned by start_recording or listed by active_recordings")] string id,
        CancellationToken cancellationToken = default) => Guard(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_active.Remove(id, out var active))
            {
                throw new McpException($"No recording '{id}'; see active_recordings.");
            }

            await active.Recording.StopAsync(cancellationToken).ConfigureAwait(false);
            var summary = Describe(id, active);
            await active.Recording.DisposeAsync().ConfigureAwait(false);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    });

    [McpServerTool(Name = "active_recordings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Background recordings started in this session: state, file, items, samples so far, elapsed time and planned stop.")]
    public Task<string> ActiveRecordingsAsync(CancellationToken cancellationToken = default) => Guard(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new JsonArray([.. _active.Select(a => (JsonNode)Describe(a.Key, a.Value))]);
        }
        finally
        {
            _gate.Release();
        }
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var active in _active.Values)
        {
            await active.Recording.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await active.Recording.DisposeAsync().ConfigureAwait(false);
        }

        _active.Clear();
        _gate.Dispose();
    }

    /// <summary>A file name from what the agent called the recording: letters, digits, '-' and '_' only, so it stays in the folder.</summary>
    internal static string SafeName(string name)
    {
        var safe = new string([.. (name ?? string.Empty).Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]).Trim('_');
        return safe.Length is > 0 and <= 80 ? safe : throw new McpException("Give the recording a name of 1-80 letters, digits, '-' or '_'.");
    }

    private static JsonObject Describe(string id, Active active) => new()
    {
        ["id"] = id,
        ["name"] = active.Recording.Options.Name,
        ["state"] = active.Recording.State.ToString(),
        ["file"] = Path.GetFileName(active.File),
        ["endpoint"] = active.Endpoint,
        ["items"] = active.Recording.Items.Count,
        ["samples"] = active.Recording.TotalSamples,
        ["elapsedSeconds"] = Math.Round(active.Recording.Elapsed.TotalSeconds),
        ["stopsAt"] = active.Recording.PlannedStopAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    };

    private static async Task<string> Guard(Func<Task<JsonNode>> tool)
    {
        try
        {
            return (await tool().ConfigureAwait(false)).ToJsonString();
        }
        catch (CliException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (Opc.Ua.ServiceResultException ex)
        {
            throw new McpException($"{StatusText.Of(ex.Result.StatusCode)}: {ex.Message}");
        }
    }

    private sealed record Active(Recording Recording, string File, string Endpoint);
}
