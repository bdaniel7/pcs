using System.Text.RegularExpressions;

namespace PlexCompatibleServer.Api;

/// <summary>
///     Builds the request/identity diagnostic strings used by the request logger. The auth token must
///     never reach the log, whether it arrives as the <c>X-Plex-Token</c> header or query parameter,
///     so both the request line and the identity dump redact it.
/// </summary>
internal static class RequestLog
{
    const string TOKEN_KEY = "X-Plex-Token";
    const string REDACTED = "***";

    static readonly Regex tokenQuery =
            new(@"(?<![A-Za-z0-9_-])X-Plex-Token=[^&]*",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

    /// <summary>Path + query with any token value removed, truncated for readability.</summary>
    internal static string DescribeTarget(HttpContext context)
    {
        var target = context.Request.Path + context.Request.QueryString;
        target = tokenQuery.Replace(target, redactedPrefix());

        return target.Length > 4000 ? target[..4000] + "..." : target;
    }

    /// <summary>Space-joined X-Plex-* headers and query parameters with the token redacted.</summary>
    internal static string DescribeIdentity(HttpContext context)
    {
        var headers = context.Request.Headers
                             .Where(h => h.Key.StartsWith("X-Plex-", StringComparison.OrdinalIgnoreCase))
                             .Select(h => $"{h.Key}={redact(h.Key, h.Value.ToString())}");

        var query = context.Request.Query
                           .Where(q => q.Key.StartsWith("X-Plex-", StringComparison.OrdinalIgnoreCase))
                           .Select(q => $"{q.Key}={redact(q.Key, q.Value.ToString())}");

        return string.Join(" ", headers.Concat(query));
    }

    static string redactedPrefix()
    {
        return TOKEN_KEY + "=" + REDACTED;
    }

    static string redact(string key,
                         string value)
    {
        return key.Equals(TOKEN_KEY, StringComparison.OrdinalIgnoreCase) ? REDACTED : value;
    }
}
