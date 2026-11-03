using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Core.Tests;

public sealed class RecentProjectsTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-recents-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Last_port_is_backwards_compatible_and_survives_touch_without_a_new_port()
    {
        Directory.CreateDirectory(home);
        string songs = Path.Combine(Path.GetTempPath(), "ample-songs-" + Guid.NewGuid().ToString("N"));   // an absolute path of this system (a Windows path is a relative path on Linux)
        File.WriteAllText(Path.Combine(home, "recent.json"), System.Text.Json.JsonSerializer.Serialize(new[] { new { path = songs, name = "Song", id = "project-id", lastOpened = "2026-10-05T10:00:00+00:00" } }));
        SystemClock clock = new();

        Assert.Null(Assert.Single(RecentProjects.List(home)).LastPort);
        RecentProjects.Touch(home, songs, "Song", "project-id", clock, 52341);
        RecentProjects.Touch(home, songs, "Song", "project-id", clock, null);

        Assert.Equal(52341, Assert.Single(RecentProjects.List(home)).LastPort);
    }

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }
}
