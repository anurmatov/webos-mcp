using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using WebosMcp.Domain;

namespace WebosMcp.Server.Tools;

/// <summary>
/// Uniform tool envelope. Errors carry a stable machine-checkable
/// <see cref="ToolError.Code"/> so a caller never has to string-match a
/// free-form message to tell PAIRING_REQUIRED from TV_OFF.
/// </summary>
public sealed record ToolResult
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolError? Error { get; init; }

    public static ToolResult Success(object? result = null) => new() { Ok = true, Result = result };

    public static ToolResult Failure(TvErrorCode code, string message) =>
        new() { Ok = false, Error = new ToolError(code.ToWireCode(), message) };
}

public sealed record ToolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Single funnel for every tool invocation. Guarantees that no exception
/// escapes untyped and that unexpected failures never leak internals.
/// </summary>
public static class ToolInvoker
{
    public static async Task<ToolResult> RunAsync(
        ILogger logger,
        string toolName,
        Func<Task<object?>> action)
    {
        try
        {
            return ToolResult.Success(await action().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Translate(logger, toolName, ex);
        }
    }

    public static Task<ToolResult> RunAsync(ILogger logger, string toolName, Func<Task> action) =>
        RunAsync(logger, toolName, async () =>
        {
            await action().ConfigureAwait(false);
            return (object?)new { done = true };
        });

    /// <summary>
    /// The funnel for a tool whose SUCCESS is a native MCP content block rather
    /// than the JSON envelope — currently only the screenshot, whose payload is
    /// image bytes that the JSON envelope cannot carry without base64-ing them
    /// into a text field.
    ///
    /// Failures go back through the identical envelope every other tool uses, so a
    /// caller still checks <c>ok</c> and reads the same <c>error.code</c>. Only the
    /// success shape differs, and only because it has to.
    /// </summary>
    public static async Task<CallToolResult> RunContentAsync(
        ILogger logger,
        string toolName,
        Func<Task<ContentBlock>> action)
    {
        try
        {
            return new CallToolResult { Content = [await action().ConfigureAwait(false)] };
        }
        catch (Exception ex)
        {
            return AsContent(Translate(logger, toolName, ex));
        }
    }

    /// <summary>Renders the standard envelope as the text block a content-returning tool sends.</summary>
    internal static CallToolResult AsContent(ToolResult result) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, EnvelopeJson) }],
    };

    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// One exception-to-code mapping for both funnels. Keeping it in a single
    /// place is what stops the content-returning tool drifting into a different
    /// error contract from the other fifty.
    /// </summary>
    private static ToolResult Translate(ILogger logger, string toolName, Exception exception)
    {
        switch (exception)
        {
            case TvException ex:
                logger.LogInformation(
                    "Tool '{Tool}' returned {Code}: {Message}", toolName, ex.Code.ToWireCode(), ex.Message);
                return ToolResult.Failure(ex.Code, ex.Message);

            case OperationCanceledException:
                return ToolResult.Failure(
                    TvErrorCode.Timeout, $"Tool '{toolName}' was cancelled before completing.");

            default:
                // Message is deliberately generic: an exception string can carry
                // configuration detail, and nothing here is worth leaking.
                logger.LogError(exception, "Tool '{Tool}' failed unexpectedly.", toolName);
                return ToolResult.Failure(
                    TvErrorCode.TvError,
                    $"Tool '{toolName}' failed unexpectedly. Check the server logs for details.");
        }
    }
}

/// <summary>
/// Builders for native MCP content blocks.
/// </summary>
public static class ToolContent
{
    /// <summary>
    /// Wraps raw image bytes as an MCP image block.
    ///
    /// ⚠️ <see cref="ImageContentBlock.Data"/> is the BASE64 TEXT, carried as its
    /// UTF-8 bytes — it is not the image. Assigning the raw bytes compiles, runs,
    /// and serialises to a <c>"type":"image"</c> block that looks correct from the
    /// server side, but the <c>data</c> field then holds the raw bytes escaped as a
    /// string and no client can decode it. Encode here, once, and never construct
    /// the block by hand.
    /// </summary>
    public static ImageContentBlock Image(ReadOnlyMemory<byte> bytes, string mimeType) => new()
    {
        Data = Encoding.UTF8.GetBytes(Convert.ToBase64String(bytes.Span)),
        MimeType = mimeType,
    };
}
