using Ample.Storage.Projects;

namespace Ample.Storage.Tests;

/// <summary>
/// The setup offered "Install Python 3.12" for ever when <c>python</c> on the PATH was an old Python 2 that came first: Python 3 was installed, the check stayed red
/// and ticking the install changed nothing. A Python 3 that is installed elsewhere is named, and nothing is offered to install.
/// </summary>
public sealed class PythonFindingTests
{
    [Theory]
    [InlineData("3 14 C:\\Users\\tim\\AppData\\Local\\Python\\pythoncore-3.14-64\\python.exe", 3, 14, "C:\\Users\\tim\\AppData\\Local\\Python\\pythoncore-3.14-64\\python.exe")]
    [InlineData("3 12 C:\\Program Files\\Python312\\python.exe", 3, 12, "C:\\Program Files\\Python312\\python.exe")]
    [InlineData("  3 10 /usr/bin/python3  ", 3, 10, "/usr/bin/python3")]
    public void The_line_a_python_prints_is_read_with_spaces_in_its_path(string line, int major, int minor, string exe)
    {
        Assert.True(EnvironmentCheck.TryParsePython(line, out int gotMajor, out int gotMinor, out string gotExe));
        Assert.Equal((major, minor, exe), (gotMajor, gotMinor, gotExe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Python 3.12.1")]
    [InlineData("3 12")]
    [InlineData("x y z")]
    [InlineData("99999999999999999999 1 C:\\python.exe")]
    public void A_line_that_is_not_that_answer_is_not_read(string line)
    {
        Assert.False(EnvironmentCheck.TryParsePython(line, out _, out _, out _));
    }

    [Fact]
    public void A_finding_that_has_nothing_to_install_says_so()
    {
        EnvironmentFinding finding = new("python (voice worker)", false, "Python 2.7.18; Python 3.14 is installed at C:\\x\\python.exe", "set the interpreter", NoFix: true);
        Assert.True(finding.NoFix);
        Assert.False(new EnvironmentFinding("x", false, "d", "h").NoFix);
    }
}
