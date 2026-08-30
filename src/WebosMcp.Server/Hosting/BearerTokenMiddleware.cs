using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WebosMcp.Server.Configuration;

namespace WebosMcp.Server.Hosting;

/// <summary>
/// Rejects every unauthenticated HTTP request with 401 before any tool logic
/// runs, whenever a token is configured. Applied to all paths — there is no
/// unauthenticated escape hatch.
/// </summary>
public sealed class BearerTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HttpTransportSettings _settings;
    private readonly ILogger<BearerTokenMiddleware> _logger;
    private readonly byte[]? _expected;

    public BearerTokenMiddleware(
        RequestDelegate next,
        HttpTransportSettings settings,
        ILogger<BearerTokenMiddleware> logger)
    {
        _next = next;
        _settings = settings;
        _logger = logger;
        _expected = settings.Token is null ? null : Encoding.UTF8.GetBytes(settings.Token);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_expected is null)
        {
            // No token configured. Only reachable on a loopback bind — the
            // startup guard refuses any other combination.
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!TryReadBearer(context, out var presented) || !IsMatch(presented))
        {
            _logger.LogWarning(
                "Rejected an unauthenticated request to {Path} from {Remote}.",
                context.Request.Path,
                context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync(
                "{\"error\":\"unauthorized\",\"message\":\"A valid bearer token is required.\"}")
                .ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool TryReadBearer(HttpContext context, out string token)
    {
        token = string.Empty;
        var header = context.Request.Headers.Authorization.ToString();

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header[prefix.Length..].Trim();
        return token.Length > 0;
    }

    /// <summary>Fixed-time comparison so a wrong token cannot be recovered by timing.</summary>
    private bool IsMatch(string presented)
    {
        var candidate = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(candidate, _expected!);
    }
}
