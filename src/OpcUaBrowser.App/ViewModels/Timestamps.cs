using System.Globalization;

namespace OpcUaBrowser.App.ViewModels;

/// <summary>
/// One timestamp format for the whole app: local time of day, e.g. "22:05:54.649". Tooltips show the full date with
/// the UTC offset ("30 Sep 2026 22:05:54.649 UTC+02:00").
/// </summary>
public static class Timestamps
{
    public const string Pattern = "HH:mm:ss.fff";

    public static string Format(DateTimeOffset time) => time.ToLocalTime().ToString(Pattern, CultureInfo.InvariantCulture);

    public static string Format(DateTime time) => Format(time.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(time, TimeSpan.Zero) : new DateTimeOffset(time));

    /// <summary>Without milliseconds, for schedules ("stops 22:10:00").</summary>
    public static string FormatSeconds(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string ToolTip(DateTimeOffset time) => time.ToLocalTime().ToString("d MMM yyyy HH:mm:ss.fff 'UTC'zzz", CultureInfo.InvariantCulture);
}
