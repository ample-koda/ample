namespace Ample.Server.Tests;

public sealed class QuantityRequestTests
{
    [Theory]
    [InlineData("Give me five samples of different guitars", 5)]
    [InlineData("geef me vijf samples", 5)]
    [InlineData("laat me 6 varianten horen", 6)]
    [InlineData("let me hear three different clips", 3)]
    [InlineData("I want 8 more samples", 8)]
    [InlineData("speel twaalf voorbeelden", 12)]
    public void A_clear_number_of_samples_is_read(string text, int expected)
    {
        Assert.True(QuantityRequest.IsRequest(text));
        Assert.Equal(expected, QuantityRequest.Limit(text));
    }

    [Theory]
    [InlineData("make the bass quieter")]
    [InlineData("give me five samples and then four more clips")]   // two quantities: no guessing
    [InlineData("show me some samples")]
    [InlineData("")]
    public void Several_or_no_numbers_give_no_limit(string text) => Assert.Null(QuantityRequest.Limit(text));

    [Fact]
    public void A_request_for_more_is_recognised_without_a_number()
    {
        Assert.True(QuantityRequest.IsRequest("geef me nog meer samples"));
        Assert.True(QuantityRequest.IsRequest("play me another sample"));
        Assert.False(QuantityRequest.IsRequest("the sample is too loud"));
    }

    [Fact]
    public async Task Clips_can_be_read_while_another_thread_adds_them()
    {
        AiTask task = new();
        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                lock (task.ClipsLock)
                {
                    task.Clips.Add(new AiClip { Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Source = "proposal" });
                }
            }
        });
        int reads = 0;
        while (!writer.IsCompleted)
        {
            reads += task.ClipsNow().Count(c => c.Source == "proposal") >= 0 ? 1 : 0;
        }

        await writer;
        Assert.True(reads > 0);
        Assert.Equal(2000, task.ClipsNow().Length);
    }
}
