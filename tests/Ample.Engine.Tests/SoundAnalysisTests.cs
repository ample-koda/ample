using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>X03: <see cref="SoundAnalysis"/> always returns the master's peak/RMS/loudness (not only as a text
/// finding when it is unusually loud or quiet), so a UI meter has numbers to show after every build.</summary>
public sealed class SoundAnalysisTests
{
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
