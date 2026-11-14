namespace Ample.Server.Tests;

public sealed class AcceptAssetsTests
{
    [Fact]
    public void A_sample_that_the_accepted_song_uses_comes_along_by_its_path()
    {
        IReadOnlyList<string> needed = AcceptAssets.RequiredBinaries(
            ["samples/kick.wav", "samples/other.wav"],
            ["sampler s {\n  file samples/kick.wav\n}\n"]);

        Assert.Equal(["samples/kick.wav"], needed);
    }

    [Fact]
    public void A_sample_is_also_found_by_its_file_name_when_the_text_is_relative_to_its_folder()
    {
        IReadOnlyList<string> needed = AcceptAssets.RequiredBinaries(["samples/set/a.wav"], ["files a.wav b.wav"]);

        Assert.Equal(["samples/set/a.wav"], needed);
    }

    [Fact]
    public void A_sample_that_no_accepted_file_uses_is_left_out()
    {
        IReadOnlyList<string> needed = AcceptAssets.RequiredBinaries(["samples/unused.wav"], ["composition \"x\" { ample 1 }\n"]);

        Assert.Empty(needed);
    }

    [Fact]
    public void Several_texts_are_looked_at_together_and_the_order_of_the_staged_list_is_kept()
    {
        IReadOnlyList<string> needed = AcceptAssets.RequiredBinaries(
            ["a.wav", "b.wav", "c.wav"],
            ["uses c.wav", "uses a.wav"]);

        Assert.Equal(["a.wav", "c.wav"], needed);
    }

    [Fact]
    public void Empty_input_gives_nothing()
    {
        Assert.Empty(AcceptAssets.RequiredBinaries([], ["text"]));
        Assert.Empty(AcceptAssets.RequiredBinaries(["a.wav"], []));
        Assert.Empty(AcceptAssets.RequiredBinaries(["a.wav"], [string.Empty, string.Empty]));
    }
}

public sealed class StageDeletionTests
{
    [Fact]
    public void A_file_that_the_proposal_deletes_is_not_in_the_staged_project_but_its_neighbours_are()
    {
        string source = Directory.CreateTempSubdirectory("ample-stage-src-").FullName;
        string stage = Directory.CreateTempSubdirectory("ample-stage-dst-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "lib"));
            File.WriteAllText(Path.Combine(source, "lib", "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(source, "lib", "gone.txt"), "gone");
            File.WriteAllText(Path.Combine(source, "song.amplet"), "old");
            Dictionary<string, string> proposed = new(StringComparer.Ordinal) { ["song.amplet"] = "new" };

            AiService.StageProject(source, stage, proposed, ["lib/gone.txt"]);

            Assert.Equal("new", File.ReadAllText(Path.Combine(stage, "song.amplet")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(stage, "lib", "keep.txt")));
            Assert.False(File.Exists(Path.Combine(stage, "lib", "gone.txt")));
            Assert.True(File.Exists(Path.Combine(source, "lib", "gone.txt")), "the real project is never changed");
        }
        finally
        {
            Directory.Delete(source, true);
            Directory.Delete(stage, true);
        }
    }

    [Fact]
    public void Without_deletions_every_file_of_the_project_is_staged_as_before()
    {
        string source = Directory.CreateTempSubdirectory("ample-stage-src-").FullName;
        string stage = Directory.CreateTempSubdirectory("ample-stage-dst-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "lib"));
            File.WriteAllText(Path.Combine(source, "lib", "a.txt"), "a");
            File.WriteAllText(Path.Combine(source, "song.amplet"), "old");

            AiService.StageProject(source, stage, new Dictionary<string, string>(StringComparer.Ordinal) { ["song.amplet"] = "new" });

            Assert.Equal("a", File.ReadAllText(Path.Combine(stage, "lib", "a.txt")));
        }
        finally
        {
            Directory.Delete(source, true);
            Directory.Delete(stage, true);
        }
    }
}
