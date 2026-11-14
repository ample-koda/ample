using System.Xml.Linq;

namespace Ample.Hub.Core.Tests;

/// <summary>
/// The setup detectors (the MSI and the Tune-up wizard) accept any .NET Desktop Runtime from major 9 on,
/// so the tray hub, which is built for net9.0-windows, has to be allowed to start on a newer major.
/// </summary>
public sealed class HubRuntimePolicyTests
{
    private static string RepoFile(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir ?? throw new InvalidOperationException("repository root not found"), .. parts]);
    }

    [Fact]
    public void The_hub_may_roll_forward_to_a_newer_major_runtime()
    {
        XDocument project = XDocument.Load(RepoFile("src", "Ample.Hub", "Ample.Hub.csproj"));
        Assert.Equal("Major", project.Descendants("RollForward").Single().Value);
        Assert.Equal("net9.0-windows", project.Descendants("TargetFramework").Single().Value);
    }
}
