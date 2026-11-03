using System.Diagnostics;
using System.Text.Json.Nodes;
using Ample.Language;

namespace Ample.Lsp.Tests;

public sealed class LspTests
{
    private const string Uri = "file:///song.amplet";
    private const string Song = "composition \"demo\" { ample 1  tempo 92  master-seed 424242 }\nmidi-clip chords {\n  chords Am F C G\n}\npiano piano pinned@2.0 {\n  from chords.notes -> notes\n}\nmixer mix {\n  from piano.audio -> audio1\n}\n";

    private static object At(int line, int ch) => new { textDocument = new { uri = Uri }, position = new { line, character = ch } };

    private static object Doc() => new { textDocument = new { uri = Uri } };

    [Fact]
    public async Task Initialize_advertises_exactly_what_is_implemented()
    {
        await using LspClient c = await LspClient.StartAsync();
        JsonNode caps = (await c.RequestAsync("initialize", new { capabilities = new { } }))!["capabilities"]!;
        foreach (string key in new[] { "hoverProvider", "definitionProvider", "referencesProvider", "documentSymbolProvider", "workspaceSymbolProvider", "foldingRangeProvider", "selectionRangeProvider", "documentFormattingProvider", "documentRangeFormattingProvider", "inlayHintProvider" })
        {
            Assert.True((bool)caps[key]!, key);
        }

        Assert.Equal(2, (int)caps["textDocumentSync"]!["change"]!);
        Assert.Equal("utf-16", (string?)caps["positionEncoding"]);
        Assert.NotNull(caps["semanticTokensProvider"]!["legend"]);
        Assert.True((bool)caps["renameProvider"]!["prepareProvider"]!);
    }

    [Fact]
    public async Task Diagnostics_carry_the_code_a_help_link_and_the_hint()
    {
        await using LspClient c = await LspClient.StartAsync("/help");
        JsonArray d = await c.DiagnosticsAfterOpenAsync(Uri, "midi-clip chords {\n}\n");
        JsonNode first = d.First(x => (string?)x!["code"] == "AMP1001")!;
        Assert.Equal(1, (int)first["severity"]!);
        Assert.Equal("/help#AMP1001", (string?)first["codeDescription"]!["href"]);
        Assert.Equal("ample", (string?)first["source"]);
    }

    [Fact]
    public async Task An_incremental_edit_updates_the_diagnostics()
    {
        await using LspClient c = await LspClient.StartAsync();
        JsonArray before = await c.DiagnosticsAfterOpenAsync(Uri, "midi-clip chords {\n}\n");
        Assert.Contains(before, x => (string?)x!["code"] == "AMP1001");
        await c.NotifyAsync("textDocument/didChange", new { textDocument = new { uri = Uri, version = 2 }, contentChanges = new[] { new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, text = "composition \"demo\" { ample 1  tempo 92  master-seed 424242 }\n" } } });
        JsonArray after = (JsonArray)(await c.NextAsync("textDocument/publishDiagnostics"))["params"]!["diagnostics"]!;
        Assert.DoesNotContain(after, x => (string?)x!["code"] == "AMP1001");
    }

    [Fact]
    public async Task Closing_a_document_clears_its_diagnostics()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, "midi-clip chords {\n}\n");
        await c.NotifyAsync("textDocument/didClose", Doc());
        JsonObject m = await c.NextAsync("textDocument/publishDiagnostics");
        Assert.Empty((JsonArray)m["params"]!["diagnostics"]!);
    }

    [Fact]
    public async Task Hover_describes_a_block_type_and_a_block()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, Song);
        JsonNode? onType = await c.RequestAsync("textDocument/hover", At(1, 2));
        Assert.Contains("MIDI", (string?)onType!["contents"]!["value"], StringComparison.OrdinalIgnoreCase);
        Assert.Null(await c.RequestAsync("textDocument/hover", At(0, 0)));
    }

    [Fact]
    public async Task Definition_references_symbols_folding_and_workspace_symbols_agree()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, Song);
        JsonNode def = (await c.RequestAsync("textDocument/definition", At(5, 9)))!;
        Assert.Equal(1, (int)def["range"]!["start"]!["line"]!);
        JsonArray refs = (JsonArray)(await c.RequestAsync("textDocument/references", new { textDocument = new { uri = Uri }, position = new { line = 1, character = 12 }, context = new { includeDeclaration = true } }))!;
        Assert.Equal(2, refs.Count);
        JsonArray symbols = (JsonArray)(await c.RequestAsync("textDocument/documentSymbol", Doc()))!;
        Assert.Equal(["chords", "piano", "mix"], symbols.Select(s => (string)s!["name"]!).ToArray());
        JsonArray folds = (JsonArray)(await c.RequestAsync("textDocument/foldingRange", Doc()))!;
        Assert.Equal(3, folds.Count);
        JsonArray ws = (JsonArray)(await c.RequestAsync("workspace/symbol", new { query = "pia" }))!;
        Assert.Single(ws);
    }

    [Fact]
    public async Task Completion_offers_block_types_as_snippets_and_ports_after_from()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, Song + "\n");
        JsonArray types = (JsonArray)(await c.RequestAsync("textDocument/completion", At(10, 0)))!["items"]!;
        JsonNode drums = types.First(i => (string?)i!["label"] == "drums")!;
        Assert.Equal(2, (int)drums["insertTextFormat"]!);
        Assert.StartsWith("drums ${1:drums} {", (string?)drums["insertText"], StringComparison.Ordinal);   // v1 header: no class and number
        JsonArray outs = (JsonArray)(await c.RequestAsync("textDocument/completion", At(5, 14)))!["items"]!;
        Assert.Contains(outs, i => (string?)i!["label"] == "notes");
    }

    [Fact]
    public async Task Completion_offers_structured_note_property_keys()
    {
        const string song = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip clip {\n  notes \"1:0:1:60:96;pa\n}\n";
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, song);
        JsonArray items = (JsonArray)(await c.RequestAsync("textDocument/completion", At(2, 22)))!["items"]!;
        Assert.Contains(items, i => (string?)i!["label"] == "pan=");
        Assert.DoesNotContain(items, i => (string?)i!["label"] == "fine=");
    }

    [Fact]
    public async Task Invalid_structured_note_property_publishes_the_existing_diagnostic()
    {
        const string song = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip clip {\n  notes \"1:0:1:60:96;pan=101\"\n}\n";
        await using LspClient c = await LspClient.StartAsync();
        JsonArray diagnostics = await c.DiagnosticsAfterOpenAsync(Uri, song);
        Assert.Contains(diagnostics, d => (string?)d!["code"] == "AMP2012" && ((string?)d["message"])!.Contains(";pan=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signature_help_lists_the_parameters_of_the_block_type()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, Song);
        JsonNode sig = (await c.RequestAsync("textDocument/signatureHelp", At(2, 4)))!;
        Assert.Contains("midi-clip(", (string?)sig["signatures"]![0]!["label"], StringComparison.Ordinal);
        Assert.Equal(1, (int)sig["activeParameter"]!);
    }

    [Fact]
    public async Task Formatting_returns_one_edit_only_when_the_text_changes()
    {
        await using LspClient c = await LspClient.StartAsync();
        string messy = "composition   \"demo\"  { ample 1  tempo 92  master-seed 424242 }\nmidi-clip   chords {\nchords Am F C G\n}\n";
        await c.DiagnosticsAfterOpenAsync(Uri, messy);
        JsonArray edits = (JsonArray)(await c.RequestAsync("textDocument/formatting", new { textDocument = new { uri = Uri }, options = new { tabSize = 2, insertSpaces = true } }))!;
        Assert.Single(edits);
        string formatted = (string)edits[0]!["newText"]!;
        Assert.Equal(Formatter.Format(messy, Root.Spec), formatted);
        await c.NotifyAsync("textDocument/didChange", new { textDocument = new { uri = Uri, version = 2 }, contentChanges = new[] { new { text = formatted } } });
        await c.NextAsync("textDocument/publishDiagnostics");
        Assert.Empty((JsonArray)(await c.RequestAsync("textDocument/formatting", new { textDocument = new { uri = Uri }, options = new { tabSize = 2 } }))!);
    }

    [Fact]
    public async Task Rename_changes_the_header_and_every_reference()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, Song);
        JsonNode prepare = (await c.RequestAsync("textDocument/prepareRename", At(5, 9)))!;
        Assert.Equal("chords", (string?)prepare["placeholder"]);
        JsonNode edit = (await c.RequestAsync("textDocument/rename", new { textDocument = new { uri = Uri }, position = new { line = 5, character = 9 }, newName = "harmony" }))!;
        JsonArray changes = (JsonArray)edit["changes"]![Uri]!;
        Assert.Equal(2, changes.Count);
        Assert.All(changes, ch => Assert.Equal("harmony", (string?)ch!["newText"]));
        Assert.Null(await c.RequestAsync("textDocument/prepareRename", At(0, 0)));
    }

    [Fact]
    public async Task Code_actions_offer_quick_fixes_fix_all_and_bake()
    {
        await using LspClient c = await LspClient.StartAsync();
        string text = null!;
        string? path = Directory.GetFiles(Path.Combine(Root.Dir, "spec-tests", "invalid"), "*.amplet").FirstOrDefault(f => Parser.Parse(File.ReadAllText(f), Root.Spec).Diagnostics.Any(d => d.Fix is not null));
        Assert.NotNull(path);
        text = File.ReadAllText(path);
        await c.DiagnosticsAfterOpenAsync(Uri, text);
        int lines = text.Split('\n').Length;
        JsonArray actions = (JsonArray)(await c.RequestAsync("textDocument/codeAction", new { textDocument = new { uri = Uri }, range = new { start = new { line = 0, character = 0 }, end = new { line = lines, character = 0 } }, context = new { diagnostics = Array.Empty<object>() } }))!;
        Assert.Contains(actions, a => (string?)a!["kind"] == "quickfix");
        Assert.Contains(actions, a => (string?)a!["kind"] == "source.fixAll");

        await c.DiagnosticsAfterOpenAsync(Uri, "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmaster master {\n}\n");
        JsonArray refactors = (JsonArray)(await c.RequestAsync("textDocument/codeAction", new { textDocument = new { uri = Uri }, range = new { start = new { line = 1, character = 0 }, end = new { line = 1, character = 0 } }, context = new { diagnostics = Array.Empty<object>() } }))!;
        JsonNode bake = refactors.First(a => ((string?)a!["title"])!.StartsWith("Bake", StringComparison.Ordinal))!;
        Assert.Contains("bake", (string?)bake["edit"]!["changes"]![Uri]![0]!["newText"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semantic_tokens_mark_types_names_parameters_and_comments()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, "# a comment\n" + Song);
        int[] data = [.. ((JsonArray)(await c.RequestAsync("textDocument/semanticTokens/full", Doc()))!["data"]!).Select(x => (int)x!)];
        Assert.True(data.Length > 0 && data.Length % 5 == 0);
        List<(int Line, int Start, int Len, int Type)> tokens = [];
        int line = 0, start = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            start = data[i] == 0 ? start + data[i + 1] : data[i + 1];
            tokens.Add((line, start, data[i + 2], data[i + 3]));
        }

        string[] types = SemanticTokensTypes();
        Assert.Contains(tokens, t => t.Line == 0 && types[t.Type] == "comment");
        Assert.Contains(tokens, t => t.Line == 2 && t.Start == 0 && types[t.Type] == "type");
        Assert.Contains(tokens, t => t.Line == 2 && t.Start == 10 && types[t.Type] == "variable");
        Assert.Contains(tokens, t => t.Line == 3 && types[t.Type] == "property");
        JsonArray ranged = (JsonArray)(await c.RequestAsync("textDocument/semanticTokens/range", new { textDocument = new { uri = Uri }, range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 5 } } }))!["data"]!;
        Assert.Equal(5, ranged.Count);
    }

    private static string[] SemanticTokensTypes() => ["keyword", "type", "variable", "property", "number", "string", "comment", "enumMember", "operator"];

    [Fact]
    public async Task Inlay_hints_show_the_type_of_each_connection()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, Song);
        JsonArray hints = (JsonArray)(await c.RequestAsync("textDocument/inlayHint", new { textDocument = new { uri = Uri }, range = new { start = new { line = 0, character = 0 }, end = new { line = 20, character = 0 } } }))!;
        Assert.Equal([" : midi", " : audio"], hints.Select(h => (string)h!["label"]!).ToArray());
    }

    [Fact]
    public async Task Selection_ranges_grow_from_the_word_to_the_whole_document()
    {
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, Song);
        JsonNode r = (await c.RequestAsync("textDocument/selectionRange", new { textDocument = new { uri = Uri }, positions = new[] { new { line = 2, character = 4 } } }))![0]!;
        int depth = 0;
        for (JsonNode? n = r; n is not null; n = n["parent"])
        {
            depth++;
        }

        Assert.Equal(4, depth);
    }

    [Fact]
    public async Task Pull_diagnostics_equal_the_pushed_ones()
    {
        await using LspClient c = await LspClient.StartAsync();
        JsonArray pushed = await c.DiagnosticsAfterOpenAsync(Uri, "midi-clip chords {\n}\n");
        JsonNode pulled = (await c.RequestAsync("textDocument/diagnostic", Doc()))!;
        Assert.Equal("full", (string?)pulled["kind"]);
        Assert.Equal(pushed.ToJsonString(), pulled["items"]!.ToJsonString());
    }

    [Fact]
    public async Task Unknown_methods_are_refused_and_a_cancelled_request_is_dropped()
    {
        await using LspClient c = await LspClient.StartAsync();
        JsonObject r = await c.RawAsync("textDocument/nothing", new { });
        Assert.Equal(-32601, (int)r["error"]!["code"]!);
        await c.DiagnosticsAfterOpenAsync(Uri, Song);
        await c.SendIdAsync(900, "textDocument/hover", At(1, 2), true);
        JsonObject answered = await c.ResponseAsync(900);
        Assert.NotNull(answered["result"]);
    }

    [Fact]
    public async Task Requests_after_shutdown_are_refused_and_exit_gives_code_zero()
    {
        LspClient c = await LspClient.StartAsync();
        await c.RequestAsync("shutdown", null);
        JsonObject r = await c.RawAsync("textDocument/hover", At(0, 0));
        Assert.Equal(-32600, (int)r["error"]!["code"]!);
        await c.DisposeAsync();
        Assert.Equal(0, c.ExitCode);
    }

    [Fact]
    public async Task The_server_survives_a_few_hundred_random_edits()
    {
        await using LspClient c = await LspClient.StartAsync();
        string text = Song;
        await c.DiagnosticsAfterOpenAsync(Uri, text);
        Random rng = new(1234);
        string[] pieces = ["{", "}", "\n", "from ", "->", "project", "\"", "#", "x", "  ", "stable@1.0", "😀", "layout a 1 2\n"];
        for (int i = 1; i <= 300; i++)
        {
            int line = rng.Next(0, 14), ch = rng.Next(0, 30);
            int endLine = line + rng.Next(0, 2), endCh = ch + rng.Next(0, 5);
            string insert = rng.Next(3) == 0 ? string.Empty : pieces[rng.Next(pieces.Length)];
            await c.NotifyAsync("textDocument/didChange", new { textDocument = new { uri = Uri, version = i + 1 }, contentChanges = new[] { new { range = new { start = new { line, character = ch }, end = new { line = endLine, character = endCh } }, text = insert } } });
            if (i % 25 == 0)
            {
                foreach (string method in new[] { "textDocument/hover", "textDocument/completion", "textDocument/signatureHelp" })
                {
                    JsonObject r = await c.RawAsync(method, At(rng.Next(0, 14), rng.Next(0, 30)));
                    Assert.True(r["error"] is null || (int)r["error"]!["code"]! != -32603, $"{method}: {r["error"]}");
                }

                JsonObject sym = await c.RawAsync("textDocument/documentSymbol", Doc());
                Assert.Null(sym["error"]);
                Assert.Null((await c.RawAsync("textDocument/semanticTokens/full", Doc()))["error"]);
            }
        }
    }

    [Fact]
    public async Task Budgets_for_a_five_thousand_line_song()
    {
        List<string> parts = ["composition \"big\" { ample 1  tempo 100  master-seed 1 }"];
        for (int i = 0; i < 400; i++)
        {
            parts.Add($"drums d{i} at 1 for 16 {{\n  kit kit/tape\n  pattern beat-A\n  bars 16\n  from p{i}.audio -> trigger\n}}\npiano p{i} pinned@2.0 {{\n  set felt-piano\n  velocity natural\n}}\nmidi-clip c{i} {{\n  chords Am F C G\n}}\nbass b{i} {{\n  from c{i}.notes -> notes\n}}\n");
        }

        string big = string.Join('\n', parts);
        Assert.True(big.Count(ch => ch == '\n') > 5000);
        await using LspClient c = await LspClient.StartAsync();
        await c.DiagnosticsAfterOpenAsync(Uri, big);
        Stopwatch sw = Stopwatch.StartNew();
        await c.NotifyAsync("textDocument/didChange", new { textDocument = new { uri = Uri, version = 2 }, contentChanges = new[] { new { range = new { start = new { line = 3, character = 0 }, end = new { line = 3, character = 0 } }, text = "  " } } });
        await c.NextAsync("textDocument/publishDiagnostics");
        long diag = sw.ElapsedMilliseconds;
        sw.Restart();
        await c.RequestAsync("textDocument/completion", At(2, 2));
        long completion = sw.ElapsedMilliseconds;
        Console.WriteLine($"5,000-line song: diagnostics after an edit {diag} ms, completion {completion} ms");
        Assert.True(diag < 1000, $"diagnostics took {diag} ms");
        Assert.True(completion < 500, $"completion took {completion} ms");
    }
}
