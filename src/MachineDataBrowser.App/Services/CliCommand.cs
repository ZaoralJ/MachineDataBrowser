using System.Globalization;
using System.Text;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.Services;

/// <summary>
/// Builds <c>mdbrowser</c> command lines for what is selected in the app, to paste into a terminal: the same endpoint,
/// the connection options that carry over, and the nodes. Never contains a password.
/// </summary>
public static class CliCommand
{
    public const string Executable = "mdbrowser";

    /// <summary>The connection part every command shares.</summary>
    public sealed record Connection(string EndpointUrl, string? UserName, bool UseSecurity, bool TrustAll);

    public static string Browse(Connection connection, string? node, int depth = 1) =>
        Build("browse", connection, node is null ? [] : [node], depth > 1 ? ["--depth", depth.ToString(CultureInfo.InvariantCulture)] : []);

    public static string Read(Connection connection, IReadOnlyList<string> nodes, bool recursive) =>
        Build("read", connection, nodes, recursive ? ["-R"] : []);

    public static string Monitor(Connection connection, IReadOnlyList<string> nodes, bool recursive, int? refreshMs) =>
        Build("monitor", connection, nodes, [.. recursive ? ["-R"] : Array.Empty<string>(), .. refreshMs is { } ms ? ["-r", ms.ToString(CultureInfo.InvariantCulture)] : Array.Empty<string>()]);

    /// <summary>The CLI's refresh time when none is given: every message for MQTT, 250 ms otherwise.</summary>
    public static int DefaultRefreshMs(string endpointUrl) => DeviceClient.IsMqtt(endpointUrl) ? 0 : 250;

    /// <summary>The endpoint without a password: <c>mqtt://user:secret@host</c> becomes <c>mqtt://user@host</c>.</summary>
    public static string WithoutPassword(string endpointUrl)
    {
        var url = endpointUrl.Trim();
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.IndexOf('@', StringComparison.Ordinal);
        if (scheme < 0 || at < scheme)
        {
            return url;
        }

        var userInfo = url[(scheme + 3)..at];
        var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? url : $"{url[..(scheme + 3)]}{userInfo[..colon]}{url[at..]}";
    }

    /// <summary>Quotes an argument for POSIX shells (zsh, bash) when it contains anything but plainly safe characters.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "_-./:@%+=,".Contains(c, StringComparison.Ordinal)))
        {
            return argument;
        }

        return $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static string Build(string verb, Connection connection, IReadOnlyList<string> nodes, IReadOnlyList<string> options)
    {
        var url = WithoutPassword(connection.EndpointUrl);
        var line = new StringBuilder($"{Executable} {verb} {Quote(url)}");
        foreach (var node in nodes)
        {
            line.Append(' ').Append(Quote(node));
        }

        foreach (var option in options)
        {
            line.Append(' ').Append(option);
        }

        if (!string.IsNullOrWhiteSpace(connection.UserName))
        {
            line.Append(" --user ").Append(Quote(connection.UserName.Trim()));
        }

        if (connection.UseSecurity && !DeviceClient.IsEip(url) && !DeviceClient.IsMqtt(url))
        {
            line.Append(" --secure");
        }

        if (connection.TrustAll)
        {
            line.Append(" --trust-all");
        }

        return line.ToString();
    }
}
