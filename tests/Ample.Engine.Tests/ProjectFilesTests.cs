using System.Text;
using Ample.Core.Abstractions;
using Ample.Audio.Midi;
using Ample.Storage.Projects;
using Ample.Language;

namespace Ample.Core.Tests;

[Trait("Category", "ProjectFiles")]
public sealed class ProjectFilesTests
{
    [Theory]
    [InlineData("song", "My Song", "My-Song.amplet")]
    [InlineData("lyrics", "Chorus", "lyrics/Chorus.lrc")]
    [InlineData("chords", "Verse", "chords/Verse.chordpro")]
    [InlineData("midi", "Lead line", "assets/midi/Lead line.mid")]
    [InlineData("module", "Groove box", "modules/Groove-box.module")]
    [InlineData("samples", "kit-a", "samples/kit-a")]
    [InlineData("note", "Ideas", "notes/Ideas.md")]
    [InlineData("folder", "renders", "renders")]
    public void Every_kind_gets_its_default_folder_and_extension(string kind, string name, string path)
    {
        NewFile item = NewFileKinds.Build(kind, name, null);
        Assert.Equal(path, item.Path);
        Assert.Equal(kind is "samples" or "folder", item.IsFolder);
        Assert.Equal(item.IsFolder, item.Bytes is null);
    }

    [Fact]
    public void A_folder_can_be_chosen_and_a_bad_kind_or_name_is_refused()
    {
        Assert.Equal("shared/lyrics/Intro.lrc", NewFileKinds.Build("lyrics", "Intro", "shared/lyrics/").Path);
        Assert.Equal("AMP5285", Assert.Throws<AmpleException>(() => NewFileKinds.Build("poster", "x", null)).DiagnosticCode);
        foreach (string bad in new[] { "", "  ", "../x", "a/b", "tab\tname", "dots.in.name", new string('a', 81) }) Assert.Throws<AmpleException>(() => NewFileKinds.Build("note", bad, null));
    }

    [Fact]
    public void The_new_song_is_a_song_that_parses_and_the_new_module_is_a_module_that_parses()
    {
        string song = Encoding.UTF8.GetString(NewFileKinds.Build("song", "Fresh", null, 120, 99).Bytes!);
        Assert.Contains("master-seed 99", song, StringComparison.Ordinal);
        Assert.Contains("tempo 120", song, StringComparison.Ordinal);
        ModuleDefinition module = ModuleDefinition.Parse(Encoding.UTF8.GetString(NewFileKinds.Build("module", "Groove box", null).Bytes!));
        Assert.Empty(module.Problems);
        Assert.Equal("Groove-box", module.Name);
        Assert.Single(module.Outputs);
    }

    [Fact]
    public void The_empty_midi_clip_is_a_valid_standard_midi_file()
    {
        byte[] bytes = NewFileKinds.Build("midi", "Empty", null).Bytes!;
        SmfFile file = SmfReader.Parse(bytes, SmfLimits.Standard);
        Assert.Equal(480, file.TicksPerQuarter);
        Assert.Single(file.Tracks);
    }

    [Theory]
    [InlineData("  kit samples/kit", "samples/kit", "samples/rusty", true, "  kit samples/rusty")]
    [InlineData("  map kick:samples/kit/kick2;snare:samples/other/s", "samples/kit", "samples/rusty", true, "  map kick:samples/rusty/kick2;snare:samples/other/s")]
    [InlineData("  kit samples/kit-b", "samples/kit", "samples/rusty", true, "  kit samples/kit-b")]
    [InlineData("  file \"lyrics/a.lrc\"", "lyrics/a.lrc", "lyrics/b.lrc", false, "  file \"lyrics/b.lrc\"")]
    [InlineData("  file lyrics/a.lrc.bak", "lyrics/a.lrc", "lyrics/b.lrc", false, "  file lyrics/a.lrc.bak")]
    [InlineData("  note thesamples/kit here", "samples/kit", "samples/rusty", true, "  note thesamples/kit here")]
    public void A_path_is_replaced_only_as_a_whole_word(string line, string from, string to, bool directory, string expected) =>
        Assert.Equal(expected, ProjectRefactor.Replace(line, from, to, directory));

    [Fact]
    public void A_move_changes_only_the_lines_that_name_the_path_and_keeps_the_rest_byte_for_byte()
    {
        string a = "composition \"s\" { ample 1  tempo 90  master-seed 1 }\r\n# kit samples/kit is a comment\r\ndrums d {\r\n  kit samples/kit\r\n  pattern beat-A\r\n}\r\n";
        string b = "composition \"t\" { ample 1  tempo 90  master-seed 1 }\nmidi m {\n  file assets/midi/lead.mid\n}\n";
        RefactorPlan plan = ProjectRefactor.Plan([("a.amplet", a), ("b.amplet", b)], "samples/kit", "samples/rusty", true);
        PathEdit edit = Assert.Single(plan.Edits);
        Assert.Equal(("a.amplet", 4), (edit.File, edit.Line));
        Assert.Equal(a.Replace("  kit samples/kit\r\n", "  kit samples/rusty\r\n", StringComparison.Ordinal), plan.Texts["a.amplet"]);
        Assert.DoesNotContain("b.amplet", plan.Texts.Keys);
    }

    [Fact]
    public void Renaming_a_module_follows_its_use_lines_and_its_own_header()
    {
        string module = "module groove version 1.0\noutput audio drums.audio\ndrums drums {\n}\n";
        string song = "composition \"s\" { ample 1  tempo 90  master-seed 1 }\nuse groove@1.0 as g1 {\n}\nuse groover as g2 {\n}\n";
        RefactorPlan plan = ProjectRefactor.Plan([("modules/groove.module", module), ("song.amplet", song)], "modules/groove.module", "modules/swing.module", false);
        Assert.StartsWith("module swing version 1.0", plan.Texts["modules/groove.module"], StringComparison.Ordinal);
        Assert.Contains("use swing@1.0 as g1 {", plan.Texts["song.amplet"], StringComparison.Ordinal);
        Assert.Contains("use groover as g2 {", plan.Texts["song.amplet"], StringComparison.Ordinal);
        Assert.Equal("modules/swing.module", ProjectRefactor.PathAfterMove("modules/groove.module", "modules/groove.module", "modules/swing.module", false));
        Assert.Equal("samples/rusty/kick.wav", ProjectRefactor.PathAfterMove("samples/kit/kick.wav", "samples/kit", "samples/rusty", true));
        Assert.Equal("samples/kit-b/kick.wav", ProjectRefactor.PathAfterMove("samples/kit-b/kick.wav", "samples/kit", "samples/rusty", true));
    }
}
