namespace Ample.Hub.Core.Tests;

public sealed class LaunchPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:55077/#token=abc", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("http://[::1]:9000/x", true)]
    [InlineData("https://127.0.0.1:55077/", false)]
    [InlineData("http://example.org/", false)]
    [InlineData("http://user:pw@127.0.0.1/", false)]
    [InlineData("file:///c:/windows/system32/calc.exe", false)]
    [InlineData("calc.exe", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_studio_of_an_instance_on_this_machine_is_opened(string? text, bool allowed) =>
        Assert.Equal(allowed, LaunchPolicy.IsStudioAddress(text));

    [Fact]
    public void Only_an_existing_folder_is_opened_as_a_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "tool.exe");
        File.WriteAllText(file, "x");
        try
        {
            Assert.True(LaunchPolicy.IsFolder(folder));
            Assert.False(LaunchPolicy.IsFolder(file));
            Assert.False(LaunchPolicy.IsFolder(Path.Combine(folder, "missing")));
            Assert.False(LaunchPolicy.IsFolder("relative"));
            Assert.False(LaunchPolicy.IsFolder(string.Empty));
            Assert.False(LaunchPolicy.IsFolder(null));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Only_the_start_recipe_that_ample_serve_writes_is_run_again()
    {
        string folder = Path.Combine(Path.GetTempPath(), "launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string ample = Path.Combine(folder, "Ample.exe"), other = Path.Combine(folder, "calc.exe");
            File.WriteAllText(ample, "x");
            File.WriteAllText(other, "x");
            Assert.True(LaunchPolicy.IsRelaunchRecipe(ample, ["serve", "--port", "1"]));
            Assert.False(LaunchPolicy.IsRelaunchRecipe(other, ["serve"]));
            Assert.False(LaunchPolicy.IsRelaunchRecipe(ample, ["build"]));
            Assert.False(LaunchPolicy.IsRelaunchRecipe(ample, []));
            Assert.False(LaunchPolicy.IsRelaunchRecipe("ample.exe", ["serve"]));
            Assert.False(LaunchPolicy.IsRelaunchRecipe(Path.Combine(folder, "ample"), ["serve"]));
            Assert.False(LaunchPolicy.IsRelaunchRecipe(null, ["serve"]));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
