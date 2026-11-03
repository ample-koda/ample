using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>The golden cases of the static checks of a helper body: the very cases the JavaScript twin runs (web/lib/test/note-lang.test.mjs), so what the browser finds and what the server finds cannot drift apart.</summary>
[Trait("Category", "Conformance")]
public sealed class NoteLangStaticCorpusTests
{
    private static string File => Path.Combine(Corpus.Root, "spec-tests", "note-lang", "static.json");

    public static IEnumerable<object[]> Cases()
    {
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray()) yield return new object[] { c.GetProperty("id").GetString()! };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_static_checks_find_what_the_corpus_expects(string id)
    {
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
        HashSet<string> ids = [.. doc.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!)];
        JsonElement c = doc.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        List<HelperIssue> issues = [];
        _ = NotePipeline.HelperTable.Build(c.GetProperty("decls").EnumerateArray().Select((d, i) => (d.GetString()!, i * 20)), issues);
        string[] found = [.. issues.Where(i => ids.Contains(i.Issue.Id)).Select(i => i.Issue.Id + (i.Issue.Strict ? "!" : string.Empty)).Order(StringComparer.Ordinal)];
        string[] expected = [.. c.GetProperty("issues").EnumerateArray().Select(x => x.GetString()!).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, found);
    }
}
