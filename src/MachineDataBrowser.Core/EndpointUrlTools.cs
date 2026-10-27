namespace MachineDataBrowser.Core;

/// <summary>Small helpers for endpoint URLs that may contain credentials.</summary>
public static class EndpointUrlTools
{
    /// <summary>Whether the endpoint URL contains a non-empty password in its user information.</summary>
    public static bool HasPassword(string endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);
        var url = endpointUrl.Trim();
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.IndexOf('@', StringComparison.Ordinal);
        if (scheme < 0 || at <= scheme + 3)
        {
            return false;
        }

        var start = scheme + 3;
        var colon = url.IndexOf(':', start, at - start);
        return colon >= start && colon < at - 1;
    }

    /// <summary>Removes the password from a URL's user information, retaining the user name for a later prompt.</summary>
    public static string WithoutPassword(string endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);
        var url = endpointUrl.Trim();
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.IndexOf('@', StringComparison.Ordinal);
        if (scheme < 0 || at < scheme)
        {
            return url;
        }

        var userInfoStart = scheme + 3;
        var colon = url.IndexOf(':', userInfoStart, at - userInfoStart);
        return colon < 0 ? url : $"{url[..colon]}{url[at..]}";
    }

    /// <summary>Reads a percent-encoded user name from an endpoint URL, if it has one.</summary>
    public static string? UserName(string endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);
        var url = endpointUrl.Trim();
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.IndexOf('@', StringComparison.Ordinal);
        if (scheme < 0 || at <= scheme + 3)
        {
            return null;
        }

        var start = scheme + 3;
        var colon = url.IndexOf(':', start, at - start);
        var user = colon < 0 ? url[start..at] : url[start..colon];
        return Uri.UnescapeDataString(user) is { Length: > 0 } value ? value : null;
    }
}
