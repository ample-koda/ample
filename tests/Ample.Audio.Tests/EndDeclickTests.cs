using Ample.Audio.Engine;

namespace Ample.Audio.Tests;

public sealed class EndDeclickTests
{
    private const int Rate = 22050;

    [Fact]
    public void A_buffer_that_ends_in_silence_is_left_exactly_as_it_was()
    {
        long[] buffer = [.. Enumerable.Range(0, 5000).Select(i => i < 4000 ? (long)(Math.Sin(i / 9.0) * 9000) : 0)];
        long[] before = [.. buffer];
        EndDeclick.Apply(buffer, Rate);
        Assert.Equal(before, buffer);
    }

    [Fact]
    public void A_voice_that_is_still_sounding_at_the_end_fades_to_zero_without_a_step()
    {
        long[] buffer = [.. Enumerable.Repeat(12000L, 5000)];
        EndDeclick.Apply(buffer, Rate);
        int fade = Rate * EndDeclick.Milliseconds / 1000;
        Assert.Equal(0, buffer[^1]);
        Assert.All(buffer.Take(buffer.Length - fade), sample => Assert.Equal(12000, sample));
        long biggestStep = Enumerable.Range(1, buffer.Length - 1).Max(i => Math.Abs(buffer[i] - buffer[i - 1]));
        Assert.True(biggestStep < 120, "the fade has no step, the biggest one is " + biggestStep);
        for (int i = buffer.Length - fade + 1; i < buffer.Length; i++)
        {
            Assert.True(buffer[i] <= buffer[i - 1], "the fade only goes down");
        }
    }

    [Fact]
    public void A_buffer_shorter_than_the_fade_fades_as_a_whole_and_an_empty_one_is_left_alone()
    {
        long[] small = [.. Enumerable.Repeat(8000L, 100)];
        EndDeclick.Apply(small, Rate);
        Assert.Equal(8000, small[0]);
        Assert.Equal(0, small[^1]);
        long[] none = [];
        EndDeclick.Apply(none, Rate);
        EndDeclick.Apply(null, Rate);
        Assert.Empty(none);
    }
}
