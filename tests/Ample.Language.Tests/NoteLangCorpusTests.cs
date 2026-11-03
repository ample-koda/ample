using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>The golden cases of the language layer: the very cases the JavaScript twin runs (web/lib/test/note-lang.test.mjs); both must give the same notes, issues and dynamic status.</summary>
[Trait("Category", "Conformance")]
public sealed class NoteLangCorpusTests
{
    private static string File => Path.Combine(Corpus.Root, "spec-tests", "note-lang", "core.json");

    public static IEnumerable<object[]> Cases()
    {
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
        foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray()) yield return new object[] { c.GetProperty("id").GetString()! };
    }

    private static string[] Strings(JsonElement c, string name) => c.TryGetProperty(name, out JsonElement a) ? [.. a.EnumerateArray().Select(x => x.GetString()!)] : [];

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_language_layer_gives_what_the_corpus_expects(string id)
    {
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
        JsonElement defaults = doc.RootElement.GetProperty("defaults");
        JsonElement c = doc.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        string theme = defaults.GetProperty("theme").GetString()!;
        string Fill(string s) => s.Replace("${T}", theme, StringComparison.Ordinal);
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build(Strings(c, "decls").Select((d, i) => (d, i * 20)), issues);
        Assert.Empty(issues);
        List<PipeNote> events = NotePipeline.ReadNotes(theme, []);
        PipeValue themeValue = new() { Events = events, Span = 4 * NotePipeline.Beat };
        LangLimits limits = new();
        if (c.TryGetProperty("limits", out JsonElement l))
        {
            limits = new LangLimits(l.TryGetProperty("loopIterations", out JsonElement a) ? a.GetInt32() : limits.LoopIterations, l.TryGetProperty("whileDefault", out JsonElement b) ? b.GetInt32() : limits.WhileDefault, l.TryGetProperty("stackDepth", out JsonElement s) ? s.GetInt32() : limits.StackDepth, l.TryGetProperty("fuel", out JsonElement f) ? f.GetInt64() : limits.Fuel, l.TryGetProperty("memoryMb", out JsonElement m) ? m.GetInt32() : limits.MemoryMb);
        }

        DateTime clock = DateTime.Parse(c.TryGetProperty("clock", out JsonElement cl) ? cl.GetString()! : defaults.GetProperty("clock").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        NotePipeline.Context Context(bool lang) => new()
        {
            Tempo = defaults.GetProperty("tempo").GetRawText(),
            Helpers = lang ? table : null,
            Clock = () => clock,
            Limits = limits,
            Capabilities = c.TryGetProperty("denied", out JsonElement denied) || c.TryGetProperty("envAllow", out _) ? CapabilityPolicy.Denying(c.TryGetProperty("denied", out JsonElement deniedNow) ? deniedNow.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : [], c.TryGetProperty("envAllow", out JsonElement allow) ? [.. allow.EnumerateArray().Select(x => x.GetString()!)] : null) : null,
            Environment = c.TryGetProperty("env", out JsonElement env) ? name => env.TryGetProperty(name, out JsonElement v) ? v.GetString() : null : null,
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(themeValue, "test:theme", null) : new NotePipeline.SourceInfo(null, null, $"There is no block \"{name}\""),
        };
        PipeResult result = NotePipeline.Evaluate(Fill(c.GetProperty("expr").GetString()!), Context(true));
        Assert.Equal(Strings(c, "errors"), result.Errors.Select(e => e.Id).ToArray());
        Assert.Equal(Strings(c, "warnings"), result.Warnings.Select(w => w.Id).ToArray());
        if (Strings(c, "errors").Length > 0) { Assert.Null(result.Notes); return; }
        if (c.TryGetProperty("sameAs", out JsonElement same))
        {
            PipeResult plain = NotePipeline.Evaluate(Fill(same.GetString()!), Context(false));
            Assert.Empty(plain.Errors);
            Assert.Equal(plain.Notes, result.Notes);
            Assert.Equal(plain.Span, result.Span);
        }

        if (c.TryGetProperty("count", out JsonElement count)) Assert.Equal(count.GetInt32(), result.Notes!.Split(' ').Length);
        if (c.TryGetProperty("dynamic", out JsonElement dynamic)) Assert.Equal(dynamic.GetBoolean(), result.Dynamic);
    }
}
