namespace Ample.Language.Tests;

public sealed class EffectEditTests
{
    private const string Song = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nsynth pad at 1 for 4 {\n}\nbus mix {\n  from pad.audio -> audio1\n}\n";

    [Fact]
    public void An_effect_put_in_and_taken_out_gives_back_the_text()
    {
        string? inserted = Edits.InsertEffectAfter(Song, Corpus.Spec, "pad", "reverb", "pad-reverb", [], out string? error);
        Assert.NotNull(inserted);
        Assert.Null(error);
        Assert.Contains("reverb pad-reverb", inserted, StringComparison.Ordinal);
        string? back = Edits.BypassEffect(inserted!, Corpus.Spec, "pad-reverb", out error);
        Assert.NotNull(back);
        Assert.Null(error);
        Assert.Equal(Song, back);
    }

    [Fact]
    public void Taking_out_an_effect_in_the_middle_keeps_the_audio_path()
    {
        string chain = Edits.InsertEffectAfter(Song, Corpus.Spec, "pad", "reverb", "room", [], out _)!;
        chain = Edits.InsertEffectAfter(chain, Corpus.Spec, "room", "delay", "echo", [], out _)!;
        string? without = Edits.BypassEffect(chain, Corpus.Spec, "room", out string? error);
        Assert.Null(error);
        AmpleDocument doc = Parser.Parse(without!, Corpus.Spec);
        Assert.DoesNotContain(doc.Blocks, b => b.Name == "room");
        Assert.Contains(doc.Blocks.First(b => b.Name == "echo").Froms, f => f.Block == "pad" && f.RawInput == "audio");
        Assert.Contains(doc.Blocks.First(b => b.Name == "mix").Froms, f => f.Block == "echo");
    }

    [Fact]
    public void An_effect_with_nothing_coming_in_cannot_be_taken_out()
    {
        string text = Song + "reverb lone {\n}\n";
        Assert.Null(Edits.BypassEffect(text, Corpus.Spec, "lone", out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_whole_text_can_be_replaced_as_one_edit()
    {
        AmpleDocument doc = Parser.Parse(Song, Corpus.Spec);
        Assert.Equal("new\ntext", Edits.Apply(Song, Edits.ReplaceText(doc, "new\ntext").Edits));
    }

    [Fact]
    public void A_block_can_be_copied_under_a_new_name_with_its_placement_and_inputs()
    {
        string text = Song + "midi-clip riff {\n  bars 2\n}\n";
        text = text.Replace("synth pad at 1 for 4 {\n}\n", "synth pad at 1 for 4, 9 for 4 {\n  from riff.notes -> notes\n}\n", StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        string copied = Edits.Apply(text, Edits.CopyBlock(doc, "pad", "pad-2").Edits);
        AmpleDocument after = Parser.Parse(copied, Corpus.Spec);
        BlockNode copy = after.Blocks.First(b => b.Name == "pad-2");
        Assert.Equal(2, copy.Parts.Count);
        Assert.Contains(copy.Froms, f => f.Block == "riff");
        Assert.False(Edits.CopyBlock(doc, "pad", "mix").Ok);
        Assert.False(Edits.CopyBlock(doc, "nothing", "x").Ok);
    }
}
