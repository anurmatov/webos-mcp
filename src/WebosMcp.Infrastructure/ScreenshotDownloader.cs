using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Infrastructure;

/// <summary>
/// Downloads one captured frame from the selected TV.
///
/// Redirects are followed MANUALLY rather than by <c>HttpClient</c>: automatic
/// redirects would leave the final target unchecked, and "the first URL pointed at
/// the TV" says nothing about where the bytes actually came from. Every hop is
/// re-pinned to the selected TV, so a cross-host redirect is refused instead of
/// silently followed.
/// </summary>
public sealed class ScreenshotDownloader : IScreenshotDownloader
{
    private readonly HttpClient _http;
    private readonly WebosMcpOptions _options;
    private readonly ILogger<ScreenshotDownloader> _logger;

    public ScreenshotDownloader(
        HttpClient http,
        IOptions<WebosMcpOptions> options,
        ILogger<ScreenshotDownloader> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ReadOnlyMemory<byte>> DownloadAsync(Uri imageUri, CancellationToken cancellationToken)
    {
        // Resolved, not raw: an unvalidated 0 or -1 here would mean "no timeout"
        // and "no size cap" rather than a configured bound.
        var maxBytes = _options.ResolvedScreenshotMaxBytes;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ResolvedScreenshotTimeoutSeconds));

        var target = imageUri;

        for (var hop = 0; ; hop++)
        {
            // The FULL policy on entry, not only the host: this loop is the one
            // place that decides what URL is actually requested, and a redirect
            // target is another URI from the same untrusted source.
            ScreenshotPolicy.ValidateTarget(target, _options, "The capture download target");

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await SendAsync(request, timeout.Token, cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (hop >= ScreenshotPolicy.MaxRedirects)
                {
                    throw new TvException(
                        TvErrorCode.TvError,
                        $"The capture download exceeded {ScreenshotPolicy.MaxRedirects} redirects without returning an image.");
                }

                var location = response.Headers.Location
                    ?? throw new TvException(
                        TvErrorCode.TvError,
                        $"The TV answered the capture download with HTTP {(int)response.StatusCode} and no Location header.");

                // A relative Location resolves against the current target; an
                // absolute one can be anything at all — scheme, credentials and host
                // included — so the resolved value goes through the same full policy
                // as the original imageUri, at the top of the next iteration.
                target = location.IsAbsoluteUri ? location : new Uri(target, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TvException(
                    TvErrorCode.TvError,
                    $"The TV answered the capture download with HTTP {(int)response.StatusCode}.");
            }

            // Cheap pre-check. It is advisory only — a wrong or absent
            // Content-Length cannot get past the streaming cap below.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                throw Oversized(maxBytes);
            }

            return await ReadCappedAsync(response, maxBytes, timeout.Token, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken bounded,
        CancellationToken caller)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw TvException.TimedOut("screenshot download");
        }
        catch (HttpRequestException ex)
        {
            // No URI in the message: it is TV-supplied and this reaches a caller.
            throw TvException.Unreachable("The capture could not be downloaded from the TV.", ex);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadCappedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken bounded,
        CancellationToken caller)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(bounded).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            var chunk = new byte[8192];

            while (true)
            {
                var read = await stream.ReadAsync(chunk, bounded).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maxBytes)
                {
                    // Abort here rather than after the fact: the point of the cap is
                    // that an oversized body is never fully held in memory.
                    throw Oversized(maxBytes);
                }

                buffer.Write(chunk, 0, read);
            }

            _logger.LogDebug("Capture download completed: {ByteCount} bytes.", buffer.Length);

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw TvException.TimedOut("screenshot download");
        }
        catch (HttpRequestException ex)
        {
            throw TvException.Unreachable("The capture download was interrupted.", ex);
        }
        catch (IOException ex)
        {
            throw TvException.Unreachable("The capture download was interrupted.", ex);
        }
    }

    private static TvException Oversized(int maxBytes) => new(
        TvErrorCode.TvError,
        $"The capture download exceeded the {maxBytes}-byte limit and was aborted.");

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
