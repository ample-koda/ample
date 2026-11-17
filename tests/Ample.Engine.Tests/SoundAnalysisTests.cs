using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>X03: <see cref="SoundAnalysis"/> always returns the master's peak/RMS/loudness (not only as a text
/// finding when it is unusually loud or quiet), so a UI meter has numbers to show after every build.</summary>
public sealed class SoundAnalysisTests
{
    [Fact]
    public void The_dB_over_full_scale_comes_back_from_the_soft_knee()
    {
        Assert.Null(SoundAnalysis.OverFullScaleDb([0, 20_000, -29_000]));   // under the knee: nothing was pressed
        Assert.Null(SoundAnalysis.OverFullScaleDb([short.MaxValue]));   // full scale is a clip, its own finding
        double over = SoundAnalysis.OverFullScaleDb([32_164])!.Value;   // the bus of Overclocked Sunrise: +5 dB on a -3.3 dBFS input
        Assert.InRange(over, 1.0, 3.0);
        Assert.True(SoundAnalysis.OverFullScaleDb([32_500])!.Value > over);
    }

    [Fact]
    public async Task A_mixer_that_goes_over_full_scale_is_a_finding_with_its_name_and_a_mixer_with_room_is_not()
    {
        static string Song(string gains) => "composition \"loud\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 2 {\n  chords Am F\n}\n"
            + "synth lead at 1 for 2 {\n  wave square\n  gain 0 dB\n  from chords.notes -> notes\n}\nmixer mix {\n  inputs 2\n  gains " + gains + "\n"
            + "  from lead.audio -> audio1\n  from lead.audio -> audio2\n}\nmaster master bake {\n  from mix.audio -> audio\n}\n";
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport loud = await r.Engine.BuildAsync(EngineRig.Parse(Song("12 12")));
        (IReadOnlyList<SoundFinding> found, _) = await SoundAnalysis.AnalyseAsync(EngineRig.Parse(Song("12 12")), EngineRig.Spec, r.Bank, loud, CancellationToken.None);
        Assert.Contains(found, f => f.Block == "mix" && (f.Code == "sound.over-full-scale" || f.Code == "sound.clips"));
        BuildReport quiet = await r.Engine.BuildAsync(EngineRig.Parse(Song("-12 -12")));
        (IReadOnlyList<SoundFinding> none, _) = await SoundAnalysis.AnalyseAsync(EngineRig.Parse(Song("-12 -12")), EngineRig.Spec, r.Bank, quiet, CancellationToken.None);
        Assert.DoesNotContain(none, f => f.Block == "mix" && f.Code is "sound.over-full-scale" or "sound.clips");
    }

    [Fact]
    public async Task A_build_reports_the_masters_numeric_levels_even_when_nothing_is_wrong_enough_to_be_a_finding()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        (_, MasterLevels? master) = await SoundAnalysis.AnalyseAsync(EngineRig.Parse(EngineRig.Hello), EngineRig.Spec, r.Bank, report, CancellationToken.None);
        // this fixture happens to be loud enough to also raise a "sound.master-loud" finding - both can be true: the
        // numbers are always there (what X03's meter needs), whether or not they are also worth a text finding.
        Assert.NotNull(master);
        Assert.True(master!.PeakDbfs <= 0.1);
        Assert.True(master.RmsDbfs <= master.PeakDbfs + 0.1);
        Assert.True(master.Lufs is > -60 and <= 0);
    }
}
