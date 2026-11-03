using Ample.Engine.Engine;
using Ample.Storage.Projects;

namespace Ample.Core.Tests;

/// <summary>How many blocks a build renders at once: the cores by default, never more than the server allows, a project or a caller can only ask for fewer. Pure functions: no global state is changed.</summary>
public sealed class BuildParallelismTests
{
    [Fact]
    public void Without_any_wish_a_build_uses_one_block_per_core()
    {
        Assert.Equal(BuildParallelism.Cores, BuildParallelism.Limit(null));
        Assert.Equal(BuildParallelism.Cores, BuildParallelism.Limit(0));
        Assert.Equal(BuildParallelism.Cores, BuildParallelism.Effective(0, null, BuildParallelism.Limit(null)));
    }

    [Fact]
    public void The_server_can_lower_its_limit_but_never_go_above_the_cores()
    {
        Assert.Equal(1, BuildParallelism.Limit(1));
        Assert.Equal(BuildParallelism.Cores, BuildParallelism.Limit(BuildParallelism.Cores + 50));
        Assert.Equal(BuildParallelism.Cores, BuildParallelism.Limit(-3));
        Assert.Equal(1, BuildParallelism.Effective(0, null, 1));
    }

    [Fact]
    public void A_project_can_ask_for_fewer_and_never_for_more()
    {
        int limit = BuildParallelism.Limit(null);
        Assert.Equal(1, BuildParallelism.Effective(0, 1, limit));
        Assert.Equal(limit, BuildParallelism.Effective(0, limit + 10, limit));
        Assert.Equal(2, BuildParallelism.Effective(0, 64, 2));
    }

    [Fact]
    public void A_caller_that_asks_for_a_number_gets_it_or_less_and_the_project_can_lower_it_further()
    {
        Assert.Equal(2, BuildParallelism.Effective(2, null, 8));
        Assert.Equal(1, BuildParallelism.Effective(2, 1, 8));
        Assert.Equal(8, BuildParallelism.Effective(10_000, null, 8));
        Assert.Equal(3, BuildParallelism.Effective(8, 3, 8));
    }

    [Fact]
    public void The_project_setting_is_written_only_when_it_is_set_and_read_back()
    {
        ProjectManifest plain = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1);
        Assert.DoesNotContain("\"build\"", ProjectFile.Serialize(plain), StringComparison.Ordinal);
        Assert.Null(ProjectFile.Parse(ProjectFile.Serialize(plain)).Settings.BuildParallelism);
        ProjectManifest limited = plain with { Settings = plain.Settings with { BuildParallelism = 3 } };
        string json = ProjectFile.Serialize(limited);
        Assert.Contains("\"parallelism\": 3", json.Replace("\":3", "\": 3", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(3, ProjectFile.Parse(json).Settings.BuildParallelism);
    }

    [Fact]
    public void The_number_a_project_asks_for_is_read_from_its_project_file()
    {
        string root = Directory.CreateTempSubdirectory("ample-parallelism-").FullName;
        try
        {
            Assert.Null(BuildParallelism.OfProject(root));
            Directory.CreateDirectory(Path.Combine(root, ProjectLayout.AmpleFolder));
            ProjectManifest limited = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1);
            limited = limited with { Settings = limited.Settings with { BuildParallelism = 2 } };
            File.WriteAllText(Path.Combine(root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), ProjectFile.Serialize(limited));
            Assert.Equal(2, BuildParallelism.OfProject(root));
            File.WriteAllText(Path.Combine(root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), "not json");
            Assert.Null(BuildParallelism.OfProject(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
