using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Server.Tests;

/// <summary>The edits that touch one row or one channel of a sequence kept in the song and nothing else.</summary>
public sealed class AiEditSequenceTests
{
    private static SpecData Spec()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        string spec = Path.Combine(dir ?? throw new InvalidOperationException("AMPLE root not found."), "spec");
        return SpecData.FromFiles(Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(spec, f).Replace((char)92, '/'), File.ReadAllText));
    }

    private const string Song = "composition \"t\" { ample 1  tempo 96  master-seed 1 }\nsequencer beat-bed at 1 for 8 {\n  gain -3 dB\n  sequence {\n    sequencer 1\n    channel kick drum sample=samples/kick.wav vol=-6\n    channel gm gm program=78\n    pattern groove bars=2\n      kick x.........x.....|x.......x...x...\n      gm 1:0:0.5:60:96\n    playlist\n      clip groove track=1 at=1 for=8\n  }\n}\n";

    [Fact]
    public void A_channel_is_added_and_one_row_is_set_while_every_other_line_stays()
    {
        SpecData spec = Spec();
        AiEdit.Result added = AiEdit.Apply(Song, JsonNode.Parse("[{\"op\":\"sequence_channel\",\"block\":\"beat-bed\",\"channel\":\"bell\",\"text\":\"gm program=10 vol=-24\"}]")!.AsArray(), spec);
        Assert.Contains("    channel bell gm program=10 vol=-24\n    pattern groove", added.Text);
        AiEdit.Result row = AiEdit.Apply(added.Text, JsonNode.Parse("[{\"op\":\"sequence_row\",\"block\":\"beat-bed\",\"pattern\":\"groove\",\"channel\":\"bell\",\"text\":\"1:0:1:80:90\"}]")!.AsArray(), spec);
        Assert.Contains("      gm 1:0:0.5:60:96\n      bell 1:0:1:80:90\n    playlist", row.Text);
        Assert.Contains("      kick x.........x.....|x.......x...x...", row.Text);
        AiEdit.Result changed = AiEdit.Apply(row.Text, JsonNode.Parse("[{\"op\":\"sequence_row\",\"block\":\"beat-bed\",\"pattern\":\"groove\",\"channel\":\"bell\",\"text\":\"2:0:1:84:80\"}]")!.AsArray(), spec);
        Assert.Contains("      bell 2:0:1:84:80\n", changed.Text);
        Assert.DoesNotContain("1:0:1:80:90", changed.Text);
        AiEdit.Result removed = AiEdit.Apply(changed.Text, JsonNode.Parse("[{\"op\":\"sequence_row\",\"block\":\"beat-bed\",\"pattern\":\"groove\",\"channel\":\"bell\",\"text\":\"\"}]")!.AsArray(), spec);
        Assert.DoesNotContain("      bell ", removed.Text);
    }

    [Fact]
    public void An_unknown_channel_or_pattern_is_refused_and_nothing_changes()
    {
        SpecData spec = Spec();
        Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, JsonNode.Parse("[{\"op\":\"sequence_row\",\"block\":\"beat-bed\",\"pattern\":\"groove\",\"channel\":\"strings\",\"text\":\"x\"}]")!.AsArray(), spec));
        Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, JsonNode.Parse("[{\"op\":\"sequence_row\",\"block\":\"beat-bed\",\"pattern\":\"nope\",\"channel\":\"kick\",\"text\":\"x\"}]")!.AsArray(), spec));
        Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, JsonNode.Parse("[{\"op\":\"sequence_channel\",\"block\":\"beat-bed\",\"channel\":\"kick\",\"text\":\"drum\"}]")!.AsArray(), spec));
    }
}
