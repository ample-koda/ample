namespace Ample.Server.Tests;

/// <summary>The component library: the alias that is proposed when a name is taken, and which files the scan looks at.</summary>
public sealed class ComponentLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ample-lib-" + Guid.NewGuid().ToString("N"));

    public ComponentLibraryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("components/triple-notes.amplec", "tripleNotes")]
    [InlineData("dream_echo.amplec", "dreamEcho")]
    [InlineData("a/Big Room.amplec", "bigRoom")]
    [InlineData("3d-reverb.amplec", "lib3dReverb")]
    public void The_alias_is_the_name_of_the_file_in_camel_case(string file, string expected) => Assert.Equal(expected, ComponentLibrary.SuggestAlias(file, new HashSet<string>(StringComparer.Ordinal)));

    [Fact]
    public void The_alias_is_made_unique_among_the_first_segments_the_song_uses()
    {
        HashSet<string> taken = new(StringComparer.Ordinal) { "tripleNotes", "tripleNotes2", "koda" };
        Assert.Equal("tripleNotes3", ComponentLibrary.SuggestAlias("components/triple-notes.amplec", taken));
    }

    [Fact]
    public void The_scan_finds_component_files_in_order_and_skips_the_folders_that_start_with_a_dot()
    {
        foreach (string file in new[] { "b.amplec", "components/a.amplec", ".ample/hidden.amplec", ".git/x.amplec", "components/readme.md" })
        {
            string full = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }

        string[] found = [.. ComponentLibrary.ComponentFiles(root).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))];
        Assert.Equal(["b.amplec", "components/a.amplec"], found);
    }
}
