using System.Text.RegularExpressions;
using Ample.Speech.Flite.Cg;

namespace Ample.Speech.Flite.Tests.Cg;

/// <summary>Milestone C8 of the clustergen voices: the voices slt, rms and awb are voices of the <c>flite</c> engine of the speech block (<see cref="FliteSpeechEngine"/>): the plain sound is the plain sound of
/// the pinned program, the engine keeps its identity per voice, and what the voices do not speak (phonemes) is refused clearly.</summary>
public sealed class CgSpeechEngineTests
{
    private const string Sentence = "The quick brown fox jumps over the lazy dog.";

    private static SpeechRequest Request(string voice, string text = Sentence, double rate = 1, double? pitch = null, double? range = null) =>
        new(text, FliteSpeechEngine.InternalRate, rate, pitch ?? FliteSpeechEngine.DefaultPitchHzOf(voice))
        {
            Engine = "flite",
            Voice = voice,
            PitchRangeHz = range ?? FliteSpeechEngine.DefaultPitchRangeHzOf(voice)
        };

    [Theory]
    [InlineData("slt")]
    [InlineData("rms")]
    [InlineData("awb")]
    public void The_plain_sound_of_the_block_is_the_plain_sound_of_the_voice(string voice)
    {
        // at 16 kHz the resampling is the identity: the samples are those of the engine, which equal the pinned program (CgEngineTests)
        SpeechResult made = FliteSpeechEngine.Render(Request(voice));
        FliteRenderResult plain = new FliteCgEngine(FliteCgVoiceData.Embedded(voice)).RenderText(Sentence + "\n");
        Assert.Equal(plain.Samples, made.Samples);
        Assert.Equal(FliteSpeechEngine.IdentityOf(voice), made.EngineIdentity);
        Assert.Contains("cmu_us_" + voice, made.EngineIdentity, StringComparison.Ordinal);
        Assert.Equal(["the", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog"], made.Words.Select(w => w.SourceWord));
        Assert.Equal(made.Words.Count, made.Spans!.Count);
        Assert.All(made.Words, w => Assert.InRange(w.EndSample, w.StartSample, made.Samples.Length));
    }

    [Fact]
    public void The_voices_have_their_own_identities_and_the_default_pitch_of_their_recording()
    {
        string[] identities = [.. FliteSpeechEngine.Voices.Select(FliteSpeechEngine.IdentityOf)];
        Assert.Equal(identities.Length, identities.Distinct().Count());
        Assert.Equal(95, FliteSpeechEngine.DefaultPitchHzOf("kal16"));
        Assert.Equal(172, FliteSpeechEngine.DefaultPitchHzOf("slt"));
        Assert.Equal(98, FliteSpeechEngine.DefaultPitchHzOf("rms"));
        Assert.Equal(132, FliteSpeechEngine.DefaultPitchHzOf("awb"));
        Assert.Equal(27, FliteSpeechEngine.DefaultPitchRangeHzOf("slt"));
        Assert.Equal(24, FliteSpeechEngine.DefaultPitchRangeHzOf("rms"));
        Assert.Equal(25, FliteSpeechEngine.DefaultPitchRangeHzOf("awb"));
    }

    [Fact]
    public void The_panel_shows_the_defaults_that_the_voices_have()
    {
        string root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Ample.sln"))) root = Path.GetDirectoryName(root)!;
        string table = File.ReadAllText(Path.Combine(root!, "web", "lib", "speech-defaults.mjs"));
        foreach (string voice in FliteSpeechEngine.Voices)
        {
            Match found = Regex.Match(table, voice + @":\s*\{\s*pitch_hz:\s*'(\d+) Hz',\s*pitch_range:\s*'(\d+) Hz'\s*\}");
            Assert.True(found.Success, $"the table of the panel has no defaults for {voice}");
            Assert.Equal(FliteSpeechEngine.DefaultPitchHzOf(voice), double.Parse(found.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(FliteSpeechEngine.DefaultPitchRangeHzOf(voice), double.Parse(found.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            // the range that the panel names for words mode is the range of the engine
            Match range = Regex.Match(table, voice + @":\s*\[(\d+), (\d+)\]");
            Assert.True(range.Success, $"the table of the panel has no words range for {voice}");
            Assert.Equal(FliteSpeechEngine.WordsMinPitchHzOf(voice), double.Parse(range.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(FliteSpeechEngine.WordsMaxPitchHzOf(voice), double.Parse(range.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void Rate_and_pitch_change_the_sound()
    {
        short[] normal = FliteSpeechEngine.Render(Request("slt")).Samples;
        short[] fast = FliteSpeechEngine.Render(Request("slt", rate: 2)).Samples;
        Assert.InRange(normal.Length / (double)fast.Length, 1.8, 2.2);
        short[] high = FliteSpeechEngine.Render(Request("slt", pitch: 260)).Samples;
        Assert.NotEqual(normal, high);
        short[] flat = FliteSpeechEngine.Render(Request("slt", range: 0)).Samples;
        Assert.NotEqual(normal, flat);
    }

    [Fact]
    public void Phonemes_belong_to_kal16()
    {
        SpeechRequest request = Request("slt", string.Empty) with { Phonemes = "HH AH0 L OW1" };
        ArgumentException refused = Assert.Throws<ArgumentException>(() => FliteSpeechEngine.Render(request));
        Assert.Contains("kal16", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => FliteSpeechEngine.Render(Request("nobody")));
    }

    [Fact]
    public void The_own_pronunciations_and_the_spelling_of_the_block_work_for_the_voices()
    {
        short[] plain = FliteSpeechEngine.Render(Request("rms", "Say tomato now.")).Samples;
        short[] own = FliteSpeechEngine.Render(Request("rms", "Say tomato now.") with { Lexicon = "tomato = T AH0 M AA1 T OW0" }).Samples;
        Assert.False(plain.SequenceEqual(own), "an own pronunciation changes the sound");
        short[] spelled = FliteSpeechEngine.Render(Request("rms", "Say banana now.") with { Spell = "banana" }).Samples;
        short[] read = FliteSpeechEngine.Render(Request("rms", "Say banana now.")).Samples;
        Assert.False(read.SequenceEqual(spelled), "spelling changes the sound");
        Assert.True(spelled.Length > read.Length, "six letters take longer than the word");
    }

    [Fact]
    public void The_markup_works_for_the_voices_and_a_pause_is_as_long_as_it_is_written()
    {
        short[] plain = FliteSpeechEngine.Render(Request("awb", "One two three four.")).Samples;
        short[] paused = FliteSpeechEngine.Render(Request("awb", "One two <break 700ms/> three four.")).Samples;
        Assert.InRange(paused.Length - plain.Length, 16000 * 0.65, 16000 * 0.75 + 4000);
        short[] slow = FliteSpeechEngine.Render(Request("awb", "One <rate 0.5>two three</rate> four.")).Samples;
        Assert.True(slow.Length > plain.Length * 1.2, "the words in the rate tag take longer");
        short[] high = FliteSpeechEngine.Render(Request("awb", "One <pitch +7st>two three</pitch> four.")).Samples;
        Assert.NotEqual(plain, high);
        Assert.InRange(high.Length, plain.Length * 0.9, plain.Length * 1.1);   // the pitch does not change the length
        short[] emph = FliteSpeechEngine.Render(Request("awb", "One <emph>two</emph> three four.")).Samples;
        Assert.True(emph.Length > plain.Length, "emphasis makes the word longer");
    }

    [Fact]
    public void The_speech_of_a_voice_is_stopped_by_its_limits()
    {
        SpeechRequest request = Request("slt", "This sentence needs more than a quarter of a second of speech.") with { MaxOutputSamples = 4000 };
        Assert.Throws<SpeechResourceLimitException>(() => FliteSpeechEngine.Render(request));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => FliteSpeechEngine.Render(Request("slt"), cancelled.Token));
    }
}
