using Ample.Audio.IO;

namespace Ample.Server.Tests;

/// <summary>
/// The component routes read an import only from inside the project (security finding 57): a sibling folder whose name starts with the name of the
/// project folder, a path with .. and a junction that leads out used to pass the test of the text of the path.
/// </summary>
public sealed class ComponentContainmentTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ample-comp-" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileSystem fs = new();

    public ComponentContainmentTests()
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(folder + "-x");   // starts with the name of the project folder
        File.WriteAllText(Path.Combine(folder, "inside.amplec"), "component inside {\n}\n");
        File.WriteAllText(Path.Combine(folder + "-x", "secret.amplec"), "component secret {\n}\n");
    }

    public void Dispose()
    {
        string link = Path.Combine(folder, "outside");
        if (Directory.Exists(link))
        {
            Directory.Delete(link);   // the junction only, not what it points at
        }

        foreach (string path in new[] { folder, folder + "-x" })
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void A_file_inside_the_project_is_found()
    {
        Assert.Equal(Path.Combine(Path.GetFullPath(folder), "inside.amplec"), ComponentsApi.SafeFull(fs, folder, "inside.amplec"));
    }

    [Theory]
    [InlineData("../SIBLING/secret.amplec")]
    [InlineData("..\\SIBLING\\secret.amplec")]
    [InlineData("a/../../SIBLING/secret.amplec")]
    [InlineData("../../../../../../../../Windows/win.ini")]
    [InlineData(".ample/project.db")]
    [InlineData("./.ample/project.db")]
    [InlineData(".AMPLE/project.db")]
    public void A_path_that_leaves_the_project_or_names_the_private_folder_is_not_resolved(string pattern)
    {
        string target = pattern.Replace("SIBLING", Path.GetFileName(folder) + "-x", StringComparison.Ordinal);
        Assert.Null(ComponentsApi.SafeFull(fs, folder, target));
    }

    [Fact]
    public void An_absolute_path_is_not_resolved()
    {
        Assert.Null(ComponentsApi.SafeFull(fs, folder, Path.Combine(folder + "-x", "secret.amplec")));
    }

    [Fact]
    public void A_junction_in_the_project_that_leads_out_is_not_followed()
    {
        string link = Path.Combine(folder, "outside");
        if (!Junction.TryCreate(link, folder + "-x"))
        {
            return;   // no junction on this system: nothing to prove
        }

        Assert.Null(ComponentsApi.SafeFull(fs, folder, "outside/secret.amplec"));
    }
}
