using Ample.Core.Abstractions;

namespace Ample.Server.Tests;

/// <summary>One rule for a folder that a request names (security finding 61): backup target, new project folder and library folder.</summary>
public sealed class ServerPathsTests
{
    private static string Temp(params string[] parts) => Path.Combine([Path.GetTempPath(), "ample-paths-test", .. parts]);

    [Fact]
    public void A_full_path_of_an_ordinary_folder_is_allowed_and_comes_back_normalised()
    {
        string folder = Temp("backups", "..", "backups", "week1").Replace("..", "x", StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(folder), ServerPaths.Folder(folder, "backup target"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("backups")]
    [InlineData("backups/week1")]
    [InlineData("./backups")]
    public void A_path_that_is_not_a_full_path_is_refused(string text)
    {
        Assert.Equal("AMP5285", Assert.Throws<AmpleException>(() => ServerPaths.Folder(text, "backup target")).DiagnosticCode);
    }

    [Fact]
    public void A_path_with_dot_dot_a_control_character_or_a_private_folder_is_refused()
    {
        string[] bad =
        [
            Path.Combine(Path.GetTempPath(), "a", "..", "b"),
            Path.GetTempPath() + "a\\..\\b",
            Path.Combine(Path.GetTempPath(), "bad\nname"),
            Path.Combine(Path.GetTempPath(), "project", ".ample", "backups"),
            Path.Combine(Path.GetTempPath(), "project", ".AMPLE"),
            Path.Combine(Path.GetTempPath(), "project", ".ample "),
        ];
        foreach (string path in bad)
        {
            Assert.Equal("AMP5285", Assert.Throws<AmpleException>(() => ServerPaths.Folder(path, "backup target")).DiagnosticCode);
        }
    }

    [Fact]
    public void The_root_of_a_drive_and_a_system_folder_are_refused()
    {
        string[] bad = OperatingSystem.IsWindows()
            ? [
                Path.GetPathRoot(Path.GetTempPath())!,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "config"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ]
            : ["/", "/etc", "/etc/ssh", "/usr/local/bin", "/proc/1"];
        foreach (string path in bad)
        {
            Assert.Equal("AMP5285", Assert.Throws<AmpleException>(() => ServerPaths.Folder(path, "library folder")).DiagnosticCode);
        }
    }

    [Fact]
    public void A_home_folder_and_a_data_drive_folder_are_allowed()
    {
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), ServerPaths.Folder(Path.GetTempPath(), "x").TrimEnd(Path.DirectorySeparatorChar));
        Assert.NotNull(ServerPaths.Folder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x"));
    }
}
