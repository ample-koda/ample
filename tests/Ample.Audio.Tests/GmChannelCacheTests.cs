using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class GmChannelCacheTests
{
    [Fact]
    public void A_channel_that_was_rendered_is_found_again_as_a_copy_and_an_unknown_key_is_a_miss()
    {
        GmChannelCache.Clear();
        short[] audio = [1, 2, 3, 4];
        GmChannelCache.Put("channel-a", audio, stereo: true);
        audio[0] = 99;   // the cache holds its own copy

        Assert.True(GmChannelCache.TryGet("channel-a", out short[]? found, out bool stereo));
        Assert.Equal(new short[] { 1, 2, 3, 4 }, found);
        Assert.True(stereo);
        Assert.False(GmChannelCache.TryGet("channel-b", out _, out _));
        GmChannelCache.Clear();
        Assert.False(GmChannelCache.TryGet("channel-a", out _, out _));
    }
}
