using System.Text.Json.Nodes;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class SampleSourcesTests
{
    [Fact]
    public void The_rules_say_cc0_by_default_and_the_places_that_fit_the_words_come_first()
    {
        JsonObject all = SampleSources.Run([]);
        Assert.Contains(all["rules"]!.AsArray(), r => r!.GetValue<string>().Contains("CC0", StringComparison.Ordinal) && r.GetValue<string>().Contains("Default", StringComparison.Ordinal));
        Assert.True(all["places"]!.AsArray().Count >= 5);
        Assert.All(all["places"]!.AsArray(), p => Assert.False(string.IsNullOrWhiteSpace(p!["licence"]!.GetValue<string>())));
        JsonObject scratch = SampleSources.Run(new JsonObject { ["text"] = "dj scratch" });
        JsonObject first = scratch["places"]![0]!.AsObject();
        Assert.True(first["matches"]!.GetValue<bool>());
        Assert.Contains(scratch["places"]!.AsArray(), p => p!["id"]!.GetValue<string>() == "freesound-cc0" && p["matches"]!.GetValue<bool>());
    }
}
