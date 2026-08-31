using System.Globalization;
using System.Text;
using System.Text.Json;
using WebosMcp.Application;

namespace WebosMcp.Infrastructure;

/// <summary>
/// Reassembles the Lounge long-poll stream incrementally.
///
/// The channel stays open and feeds events as they happen, so a complete chunk
/// must be surfaced the moment its bytes have arrived — not when the response
/// ends. Buffering to the end of the response is why a video the receiver really
/// had started was never observed inside the verification window.
///
/// Framing is a repeated "&lt;byte length&gt;\n&lt;json&gt;". The length is a BYTE
/// count, so everything here works on bytes and only complete payloads are decoded.
/// </summary>
internal sealed class LoungeChunkStream
{
    private readonly List<byte> _pending = [];

    /// <summary>Highest event id seen, which the next poll resumes from.</summary>
    public int LastEventId { get; private set; }

    /// <summary>
    /// Adds newly-read bytes and returns every state report that is now complete.
    /// A partial chunk is held until the rest of it arrives rather than being
    /// parsed early and dropped.
    /// </summary>
    public IReadOnlyList<LoungeReceiverState> Append(ReadOnlySpan<byte> data)
    {
        _pending.AddRange(data);

        var states = new List<LoungeReceiverState>();

        while (TryTakeChunk(out var json))
        {
            foreach (var entry in ParseEntries(json))
            {
                if (LoungeSession.EventId(entry) is { } id)
                {
                    LastEventId = Math.Max(LastEventId, id);
                }

                if (LoungeSession.ParseReceiverState(entry) is { } state)
                {
                    states.Add(state);
                }
            }
        }

        return states;
    }

    private bool TryTakeChunk(out string json)
    {
        json = string.Empty;

        var newline = _pending.IndexOf((byte)'\n');
        if (newline < 0)
        {
            return false;
        }

        var header = Encoding.ASCII.GetString(_pending.GetRange(0, newline).ToArray()).Trim();

        if (!int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ||
            length < 0)
        {
            // Not a framed chunk. Drop the bad header rather than stalling forever
            // on bytes that will never parse.
            _pending.RemoveRange(0, newline + 1);
            return false;
        }

        var start = newline + 1;

        if (_pending.Count - start < length)
        {
            // The rest is still in flight. Wait for it — parsing now would truncate.
            return false;
        }

        json = Encoding.UTF8.GetString(_pending.GetRange(start, length).ToArray());
        _pending.RemoveRange(0, start + length);
        return true;
    }

    private static IReadOnlyList<JsonElement> ParseEntries(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
