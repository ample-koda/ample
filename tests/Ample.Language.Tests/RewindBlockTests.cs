namespace Ample.Language.Tests;

public sealed class RewindBlockTests
{
    [Fact]
    public void ScratchBlockBecomesEventsAndBack()
    {
        string[] body = ["    at 6s { duration 1.5s; back 0.5s }", "    at 14s {", "      duration 1s", "      back 2s", "    }"];
        Assert.True(RewindBlock.TryCompact(body, out string events, out string error), error);
        Assert.Equal("at 6s scratch 1.5s back 0.5s; at 14s scratch 1s back 2s", events);
        Assert.Equal(["  scratch {", "    at 6s { duration 1.5s; back 0.5s }", "    at 14s { duration 1s; back 2s }", "  }"], RewindBlock.Expand(events, "  ")!);
    }

    [Fact]
    public void BadLineIsNamed()
    {
        Assert.False(RewindBlock.TryCompact(["    at soon { duration 1s }"], out _, out string error));
        Assert.Contains("soon", error);
        Assert.False(RewindBlock.TryCompact(["    at 1s { colour red }"], out _, out error));
        Assert.Contains("colour red", error);
    }

    private const string Song = "composition \"d\" { ample 1  tempo 92  master-seed 1 }\ndrums beats at 1 for 4 {\n}\nrewind r {\n  events \"at 1s scratch 1s back 2s\"\n  from beats.audio -> audio\n}\nmaster master {\n  from r.audio -> audio\n}\n";

    [Fact]
    public void SettingEventsWritesTheScratchBlockAndItIsReadBack()
    {
        AmpleDocument doc = Parser.Parse(Song, Corpus.Spec, ParseOptions.Editor);
        EditResult edit = Edits.SetParameter(doc, "r", "events", "at 6s scratch 1.5s back 0.5s; at 14s scratch 1s back 2s");
        string text = Edits.Apply(Song, edit.Edits);
        Assert.Contains("  scratch {\n    at 6s { duration 1.5s; back 0.5s }\n    at 14s { duration 1s; back 2s }\n  }\n", text);
        AmpleDocument again = Parser.Parse(text, Corpus.Spec, ParseOptions.Editor);
        Assert.Equal("at 6s scratch 1.5s back 0.5s; at 14s scratch 1s back 2s", again.Blocks.Single(b => b.Name == "r").Parameters.Single(p => p.Name == "events").Value);
        Assert.DoesNotContain(again.Diagnostics, d => d.Severity == Severity.Error);
        string second = Edits.Apply(text, Edits.SetParameter(again, "r", "events", "at 2s scratch 1s back 1s").Edits);
        Assert.Single(second.Split('\n'), l => l.Trim() == "scratch {");
        Assert.DoesNotContain("scratch {", Edits.Apply(second, Edits.SetParameter(Parser.Parse(second, Corpus.Spec, ParseOptions.Editor), "r", "events", "").Edits));
    }
}
