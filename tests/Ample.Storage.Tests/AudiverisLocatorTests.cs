using Ample.Storage.Projects;

namespace Ample.Storage.Tests;

/// <summary>Where the Audiveris of the pack is found (the marker of <c>ample packs install audiveris</c>).</summary>
public sealed class AudiverisLocatorTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-av-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    private void Install(string marker, string? file)
    {
        string folder = Path.Combine(home, "audiveris");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "installed.json"), marker);
        if (file is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, file))!);
            File.WriteAllText(Path.Combine(folder, file), "x");
        }
    }

    [Fact]
    public void The_program_named_by_the_marker_is_found_only_when_it_is_there()
    {
        Assert.Null(AudiverisLocator.FindExe(home));
        Install("{\"version\":\"5.11.0\",\"exe\":\"opt/audiveris/bin/Audiveris\"}", null);
        Assert.Null(AudiverisLocator.FindExe(home));   // the marker without the file
        Install("{\"version\":\"5.11.0\",\"exe\":\"opt/audiveris/bin/Audiveris\"}", "opt/audiveris/bin/Audiveris");
        Assert.EndsWith("Audiveris", AudiverisLocator.FindExe(home));
    }

    [Fact]
    public void A_damaged_marker_or_one_that_points_outside_the_folder_is_not_installed()
    {
        Install("not json", null);
        Assert.Null(AudiverisLocator.FindExe(home));
        File.WriteAllText(Path.Combine(home, "outside.txt"), "x");
        Install("{\"exe\":\"../outside.txt\"}", null);
        Assert.Null(AudiverisLocator.FindExe(home));
    }
}
