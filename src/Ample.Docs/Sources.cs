using System.Net;
using System.Text;
using System.Text.Json;

namespace Ample.Docs;

/// <summary>Renders a list of Markdown files as pages (spec pages, concept pages).</summary>
/// <param name="name">Source name.</param>
/// <param name="section">Sidebar section.</param>
/// <param name="entries">Files in display order: absolute path, page id, optional title override.</param>
public sealed class MarkdownFilesSource(string name, (string Title, int Order) section, IReadOnlyList<(string File, string Id, string? Title)> entries) : IDocSource
{
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public (string Title, int Order) Section => section;

    /// <inheritdoc />
    public IEnumerable<DocPage> GetPages(DocContext context)
    {
        int order = 0;
        foreach ((string file, string id, string? titleOverride) in entries)
        {
            string text = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
            string title = titleOverride ?? FirstHeading(text) ?? Path.GetFileNameWithoutExtension(file);
            (string html, string plain) = context.Renderer.Render(text, file, id);
            yield return new DocPage(id, title, section.Title, order++, html, plain);
        }
    }

    private static string? FirstHeading(string text) =>
        text.Split('\n').Where(l => l.StartsWith("# ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).FirstOrDefault();
}

/// <summary>One card per block template, from <c>spec/templates/*.json</c>.</summary>
public sealed class TemplatesSource : IDocSource
{
    /// <inheritdoc />
    public string Name => "templates";

    /// <inheritdoc />
    public (string Title, int Order) Section => ("Block templates", 20);

    /// <inheritdoc />
    public IEnumerable<DocPage> GetPages(DocContext context)
    {
        StringBuilder html = new();
        StringBuilder text = new();
        html.Append("<p>Every block type of the language, generated from <code>spec/templates/</code>. Click a type for its ports and parameters.</p>\n");
        foreach (string file in Directory.GetFiles(context.Root.Combine("spec", "templates"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement t = doc.RootElement;
            string id = t.GetProperty("id").GetString()!;
            string title = t.GetProperty("title").GetString()!;
            html.Append(CultureFormat($"<article class=\"card\" id=\"template-{id}\"><h2>{E(title)} <code>{E(id)}</code></h2>\n"));
            html.Append("<p>").Append(E(t.GetProperty("description").GetString()!)).Append("</p>\n<p>");
            html.Append("<span class=\"badge\">version ").Append(E(t.GetProperty("version").GetString()!)).Append("</span> ");
            html.Append("<span class=\"badge\">").Append(E(t.GetProperty("category").GetString()!)).Append("</span> ");
            if (t.GetProperty("dynamic").GetBoolean())
            {
                html.Append("<span class=\"badge warn\">dynamic</span> ");
            }

            if (t.TryGetProperty("plugin", out JsonElement plugin) && plugin.ValueKind == JsonValueKind.String)
            {
                html.Append("<span class=\"badge warn\">plugin: ").Append(E(plugin.GetString()!)).Append("</span> ");
            }

            html.Append("<span class=\"badge\">").Append(E(t.GetProperty("status").GetString()!)).Append(" since ").Append(E(t.GetProperty("since").GetString()!)).Append("</span></p>\n");
            if (t.TryGetProperty("needs", out JsonElement needs) && needs.ValueKind == JsonValueKind.Array && needs.GetArrayLength() > 0)
            {
                html.Append("<h3>Needs</h3>\n<ul>");
                foreach (JsonElement need in needs.EnumerateArray())
                {
                    html.Append("<li>").Append(E(need.GetString()!)).Append("</li>");
                }

                html.Append("</ul>\n");
            }

            if (t.TryGetProperty("soundSource", out JsonElement soundSource) && soundSource.ValueKind == JsonValueKind.True)
            {
                html.Append("<p><span class=\"badge ok\">sound source</span> This block makes a sound by itself, so its example can be rendered and heard.</p>\n");
            }

            Ports(html, "Inputs", t.GetProperty("inputs"), withRequired: true);
            Ports(html, "Outputs", t.GetProperty("outputs"), withRequired: false);
            html.Append("<h3>Parameters</h3>\n");
            JsonElement parameters = t.GetProperty("parameters");
            if (parameters.GetArrayLength() == 0)
            {
                html.Append("<p>None.</p>\n");
            }
            else
            {
                html.Append("<table><thead><tr><th>Name</th><th>Type</th><th>Default</th></tr></thead><tbody>\n");
                foreach (JsonElement p in parameters.EnumerateArray())
                {
                    html.Append("<tr><td><code>").Append(E(p.GetProperty("name").GetString()!)).Append("</code></td><td>").Append(E(p.GetProperty("type").GetString()!)).Append("</td><td><code>").Append(E(p.GetProperty("default").ToString())).Append("</code></td></tr>\n");
                }

                html.Append("</tbody></table>\n");
            }

            string example = t.TryGetProperty("example", out JsonElement written) && written.ValueKind == JsonValueKind.String ? written.GetString()!.TrimEnd('\n') : Example(t);
            string? problem = context.Renderer.CheckExample("run", null, example);
            if (problem is not null)
            {
                context.Problems.Add($"template {id}: generated example failed: {problem}");
            }

            html.Append("<h3>Example</h3>\n<div class=\"code\"><span class=\"badge ok\">example: compiles</span><button class=\"copy\" type=\"button\" aria-label=\"Copy code\">Copy</button><pre><code class=\"language-amplet\">")
                .Append(context.Renderer.Highlight(example)).Append("</code></pre></div>\n</article>\n");
            text.Append(title).Append(' ').Append(id).Append(' ').Append(t.GetProperty("description").GetString()).Append(' ');
        }

        yield return new DocPage("block-templates", "Block templates", Section.Title, 0, html.ToString(), text.ToString());
    }

    private static string Example(JsonElement t)
    {
        StringBuilder sb = new();
        sb.Append("composition \"demo\" { ample 1  tempo 92  master-seed 424242 }\n");
        sb.Append(t.GetProperty("id").GetString()).Append(" example {\n");
        foreach (JsonElement p in t.GetProperty("parameters").EnumerateArray().Where(p => p.GetProperty("default").ToString().Length > 0))
        {
            sb.Append("  ").Append(p.GetProperty("name").GetString()).Append(' ').Append(p.GetProperty("default").ToString()).Append('\n');
        }

        sb.Append("}\n");
        return sb.ToString().TrimEnd('\n');
    }

    private static void Ports(StringBuilder html, string title, JsonElement ports, bool withRequired)
    {
        html.Append("<h3>").Append(title).Append("</h3>\n");
        if (ports.GetArrayLength() == 0)
        {
            html.Append("<p>None.</p>\n");
            return;
        }

        html.Append("<table><thead><tr><th>Name</th><th>Type</th>").Append(withRequired ? "<th>Required</th>" : string.Empty).Append("</tr></thead><tbody>\n");
        foreach (JsonElement p in ports.EnumerateArray().Where(x => !(x.TryGetProperty("alias", out JsonElement alias) && alias.GetBoolean())))
        {
            html.Append("<tr><td><code>").Append(E(p.GetProperty("name").GetString()!)).Append("</code></td><td>").Append(E(p.GetProperty("type").GetString()!)).Append("</td>");
            if (withRequired)
            {
                html.Append("<td>").Append(p.GetProperty("required").GetBoolean() ? "yes" : "no").Append("</td>");
            }

            html.Append("</tr>\n");
        }

        html.Append("</tbody></table>\n");
    }

    private static string E(string s) => WebInput.Encode(s);

    private static string CultureFormat(string s) => s;
}

/// <summary>The diagnostics catalogue, one anchored entry per code, from <c>spec/diagnostics.json</c>.</summary>
public sealed class DiagnosticsSource : IDocSource
{
    /// <inheritdoc />
    public string Name => "diagnostics";

    /// <inheritdoc />
    public (string Title, int Order) Section => ("Diagnostics and lint", 30);

    /// <inheritdoc />
    public IEnumerable<DocPage> GetPages(DocContext context)
    {
        using JsonDocument doc = context.Root.ReadJson("spec", "diagnostics.json");
        StringBuilder html = new("<p>Every diagnostic code, generated from <code>spec/diagnostics.json</code>. <code>ample explain CODE</code> prints the same text. Codes are never reused.</p>\n<table><thead><tr><th>Code</th><th>Severity</th><th>Title</th></tr></thead><tbody>\n");
        StringBuilder text = new();
        JsonElement[] all = [.. doc.RootElement.GetProperty("diagnostics").EnumerateArray()];
        foreach (JsonElement d in all)
        {
            string code = d.GetProperty("code").GetString()!;
            html.Append("<tr><td><a href=\"#").Append(code).Append("\"><code>").Append(code).Append("</code></a></td><td>").Append(WebInput.Encode(d.GetProperty("severity").GetString()!)).Append("</td><td>").Append(WebInput.Encode(d.GetProperty("title").GetString()!)).Append("</td></tr>\n");
        }

        html.Append("</tbody></table>\n");
        foreach (JsonElement d in all)
        {
            string code = d.GetProperty("code").GetString()!;
            string title = d.GetProperty("title").GetString()!;
            html.Append("<article class=\"card\" id=\"").Append(code).Append("\"><h2><code>").Append(code).Append("</code> ").Append(WebInput.Encode(title)).Append("</h2>\n<p>");
            html.Append("<span class=\"badge ").Append(d.GetProperty("severity").GetString() == "error" ? "bad" : "warn").Append("\">").Append(WebInput.Encode(d.GetProperty("severity").GetString()!)).Append("</span> <span class=\"badge\">").Append(WebInput.Encode(d.GetProperty("stage").GetString()!)).Append("</span> <span class=\"badge\">since ").Append(WebInput.Encode(d.GetProperty("since").GetString()!)).Append("</span></p>\n");
            html.Append("<p>").Append(WebInput.Encode(d.GetProperty("explanation").GetString()!)).Append("</p>\n");
            string wrong = d.GetProperty("wrongExample").GetString()!;
            string right = d.GetProperty("rightExample").GetString()!;
            if (wrong.Length > 0)
            {
                string? problem = context.Renderer.CheckExample("snippet", code, wrong);
                if (problem is not null)
                {
                    context.Problems.Add($"diagnostic {code}: {problem}");
                }

                html.Append("<h3>Wrong</h3>\n<div class=\"code\"><span class=\"badge bad\">example: ").Append(code).Append("</span><pre><code class=\"language-amplet\">").Append(context.Renderer.Highlight(wrong)).Append("</code></pre></div>\n");
            }

            if (right.Length > 0)
            {
                html.Append("<h3>Right</h3>\n<div class=\"code\"><pre><code class=\"language-amplet\">").Append(context.Renderer.Highlight(right)).Append("</code></pre></div>\n");
            }

            html.Append("</article>\n");
            text.Append(code).Append(' ').Append(title).Append(' ').Append(d.GetProperty("explanation").GetString()).Append(' ');
        }

        yield return new DocPage("diagnostics", "Diagnostics", Section.Title, 0, html.ToString(), text.ToString());
    }
}

/// <summary>The lint rules, from <c>spec/lint-rules.json</c>.</summary>
public sealed class LintSource : IDocSource
{
    /// <inheritdoc />
    public string Name => "lint";

    /// <inheritdoc />
    public (string Title, int Order) Section => ("Diagnostics and lint", 30);

    /// <inheritdoc />
    public IEnumerable<DocPage> GetPages(DocContext context)
    {
        using JsonDocument doc = context.Root.ReadJson("spec", "lint-rules.json");
        StringBuilder html = new("<p>Lint rules find things that compile but are probably not what you meant. Set a level per rule with <code>lint { L004 error }</code>; silence a rule for the next block with <code># ample-ignore L004</code>.</p>\n<table><thead><tr><th>Rule</th><th>Name</th><th>Default</th><th>Finds</th></tr></thead><tbody>\n");
        StringBuilder text = new();
        foreach (JsonElement r in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            string code = r.GetProperty("code").GetString()!;
            html.Append("<tr id=\"").Append(code).Append("\"><td><code>").Append(code).Append("</code></td><td>").Append(WebInput.Encode(r.GetProperty("name").GetString()!)).Append("</td><td>").Append(WebInput.Encode(r.GetProperty("defaultLevel").GetString()!)).Append("</td><td>").Append(WebInput.Encode(r.GetProperty("description").GetString()!)).Append("</td></tr>\n");
            text.Append(code).Append(' ').Append(r.GetProperty("name").GetString()).Append(' ').Append(r.GetProperty("description").GetString()).Append(' ');
        }

        html.Append("</tbody></table>\n");
        yield return new DocPage("lint-rules", "Lint rules", Section.Title, 1, html.ToString(), text.ToString());
    }
}

/// <summary>The CLI reference, generated from the real command tree so it cannot drift.</summary>
public sealed class CliSource : IDocSource
{
    /// <inheritdoc />
    public string Name => "cli";

    /// <inheritdoc />
    public (string Title, int Order) Section => ("Command line", 40);

    /// <inheritdoc />
    public IEnumerable<DocPage> GetPages(DocContext context)
    {
        StringBuilder html = new();
        StringBuilder text = new();
        if (context.Cli is null)
        {
            html.Append("<p>The command tree was not supplied when this help was generated.</p>");
        }
        else
        {
            Write(html, text, context.Cli, context.Cli.Name);
        }

        yield return new DocPage("cli-reference", "Command line reference", Section.Title, 0, html.ToString(), text.ToString());
    }

    private static void Write(StringBuilder html, StringBuilder text, CliCommandInfo command, string path)
    {
        string id = "cli-" + MarkdownRenderer.Slug(path);
        html.Append("<article class=\"card\" id=\"").Append(id).Append("\"><h2><code>").Append(WebInput.Encode(path)).Append("</code></h2>\n<p>").Append(WebInput.Encode(command.Description)).Append("</p>\n");
        if (command.Options.Count > 0)
        {
            html.Append("<ul>\n");
            foreach (string option in command.Options)
            {
                html.Append("<li><code>").Append(WebInput.Encode(option.Split(':', 2)[0])).Append("</code>").Append(option.Contains(':', StringComparison.Ordinal) ? ": " + WebInput.Encode(option.Split(':', 2)[1].Trim()) : string.Empty).Append("</li>\n");
            }

            html.Append("</ul>\n");
        }

        html.Append("</article>\n");
        text.Append(path).Append(' ').Append(command.Description).Append(' ').AppendJoin(' ', command.Options).Append(' ');
        foreach (CliCommandInfo sub in command.Subcommands.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            Write(html, text, sub, path + " " + sub.Name);
        }
    }
}

internal static class WebInput
{
    internal static string Encode(string s) => WebUtility.HtmlEncode(s);
}
