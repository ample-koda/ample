namespace Ample.Server.Tests;

/// <summary>
/// A test that reads files of the repository that the source it checks lives in (the command line tool, the studio): it runs when every file is there and is skipped
/// with a message when one is not (the repository is published layer by layer, so a layer may be checked before the file it is wired to).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RepoFactAttribute : FactAttribute
{
    public RepoFactAttribute(params string[] files)
    {
        string? root = FindRoot();
        string[] missing = root is null ? files : [.. files.Where(f => !File.Exists(Path.Combine(root, f)))];
        if (missing.Length > 0)
        {
            Skip = "needs " + string.Join(", ", missing) + " (not in this checkout)";
        }
    }

    private static string? FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "spec")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
