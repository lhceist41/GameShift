using System;

namespace GameShift.Core.Updates;

/// <summary>
/// Validates URLs against the GitHub domain allowlist used by the update pipeline.
/// </summary>
public static class GitHubUrlValidator
{
    /// <summary>
    /// Returns true if the URL is a valid HTTPS URL pointing to github.com,
    /// *.github.com, or *.githubusercontent.com.
    /// </summary>
    public static bool IsValid(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();
        return host == "github.com"
            || host.EndsWith(".github.com")
            || host.EndsWith(".githubusercontent.com");
    }
}
