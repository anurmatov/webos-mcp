using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
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
        catch (TvException ex)
        {
            logger.LogInformation(
                "Tool '{Tool}' returned {Code}: {Message}", toolName, ex.Code.ToWireCode(), ex.Message);
            return ToolResult.Failure(ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Failure(TvErrorCode.Timeout, $"Tool '{toolName}' was cancelled before completing.");
        }
        catch (Exception ex)
        {
            // Message is deliberately generic: an exception string can carry
            // configuration detail, and nothing here is worth leaking.
            logger.LogError(ex, "Tool '{Tool}' failed unexpectedly.", toolName);
            return ToolResult.Failure(
                TvErrorCode.TvError,
                $"Tool '{toolName}' failed unexpectedly. Check the server logs for details.");
        }
    }

    public static Task<ToolResult> RunAsync(ILogger logger, string toolName, Func<Task> action) =>
        RunAsync(logger, toolName, async () =>
        {
            await action().ConfigureAwait(false);
            return (object?)new { done = true };
        });
}
