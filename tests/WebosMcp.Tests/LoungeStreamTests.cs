using System.Text;
using WebosMcp.Application;
using WebosMcp.Infrastructure;
using Xunit;

namespace WebosMcp.Tests;

/// <summary>
/// Incremental reassembly of the Lounge long-poll stream.
///
/// Physical evidence: the product started the requested video, the receiver was
/// playing it, and the tool still returned TV_ERROR because the state report had
/// not been read. The channel stays open feeding events as they happen, so waiting
/// for the response to END means waiting for the server to close it — well past the
/// verification window. A complete chunk must surface the moment its bytes arrive.
/// </summary>
public sealed class LoungeStreamTests
{
    private static byte[] Framed(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        return [.. Encoding.ASCII.GetBytes($"{bytes.Length}\n"), .. bytes];
    }

    private static string NowPlaying(string videoId, string state) =>
        $$"""[[1,["nowPlaying",{"videoId":"{{videoId}}","state":"{{state}}"}]]]""";

    [Fact]
    public void A_complete_chunk_is_surfaced_as_soon_as_its_bytes_arrive()
    {
        var stream = new LoungeChunkStream();

        var state = Assert.Single(stream.Append(Framed(NowPlaying("dQw4w9WgXcQ", "1"))));

        Assert.Equal("dQw4w9WgXcQ", state.VideoId);
        Assert.Equal(LoungePlayerState.Playing, state.State);
    }

    [Fact]
    public void A_chunk_split_across_reads_is_held_until_it_is_whole()
    {
        // The read boundary is arbitrary — parsing a partial payload would either
        // throw or, worse, silently drop the report.
        var framed = Framed(NowPlaying("dQw4w9WgXcQ", "1"));
        var stream = new LoungeChunkStream();

        Assert.Empty(stream.Append(framed.AsSpan(0, framed.Length / 2)));

        var state = Assert.Single(stream.Append(framed.AsSpan(framed.Length / 2)));
        Assert.Equal("dQw4w9WgXcQ", state.VideoId);
    }

    [Fact]
    public void A_chunk_split_inside_its_LENGTH_HEADER_still_reassembles()
    {
        var framed = Framed(NowPlaying("dQw4w9WgXcQ", "1"));
        var stream = new LoungeChunkStream();

        Assert.Empty(stream.Append(framed.AsSpan(0, 1)));
        Assert.Single(stream.Append(framed.AsSpan(1)));
    }

    [Fact]
    public void A_split_multibyte_character_is_not_decoded_early()
    {
        // Cutting mid-character and decoding would corrupt the payload; the byte
        // count is what decides when a chunk is whole.
        var framed = Framed($$"""[[1,["nowPlaying",{"videoId":"dQw4w9WgXcQ","state":"1","title":"Кыргызстан"}]]]""");
        var stream = new LoungeChunkStream();

        for (var i = 0; i < framed.Length - 1; i++)
        {
            Assert.Empty(stream.Append(framed.AsSpan(i, 1)));
        }

        var state = Assert.Single(stream.Append(framed.AsSpan(framed.Length - 1, 1)));
        Assert.Equal("dQw4w9WgXcQ", state.VideoId);
    }

    [Fact]
    public void Several_chunks_in_one_read_all_surface_in_order()
    {
        var stream = new LoungeChunkStream();

        var states = stream.Append(
            [.. Framed(NowPlaying("aBcDeFgHiJk", "2")), .. Framed(NowPlaying("dQw4w9WgXcQ", "1"))]);

        Assert.Equal(2, states.Count);
        Assert.Equal("aBcDeFgHiJk", states[0].VideoId);
        Assert.Equal("dQw4w9WgXcQ", states[1].VideoId);
    }

    [Fact]
    public void The_event_id_advances_so_the_next_poll_resumes_where_this_one_stopped()
    {
        var stream = new LoungeChunkStream();

        stream.Append(Framed("""[[7,["nowPlaying",{"videoId":"dQw4w9WgXcQ","state":"1"}]]]"""));

        Assert.Equal(7, stream.LastEventId);
    }

    [Fact]
    public void A_trailing_partial_chunk_does_not_block_the_complete_one_before_it()
    {
        var stream = new LoungeChunkStream();

        var states = stream.Append([.. Framed(NowPlaying("dQw4w9WgXcQ", "1")), .. Encoding.ASCII.GetBytes("99\n[[2,")]);

        Assert.Single(states);
    }

    [Fact]
    public void An_unframed_header_is_discarded_rather_than_stalling_the_stream()
    {
        // Otherwise one bad line wedges the channel forever.
        var stream = new LoungeChunkStream();

        stream.Append(Encoding.ASCII.GetBytes("garbage\n"));

        Assert.Single(stream.Append(Framed(NowPlaying("dQw4w9WgXcQ", "1"))));
    }
}
