using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Ample.Docs;

/// <summary>Checks <c>amplet</c> examples that appear in the docs. The real analyser replaces the stub in chunk 20.</summary>
public interface IExampleChecker
{
    /// <summary>Returns a problem message when the example does not behave as its fence says, otherwise null.</summary>
    string? Check(string kind, string? expectedCode, string text);
}

/// <summary>Stub checker used until the real language library exists (chunk 20): it only checks that a fence is well formed.</summary>
public sealed class StubExampleChecker(IReadOnlySet<string> knownCodes) : IExampleChecker
{
    /// <inheritdoc />
    public string? Check(string kind, string? expectedCode, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "example is empty";
        }

        int depth = 0;
        foreach (char c in text)
        {
            depth += c == '{' ? 1 : c == '}' ? -1 : 0;
            if (depth < 0)
            {
                break;
            }
        }

        if (kind == "run" && depth != 0)
        {
            return "run example has unbalanced braces";
        }

        if (kind == "invalid" && (expectedCode is null || !knownCodes.Contains(expectedCode)))
        {
            return $"invalid example names an unknown code '{expectedCode}'";
        }

        return null;
    }
}

/// <summary>Renders Markdown to HTML with page-scoped heading ids, rewritten links and highlighted AMPLE code.</summary>
public sealed partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    private readonly IReadOnlyDictionary<string, string> pagesByFile;
    private readonly IExampleChecker checker;
    private readonly AmpletHighlighter highlighter;

    /// <summary>Initializes a new instance of the <see cref="MarkdownRenderer"/> class.</summary>
    /// <param name="pagesByFile">Maps absolute Markdown file paths to the page id they are rendered into.</param>
    /// <param name="checker">Checks executable examples.</param>
    /// <param name="highlighter">Highlights AMPLE code.</param>
    public MarkdownRenderer(IReadOnlyDictionary<string, string> pagesByFile, IExampleChecker checker, AmpletHighlighter highlighter)
    {
        this.pagesByFile = pagesByFile;
        this.checker = checker;
        this.highlighter = highlighter;
    }

    /// <summary>Gets the problems found while rendering.</summary>
    public IList<string> Problems { get; } = [];

    private static bool InTableCell(Inline inline)
    {
        ContainerInline? top = inline.Parent;
        while (top?.Parent is not null)
        {
            top = top.Parent;
        }

        return top?.ParentBlock?.Parent is Markdig.Extensions.Tables.TableCell;
    }

    /// <summary>Renders one Markdown file.</summary>
    /// <param name="markdown">The Markdown text.</param>
    /// <param name="filePath">Absolute path of the file (used to resolve links).</param>
    /// <param name="pageId">Id of the page the text is rendered into.</param>
    /// <param name="skipFirstHeading">Omit the first level-1 heading (the page title is rendered separately).</param>
    public (string Html, string Text) Render(string markdown, string filePath, string pageId, bool skipFirstHeading = true)
    {
        MarkdownDocument doc = Markdown.Parse(markdown, Pipeline);
        HeadingBlock? first = skipFirstHeading ? doc.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1) : null;
        if (first is not null)
        {
            doc.Remove(first);
        }

        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (HeadingBlock heading in doc.Descendants<HeadingBlock>())
        {
            string slug = Slug(heading.Inline?.FirstChild is null ? string.Empty : ToText(heading.Inline));
            string id = $"{pageId}--{(slug.Length == 0 ? "section" : slug)}";
            for (int n = 2; !used.Add(id); n++)
            {
                id = $"{pageId}--{slug}-{n.ToString(CultureInfo.InvariantCulture)}";
            }

            heading.GetAttributes().Id = id;
        }

        foreach (LinkInline link in doc.Descendants<LinkInline>().Where(l => !l.IsImage).ToList())
        {
            RewriteLink(link, filePath);
        }

        // In a table a pipe inside a code span is written \| so that it does not start a new cell; the escape is not part of the text (Markdig leaves the backslash in a code span)
        foreach (CodeInline code in doc.Descendants<CodeInline>().Where(c => c.Content.Contains("\\|", StringComparison.Ordinal) && InTableCell(c)).ToList())
        {
            code.Content = code.Content.Replace("\\|", "|", StringComparison.Ordinal);
        }

        using StringWriter writer = new(CultureInfo.InvariantCulture);
        HtmlRenderer renderer = new(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new AmpleCodeBlockRenderer(this, filePath));
        renderer.Render(doc);
        return (writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Markdown.ToPlainText(markdown, Pipeline).Replace("\\|", "|", StringComparison.Ordinal));
    }

    /// <summary>Turns a heading text into a URL-safe slug.</summary>
    public static string Slug(string text) => SlugBreaks().Replace(text.ToLowerInvariant(), "-").Trim('-');

    private static string ToText(ContainerInline inline)
    {
        StringBuilder sb = new();
        foreach (Inline child in inline.Descendants())
        {
            switch (child)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
            }
        }

        return sb.ToString();
    }

    private void RewriteLink(LinkInline link, string filePath)
    {
        string? url = link.Url;
        if (string.IsNullOrEmpty(url) || url.StartsWith('#') || url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return;
        }

        int hash = url.IndexOf('#', StringComparison.Ordinal);
        string pathPart = hash < 0 ? url : url[..hash];
        string anchor = hash < 0 ? string.Empty : url[(hash + 1)..];
        string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, WebUtility.UrlDecode(pathPart)));
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            Problems.Add($"{Path.GetFileName(filePath)}: dead link '{url}'");
            return;
        }

        if (pagesByFile.TryGetValue(target, out string? pageId))
        {
            link.Url = anchor.Length == 0 ? $"#{pageId}" : $"#{pageId}--{anchor}";
        }
        else
        {
            // Not part of the help: keep the text, drop the link target, show where it lives in the repository.
            link.Url = null;
            link.GetAttributes().AddClass("repo-ref");
            link.GetAttributes().AddProperty("title", "Not part of this help; see the repository file " + Path.GetFileName(target));
        }
    }

    internal string? CheckExample(string kind, string? code, string text) => checker.Check(kind, code, text);

    internal string Highlight(string text) => highlighter.Highlight(text);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugBreaks();

    private sealed class AmpleCodeBlockRenderer(MarkdownRenderer owner, string filePath) : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            string text = block.Lines.ToString().TrimEnd('\n', '\r');
            string[] words = block is FencedCodeBlock fenced ? ((fenced.Info ?? string.Empty) + " " + (fenced.Arguments ?? string.Empty)).Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            string lang = words.Length > 0 ? words[0] : string.Empty;
            string body;
            string badge = string.Empty;
            if (lang == "amplet")
            {
                string kind = words.Length > 1 ? words[1] : "plain";
                string? code = words.Length > 2 ? words[2] : null;
                if (kind is "run" or "invalid")
                {
                    string? problem = owner.CheckExample(kind, code, text);
                    if (problem is not null)
                    {
                        owner.Problems.Add($"{Path.GetFileName(filePath)}: {problem}");
                    }

                    badge = kind == "run" ? "<span class=\"badge ok\">example: compiles</span>" : $"<span class=\"badge bad\">example: {WebUtility.HtmlEncode(code)}</span>";
                }

                body = owner.Highlight(text);
            }
            else
            {
                body = WebUtility.HtmlEncode(text);
            }

            renderer.Write("<div class=\"code\">").Write(badge).Write("<button class=\"copy\" type=\"button\" aria-label=\"Copy code\">Copy</button><pre><code");
            if (lang.Length > 0)
            {
                renderer.Write(" class=\"language-").Write(WebUtility.HtmlEncode(lang)).Write('"');
            }

            renderer.Write('>').Write(body).Write("</code></pre></div>\n");
        }
    }
}

/// <summary>Highlights AMPLE source text into static HTML spans at build time.</summary>
public sealed partial class AmpletHighlighter
{
    private readonly Regex token;

    /// <summary>Initializes a new instance of the <see cref="AmpletHighlighter"/> class.</summary>
    /// <param name="keywords">Language keywords.</param>
    /// <param name="blockTypes">Block type ids from the template catalogue.</param>
    public AmpletHighlighter(IEnumerable<string> keywords, IEnumerable<string> blockTypes)
    {
        string kw = string.Join('|', keywords.OrderByDescending(k => k.Length).Select(Regex.Escape));
        string bt = string.Join('|', blockTypes.OrderByDescending(k => k.Length).Select(Regex.Escape));
        token = new Regex(
            $"(?<c>#[^\\n]*)|(?<s>\"(?:[^\"\\\\\\n]|\\\\.)*\")|(?<k>(?<![\\w-])(?:{kw})(?![\\w-]))|(?<t>(?<![\\w-])(?:{bt})(?![\\w-]))|(?<n>(?<![\\w@.-])-?\\d+(?:\\.\\d+)?)|(?<p>->|[{{}},@])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
    }

    /// <summary>Returns the HTML-encoded text with token spans.</summary>
    public string Highlight(string text)
    {
        StringBuilder sb = new();
        int pos = 0;
        foreach (Match m in token.Matches(text))
        {
            sb.Append(WebUtility.HtmlEncode(text[pos..m.Index]));
            string cls = m.Groups["c"].Success ? "c" : m.Groups["s"].Success ? "s" : m.Groups["k"].Success ? "k" : m.Groups["t"].Success ? "t" : m.Groups["n"].Success ? "n" : "p";
            sb.Append("<span class=\"tk-").Append(cls).Append("\">").Append(WebUtility.HtmlEncode(m.Value)).Append("</span>");
            pos = m.Index + m.Length;
        }

        sb.Append(WebUtility.HtmlEncode(text[pos..]));
        return sb.ToString();
    }
}
