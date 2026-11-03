using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>The golden vectors of the note pipelines: the very cases the JavaScript evaluator runs (web/lib/test/note-pipeline.test.mjs); both must give the same notes, spans and problems.</summary>
[Trait("Category", "Conformance")]
public sealed class NotePipelineCorpusTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(Corpus.Root, "spec-tests", "note-pipelines"), "*.json").Where(f => Path.GetFileName(f) != "seeds.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray()) yield return new object[] { Path.GetFileName(file), c.GetProperty("id").GetString()! };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_evaluator_gives_what_the_corpus_expects(string file, string id)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Root, "spec-tests", "note-pipelines", file)));
        JsonElement defaults = doc.RootElement.GetProperty("defaults");
        JsonElement c = doc.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        Dictionary<string, PipeValue> sources = [];
        foreach (JsonProperty p in defaults.GetProperty("sources").EnumerateObject()) sources[p.Name] = Read(p.Value.GetString()!);
        if (c.TryGetProperty("sources", out JsonElement own)) foreach (JsonProperty p in own.EnumerateObject()) sources[p.Name] = Read(p.Value.GetString()!);
        string tempo = c.TryGetProperty("tempo", out JsonElement t) ? t.GetRawText() : defaults.GetProperty("tempo").GetRawText();
        NotePipeline.Context context = new()
        {
            Tempo = tempo,
            Limit = c.TryGetProperty("limit", out JsonElement l) ? l.GetInt32() : NotePipeline.MaxEvents,
            MasterSeed = ulong.Parse(c.TryGetProperty("master", out JsonElement master) ? master.GetString()! : defaults.TryGetProperty("master", out JsonElement defaultMaster) ? defaultMaster.GetString()! : "0", System.Globalization.CultureInfo.InvariantCulture),
            Streams = c.TryGetProperty("streams", out JsonElement streams) ? streams.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : null,
            Source = name => sources.TryGetValue(name, out PipeValue? v) ? new NotePipeline.SourceInfo(v, "test:" + name, null) : new NotePipeline.SourceInfo(null, null, $"There is no block \"{name}\""),
        };
        PipeResult result = NotePipeline.Evaluate(c.GetProperty("expr").GetString()!, context);
        JsonElement expect = c.GetProperty("expect");
        List<string> expectedErrors = expect.TryGetProperty("errors", out JsonElement errors) ? [.. errors.EnumerateArray().Select(e => e.GetString()!)] : [];
        List<string> expectedWarnings = expect.TryGetProperty("warnings", out JsonElement warnings) ? [.. warnings.EnumerateArray().Select(e => e.GetString()!)] : [];
        Assert.Equal(expectedErrors, [.. result.Errors.Select(e => e.Id)]);
        Assert.Equal(expectedWarnings, [.. result.Warnings.Select(e => e.Id)]);
        if (expectedErrors.Count > 0)
        {
            Assert.Null(result.Notes);
            return;
        }

        Assert.Equal(expect.GetProperty("notes").GetString(), result.Notes);
        Assert.Equal(expect.GetProperty("span").GetInt64(), result.Span);
    }

    private static PipeValue Read(string text)
    {
        List<PipeNote> events = NotePipeline.ReadNotes(text, []);
        long end = events.Count == 0 ? 0 : events.Max(e => e.At + e.Len);
        return new PipeValue { Events = events, Span = Math.Max(1, (end + NotePipeline.Bar - 1) / NotePipeline.Bar) * NotePipeline.Bar };
    }
}
