namespace Ample.Cli.Tests;

public sealed class VsConnectTests
{
    [Fact]
    public void Identical_files_are_no_conflict_differing_ones_move_aside_and_local_only_files_join_the_project()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-vs-connect-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "local");
        string project = Path.Combine(root, "project");
        try
        {
            Directory.CreateDirectory(Path.Combine(local, "lyrics"));
            Directory.CreateDirectory(Path.Combine(project, "lyrics"));
            File.WriteAllText(Path.Combine(local, "same.amplet"), "x");
            File.WriteAllText(Path.Combine(project, "same.amplet"), "x");
            File.WriteAllText(Path.Combine(local, "lyrics", "t.txt"), "mine");
            File.WriteAllText(Path.Combine(project, "lyrics", "t.txt"), "theirs");
            File.WriteAllText(Path.Combine(local, "only-local.amplet"), "local only");
            File.WriteAllText(Path.Combine(project, "only-project.amplet"), "project only");
            File.WriteAllText(Path.Combine(local, "Demo.ampleproj"), "<Project/>");

            VsConnect.Result result = VsConnect.Run(local, project, "abc123");

            Assert.Equal("abc123", result.ConflictId);
            Assert.Equal("mine", File.ReadAllText(Path.Combine(local, "_conflicts", "abc123", "lyrics", "t.txt")));
            Assert.Equal("theirs", File.ReadAllText(Path.Combine(local, "lyrics", "t.txt")));
            Assert.Equal("theirs", File.ReadAllText(Path.Combine(project, "lyrics", "t.txt")));
            Assert.Equal("local only", File.ReadAllText(Path.Combine(project, "only-local.amplet")));
            Assert.Equal("project only", File.ReadAllText(Path.Combine(local, "only-project.amplet")));
            Assert.True(File.Exists(Path.Combine(local, "_conflicts", "abc123", "README.txt")));
            Assert.True(File.Exists(Path.Combine(local, "_conflicts", "abc123", "manifest.json")));
            Assert.False(File.Exists(Path.Combine(project, "Demo.ampleproj")));
            Assert.False(Directory.Exists(Path.Combine(project, "_conflicts")));

            // a second connect finds nothing left to resolve, and makes no new conflict folder
            VsConnect.Result again = VsConnect.Run(local, project);
            Assert.Null(again.ConflictId);
            Assert.Single(Directory.GetDirectories(Path.Combine(local, "_conflicts")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_differing_file_with_unsaved_Studio_changes_stops_the_connect_before_anything_moves()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-vs-draft-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(root, "local");
        string project = Path.Combine(root, "project");
        try
        {
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(local, "a.amplet"), "mine");
            File.WriteAllText(Path.Combine(project, "a.amplet"), "theirs");
            File.WriteAllText(Path.Combine(local, "b.amplet"), "local only");

            Assert.Throws<InvalidOperationException>(() => VsConnect.Run(local, project, "id1", ["a.amplet"]));

            Assert.Equal("mine", File.ReadAllText(Path.Combine(local, "a.amplet")));
            Assert.False(Directory.Exists(Path.Combine(local, "_conflicts")));
            Assert.False(File.Exists(Path.Combine(project, "b.amplet")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
