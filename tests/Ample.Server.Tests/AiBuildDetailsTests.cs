using Ample.Engine.Engine;
using Ample.Language;

namespace Ample.Server.Tests;

public sealed class AiBuildDetailsTests
{
    [Fact]
    public void A_block_that_did_not_build_comes_with_its_message_a_hint_what_feeds_it_and_its_text()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        string root = dir ?? throw new InvalidOperationException("AMPLE root not found.");
        string spec = Path.Combine(root, "spec");
        SpecData data = SpecData.FromFiles(Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText));
        AmpleDocument doc = Parser.Parse("project demo { tempo 100  master-seed 7 }\nsequencer band at 1 for 4 {\n}\nmixer mix {\n  from band.audio -> audio1\n}\n", data);
        BuildReport report = new([new BlockResult("band", "failed", null, "AMP5380: The sequencer has no sequence: choose one."), new BlockResult("mix", "skipped", null, null)], null, 0, 0);

        System.Text.Json.Nodes.JsonArray details = AiContext.BuildDetails(doc, report, root);

        Assert.Equal(2, details.Count);
        Assert.Equal("AMP5380", details[0]!["code"]!.ToString());
        Assert.Contains("ample_block_types type=sequencer", details[0]!["hint"]!.ToString(), StringComparison.Ordinal);
        Assert.Contains("sequencer band", details[0]!["text"]!.ToString(), StringComparison.Ordinal);
        Assert.Contains("band.audio -> audio1", details[1]!["fedBy"]![0]!.ToString(), StringComparison.Ordinal);   // the parser writes the real port names of the block
        Assert.Contains("skipped", details[1]!["hint"]!.ToString(), StringComparison.Ordinal);
    }
}
