using Microsoft.Extensions.Logging;

namespace WebosMcp.Server.Hosting;

/// <summary>
/// Keeps secrets out of the log stream.
///
/// The YouTube Lounge protocol carries its token as a QUERY STRING parameter
/// (<c>loungeIdToken=…</c>), and <c>HttpClient</c>'s built-in logging writes the
/// full request URI at Information. So simply using HttpClient normally would
/// print a live credential on every YouTube call — no code of ours has to log it
/// for it to leak.
///
/// Both hosts and the operator CLI call this one method, so the policy cannot
/// hold in one entry point and be missing from another.
/// </summary>
public static class SecretSafeLogging
{
    /// <summary>
    /// The HttpClient logging categories that emit request URIs. Both are needed:
    /// the outer one logs the logical request, the inner one logs each attempt.
    /// </summary>
    internal static readonly string[] UriEmittingCategories =
    [
        "System.Net.Http.HttpClient",
        "Microsoft.Extensions.Http",
    ];

    /// <summary>
    /// Raises the floor for URI-emitting categories to Warning, which is above the
    /// level at which request URIs are written. Warnings and errors still surface —
    /// they carry no URI.
    /// </summary>
    public static ILoggingBuilder AddSecretSafeFilters(this ILoggingBuilder logging)
    {
        foreach (var category in UriEmittingCategories)
        {
            logging.AddFilter(category, LogLevel.Warning);
        }

        return logging;
    }
}
