using System.Text.Json.Serialization;
using WebosMcp.Domain;

namespace WebosMcp.Server.Tools;

/// <summary>
/// Why one field of an aggregate response is null. Machine-readable: the code is
/// always a <see cref="TvErrorCode"/> wire value, never free-form text.
/// </summary>
public sealed record ToolWarning(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Runs the sub-reads of an aggregate tool so that one denied field does not
/// discard the fields that worked.
///
/// The distinction this class exists to enforce is between a COMMAND-level
/// failure and a CONNECTION-level one, because degrading over the wrong one
/// produces a far worse bug than the one being fixed:
///
///   - Command-level (<c>TV_PERMISSION_DENIED</c>, <c>TV_UNSUPPORTED_CAPABILITY</c>,
///     a command <c>TV_ERROR</c>) means the session is healthy and this one read
///     was refused. The field goes null, a warning names it, and every other field
///     is returned as usual.
///   - Connection/session-level (<c>PAIRING_REQUIRED</c>, <c>TV_OFF</c>,
///     <c>TV_UNREACHABLE</c>, <c>TIMEOUT</c>) means nothing was read and nothing
///     will be. It propagates and fails the whole call. Degrading here would hand
///     a caller <c>ok:true</c> with every field null for a TV that is switched off
///     — success reported for a call that never reached the TV, which is exactly
///     the class of lie the rest of this project refuses.
///
/// Propagation is immediate, so a connection-level failure on the first sub-read
/// never attempts the rest.
/// </summary>
internal sealed class PartialRead
{
    private readonly List<ToolWarning> _warnings = [];

    /// <summary>Null when every read succeeded, so an all-success response stays byte-identical.</summary>
    public IReadOnlyList<ToolWarning>? Warnings => _warnings.Count == 0 ? null : _warnings;

    /// <summary>
    /// The codes that may degrade a single field. Anything absent from this set is
    /// connection/session-level and fails the whole call — a deliberate allowlist,
    /// because a new code added later should have to be considered rather than
    /// silently inheriting partial-result behaviour.
    /// </summary>
    public static bool IsCommandLevel(TvErrorCode code) => code is
        TvErrorCode.TvPermissionDenied or
        TvErrorCode.TvUnsupportedCapability or
        TvErrorCode.TvError;

    public async Task<T?> TryAsync<T>(string field, Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (TvException ex) when (IsCommandLevel(ex.Code))
        {
            _warnings.Add(new ToolWarning(field, ex.Code.ToWireCode(), ex.Message));
            return null;
        }
    }
}
