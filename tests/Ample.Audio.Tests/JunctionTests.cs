using Ample.Audio.IO;

namespace Ample.Audio.Tests;

/// <summary>A folder called "a&amp;calc" in a project from someone else must not become a command of cmd.exe when AMPLE links it.</summary>
public sealed class JunctionTests
{
    [Theory]
    [InlineData("C:\\Music\\R&B\\samples", true)]
    [InlineData("C:\\Music & Samples\\x", true)]
    [InlineData("C:\\a^b|c<d>e", true)]
    [InlineData("C:\\a\"b", false)]
    [InlineData("C:\\100%\\x", false)]
    [InlineData("C:\\a\nb", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void A_path_is_quoted_unless_cmd_could_still_read_something_of_it(string path, bool allowed) =>
        Assert.Equal(allowed, Junction.CanQuote(path));

    [Fact]
    public void Both_paths_and_the_whole_command_are_between_quotes()
    {
        Assert.Equal("/d /s /c \"mklink /J \"C:\\x\\link\" \"C:\\y&z\\target\"\"", Junction.Arguments("C:\\x\\link", "C:\\y&z\\target"));
    }

    [Fact]
    public void A_folder_name_with_a_command_separator_is_linked_and_nothing_else_is_run()
    {
        if (!OperatingSystem.IsWindows()) return;   // mklink /J is Windows only
        string work = Path.Combine(Path.GetTempPath(), "ample-junction-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(work, "t&mkdir pwned&x");
        string link = Path.Combine(work, "l&mkdir pwned&y");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "hello");
        try
        {
            Assert.True(Junction.TryCreate(link, target));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "a.txt")));
            Assert.False(Directory.Exists("pwned"));
            Assert.False(Directory.Exists(Path.Combine(work, "pwned")));
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); }
            catch (IOException) { }
            try { Directory.Delete(work, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void A_path_that_cannot_be_quoted_is_refused_without_starting_anything()
    {
        Assert.False(Junction.TryCreate("C:\\x\\a\"b", "C:\\y"));
        Assert.False(Junction.TryCreate("C:\\x", "C:\\100%\\y"));
    }
}
