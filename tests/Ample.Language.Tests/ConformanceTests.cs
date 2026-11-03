using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>Locates the AMPLE folder and loads the spec data and corpus files.</summary>
internal static class Corpus
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "spec-tests")) && Directory.Exists(Path.Combine(dir.FullName, "spec")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("AMPLE folder not found");
    });

    private static readonly Lazy<SpecData> SpecValue = new(() =>
    {
        string spec = Path.Combine(RootPath.Value, "spec");
        Dictionary<string, string> files = Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText);
        return SpecData.FromFiles(files);
    });

    public static string Root => RootPath.Value;

    public static SpecData Spec => SpecValue.Value;

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    public static IEnumerable<object[]> Cases(string folder, string pattern) =>
        Directory.GetFiles(Path.Combine(Root, "spec-tests", folder), pattern).OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}

[Trait("Category", "Conformance")]
public sealed class ConformanceTests
{
    public static IEnumerable<object[]> Valid() => Corpus.Cases("valid", "*.amplet");

    public static IEnumerable<object[]> Invalid() => Corpus.Cases("invalid", "*.amplet");

    public static IEnumerable<object[]> Recovery() => Corpus.Cases("recovery", "*.amplet");

    public static IEnumerable<object[]> FormatPairs() => Corpus.Cases("format", "*.in.amplet");

    public static IEnumerable<object[]> RoundTrips() => Corpus.Cases("roundtrip", "*.amplet");

    private static string SeverityWord(Severity s) => s.ToString().ToLowerInvariant();

    private static List<string> Describe(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Col).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal)
            .Select(d => $"{d.Code}|{SeverityWord(d.Severity)}|{d.Line}:{d.Col}-{d.EndCol}|{d.Message}")];

    private static List<string> Describe(JsonElement expected) =>
        [.. expected.EnumerateArray()
            .Select(e => (Code: e.GetProperty("code").GetString()!, Sev: e.GetProperty("severity").GetString()!, Line: e.GetProperty("line").GetInt32(), Col: e.GetProperty("col").GetInt32(), End: e.GetProperty("endCol").GetInt32(), Msg: e.TryGetProperty("message", out JsonElement m) ? m.GetString()! : string.Empty))
            .OrderBy(e => e.Line).ThenBy(e => e.Col).ThenBy(e => e.Code, StringComparer.Ordinal).ThenBy(e => e.Msg, StringComparer.Ordinal)
            .Select(e => $"{e.Code}|{e.Sev}|{e.Line}:{e.Col}-{e.End}|{e.Msg}")];

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_programs_have_no_errors_and_match_the_expected_summary(string file)
    {
        string text = Corpus.Read("spec-tests/valid/" + file);
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        using JsonDocument expected = JsonDocument.Parse(Corpus.Read("spec-tests/valid/" + file.Replace(".amplet", ".expected.json", StringComparison.Ordinal)));

        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        JsonElement project = expected.RootElement.GetProperty("project");
        Assert.NotNull(doc.Project);
        Assert.Equal(project.GetProperty("name").GetString(), doc.Project!.Name);
        Assert.Equal(project.GetProperty("tempo").GetDouble(), doc.Project.Tempo);
        Assert.Equal(project.GetProperty("masterSeed").GetUInt64(), doc.Project.MasterSeed);
        Assert.Equal(
            [.. expected.RootElement.GetProperty("blocks").EnumerateArray().Select(b => $"{b.GetProperty("type").GetString()} {b.GetProperty("name").GetString()} {b.GetProperty("class").GetString()}@{b.GetProperty("version").GetString()}")],
            [.. doc.Blocks.Select(b => $"{b.Type} {b.Name} {b.Class}@{b.Version}")]);
        Assert.Equal(Describe(expected.RootElement.GetProperty("diagnostics")), Describe(doc.Diagnostics));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_programs_produce_exactly_the_expected_diagnostics(string file)
    {
        string text = Corpus.Read("spec-tests/invalid/" + file);
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        using JsonDocument expected = JsonDocument.Parse(Corpus.Read("spec-tests/invalid/" + file.Replace(".amplet", ".expected-diagnostics.json", StringComparison.Ordinal)));

        Assert.Equal(Describe(expected.RootElement), Describe(doc.Diagnostics));
        Assert.Contains(doc.Diagnostics, d => file.StartsWith(d.Code, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Recovery))]
    public void Broken_input_keeps_the_understood_blocks_and_reports_the_expected_codes(string file)
    {
        AmpleDocument doc = Parser.Parse(Corpus.Read("spec-tests/recovery/" + file), Corpus.Spec);
        using JsonDocument expected = JsonDocument.Parse(Corpus.Read("spec-tests/recovery/" + file.Replace(".amplet", ".expected.json", StringComparison.Ordinal)));

        Assert.Equal(
            [.. expected.RootElement.GetProperty("blocks").EnumerateArray().Select(b => $"{b.GetProperty("type").GetString()} {b.GetProperty("name").GetString()}")],
            [.. doc.Blocks.Where(b => !b.Skipped).Select(b => $"{b.Type} {b.Name}")]);
        Assert.Equal(
            [.. expected.RootElement.GetProperty("diagnosticCodes").EnumerateArray().Select(c => c.GetString()!).Order(StringComparer.Ordinal)],
            [.. doc.Diagnostics.Select(d => d.Code).Order(StringComparer.Ordinal)]);
    }

    [Theory]
    [MemberData(nameof(FormatPairs))]
    public void Formatting_gives_the_expected_output_and_is_idempotent(string file)
    {
        string input = Corpus.Read("spec-tests/format/" + file);
        string expected = Corpus.Read("spec-tests/format/" + file.Replace(".in.amplet", ".out.amplet", StringComparison.Ordinal));

        string once = Formatter.Format(input, Corpus.Spec);
        Assert.Equal(Corpus.Normalize(expected), once);
        Assert.Equal(once, Formatter.Format(once, Corpus.Spec));
    }

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void Canonical_files_round_trip_byte_for_byte(string file)
    {
        string text = Corpus.Read("spec-tests/roundtrip/" + file);
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        Assert.Equal(text, doc.ToText());
        Assert.Equal(text, Formatter.Format(text, Corpus.Spec));
    }
}
