using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>
/// An unplaced block of a renderer that plans its own duration (<see cref="IBlockDurationPlanner"/>) gets that many bars, rounded up, instead of the default of 8:
/// proved here with a fake planner, before speech uses the hook. Placement, a bars parameter and a renderer without a planner keep their rules.
/// </summary>
[Trait("Category", "Engine")]
public sealed class DurationPlannerHookTests
{
    private sealed class FakePlanner(long? samples, bool throws = false) : IBlockRenderer, IBlockDurationPlanner
    {
        public List<RenderRequest> Requests { get; } = [];

        public int Asked { get; private set; }

        public int AskedRate { get; private set; }

        public string BlockType => "speech";

        public string Id => "test.fake-planner";

        public string Version => "1";

        public long? PlannedSamples(BlockNode block, TemplateSpec blockTemplate, int sampleRate)
        {
            Asked++;
            AskedRate = sampleRate;
            if (throws) throw new Ample.Core.Abstractions.AmpleException("AMP5351", "cannot plan");
            return samples;
        }

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(new short[request.SamplesPerBar * request.BarCount], request.SampleRate, cancellationToken))]);
        }
    }

    // tempo 120 in 4/4 at 44.1 kHz: a bar is 2 seconds = 88,200 samples
    private const string Header = "composition \"t\" { ample 1 tempo 120 master-seed 1 }\n";
    private const int SamplesPerBar = 88_200;

    private static async Task<(FakePlanner Planner, BuildReport Report)> BuildAsync(string blocks, long? samples, bool throws = false)
    {
        FakePlanner planner = new(samples, throws);
        using EngineRig rig = await EngineRig.CreateAsync();
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(Header + blocks), new BuildOptions(ExtraRenderers: [planner]));
        return (planner, report);
    }

    private const string Unplaced = "speech voice {\n  text \"hello\"\n}\n";

    [Theory]
    [InlineData(1, 1)]                          // anything above zero is at least one bar
    [InlineData(SamplesPerBar, 1)]              // exactly one bar stays one bar
    [InlineData(SamplesPerBar + 1, 2)]          // one sample more is the next bar: only the enclosing bar count is rounded up
    [InlineData(SamplesPerBar * 3, 3)]
    [InlineData(SamplesPerBar * 3 - 1, 3)]
    public async Task An_unplaced_block_gets_the_planned_duration_rounded_up_to_whole_bars(long samples, int bars)
    {
        (FakePlanner planner, BuildReport report) = await BuildAsync(Unplaced, samples);

        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "voice").Status);
        Assert.Equal(bars, planner.Requests.Single().BarCount);
        Assert.Equal(44_100, planner.AskedRate);   // always counted at the full rate, so a preview gives the same bars
    }

    [Fact]
    public async Task Without_an_answer_the_ordinary_rule_of_eight_bars_stays()
    {
        (FakePlanner none, _) = await BuildAsync(Unplaced, null);
        (FakePlanner zero, _) = await BuildAsync(Unplaced, 0);
        (FakePlanner broken, BuildReport report) = await BuildAsync(Unplaced, 1, throws: true);

        Assert.Equal(8, none.Requests.Single().BarCount);
        Assert.Equal(8, zero.Requests.Single().BarCount);
        Assert.Equal(8, broken.Requests.Single().BarCount);   // the planner could not plan: the render reports the problem, not the preflight
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "voice").Status);
    }

    [Fact]
    public async Task A_placement_or_a_bars_parameter_wins_and_the_planner_is_not_asked()
    {
        (FakePlanner placed, _) = await BuildAsync("speech voice at 1 for 2 {\n  text \"hello\"\n}\n", SamplesPerBar * 5);
        (FakePlanner barred, _) = await BuildAsync("speech voice {\n  bars 4\n  text \"hello\"\n}\n", SamplesPerBar * 5);

        Assert.Equal(0, placed.Asked);
        Assert.Equal(0, barred.Asked);
        Assert.Equal(2, placed.Requests.Single().BarCount);
        Assert.Equal(4, barred.Requests.Single().BarCount);
    }

    [Fact]
    public async Task The_longest_unplaced_block_sets_the_length_of_the_song()
    {
        // two unplaced blocks of 3 and 5 bars: the render range (the song) is the longer one
        (FakePlanner planner, _) = await BuildAsync("speech a {\n  text \"one\"\n}\nspeech b {\n  text \"two\"\n}\n", SamplesPerBar * 5);

        Assert.All(planner.Requests, request => Assert.Equal(5, request.BarCount));
    }

    [Fact]
    public async Task A_renderer_without_a_planner_is_not_affected()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));

        Assert.DoesNotContain(report.Blocks, b => b.Status == "failed");
    }
}

/// <summary>Speech is the first user of the hook: an unplaced text-mode block lasts as long as its speech, and the plan is the plan of the render.</summary>
[Trait("Category", "Engine")]
public sealed class SpeechDurationPlannerTests
{
    private const string Header = "composition \"t\" { ample 1 tempo 120 master-seed 1 }\n";   // a bar is 2 seconds

    private static int Bars(string block, out AmpleDocument doc)
    {
        doc = EngineRig.Parse(Header + block);
        return Ample.Engine.Engine.BuildEngine.EffectiveBars(doc.Blocks.Single(b => b.Type == "speech"), doc, _ => null, EngineRig.Spec);
    }

    [Fact]
    public void An_unplaced_text_block_gets_the_bars_of_its_speech_not_eight()
    {
        Assert.Equal(1, Bars("speech voice {\n  text \"I am a robot.\"\n}\n", out _));   // about 1.2 seconds
        int longer = Bars("speech voice {\n  text \"" + string.Join(' ', Enumerable.Repeat("the quick brown fox jumps over", 4)) + "\"\n}\n", out _);
        Assert.InRange(longer, 3, 7);   // a longer text is longer, and not the default of 8 either
        Assert.Equal(1, Bars("speech voice {\n  engine \"retro\"\n  text \"I am a robot.\"\n}\n", out _));
    }

    [Fact]
    public void A_slower_rate_makes_the_block_longer()
    {
        int normal = Bars("speech voice {\n  text \"I am a robot, you are a person.\"\n}\n", out _);
        int slow = Bars("speech voice {\n  rate 0.5\n  text \"I am a robot, you are a person.\"\n}\n", out _);

        Assert.True(slow > normal, $"{slow} bars at rate 0.5, {normal} at rate 1");
    }

    [Fact]
    public void Placement_bars_words_mode_and_unplannable_blocks_keep_the_ordinary_rules()
    {
        Assert.Equal(2, Bars("speech voice at 1 for 2 {\n  text \"I am a robot.\"\n}\n", out _));
        Assert.Equal(4, Bars("speech voice {\n  bars 4\n  text \"I am a robot.\"\n}\n", out _));
        Assert.Equal(8, Bars("speech voice {\n  mode \"words\"\n  text \"I am a robot.\"\n}\n", out _));
        Assert.Equal(8, Bars("speech voice {\n  phonemes \"QQ ZZ\"\n}\n", out _));   // not speakable: the render reports it, the length stays the ordinary one
        Assert.Equal(8, Bars("speech voice {\n  text \"...\"\n}\n", out _));        // no words: nothing to plan
    }

    [Theory]
    [InlineData("I am a robot.", 1.0)]
    [InlineData("Get down get funky get up get loud", 0.8)]
    [InlineData("one two three", 1.6)]
    public void The_planned_length_is_the_length_the_render_makes(string text, double rate)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("text", text, 0));
        block.Parameters.Add(new Parameter("rate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), 0));
        IBlockRenderer renderer = BuiltInRenderers.All.Single(item => item.BlockType == "speech");
        long? planned = ((IBlockDurationPlanner)renderer).PlannedSamples(block, EngineRig.Spec.Templates["speech"], 44_100);

        Ample.Speech.SpeechResult rendered = Ample.Speech.HumanicoSpeechEngine.Render(new(text, 44_100, rate));
        Assert.Equal(rendered.Samples.Length, planned);
    }

    [Fact]
    public async Task The_song_is_as_long_as_the_speech_and_the_block_builds()
    {
        const string song = "speech voice {\n  text \"I am a robot.\"\n}\nmaster out {\n  from voice.audio -> audio\n}\n";
        using EngineRig rig = await EngineRig.CreateAsync();
        AmpleDocument doc = EngineRig.Parse(Header + song);

        Assert.Equal(2.0, rig.Engine.SongSeconds(doc));   // one bar of 2 seconds, not 8 bars of 16
        BuildReport report = await rig.Engine.BuildAsync(doc);
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "voice").Status);
    }
}
