using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Ample.Language;

namespace Ample.Lsp;

/// <summary>
/// The AMPLE language server (Language Server Protocol 3.17). It holds no language logic: every answer comes from
/// <c>Ample.Language</c> (parser, queries, formatter, edits), so the editor, the CLI and the UI never disagree.
/// Requests are handled one after another, which also keeps results in order; a cancelled request that has not started is dropped.
/// </summary>
public sealed class LspServer(SpecData spec, string helpBase = "/help", IReadOnlyList<Ample.Language.SettingHint>? settingHints = null, Func<string, string?>? readFile = null, Func<IReadOnlyList<BlockNode>, IReadOnlyList<string>, IReadOnlyList<Ample.Language.Diagnostic>>? preflight = null, Func<string, (IReadOnlyList<ComponentDefinition> Components, IReadOnlyList<Ample.Language.Diagnostic> Issues)>? components = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly Dictionary<string, Doc> docs = [];
    private readonly HashSet<string> cancelled = [];
    private ILspChannel channel = null!;
    private string help = helpBase;
    private bool shutdownRequested;
    private bool exiting;

    /// <summary>Runs until the editor sends <c>exit</c> or the channel closes. Returns the process exit code (0 after a clean shutdown).</summary>
    public async Task<int> RunAsync(ILspChannel connection, CancellationToken cancellationToken)
    {
        channel = connection;
        Channel<JsonObject> queue = Channel.CreateUnbounded<JsonObject>();
        Task reader = Task.Run(async () =>
        {
            try
            {
                for (; ; )
                {
                    string? text = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (text is null)
                    {
                        break;
                    }

                    JsonObject? msg;
                    try
                    {
                        msg = JsonNode.Parse(text) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        await SendAsync(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (msg is null)
                    {
                        continue;
                    }

                    if ((string?)msg["method"] == "$/cancelRequest" && msg["params"]?["id"] is JsonNode cid)
                    {
                        lock (cancelled)
                        {
                            cancelled.Add(cid.ToJsonString());
                        }

                        continue;
                    }

                    await queue.Writer.WriteAsync(msg, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }, cancellationToken);

        try
        {
            await foreach (JsonObject msg in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await HandleAsync(msg, cancellationToken).ConfigureAwait(false);
                if (exiting)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        await Task.WhenAny(reader, Task.Delay(50, CancellationToken.None)).ConfigureAwait(false);
        return shutdownRequested ? 0 : 1;
    }

    private async Task HandleAsync(JsonObject msg, CancellationToken ct)
    {
        string? method = (string?)msg["method"];
        JsonNode? id = msg["id"];
        JsonNode? p = msg["params"];
        if (method is null)
        {
            return; // an answer from the editor to a request of ours (there are none yet)
        }

        try
        {
            if (id is null)
            {
                await NotificationAsync(method, p, ct).ConfigureAwait(false);
                return;
            }

            bool wasCancelled;
            lock (cancelled)
            {
                wasCancelled = cancelled.Remove(id.ToJsonString());
            }

            if (wasCancelled)
            {
                await ErrorAsync(id, -32800, "Request cancelled", ct).ConfigureAwait(false);
                return;
            }

            if (shutdownRequested && method != "shutdown")
            {
                await ErrorAsync(id, -32600, "The server is shutting down.", ct).ConfigureAwait(false);
                return;
            }

            (bool known, object? result) = Request(method, p);
            if (!known)
            {
                await ErrorAsync(id, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
                return;
            }

            await SendAsync(new { jsonrpc = "2.0", id = id.DeepClone(), result }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (id is not null)
            {
                await ErrorAsync(id, -32603, "Internal error: " + ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    private ValueTask ErrorAsync(JsonNode id, int code, string message, CancellationToken ct) => SendAsync(new { jsonrpc = "2.0", id = id.DeepClone(), error = new { code, message } }, ct);

    private ValueTask SendAsync(object message, CancellationToken ct) => channel.WriteAsync(JsonSerializer.Serialize(message, Json), ct);

    private ValueTask NotifyAsync(string method, object parameters, CancellationToken ct) => SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, ct);

    /* ---------- notifications ---------- */
    private async Task NotificationAsync(string method, JsonNode? p, CancellationToken ct)
    {
        switch (method)
        {
            case "exit":
                exiting = true;
                break;
            case "textDocument/didOpen":
                {
                    string uri = Uri(p);
                    JsonNode item = p!["textDocument"]!;
                    docs[uri] = new Doc((string?)item["text"] ?? string.Empty, (int?)item["version"] ?? 0) { ReadFile = readFile, Preflight = preflight, Components = components };
                    await PublishAsync(uri, ct).ConfigureAwait(false);
                    break;
                }

            case "textDocument/didChange":
                {
                    string uri = Uri(p);
                    if (!docs.TryGetValue(uri, out Doc? d))
                    {
                        break;
                    }

                    string text = d.Text;
                    foreach (JsonNode? change in p!["contentChanges"]!.AsArray())
                    {
                        JsonNode? range = change!["range"];
                        text = range is null
                            ? (string?)change["text"] ?? string.Empty
                            : Positions.Apply(text, (int?)range["start"]!["line"], (int)range["start"]!["character"]!, (int)range["end"]!["line"]!, (int)range["end"]!["character"]!, (string?)change["text"] ?? string.Empty);
                    }

                    d.Set(text, (int?)p["textDocument"]!["version"] ?? d.Version + 1);
                    await PublishAsync(uri, ct).ConfigureAwait(false);
                    break;
                }

            case "textDocument/didSave":
                {
                    string uri = Uri(p);
                    if (docs.ContainsKey(uri))
                    {
                        await PublishAsync(uri, ct).ConfigureAwait(false);
                    }

                    break;
                }

            case "textDocument/didClose":
                {
                    string uri = Uri(p);
                    docs.Remove(uri);
                    await NotifyAsync("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() }, ct).ConfigureAwait(false);
                    break;
                }

            default:
                break; // initialized, workspace/didChangeConfiguration, didChangeWatchedFiles, $/setTrace and the rest need no action yet
        }
    }

    private async Task PublishAsync(string uri, CancellationToken ct)
    {
        Doc d = docs[uri];
        await NotifyAsync("textDocument/publishDiagnostics", new { uri, version = d.Version, diagnostics = DiagnosticsOf(uri, d) }, ct).ConfigureAwait(false);
    }

    private object[] DiagnosticsOf(string uri, Doc d)
    {
        AmpleDocument parsed = d.Parse(spec);
        string[] lines = Positions.Lines(d.Text);
        return
        [
            .. parsed.Diagnostics.Select(x =>
            {
                string lt = LineText(lines, x.Line);
                return (object)new
                {
                    range = Positions.RangeOf(x.Line - 1, Positions.CodePointsToUtf16(lt, x.Col - 1), x.Line - 1, Positions.CodePointsToUtf16(lt, x.EndCol - 1)),
                    severity = x.Severity switch { Severity.Error => 1, Severity.Warning => 2, _ => 3 },
                    code = x.Code,
                    codeDescription = new { href = $"{help}#{x.Code}" },
                    source = "ample",
                    message = x.Hint.Length > 0 ? x.Message + "\n" + x.Hint : x.Message,
                    relatedInformation = x.Related is null ? null : new[] { new { location = new { uri, range = Positions.RangeOf(x.Related.Line - 1, 0, x.Related.Line - 1, 0) }, message = x.Related.Message } },
                };
            }),
        ];
    }

    /* ---------- requests ---------- */
    private (bool Known, object? Result) Request(string method, JsonNode? p)
    {
        switch (method)
        {
            case "initialize":
                if ((string?)p?["initializationOptions"]?["helpBase"] is string hb && hb.Length > 0)
                {
                    help = hb;
                }

                return (true, Capabilities());
            case "shutdown":
                shutdownRequested = true;
                return (true, null);
            case "textDocument/hover":
                return (true, Hover(p));
            case "textDocument/definition":
                return (true, Definition(p));
            case "textDocument/references":
                return (true, References(p));
            case "textDocument/documentSymbol":
                return (true, DocumentSymbols(p));
            case "workspace/symbol":
                return (true, WorkspaceSymbols((string?)p?["query"] ?? string.Empty));
            case "textDocument/foldingRange":
                return (true, Folding(p));
            case "textDocument/completion":
                return (true, Completion(p));
            case "textDocument/signatureHelp":
                return (true, SignatureHelp(p));
            case "textDocument/formatting":
            case "textDocument/rangeFormatting":
                return (true, Formatting(p));
            case "textDocument/prepareRename":
                return (true, PrepareRename(p));
            case "textDocument/rename":
                return (true, Rename(p));
            case "textDocument/codeAction":
                return (true, CodeActions(p));
            case "textDocument/semanticTokens/full":
                return (true, new { data = SemanticData(p, null) });
            case "textDocument/semanticTokens/range":
                return (true, new { data = SemanticData(p, p?["range"]) });
            case "textDocument/inlayHint":
                return (true, InlayHints(p));
            case "textDocument/selectionRange":
                return (true, SelectionRanges(p));
            case "textDocument/diagnostic":
                return (true, new { kind = "full", items = DocOf(p) is Doc d ? DiagnosticsOf(Uri(p), d) : [] });
            default:
                return (false, null);
        }
    }

    private object Capabilities() => new
    {
        capabilities = new
        {
            positionEncoding = "utf-16",
            textDocumentSync = new { openClose = true, change = 2, save = new { includeText = false } },
            hoverProvider = true,
            definitionProvider = true,
            referencesProvider = true,
            documentSymbolProvider = true,
            workspaceSymbolProvider = true,
            foldingRangeProvider = true,
            selectionRangeProvider = true,
            completionProvider = new { triggerCharacters = new[] { " ", ".", ">", "@" }, resolveProvider = false },
            signatureHelpProvider = new { triggerCharacters = new[] { " " } },
            documentFormattingProvider = true,
            documentRangeFormattingProvider = true,
            renameProvider = new { prepareProvider = true },
            codeActionProvider = new { codeActionKinds = new[] { "quickfix", "refactor", "source.fixAll" } },
            semanticTokensProvider = new { legend = new { tokenTypes = SemanticTokens.Types, tokenModifiers = SemanticTokens.Modifiers }, full = true, range = true },
            inlayHintProvider = true,
            diagnosticProvider = new { interFileDependencies = false, workspaceDiagnostics = false },
        },
        serverInfo = new { name = "ample-lsp", version = spec.Version },
    };

    private static string LineText(string[] lines, int oneBasedLine) => oneBasedLine >= 1 && oneBasedLine <= lines.Length ? lines[oneBasedLine - 1] : string.Empty;

    private static string Uri(JsonNode? p) => (string?)p?["textDocument"]?["uri"] ?? string.Empty;

    private Doc? DocOf(JsonNode? p) => docs.TryGetValue(Uri(p), out Doc? d) ? d : null;

    private static (int Line, int Col) Pos(JsonNode? p) => ((int)p!["position"]!["line"]! + 1, (int)p["position"]!["character"]! + 1);

    private object? Hover(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return null;
        }

        (int line, int col) = Pos(p);
        string? text = Queries.HoverAt(d.Parse(spec), spec, line, col);
        return text is null ? null : new { contents = new { kind = "markdown", value = text } };
    }

    private object? Definition(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return null;
        }

        (int line, int col) = Pos(p);
        AmpleDocument parsed = d.Parse(spec);
        Location? loc = Queries.FindDefinition(parsed, line, col);
        if (loc is not null) return new { uri = Uri(p), range = Positions.Range(loc) };
        // the type of a component instance: the declaration in this song, or in the imported file (file is its path in the project)
        if (Queries.ComponentTypeAt(parsed, line, col) is { } component)
        {
            Location at = new(component.HeaderLine + 1, 1, 1 + component.DeclaredName.Length);
            string? file = parsed.ImportedComponents.Contains(component) ? component.Origin : null;
            return new { uri = Uri(p), range = Positions.Range(at), file };
        }

        return null;
    }

    private object[] References(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return [];
        }

        (int line, int col) = Pos(p);
        bool declaration = (bool?)p!["context"]?["includeDeclaration"] ?? true;
        return [.. Queries.FindReferences(d.Parse(spec), line, col).Skip(declaration ? 0 : 1).Select(l => (object)new { uri = Uri(p), range = Positions.Range(l) })];
    }

    private object[] DocumentSymbols(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return [];
        }

        string[] lines = Positions.Lines(d.Text);
        return
        [
            .. Queries.DocumentSymbols(d.Parse(spec)).Select(s => (object)new
            {
                name = s.Name,
                detail = s.Type,
                kind = 19,
                range = Positions.RangeOf(s.Header.Line - 1, 0, s.LastLine - 1, lines[Math.Min(s.LastLine - 1, lines.Length - 1)].Length),
                selectionRange = Positions.Range(s.Header),
            }),
        ];
    }

    private object[] WorkspaceSymbols(string query) =>
    [
        .. docs.SelectMany(kv => Queries.DocumentSymbols(kv.Value.Parse(spec)).Where(s => query.Length == 0 || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(s => (object)new { name = s.Name, kind = 19, containerName = s.Type, location = new { uri = kv.Key, range = Positions.Range(s.Header) } })),
    ];

    private object[] Folding(JsonNode? p) => DocOf(p) is Doc d
        ? [.. Queries.DocumentSymbols(d.Parse(spec)).Where(s => s.LastLine - 2 >= s.Header.Line - 1).Select(s => (object)new { startLine = s.Header.Line - 1, endLine = s.LastLine - 2, kind = "region" })]
        : [];

    private object Completion(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return new { isIncomplete = false, items = Array.Empty<object>() };
        }

        (int line, int col) = Pos(p);
        IReadOnlyList<Ample.Language.CompletionItem> items = Queries.CompletionAt(d.Parse(spec), spec, line, col, settingHints);
        return new { isIncomplete = false, items = items.Select(Item).ToArray() };
    }

    private object Item(Ample.Language.CompletionItem c)
    {
        int kind = c.Kind switch { "block-type" => 7, "parameter" => 10, "block" => 6, "output" or "input" or "lane" => 5, "keyword" => 14, "class" => 20, _ => 1 };
        if (c.Kind == "block-type" && spec.Templates.TryGetValue(c.Label, out TemplateSpec? t))
        {
            StringBuilder sb = new();
            sb.Append(c.Label).Append(" ${1:").Append(c.Label).Append("} {\n");
            int n = 2;
            foreach (KeyValuePair<string, string> prm in t.Parameters.Where(p => p.Value.Length > 0))
            {
                sb.Append("  ").Append(prm.Key).Append(" ${").Append(n++).Append(':').Append(prm.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal)).Append("}\n");
            }

            sb.Append("}$0");
            return new { label = c.Label, kind, detail = c.Detail, insertText = sb.ToString(), insertTextFormat = 2 };
        }

        return c.Insert is null ? (object)new { label = c.Label, kind, detail = c.Detail } : new { label = c.Label, kind, detail = c.Detail, insertText = c.Insert, insertTextFormat = 1 };
    }

    private object? SignatureHelp(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return null;
        }

        (int line, int column) = Pos(p);
        AmpleDocument parsed = d.Parse(spec);
        string[] textLines = Positions.Lines(d.Text);
        string before = string.Join('\n', textLines.Take(Math.Max(0, line - 1))) + (line > 1 ? "\n" : string.Empty) + (line - 1 < textLines.Length ? textLines[line - 1][..Math.Min(Math.Max(0, column - 1), textLines[line - 1].Length)] : string.Empty);
        if (Queries.SignatureAt(parsed, before) is { } call)
        {
            return new
            {
                signatures = new[] { new { label = call.Label, documentation = call.Documentation, parameters = call.Parameters.Select(x => new { label = x }).ToArray() } },
                activeSignature = 0,
                activeParameter = call.Active,
            };
        }

        BlockNode? owner = parsed.Blocks.FirstOrDefault(b => b.Line < line - 1 && (b.CloseLine < 0 || b.CloseLine >= line - 1));
        if (owner is null || !spec.Templates.TryGetValue(owner.Type, out TemplateSpec? t) || t.Parameters.Count == 0)
        {
            return null;
        }

        string[] names = [.. t.Parameters.Keys];
        string first = Positions.Lines(d.Text)[line - 1].TrimStart().Split(' ', 2)[0];
        int active = Math.Max(0, Array.IndexOf(names, first));
        return new
        {
            signatures = new[] { new { label = $"{owner.Type}({string.Join(", ", names)})", documentation = t.Description, parameters = names.Select(n => new { label = n, documentation = $"default {t.Parameters[n]}" }).ToArray(), activeParameter = active } },
            activeSignature = 0,
            activeParameter = active,
        };
    }

    private object[] Formatting(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return [];
        }

        // The formatter works on whole songs, so a range request formats the whole document too.
        string formatted = Formatter.Format(d.Text, spec);
        return formatted == d.Text ? [] : [new { range = Positions.Whole(d.Text), newText = formatted }];
    }

    private (string Name, Location Header)? SymbolAt(Doc d, int line, int col)
    {
        AmpleDocument parsed = d.Parse(spec);
        IReadOnlyList<Location> refs = Queries.FindReferences(parsed, line, col);
        if (refs.Count == 0 || !refs.Any(r => r.Line == line && col >= r.Col && col <= r.EndCol))
        {
            return null;
        }

        Location? def = Queries.FindDefinition(parsed, line, col);
        DocumentSymbol? sym = def is null ? null : Queries.DocumentSymbols(parsed).FirstOrDefault(s => s.Header == def);
        return sym is null ? null : (sym.Name, refs.First(r => r.Line == line && col >= r.Col && col <= r.EndCol));
    }

    private object? PrepareRename(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return null;
        }

        (int line, int col) = Pos(p);
        if (Queries.HelperAt(d.Parse(spec), line, col) is { } helper)
        {
            Location word = Queries.HelperOccurrences(d.Parse(spec), helper).FirstOrDefault(l => l.Line == line && col >= l.Col && col <= l.EndCol) ?? Queries.HelperDeclaration(d.Parse(spec), helper);
            return new { range = Positions.Range(word), placeholder = helper.Name };
        }

        (string Name, Location Header)? at = SymbolAt(d, line, col);
        return at is null ? null : new { range = Positions.Range(at.Value.Header), placeholder = at.Value.Name };
    }

    private object? Rename(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return null;
        }

        (int line, int col) = Pos(p);
        if (Queries.HelperAt(d.Parse(spec), line, col) is { } helper)
        {
            EditResult renamed = Edits.RenameHelper(d.Parse(spec), helper.Name, (string?)p!["newName"] ?? string.Empty);
            return renamed.Ok ? new { changes = new Dictionary<string, object[]> { [Uri(p)] = [.. renamed.Edits.Select(Positions.Edit)] } } : throw new InvalidOperationException(renamed.Error);
        }

        (string Name, Location Header)? at = SymbolAt(d, line, col);
        if (at is null)
        {
            throw new InvalidOperationException("There is no block name at this position.");
        }

        EditResult r = Edits.RenameBlock(d.Parse(spec), at.Value.Name, (string?)p!["newName"] ?? string.Empty);
        return r.Ok ? new { changes = new Dictionary<string, object[]> { [Uri(p)] = [.. r.Edits.Select(Positions.Edit)] } } : throw new InvalidOperationException(r.Error);
    }

    private object[] CodeActions(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return [];
        }

        string uri = Uri(p);
        AmpleDocument parsed = d.Parse(spec);
        int startLine = (int)p!["range"]!["start"]!["line"]! + 1, endLine = (int)p["range"]!["end"]!["line"]! + 1;
        List<object> actions = [];
        List<Ample.Language.Diagnostic> fixable = [.. parsed.Diagnostics.Where(x => x.Fix is not null)];
        string[] lines = Positions.Lines(d.Text);
        object FixEdit(Fix f)
        {
            string lt = LineText(lines, f.Line);
            int endLine = f.EndLine > f.Line ? f.EndLine : f.Line;
            string endText = endLine == f.Line ? lt : LineText(lines, endLine);
            return new { range = Positions.RangeOf(f.Line - 1, Positions.CodePointsToUtf16(lt, f.Col - 1), endLine - 1, Positions.CodePointsToUtf16(endText, f.EndCol - 1)), newText = f.Text };
        }

        foreach (Ample.Language.Diagnostic x in fixable.Where(x => x.Line >= startLine && x.Line <= endLine))
        {
            actions.Add(new { title = x.Fix!.Label, kind = "quickfix", isPreferred = !x.Fix.Risky, diagnostics = new[] { new { code = x.Code, message = x.Message, range = Positions.RangeOf(x.Line - 1, 0, x.Line - 1, 0) } }, edit = new { changes = new Dictionary<string, object[]> { [uri] = [FixEdit(x.Fix)] } } });
        }

        List<Fix> safe = [.. fixable.Where(x => !x.Fix!.Risky).Select(x => x.Fix!).OrderBy(f => f.Line).ThenBy(f => f.Col)];
        List<Fix> chosen = [];
        foreach (Fix f in safe.Where(f => !chosen.Any(c => c.Line == f.Line && f.Col < c.EndCol && c.Col < f.EndCol)))
        {
            chosen.Add(f);
        }

        if (chosen.Count > 0)
        {
            actions.Add(new { title = $"Fix all ({chosen.Count})", kind = "source.fixAll", edit = new { changes = new Dictionary<string, object[]> { [uri] = [.. chosen.Select(FixEdit)] } } });
        }

        int startCol = (int)p["range"]!["start"]!["character"]! + 1;
        EditResult callBake = Edits.BakeCallSite(parsed, startLine, startCol, parsed.ParsedAt);
        if (callBake.Ok && callBake.Edits.Count > 0)
        {
            actions.Add(new { title = "Bake this call (" + callBake.Edits[0].NewText + ")", kind = "refactor", edit = new { changes = new Dictionary<string, object[]> { [uri] = [.. callBake.Edits.Select(Positions.Edit)] } } });
        }

        BlockNode? block = parsed.Blocks.FirstOrDefault(b => b.Line + 1 >= startLine && b.Line + 1 <= endLine && !b.Skipped);
        if (block is not null)
        {
            foreach ((string flag, string title, bool dynamicOnly) in new[] { ("bake", $"Bake \"{block.Name}\" (freeze the result into the project)", true), ("lock", $"Lock \"{block.Name}\" (freeze its parameters)", false) })
            {
                bool dyn = spec.Templates.TryGetValue(block.Type, out TemplateSpec? t) && t.Dynamic;
                EditResult r = Edits.AddFlag(parsed, block.Name, flag);
                if (r.Ok && r.Edits.Count > 0 && (!dynamicOnly || dyn))
                {
                    actions.Add(new { title, kind = "refactor", edit = new { changes = new Dictionary<string, object[]> { [uri] = [.. r.Edits.Select(Positions.Edit)] } } });
                }
            }
        }

        return [.. actions];
    }

    private int[] SemanticData(JsonNode? p, JsonNode? range)
    {
        if (DocOf(p) is not Doc d)
        {
            return [];
        }

        IEnumerable<(int Line, int Start, int Length, int Type, int Mods)> tokens = SemanticTokens.Compute(d.Parse(spec), spec);
        if (range is not null)
        {
            int a = (int)range["start"]!["line"]!, b = (int)range["end"]!["line"]!;
            tokens = tokens.Where(t => t.Line >= a && t.Line <= b);
        }

        return SemanticTokens.Encode(tokens);
    }

    private object[] InlayHints(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return [];
        }

        AmpleDocument parsed = d.Parse(spec);
        string[] lines = Positions.Lines(d.Text);
        int a = (int?)p!["range"]?["start"]?["line"] ?? 0, b = (int?)p["range"]?["end"]?["line"] ?? int.MaxValue;
        List<object> hints = [];
        foreach (BlockNode block in parsed.Blocks.Where(x => !x.Skipped))
        {
            foreach (FromLine f in block.Froms.Where(x => x.Line >= a && x.Line <= b && x.Line < lines.Length))
            {
                BlockNode? src = parsed.Blocks.FirstOrDefault(x => x.Name == f.Block);
                string? type = src is not null && spec.Templates.TryGetValue(src.Type, out TemplateSpec? t) ? t.Outputs.FirstOrDefault(o => o.Name == f.Output)?.Type : null;
                if (type is null)
                {
                    continue;
                }

                string line = lines[f.Line];
                int hash = line.IndexOf('#', StringComparison.Ordinal);
                int end = (hash < 0 ? line : line[..hash]).TrimEnd().Length;
                hints.Add(new { position = new { line = f.Line, character = end }, label = $" : {type}", kind = 1, paddingLeft = true });
            }
        }

        return [.. hints];
    }

    private object[] SelectionRanges(JsonNode? p)
    {
        if (DocOf(p) is not Doc d)
        {
            return [];
        }

        string[] lines = Positions.Lines(d.Text);
        AmpleDocument parsed = d.Parse(spec);
        List<object> result = [];
        foreach (JsonNode? pos in p!["positions"]!.AsArray())
        {
            int line = (int)pos!["line"]!, ch = (int)pos["character"]!;
            string lt = line < lines.Length ? lines[line] : string.Empty;
            ch = Math.Clamp(ch, 0, lt.Length);
            int s = ch, e = ch;
            while (s > 0 && (char.IsLetterOrDigit(lt[s - 1]) || lt[s - 1] is '_' or '-'))
            {
                s--;
            }

            while (e < lt.Length && (char.IsLetterOrDigit(lt[e]) || lt[e] is '_' or '-'))
            {
                e++;
            }

            object node = new { range = Positions.RangeOf(0, 0, lines.Length - 1, lines[^1].Length) };
            BlockNode? b = parsed.Blocks.FirstOrDefault(x => x.Line <= line && (x.CloseLine < 0 ? x.Line == line : x.CloseLine >= line));
            if (b is not null)
            {
                int close = b.CloseLine < 0 ? b.Line : b.CloseLine;
                node = new { range = Positions.RangeOf(b.Line, 0, close, lines[Math.Min(close, lines.Length - 1)].Length), parent = node };
            }

            int indent = lt.Length - lt.TrimStart().Length;
            node = new { range = Positions.RangeOf(line, indent, line, lt.TrimEnd().Length), parent = node };
            if (e > s)
            {
                node = new { range = Positions.RangeOf(line, s, line, e), parent = node };
            }

            result.Add(node);
        }

        return [.. result];
    }

    private sealed class Doc(string text, int version)
    {
        private AmpleDocument? parsed;

        public string Text { get; private set; } = text;

        public int Version { get; private set; } = version;

        public Func<string, string?>? ReadFile { get; init; }

        public Func<IReadOnlyList<BlockNode>, IReadOnlyList<string>, IReadOnlyList<Ample.Language.Diagnostic>>? Preflight { get; init; }

        /// <summary>Gets what reads the components a song imports (they are block types of the editor, and their problems are shown at the import line).</summary>
        public Func<string, (IReadOnlyList<ComponentDefinition> Components, IReadOnlyList<Ample.Language.Diagnostic> Issues)>? Components { get; init; }

        public AmpleDocument Parse(SpecData spec)
        {
            if (parsed is not null) return parsed;
            ParseOptions options = ParseOptions.Editor;
            options.ReadFile = ReadFile;
            options.Preflight = Preflight;
            if (Components is not null)
            {
                (IReadOnlyList<ComponentDefinition> found, IReadOnlyList<Ample.Language.Diagnostic> issues) = Components(Text);
                options.Components = found;
                options.ExtraDiagnostics = issues;
            }

            return parsed = Parser.Parse(Text, spec, options);
        }

        public void Set(string text, int version)
        {
            Text = text;
            Version = version;
            parsed = null;
        }
    }
}
