using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

[Trait("Category", "Engine")]
public sealed class EngineParameterPlanTests
{
    private const string Song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nace-music tune at 1 for 1 {\n}\n";
    private static readonly LambdaRenderer Fake = new("ace-music", "machine-exact", _ => [new RenderOutput("audio", Wav.Encode(new short[2000], 44100))]);

    private static async Task<(string PlanId, string Settings)> PlanOfAsync(string text)
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(text), new BuildOptions(ExtraRenderers: [Fake]));
        string take = report.Blocks.Single(b => b.Block == "tune").TakeId!;
        StoredPlan stored = r.Bank.GetPlan(r.Bank.GetTake(take).PlanId);
        return (stored.PlanId, stored.Plan.SettingsJson);
    }

    [Fact]
    public async Task An_empty_engine_is_not_a_template_default_so_plans_of_old_songs_keep_their_identity()
    {
        (_, string settings) = await PlanOfAsync(Song);
        Assert.DoesNotContain("engine", settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_an_engine_makes_another_plan_so_a_local_take_is_never_reused_for_faer()
    {
        (string plain, _) = await PlanOfAsync(Song);
        (string local, string localSettings) = await PlanOfAsync(Song.Replace("{\n}", "{\n  engine \"local\"\n}", StringComparison.Ordinal));
        (string faer, _) = await PlanOfAsync(Song.Replace("{\n}", "{\n  engine \"faer:xl-sft\"\n}", StringComparison.Ordinal));
        Assert.Contains("engine", localSettings, StringComparison.Ordinal);
        Assert.NotEqual(plain, local);
        Assert.NotEqual(local, faer);
        Assert.NotEqual(plain, faer);
    }
}
