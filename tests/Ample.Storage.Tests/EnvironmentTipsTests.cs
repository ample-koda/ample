using Ample.Storage.Projects;

namespace Ample.Storage.Tests;

public sealed class EnvironmentTipsTests
{
    [Theory]
    [InlineData("NAME=\"Ubuntu\"\nID=ubuntu\nID_LIKE=debian\n", true)]
    [InlineData("ID=debian\n", true)]
    [InlineData("ID=\"linuxmint\"\nID_LIKE=\"ubuntu debian\"\n", true)]
    [InlineData("ID=fedora\nID_LIKE=\"rhel\"\n", false)]
    [InlineData("ID=arch\n", false)]
    [InlineData("", false)]
    public void The_debian_family_is_read_from_os_release(string text, bool expected)
    {
        Assert.Equal(expected, InstallEnvironmentDetector.IsDebianFamily(text));
    }

    [Fact]
    public void The_ffmpeg_tip_names_the_command_of_each_machine()
    {
        Assert.Contains("sudo apt install ffmpeg", EnvironmentCheck.FfmpegHint(InstallEnvironment.Debian), StringComparison.Ordinal);
        Assert.Contains("winget install Gyan.FFmpeg", EnvironmentCheck.FfmpegHint(InstallEnvironment.Windows), StringComparison.Ordinal);
        Assert.Contains("brew install ffmpeg", EnvironmentCheck.FfmpegHint(InstallEnvironment.MacOs), StringComparison.Ordinal);
        Assert.DoesNotContain("winget", EnvironmentCheck.FfmpegHint(InstallEnvironment.Debian), StringComparison.Ordinal);
    }

    [Fact]
    public void The_python_tip_on_ubuntu_is_apt_and_python3()
    {
        string hint = EnvironmentCheck.PythonHint(InstallEnvironment.Debian);

        Assert.Contains("sudo apt install python3 python3-venv", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("winget", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_tip_starts_with_a_sentence_and_keeps_the_commands_on_their_own_lines()
    {
        foreach (InstallEnvironment env in Enum.GetValues<InstallEnvironment>())
        {
            string[] lines = EnvironmentCheck.FfmpegHint(env).Split('\n');

            Assert.True(lines.Length >= 2, env + " has no command line");
            Assert.EndsWith(".", lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void One_install_command_covers_only_what_is_missing_and_installable()
    {
        EnvironmentFinding[] findings =
        [
            new("program libraries", true, "ok", string.Empty),
            new("ffmpeg (mp3 export)", false, "not found", "x"),
            new("Audiveris (score PDF/image import)", false, "not found", "x"),
            new("python (voice worker)", false, "not found", "x"),
        ];

        Assert.Equal("sudo apt install ffmpeg python3 python3-venv", EnvironmentCheck.InstallCommand(findings, InstallEnvironment.Debian));
        Assert.Equal("brew install ffmpeg python", EnvironmentCheck.InstallCommand(findings, InstallEnvironment.MacOs));
        Assert.Null(EnvironmentCheck.InstallCommand(findings, InstallEnvironment.Windows));
        Assert.Null(EnvironmentCheck.InstallCommand([new("ffmpeg (mp3 export)", true, "ok", string.Empty)], InstallEnvironment.Debian));
    }

    [Fact]
    public void In_wsl_the_smaller_of_the_virtual_disk_and_the_windows_drive_counts()
    {
        EnvironmentFinding finding = EnvironmentCheck.DiskFinding(944.2, "/", 18.0);

        Assert.True(finding.Ok);
        Assert.StartsWith("18.0 GB really free", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("944.2 GB free on /", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("Windows drive C:", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void In_wsl_a_nearly_full_windows_drive_is_a_finding_even_when_the_virtual_disk_looks_empty()
    {
        EnvironmentFinding finding = EnvironmentCheck.DiskFinding(944.2, "/", 3.0);

        Assert.False(finding.Ok);
        Assert.Contains("free up space on C:", finding.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_wsl_the_disk_check_is_the_plain_free_space()
    {
        EnvironmentFinding finding = EnvironmentCheck.DiskFinding(120.5, "/", null);

        Assert.True(finding.Ok);
        Assert.Equal("120.5 GB free on /", finding.Detail);
        Assert.False(EnvironmentCheck.DiskFinding(2.0, "/", null).Ok);
    }
}
