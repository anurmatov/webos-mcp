using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WebosMcp.Application;
using WebosMcp.Domain;

namespace WebosMcp.Server.Tools;

[McpServerToolType]
public sealed class NavigationTools
{
    private readonly TvControlService _tv;
    private readonly ILogger<NavigationTools> _logger;

    public NavigationTools(TvControlService tv, ILogger<NavigationTools> logger)
    {
        _tv = tv;
        _logger = logger;
    }

    [McpServerTool(Name = "tv_send_button")]
    [Description(
        "Press a remote-control button. The button is chosen from a fixed allowlist — there is no " +
        "free-text key-name path.")]
    public Task<ToolResult> SendButton(
        [Description("Button to press, from the fixed allowlist.")] RemoteButton button,
        [Description("How many times to press it, 1-20. Defaults to 1.")] int repeat = 1,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_send_button",
            () => _tv.SendButtonAsync(button, repeat, cancellationToken));

    [McpServerTool(Name = "tv_type_text")]
    [Description(
        "Type text into the field currently focused on the TV. Returns TV_UNSUPPORTED_CAPABILITY when the " +
        "foreground app uses a custom on-screen keyboard that ignores standard text entry (YouTube does), " +
        "rather than silently typing nothing and reporting success.")]
    public Task<ToolResult> TypeText(
        [Description("Text to type, up to 512 characters.")] string text,
        [Description("Replace the field's existing contents instead of appending.")] bool replace = false,
        [Description("Send Enter after typing.")] bool submit = false,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_type_text",
            () => _tv.TypeTextAsync(text, replace, submit, cancellationToken));

    [McpServerTool(Name = "tv_delete_characters")]
    [Description("Delete characters from the focused field.")]
    public Task<ToolResult> DeleteCharacters(
        [Description("Number of characters to delete, 1-20.")] int count = 1,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_delete_characters",
            () => _tv.DeleteCharactersAsync(count, cancellationToken));

    [McpServerTool(Name = "tv_send_enter")]
    [Description("Send the Enter key to the focused field.")]
    public Task<ToolResult> SendEnter(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_send_enter", () => _tv.SendEnterAsync(cancellationToken));

    [McpServerTool(Name = "tv_pointer_move")]
    [Description("Move the on-screen pointer by a bounded delta (each axis within +/-500).")]
    public Task<ToolResult> PointerMove(
        [Description("Horizontal delta, -500 to 500.")] int deltaX,
        [Description("Vertical delta, -500 to 500.")] int deltaY,
        [Description("Hold the pointer down while moving (drag).")] bool drag = false,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_pointer_move",
            () => _tv.PointerMoveAsync(deltaX, deltaY, drag, cancellationToken));

    [McpServerTool(Name = "tv_pointer_click")]
    [Description("Click at the pointer's current position.")]
    public Task<ToolResult> PointerClick(CancellationToken cancellationToken) =>
        ToolInvoker.RunAsync(_logger, "tv_pointer_click", () => _tv.PointerClickAsync(cancellationToken));

    [McpServerTool(Name = "tv_pointer_scroll")]
    [Description("Scroll by a bounded delta (each axis within +/-500).")]
    public Task<ToolResult> PointerScroll(
        [Description("Horizontal scroll delta, -500 to 500.")] int deltaX,
        [Description("Vertical scroll delta, -500 to 500.")] int deltaY,
        CancellationToken cancellationToken = default) =>
        ToolInvoker.RunAsync(_logger, "tv_pointer_scroll",
            () => _tv.PointerScrollAsync(deltaX, deltaY, cancellationToken));
}
