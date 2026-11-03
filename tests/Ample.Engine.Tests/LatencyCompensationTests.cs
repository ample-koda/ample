using Ample.Audio.Engine;
using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>Optional delay compensation between blocks: off (or absent) leaves the song text alone, on aligns the inputs of a mix and trims the start at the master.</summary>
[Trait("Category", "Modules")]
public sealed class LatencyCompensationTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-latency-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static string Song(string setting, string lookahead = "") => $$"""
        composition "p" { ample 1  tempo 120  master-seed 1 }
        {{setting}}

        midi-clip a at 1 for 1 {
          notes 1:0:1:60:100
        }

        synth s1 at 1 for 1 {
          from a.notes -> notes
        }

        synth s2 at 1 for 1 {
          from a.notes -> notes
        }

        limiter lim at 1 for 1 {
        {{lookahead}}  from s1.audio -> audio
        }

        mixer mix {
          inputs 4
          from lim.audio -> audio1
          from s2.audio -> audio2
        }

        master out {
          from mix.audio -> audio
        }
        """;

    private static string Lookahead(int ms) => $"  lookahead {ms} ms" + (char)10;

    private static readonly SpecData Delaying = EngineRig.Spec.WithLatency("limiter", 5);

    private ModuleService Service() => new(folder, (Ample.Storage.Library.Library?)null);

    [Fact]
    public void Without_the_setting_or_with_off_the_song_text_is_left_alone()
    {
        foreach (string setting in new[] { string.Empty, "settings { audio.latencyCompensation off }" })
        {
            string song = Song(setting);
            ExpandedSong expanded = Service().Expand(song, Delaying);
            Assert.Equal(song, expanded.Text);
            Assert.DoesNotContain(expanded.Diagnostics, d => d.Code == "AMP2250");
        }
    }

    [Fact]
    public void On_the_shorter_input_gets_a_shift_and_the_master_a_trim()
    {
        ExpandedSong expanded = Service().Expand(Song("settings { audio.latencyCompensation on }"), Delaying);
        Assert.Contains("shift mix__lat_in2 {", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("seconds 0.005", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from mix__lat_in2.audio -> audio2", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from lim.audio -> audio1", expanded.Text, StringComparison.Ordinal);   // the later input stays as it is
        Assert.Contains("cut out__lat_trim {", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("start 0.005", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from out__lat_trim.audio -> audio", expanded.Text, StringComparison.Ordinal);
        Assert.Equal(expanded.Text.Split('\n').Length, expanded.LineOrigin.Count);
        Assert.Contains(expanded.Diagnostics, d => d.Code == "AMP2250" && d.Severity == Severity.Info);
        Assert.DoesNotContain(Service().ParseExpanded(Song("settings { audio.latencyCompensation on }"), Delaying).Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void The_delay_is_what_the_template_declares()
    {
        ExpandedSong expanded = Service().Expand(Song("settings { audio.latencyCompensation on }"), EngineRig.Spec.WithLatency("limiter", 12));
        Assert.Contains("seconds 0.012", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("start 0.012", expanded.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_song_without_latency_gets_no_blocks_even_when_on_and_that_is_every_song_today()
    {
        string song = Song("settings { audio.latencyCompensation on }");
        Assert.Equal(song, Service().Expand(song, EngineRig.Spec).Text);   // no built-in template declares a latency: the limiter looks ahead without delaying
    }

    [Fact]
    public async Task The_limiter_renders_without_delay_so_its_input_and_output_start_together()
    {
        // a loud click through the limiter keeps its place: the renderers look ahead over the whole file, they do not delay it
        using EngineRig rig = await EngineRig.CreateAsync();
        byte[] click = Wav.Encode([.. Enumerable.Range(0, 4410).Select(i => i == 2000 ? (short)30000 : (short)0)], 44100);
        byte[] limited = Ample.Audio.Engine.AudioEffects.LimitAudio(click, -6);
        short[] before = Wav.Decode(click).Samples, after = Wav.Decode(limited).Samples;
        Assert.Equal(Array.FindIndex(before, v => v != 0), Array.FindIndex(after, v => v != 0));
        Assert.Equal(before.Length, after.Length);
    }
}
