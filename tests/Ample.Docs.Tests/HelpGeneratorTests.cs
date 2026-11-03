using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ample.Docs.Tests;

[Trait("Category", "Docs")]
public sealed partial class HelpGeneratorTests
{
    private static readonly CliCommandInfo Cli = new("ample", "Test tool.", ["--version: Shows the version."], [new CliCommandInfo("docs", "Docs commands.", [], [new CliCommandInfo("build", "Builds.", ["--out: Output file."], [])])]);

    private static SpecRoot Root => SpecRoot.Find(AppContext.BaseDirectory);

    private static HelpResult Build() => HelpGenerator.Build(Root, Cli, "0.0.0-test");

    [Fact]
    public void Every_diagnostic_code_and_lint_rule_has_an_anchor()
    {
        string html = Build().Html;
        using JsonDocument diagnostics = Root.ReadJson("spec", "diagnostics.json");
        foreach (JsonElement d in diagnostics.RootElement.GetProperty("diagnostics").EnumerateArray())
        {
            Assert.Contains($"id=\"{d.GetProperty("code").GetString()}\"", html, StringComparison.Ordinal);
        }

        using JsonDocument lint = Root.ReadJson("spec", "lint-rules.json");
        foreach (JsonElement r in lint.RootElement.GetProperty("rules").EnumerateArray())
        {
            Assert.Contains($"id=\"{r.GetProperty("code").GetString()}\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_pipe_written_as_backslash_pipe_in_a_table_is_shown_as_a_pipe()
    {
        HelpResult built = Build();
        Assert.DoesNotContain("\\|", built.Html, StringComparison.Ordinal);   // the escape that keeps a pipe in one cell is not part of the text
    }

    [Fact]
    public void Every_template_has_a_card_with_an_example()
    {
        string html = Build().Html;
        foreach (string file in Directory.GetFiles(Root.Combine("spec", "templates"), "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            Assert.Contains($"id=\"template-{id}\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("example: compiles", html, StringComparison.Ordinal);
    }

    [Fact]
    public void All_spec_pages_and_the_cli_reference_are_present()
    {
        HelpResult result = Build();
        Assert.Contains(result.Pages, p => p.Id == "language-03-syntax");
        Assert.Contains(result.Pages, p => p.Id == "language-99-maintenance");
        Assert.Contains(result.Pages, p => p.Id == "cli-reference");
        Assert.Contains("id=\"cli-ample-docs-build\"", result.Html, StringComparison.Ordinal);
        Assert.Equal("language-index", result.Pages.First(p => p.Section == "Language specification").Id);
    }

    [Fact]
    public void The_real_sources_have_no_problems()
    {
        Assert.Empty(Build().Problems);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        Assert.Equal(Build().Html, Build().Html);
    }

    [Fact]
    public void The_build_info_block_is_the_only_thing_that_changes_with_the_version()
    {
        string a = HelpGenerator.Build(Root, Cli, "1.0.0").Html;
        string b = HelpGenerator.Build(Root, Cli, "2.0.0").Html;
        Assert.NotEqual(a, b);
        Assert.Equal(HelpGenerator.StripBuildInfo(a), HelpGenerator.StripBuildInfo(b));
    }

    [Fact]
    public void Internal_links_all_resolve_to_an_id()
    {
        string html = Build().Html;
        html = Regex.Replace(html, "<script.*?</script>", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        HashSet<string> ids = [.. IdAttribute().Matches(html).Select(m => m.Groups[1].Value)];
        List<string> dead = [.. HrefAnchor().Matches(html).Select(m => m.Groups[1].Value).Where(h => !ids.Contains(h)).Distinct()];
        Assert.True(dead.Count == 0, "dead in-page links: " + string.Join(", ", dead));
    }

    [Fact]
    public void The_page_stays_offline_and_small()
    {
        HelpResult result = Build();
        Assert.DoesNotContain("http://", Regex.Replace(result.Html, "https?://[^\"' <)]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5)), StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link ", result.Html, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Html) < HelpGenerator.SizeWarningBytes);
    }

    [Fact]
    public void A_broken_example_in_a_temporary_spec_fails_the_build_with_a_clear_message()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "ample-docs-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Root.Combine("spec"), Path.Combine(tmp, "spec"));
            CopyDirectory(Root.Combine("docs", "language"), Path.Combine(tmp, "docs", "language"));
            File.AppendAllText(Path.Combine(tmp, "docs", "language", "01-overview.md"), "\n```amplet run\nproject \"x\" { tempo 90\n```\n\n[gone](missing.md)\n");
            HelpResult result = HelpGenerator.Build(SpecRoot.At(tmp), null, "0.0.0-test");
            Assert.Contains(result.Problems, p => p.Contains("01-overview.md", StringComparison.Ordinal) && p.Contains("run example has errors", StringComparison.Ordinal));
            Assert.Contains(result.Problems, p => p.Contains("dead link 'missing.md'", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Topics_can_be_found_by_id_and_by_text()
    {
        HelpResult result = Build();
        Assert.Single(HelpGenerator.FindTopic(result.Pages, "diagnostics"));
        Assert.NotEmpty(HelpGenerator.FindTopic(result.Pages, "AMP2003"));
        Assert.Empty(HelpGenerator.FindTopic(result.Pages, "zzzz-not-a-topic"));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    [GeneratedRegex("\\sid=\"([^\"]+)\"")]
    private static partial Regex IdAttribute();

    [GeneratedRegex("href=\"#([^\"]+)\"")]
    private static partial Regex HrefAnchor();
}
