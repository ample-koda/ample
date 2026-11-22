namespace Ample.Lsp.Tests;

public sealed class ProjectFilesTests
{
    [Fact]
    public void Lists_project_files_with_forward_slashes_and_skips_plumbing_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-pf-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string f in new[] { "a.amplet", "samples/kit/k.wav", ".ample/project.json", "bin/x.dll", "obj/y.txt" })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, f))!);
                File.WriteAllText(Path.Combine(root, f), "x");
            }

            Assert.Equal(["a.amplet", "samples/kit/k.wav"], ProjectFiles.Cached(root).Order().ToList());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
