namespace Ample.Speech.Tests;

public sealed class LicenseNoticeTests
{
    private static string Root()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "THIRD-PARTY-NOTICES.md"))) folder = Path.GetDirectoryName(folder);
        return folder ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public void Humanico_and_cmudict_are_named_with_their_licences_in_the_notices_and_the_licence_files_match()
    {
        string root = Root();
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.Contains("Jacob Garcia", notices, StringComparison.Ordinal);
        Assert.Contains("LICENSES/humanico-voice.txt", notices, StringComparison.Ordinal);
        Assert.Contains("LICENSES/cmudict.txt", notices, StringComparison.Ordinal);
        Assert.Contains("LICENSES/humanico-voice.txt", readme, StringComparison.Ordinal);

        string humanico = File.ReadAllText(Path.Combine(root, "LICENSES", "humanico-voice.txt"));
        Assert.StartsWith("MIT License", humanico, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2026 Jacob Garcia", humanico, StringComparison.Ordinal);
        Assert.Equal(humanico, File.ReadAllText(Path.Combine(root, "src", "Ample.Speech", "Data", "humanico-LICENSE.txt")));
        Assert.Equal(File.ReadAllText(Path.Combine(root, "LICENSES", "cmudict.txt")), File.ReadAllText(Path.Combine(root, "src", "Ample.Speech", "Data", "cmudict-LICENSE.txt")));
    }

    [Fact]
    public void Both_licence_files_are_copied_next_to_the_assembly()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Data", "humanico-LICENSE.txt")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Data", "cmudict-LICENSE.txt")));
    }
}

public sealed class GeneratorLineEndingTests
{
    [Fact]
    public void Reference_generators_write_LF_so_the_pinned_hashes_hold_on_Windows_and_Linux()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !Directory.Exists(Path.Combine(folder, "tools"))) folder = Path.GetDirectoryName(folder);
        string[] generators = [.. Directory.GetFiles(Path.Combine(folder!, "tools"), "humanico-*.py")];

        Assert.NotEmpty(generators);
        foreach (string generator in generators)
        {
            foreach (string line in File.ReadLines(generator).Where(line => line.Contains("write_text(", StringComparison.Ordinal)))
                Assert.True(line.Contains("newline=\"\\n\"", StringComparison.Ordinal), $"{Path.GetFileName(generator)} writes text without newline=LF: {line.Trim()}");
        }
    }
}
