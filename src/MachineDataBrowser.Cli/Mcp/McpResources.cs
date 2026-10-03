using System.ComponentModel;
using System.Text.Json;
using MachineDataBrowser.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MachineDataBrowser.Cli.Mcp;

/// <summary>
/// Documents the agent can read without calling tools: the machines it may use, the app session files it was given
/// (endpoint, options and watch list; never a password) and the recording files with their recordings.
/// </summary>
internal sealed class McpResources(EndpointPool pool, IReadOnlyList<string> sessionFiles, RecordingLibrary recordings)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [McpServerResource(UriTemplate = "mdbrowser://endpoints", Name = "endpoints", Title = "Machines", MimeType = "application/json")]
    [Description("The endpoints this server may connect to, the patterns for ones the user may name, and the session resources.")]
    public string Endpoints() => JsonSerializer.Serialize(new
    {
        endpoints = pool.Endpoints.Select(e => new { endpoint = e.Url, state = pool.StateOf(e).ToString(), readOnly = e.ReadOnly }),
        allowedPatterns = pool.Patterns,
        sessions = sessionFiles.Select(f => $"mdbrowser://sessions/{Path.GetFileName(f)}"),
    }, Indented);

    [McpServerResource(UriTemplate = "mdbrowser://sessions/{name}", Name = "session", Title = "Session file", MimeType = "application/json")]
    [Description("An app session file given to the server: endpoint, options and watch list (the nodes worth looking at first).")]
    public async Task<string> SessionAsync([Description("File name of the session, e.g. line1.mdbsession")] string name, CancellationToken cancellationToken = default)
    {
        var file = sessionFiles.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal))
            ?? throw new McpException($"No session '{name}'. Available: {string.Join(", ", sessionFiles.Select(Path.GetFileName))}.");
        return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
    }

    [McpServerResource(UriTemplate = "mdbrowser://recordings", Name = "recordings", Title = "Recording files", MimeType = "application/json")]
    [Description("The SQLite recording files this server may read and the recordings in each.")]
    public async Task<string> RecordingsAsync(CancellationToken cancellationToken = default)
    {
        var files = new List<object>();
        foreach (var file in recordings.Files())
        {
            try
            {
                files.Add(new { file = Path.GetFileName(file), recordings = await SqliteRecordingQuery.RecordingsAsync(file, cancellationToken).ConfigureAwait(false) });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                files.Add(new { file = Path.GetFileName(file), error = ex.Message });
            }
        }

        return JsonSerializer.Serialize(files, Indented);
    }
}

/// <summary>Ready-made workflows the user can pick in the agent: they chain the tools for common questions.</summary>
internal sealed class McpPrompts
{
    [McpServerPrompt(Name = "diagnose_machine", Title = "Diagnose a machine")]
    [Description("Find out why a machine or area misbehaves: connection, alarms, and the values that look wrong.")]
    public static string DiagnoseMachine(
        [Description("Endpoint URL, if several are configured")] string? endpoint = null,
        [Description("Area to focus on, e.g. /Objects/Line1")] string? area = null) => $"""
        Diagnose {(area is null ? "the machine" : $"the area {area}")}{(endpoint is null ? string.Empty : $" on {endpoint}")}:
        1. Run diagnostics: is the connection healthy, is the server running, is its clock in sync?
        2. List the current alarms (alarms) and explain the most severe ones.
        3. {(area is null ? "Browse from the root a level or two to find the main areas, then" : $"Browse {area} and")} read its values
           (recursive=true for a folder); point out values that are not Good, out of their range (see attributes for units
           and EURange) or suspicious.
        4. Sample the most relevant values for 30 seconds to see whether they move, fluctuate or are stuck.
        Summarise the likely causes first, then the evidence. Don't change anything.
        """;

    [McpServerPrompt(Name = "check_alarms", Title = "Check alarms")]
    [Description("Explain the current alarms, most severe first, with what to check for each.")]
    public static string CheckAlarms([Description("Endpoint URL, if several are configured")] string? endpoint = null) => $"""
        List the current alarms{(endpoint is null ? string.Empty : $" on {endpoint}")} with the alarms tool. For each, most severe
        first: what it means, since when it is active, whether it is acknowledged, and which values to look at (read them,
        with attributes for units). End with what should be checked first on site.
        """;

    [McpServerPrompt(Name = "summarize_recording", Title = "Summarise a recording")]
    [Description("Summarise what happened in a recording: ranges, trends, anomalies and moments when values were not Good.")]
    public static string SummarizeRecording(
        [Description("Recording file name, if several are available")] string? file = null,
        [Description("Item to focus on, e.g. Speed")] string? item = null) => $"""
        Summarise the recording{(file is null ? string.Empty : $" in {file}")}{(item is null ? string.Empty : $", focusing on {item}")}:
        1. list_recordings to see what was recorded and when; recording_items for each item's range, average and last value.
        2. recording_samples with bucketSeconds chosen so the whole range fits in about 100 buckets per item, to see trends.
        3. Point out anomalies: jumps, flat lines, values outside the usual range, and buckets with samples that were not Good;
           zoom in on those periods with raw samples.
        Report the overall behaviour first, then the notable moments with their times (UTC).
        """;
}
