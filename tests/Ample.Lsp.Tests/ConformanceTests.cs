using System.Text.Json.Nodes;
using Ample.Language;

namespace Ample.Lsp.Tests;

public sealed class ConformanceTests
{
    private static readonly string[] Folders = ["valid", "invalid", "recovery"];

    public static IEnumerable<object[]> Files() =>
        Folders.SelectMany(dir => Directory.GetFiles(Path.Combine(Root.Dir, "spec-tests", dir), "*.amplet").Order(StringComparer.Ordinal)).Select(f => new object[] { Path.GetRelativePath(Root.Dir, f).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(Files))]
    public async Task The_server_reports_the_diagnostics_of_the_library_for_every_corpus_file(string relative)
    {
        string path = Path.Combine(Root.Dir, relative);
        string text = File.ReadAllText(path);
        await using LspClient c = await LspClient.StartAsync();
        JsonArray got = await c.DiagnosticsAfterOpenAsync("file:///corpus.amplet", text);
        AmpleDocument parsed = Parser.Parse(text, Root.Spec, ParseOptions.Editor);
        Assert.Equal(parsed.Diagnostics.Select(d => d.Code), got.Select(d => (string?)d!["code"]));
        Assert.Equal(parsed.Diagnostics.Select(d => d.Line - 1), got.Select(d => (int)d!["range"]!["start"]!["line"]!));

        string expectedFile = Path.ChangeExtension(path, ".expected-diagnostics.json");
        if (File.Exists(expectedFile))
        {
            JsonArray expected = (JsonArray)JsonNode.Parse(File.ReadAllText(expectedFile))!;
            // AMP2013 is reported only by the editors (ParseOptions.Editor); the expected files describe the library without it.
            Assert.Equal(expected.Select(e => (string?)e!["code"]), got.Select(d => (string?)d!["code"]).Where(code => code != "AMP2013"));
        }

        if (relative.Contains("/recovery/", StringComparison.Ordinal))
        {
            JsonArray symbols = (JsonArray)(await c.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri = "file:///corpus.amplet" } }))!;
            Assert.Equal(Queries.DocumentSymbols(parsed).Count, symbols.Count);
            Assert.NotNull(await c.RequestAsync("textDocument/completion", new { textDocument = new { uri = "file:///corpus.amplet" }, position = new { line = 0, character = 0 } }));
            Assert.Null((await c.RawAsync("textDocument/semanticTokens/full", new { textDocument = new { uri = "file:///corpus.amplet" } }))["error"]);
        }
    }
}
