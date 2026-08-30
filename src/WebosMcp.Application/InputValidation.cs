using System.Text.RegularExpressions;
using WebosMcp.Domain;

namespace WebosMcp.Application;

/// <summary>
/// Every value that reaches the TV passes through here first. Validation
/// failures are INVALID_INPUT and never open a connection.
/// </summary>
public static partial class InputValidation
{
    public const int MaxPointerDelta = 500;
    public const int MaxTextLength = 512;
    public const int MaxToastLength = 512;
    public const int MaxSearchQueryLength = 256;
    public const int MaxUrlLength = 2048;
    public const int MaxRepeat = 20;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,127}$")]
    private static partial Regex AppIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{11}$")]
    private static partial Regex YouTubeIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,127}$")]
    private static partial Regex InputIdPattern();

    [GeneratedRegex(@"^[0-9]{1,5}(-[0-9]{1,5})?$")]
    private static partial Regex ChannelNumberPattern();

    /// <summary>HTTPS-only by design — plain HTTP is rejected, not upgraded.</summary>
    public static Uri ValidateHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw TvException.Invalid("A URL is required.");
        }

        if (url.Length > MaxUrlLength)
        {
            throw TvException.Invalid($"URL exceeds the maximum length of {MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw TvException.Invalid($"'{Redact(url)}' is not a well-formed absolute URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw TvException.Invalid(
                $"Only HTTPS URLs are accepted; got scheme '{uri.Scheme}'. Plain HTTP is rejected rather than silently upgraded.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw TvException.Invalid("URL has no host component.");
        }

        return uri;
    }

    public static string ValidateAppId(string? appId)
    {
        var value = (appId ?? string.Empty).Trim();
        if (!AppIdPattern().IsMatch(value))
        {
            throw TvException.Invalid(
                $"'{Redact(appId)}' is not a valid app id. Expected 1-128 characters of letters, digits, dot, underscore or hyphen.");
        }

        return value;
    }

    public static string ValidateInputId(string? inputId)
    {
        var value = (inputId ?? string.Empty).Trim();
        if (!InputIdPattern().IsMatch(value))
        {
            throw TvException.Invalid($"'{Redact(inputId)}' is not a valid input id.");
        }

        return value;
    }

    public static string ValidateChannelNumber(string? channel)
    {
        var value = (channel ?? string.Empty).Trim();
        if (!ChannelNumberPattern().IsMatch(value))
        {
            throw TvException.Invalid(
                $"'{Redact(channel)}' is not a valid channel number. Expected digits, optionally major-minor such as 7-1.");
        }

        return value;
    }

    public static int ValidateVolume(int volume)
    {
        if (volume is < 0 or > 100)
        {
            throw TvException.Invalid($"Volume must be between 0 and 100; got {volume}.");
        }

        return volume;
    }

    public static int ValidatePointerDelta(int delta, string name)
    {
        if (Math.Abs(delta) > MaxPointerDelta)
        {
            throw TvException.Invalid(
                $"Pointer {name} delta must be within +/-{MaxPointerDelta}; got {delta}.");
        }

        return delta;
    }

    public static int ValidateRepeat(int repeat)
    {
        if (repeat is < 1 or > MaxRepeat)
        {
            throw TvException.Invalid($"Repeat must be between 1 and {MaxRepeat}; got {repeat}.");
        }

        return repeat;
    }

    public static string ValidateText(string? text)
    {
        if (text is null)
        {
            throw TvException.Invalid("Text is required.");
        }

        if (text.Length > MaxTextLength)
        {
            throw TvException.Invalid($"Text exceeds the maximum length of {MaxTextLength} characters.");
        }

        if (text.Any(c => char.IsControl(c) && c != '\t'))
        {
            throw TvException.Invalid("Text must not contain control characters.");
        }

        return text;
    }

    public static string ValidateToastMessage(string? message)
    {
        var value = (message ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw TvException.Invalid("Toast message must not be empty.");
        }

        if (value.Length > MaxToastLength)
        {
            throw TvException.Invalid($"Toast message exceeds the maximum length of {MaxToastLength} characters.");
        }

        return value;
    }

    public static string ValidateSearchQuery(string? query)
    {
        var value = (query ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw TvException.Invalid("Search query must not be empty.");
        }

        if (value.Length > MaxSearchQueryLength)
        {
            throw TvException.Invalid(
                $"Search query exceeds the maximum length of {MaxSearchQueryLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw TvException.Invalid("Search query must not contain control characters.");
        }

        return value;
    }

    /// <summary>
    /// Accepts a bare 11-character video id or a YouTube URL, and returns the
    /// bare id. Any other host is rejected — this is not a general URL opener.
    /// </summary>
    public static string ValidateYouTubeVideoId(string? videoOrUrl)
    {
        var value = (videoOrUrl ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw TvException.Invalid("A YouTube video id or URL is required.");
        }

        if (YouTubeIdPattern().IsMatch(value))
        {
            return value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.TrimStart('w', '.').ToLowerInvariant();
            var candidate = host switch
            {
                "youtu.be" => uri.AbsolutePath.Trim('/'),
                _ when uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) =>
                    ExtractQueryValue(uri.Query, "v") ?? uri.AbsolutePath.Split('/').LastOrDefault(),
                _ => null,
            };

            if (candidate is not null && YouTubeIdPattern().IsMatch(candidate))
            {
                return candidate;
            }
        }

        throw TvException.Invalid(
            $"'{Redact(videoOrUrl)}' is not a recognised YouTube video id or URL. Expected an 11-character id, a youtu.be link, or a youtube.com watch URL.");
    }

    private static string? ExtractQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            if (index > 0 && pair[..index].Equals(key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(index + 1)..]);
            }
        }

        return null;
    }

    /// <summary>Bounds echoed user input so a malformed value cannot flood a log line.</summary>
    private static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 64 ? value : value[..64] + "...";
    }
}
