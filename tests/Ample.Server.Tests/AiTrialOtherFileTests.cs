using System.Text.Json.Nodes;

namespace Ample.Server.Tests;

/// <summary>A trial edit whose text is not in the song but in another file of the proposal (the .ampleseq of a sequencer) says where the text is.</summary>
public sealed class AiTrialOtherFileTests
{
    private static JsonArray Replace(string old) => new(new JsonObject { ["op"] = "replace", ["old"] = old, ["new"] = "x" });

    private static readonly Dictionary<string, string> Proposed = new(StringComparer.Ordinal)
    {
        ["Covers/cover.amplet"] = "composition \"c\" { ample 1 }\nsequencer score {\n  file \"Covers/score.ampleseq\"\n  lanes lane1=piano-lh\n}\n",
        ["Covers/score.ampleseq"] = "sequencer 1\nchannel piano-lh gm file=pack:musescore-general program=0 vol=-6\n",
    };

    [Fact]
    public void The_file_that_holds_the_text_is_named()
    {
        Assert.Equal("Covers/score.ampleseq", AiService.FileWithTextOfTrial(Proposed, "Covers/cover.amplet", Replace("channel piano-lh gm file=pack:musescore-general program=0 vol=-6")));
    }

    [Fact]
    public void Text_that_is_nowhere_or_only_in_the_song_itself_names_no_other_file()
    {
        Assert.Null(AiService.FileWithTextOfTrial(Proposed, "Covers/cover.amplet", Replace("channel violin gm program=40")));
        Assert.Null(AiService.FileWithTextOfTrial(Proposed, "Covers/cover.amplet", Replace("sequencer score {")));
        Assert.Null(AiService.FileWithTextOfTrial(Proposed, "Covers/cover.amplet", Replace("short")));
    }
}
