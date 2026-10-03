using System.Globalization;

namespace MachineDataBrowser.Core;

/// <summary>Shared wording for the protocol clients' <see cref="IConnectionDiagnosticsSource"/> rows.</summary>
internal static class DiagnosticsFormat
{
    public static string Duration(double ms) => ms >= 1000
        ? (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s"
        : ms.ToString("0", CultureInfo.InvariantCulture) + " ms";

    public static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Ago(DateTime utc)
    {
        if (utc == DateTime.MinValue)
        {
            return "never";
        }

        var age = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return age.TotalSeconds switch
        {
            < 1 => "just now",
            < 60 => $"{(int)age.TotalSeconds} s ago",
            < 3600 => $"{(int)age.TotalMinutes} min ago",
            < 86400 => $"{(int)age.TotalHours} h {age.Minutes} min ago",
            _ => $"{(int)age.TotalDays} days ago",
        };
    }

    public static string Since(DateTime? utc) => utc is { } since ? $"{Local(since)} ({Ago(since)})" : string.Empty;

    public static string Reconnects(int count, DateTime? last) =>
        count == 0 || last is null ? "0" : $"{count} (last {Local(last.Value)})";

    /// <summary>Events per second between two samples of a running counter; "measuring…" for the first sample.</summary>
    public static string Rate(long count, ref (long Count, DateTime At)? previous)
    {
        var now = DateTime.UtcNow;
        var text = previous is { } p && now > p.At
            ? (Math.Max(0, count - p.Count) / (now - p.At).TotalSeconds).ToString("0.#", CultureInfo.InvariantCulture) + " / s"
            : "measuring…";
        previous = (count, now);
        return text;
    }

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };
}
