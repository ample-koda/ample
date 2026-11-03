using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ample.Docs;

/// <summary>The result of generating the help.</summary>
/// <param name="Html">The single-file help page.</param>
/// <param name="Pages">The pages in display order.</param>
/// <param name="Problems">Dead links, failing examples and other source problems.</param>
public sealed record HelpResult(string Html, IReadOnlyList<DocPage> Pages, IReadOnlyList<string> Problems);

/// <summary>Builds the offline <c>help.html</c> and the terminal help from the spec data and docs of the repository.</summary>
public static partial class HelpGenerator
{
    /// <summary>Soft size limit of the generated page, in bytes.</summary>
    public const int SizeWarningBytes = 3 * 1024 * 1024;

    /// <summary>Generates the help for the AMPLE folder.</summary>
    /// <param name="root">The AMPLE folder.</param>
    /// <param name="cli">The CLI command tree for the reference page, or null.</param>
    /// <param name="amplesVersion">The tool version shown in the build info.</param>
    public static HelpResult Build(SpecRoot root, CliCommandInfo? cli, string amplesVersion)
    {
        (string File, string Id, string? Title)[] language = [.. Directory.GetFiles(root.Combine("docs", "language"), "*.md")
            .OrderBy(f => Path.GetFileName(f).Equals("README.md", StringComparison.Ordinal) ? 0 : 1).ThenBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f => (f, "language-" + (Path.GetFileNameWithoutExtension(f).Equals("README", StringComparison.Ordinal) ? "index" : Path.GetFileNameWithoutExtension(f).ToLowerInvariant()), (string?)null))];
        (string File, string Id, string? Title)[] concepts = ReadManifest(root);

        Dictionary<string, string> pagesByFile = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string file, string id, _) in language.Concat(concepts))
        {
            pagesByFile[Path.GetFullPath(file)] = id;
        }

        using JsonDocument tokens = root.ReadJson("spec", "tokens.json");
        string[] keywords = [.. tokens.RootElement.GetProperty("keywords").EnumerateArray().Select(e => e.GetString()!)];
        string[] blockTypes = [.. Directory.GetFiles(root.Combine("spec", "templates"), "*.json").Select(f => Path.GetFileNameWithoutExtension(f))];
        using JsonDocument diag = root.ReadJson("spec", "diagnostics.json");
        HashSet<string> codes = [.. diag.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()!)];

        MarkdownRenderer renderer = new(pagesByFile, new AnalyzerExampleChecker(root.LoadSpecData(), codes), new AmpletHighlighter(keywords, blockTypes));
        DocContext context = new(root, renderer, cli);

        IDocSource[] sources =
        [
            new MarkdownFilesSource("language", ("Language specification", 10), language),
            new TemplatesSource(),
            new DiagnosticsSource(),
            new LintSource(),
            new CliSource(),
            new ApiSource(),
            new MarkdownFilesSource("concepts", ("Concepts", 50), concepts),
        ];

        List<DocPage> pages = [.. sources.SelectMany(s => s.GetPages(context))
            .OrderBy(p => SectionOrder(sources, p.Section)).ThenBy(p => p.Section, StringComparer.Ordinal).ThenBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal)];

        string html = Assemble(pages, root.SpecVersion, amplesVersion);
        List<string> problems = [.. context.Problems.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)];
        if (Encoding.UTF8.GetByteCount(html) > SizeWarningBytes)
        {
            problems.Add($"help.html is larger than {SizeWarningBytes / (1024 * 1024)} MB");
        }

        return new HelpResult(html, pages, problems);
    }

    /// <summary>Removes the build-info block so two generations can be compared byte for byte.</summary>
    public static string StripBuildInfo(string html) => BuildInfoBlock().Replace(html, string.Empty);

    /// <summary>Finds pages for the terminal: an exact id first, then titles that contain the topic.</summary>
    public static IReadOnlyList<DocPage> FindTopic(IReadOnlyList<DocPage> pages, string topic)
    {
        string t = topic.Trim();
        List<DocPage> exact = [.. pages.Where(p => p.Id.Equals(t, StringComparison.OrdinalIgnoreCase) || p.Title.Equals(t, StringComparison.OrdinalIgnoreCase))];
        return exact.Count > 0 ? exact : [.. pages.Where(p => p.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(t, StringComparison.OrdinalIgnoreCase) || p.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase))];
    }

    private static int SectionOrder(IDocSource[] sources, string section) => sources.First(s => s.Section.Title == section).Section.Order;

    private static (string File, string Id, string? Title)[] ReadManifest(SpecRoot root)
    {
        string path = root.Combine("docs", "help-manifest.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return [.. doc.RootElement.GetProperty("concepts").EnumerateArray().Select(e =>
        {
            string file = e.GetProperty("file").GetString()!;
            string id = "concept-" + Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            string? title = e.TryGetProperty("title", out JsonElement t) ? t.GetString() : null;
            return (root.Combine("docs", file), id, title);
        })];
    }

    private static string Assemble(List<DocPage> pages, string specVersion, string ampleVersion)
    {
        StringBuilder nav = new();
        string? section = null;
        foreach (DocPage p in pages)
        {
            if (p.Section != section)
            {
                if (section is not null)
                {
                    nav.Append("</ul></details>\n");
                }

                nav.Append("<details open><summary>").Append(WebUtility.HtmlEncode(p.Section)).Append("</summary><ul>\n");
                section = p.Section;
            }

            nav.Append("<li><a href=\"#").Append(p.Id).Append("\">").Append(WebUtility.HtmlEncode(p.Title)).Append("</a></li>\n");
        }

        nav.Append("</ul></details>\n");

        StringBuilder content = new();
        foreach (DocPage p in pages)
        {
            content.Append("<section class=\"page\" id=\"").Append(p.Id).Append("\" data-title=\"").Append(WebUtility.HtmlEncode(p.Title)).Append("\">\n<h1>").Append(WebUtility.HtmlEncode(p.Title)).Append("</h1>\n");
            if (p.Since is not null)
            {
                content.Append("<p><span class=\"badge\">since ").Append(WebUtility.HtmlEncode(p.Since)).Append("</span></p>\n");
            }

            content.Append(p.Html).Append("</section>\n");
        }

        List<object> entries = [];
        foreach (DocPage p in pages)
        {
            string text = Regex.Replace(p.SearchText, "\\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(2)).Trim();
            if (text.Length > 6000)
            {
                text = text[..6000];
            }

            entries.Add(new { id = p.Id, title = p.Title, section = p.Section, text });
        }

        string index = JsonSerializer.Serialize(entries);
        string info = string.Create(CultureInfo.InvariantCulture, $"<!--build-info-->AMPLE {WebUtility.HtmlEncode(ampleVersion)} · language spec {WebUtility.HtmlEncode(specVersion)} (draft)<!--/build-info-->");
        return Resource("template.html")
            .Replace("{{STYLE}}", Resource("help.css"), StringComparison.Ordinal)
            .Replace("{{SCRIPT}}", Resource("help.js"), StringComparison.Ordinal)
            .Replace("{{NAV}}", nav.ToString(), StringComparison.Ordinal)
            .Replace("{{CONTENT}}", content.ToString(), StringComparison.Ordinal)
            .Replace("{{INDEX}}", index.Replace("</", "<\\/", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("{{BUILDINFO}}", info, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Resource(string name)
    {
        Assembly assembly = typeof(HelpGenerator).Assembly;
        string full = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using StreamReader reader = new(assembly.GetManifestResourceStream(full)!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("<!--build-info-->.*?<!--/build-info-->", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BuildInfoBlock();
}
