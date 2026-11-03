using System.Net;
using System.Text;
using System.Text.Json;

namespace Ample.Docs;

/// <summary>The API reference, from the committed <c>docs/api/openapi.json</c> (which a server test keeps equal to the real routes).</summary>
public sealed class ApiSource : IDocSource
{
    /// <inheritdoc />
    public string Name => "api";

    /// <inheritdoc />
    public (string Title, int Order) Section => ("Local API", 45);

    /// <inheritdoc />
    public IEnumerable<DocPage> GetPages(DocContext context)
    {
        string file = context.Root.Combine("docs", "api", "openapi.json");
        if (!File.Exists(file))
        {
            yield break;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        StringBuilder html = new("<p>The API of <code>ample serve</code>: loopback only, every call carries the session token in <code>X-Ample-Token</code>, every error is RFC 9457 problem details with the AMP code. Generated from <code>docs/api/openapi.json</code>.</p>\n");
        StringBuilder text = new();
        var rows = new List<(string Tag, string Method, string Path, string Summary, string Description)>();
        foreach (JsonProperty path in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty method in path.Value.EnumerateObject())
            {
                rows.Add((method.Value.GetProperty("tags")[0].GetString()!, method.Name.ToUpperInvariant(), path.Name, method.Value.GetProperty("summary").GetString()!, method.Value.GetProperty("description").GetString()!));
            }
        }

        foreach (IGrouping<string, (string Tag, string Method, string Path, string Summary, string Description)> group in rows.GroupBy(r => r.Tag).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            html.Append("<h2>").Append(WebUtility.HtmlEncode(group.Key)).Append("</h2>\n<table><thead><tr><th>Method</th><th>Path</th><th>What it does</th></tr></thead><tbody>\n");
            foreach ((string _, string method, string path, string summary, string description) in group)
            {
                html.Append("<tr><td><code>").Append(method).Append("</code></td><td><code>").Append(WebUtility.HtmlEncode(path)).Append("</code></td><td>").Append(WebUtility.HtmlEncode(summary)).Append(" <small>").Append(WebUtility.HtmlEncode(description)).Append("</small></td></tr>\n");
                text.Append(method).Append(' ').Append(path).Append(' ').Append(summary).Append(' ');
            }

            html.Append("</tbody></table>\n");
        }

        yield return new DocPage("api-reference", "API reference", Section.Title, 0, html.ToString(), text.ToString());
    }
}
