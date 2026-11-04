using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>
/// Access outside the project for the assistant: a request with a confirmation (what, why, how long), pass-through commands that check the
/// grants, grants that last for minutes (this task) or for the project, and the emergency stop when a run goes outside what is allowed.
/// </summary>
internal sealed partial class AiService
{
    private const int MaxGrantMinutes = 120;
    private static readonly ConcurrentDictionary<string, List<AccessGrant>> TaskGrants = new(StringComparer.Ordinal);
    private static readonly object GrantsGate = new();
    private static readonly string[] ProtectedFolders = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.System)];

    /// <summary>A permission: a file or a folder, for how long, and whether it only allows AMPLE's own pass-through commands.</summary>
    /// <param name="Path">The full path (a folder allows everything under it).</param>
    /// <param name="Kind"><c>write</c> or <c>passthrough</c> (only <c>ample_write_outside</c>, never a direct write).</param>
    /// <param name="Expires">When it ends (null: it does not, a project grant).</param>
    internal sealed record AccessGrant(string Path, string Kind, DateTimeOffset? Expires);

    private string GrantsFile => Path.Combine(s.Root, ProjectLayout.AmpleFolder, "ai", "grants.json");

    /// <summary>Gets the grants that last for the project.</summary>
    internal List<AccessGrant> ProjectGrants()
    {
        lock (GrantsGate)
        {
            try
            {
                if (File.Exists(GrantsFile) && JsonNode.Parse(File.ReadAllText(GrantsFile)) is JsonArray array)
                {
                    return [.. array.OfType<JsonObject>().Where(o => (string?)o["path"] is { Length: > 0 }).Select(o => new AccessGrant((string)o["path"]!, (string?)o["kind"] ?? "write", null))];
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // damaged: no grants
            }

            return [];
        }
    }

    /// <summary>Removes a project grant.</summary>
    internal void RevokeGrant(string path)
    {
        lock (GrantsGate)
        {
            List<AccessGrant> keep = [.. ProjectGrants().Where(g => !string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase))];
            SaveGrants(keep);
        }
    }

    private void SaveGrants(List<AccessGrant> grants)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GrantsFile)!);
        JsonArray array = [.. grants.Select(g => (JsonNode)new JsonObject { ["path"] = g.Path, ["kind"] = g.Kind })];
        File.WriteAllText(GrantsFile, array.ToJsonString() + "\n");
    }

    private void AddProjectGrant(string path, string kind)
    {
        lock (GrantsGate)
        {
            List<AccessGrant> grants = ProjectGrants();
            if (!grants.Any(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase) && g.Kind == kind))
            {
                grants.Add(new AccessGrant(path, kind, null));
                SaveGrants(grants);
            }
        }
    }

    private static bool Under(string root, string path)
    {
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks a path a model wants to write outside its working folder: returns the full path, or throws why it can never be allowed.</summary>
    private string CheckOutside(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5610", "Give a full path (for example C:\\temp\\findings.txt), without '..'. A file of the project is changed with ample_edit, not with this tool.");
        }

        string full = Path.GetFullPath(path);
        if (Under(Path.Combine(s.Root, ProjectLayout.AmpleFolder), full) || ProtectedFolders.Any(f => f.Length > 0 && Under(f, full)))
        {
            throw new AmpleException("AMP5610", "That place is never open for writing (the project's own .ample folder and the system folders).");
        }

        return full;
    }

    private bool GrantCovers(AiTask task, string fullPath, bool passthroughOnly)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IEnumerable<AccessGrant> all = ProjectGrants();
        if (TaskGrants.TryGetValue(task.Id, out List<AccessGrant>? mine))
        {
            lock (mine)
            {
                all = [.. all, .. mine.Where(g => g.Expires is null || g.Expires > now)];
            }
        }

        return all.Any(g => (passthroughOnly || g.Kind == "write") && (string.Equals(g.Path, fullPath, StringComparison.OrdinalIgnoreCase) || Under(g.Path, fullPath)));
    }

    private static void GrantToTask(AiTask task, string fullPath, string kind, int minutes)
    {
        List<AccessGrant> list = TaskGrants.GetOrAdd(task.Id, _ => []);
        lock (list)
        {
            list.Add(new AccessGrant(fullPath, kind, DateTimeOffset.UtcNow.AddMinutes(minutes)));
        }
    }

    /// <summary>
    /// The model asks to write outside its working folder. The person sees what, why and for how long; the answer is allow (for that long, this task),
    /// always for this project, or deny.
    /// </summary>
    public async Task<JsonObject> RequestAccessAsync(string token, string path, string why, string minutesText, string kind, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string full = CheckOutside(path);
        int minutes = int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out int m) ? Math.Clamp(m, 1, MaxGrantMinutes) : 15;
        string mode = kind == "passthrough" ? "passthrough" : "write";
        if (GrantCovers(task, full, mode == "passthrough"))
        {
            return new JsonObject { ["allowed"] = true, ["path"] = full, ["note"] = "Already allowed." };
        }

        AiApproval ask = new()
        {
            Kind = "access",
            Summary = full,
            Detail = $"Codex wants to write here. Why: {(why.Length == 0 ? "(no reason given)" : why)}. For: {minutes} minute(s) in this task{(mode == "passthrough" ? "; only through AMPLE's own commands (ample_write_outside)" : string.Empty)}.",
        };
        task.Approvals[ask.Id] = ask;
        task.Status = "waiting-approval";
        task.Add("approval", $"Codex asks to write outside the project: {full} ({why}), {minutes} min.");
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token);
        string decision;
        try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), limit.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "decline"; }
        ask.Answered = decision;
        task.Status = "running";
        if (decision is not ("accept" or "acceptForSession"))
        {
            task.Add("approval", "Denied: nothing is written outside the project.");
            return new JsonObject { ["allowed"] = false, ["note"] = "The person did not allow it. Do not try another way to write there: put what you wanted to say in ample_findings (inside the project), or in your last message." };
        }

        GrantToTask(task, full, mode, minutes);
        if (decision == "acceptForSession")
        {
            AddProjectGrant(full, mode);
        }

        task.Add("approval", decision == "acceptForSession" ? $"Allowed for this project: {full}" : $"Allowed for {minutes} minute(s): {full}");
        return new JsonObject { ["allowed"] = true, ["path"] = full, ["minutes"] = minutes, ["note"] = mode == "passthrough" ? "Write with ample_write_outside." : "Write with ample_write_outside, or directly." };
    }

    /// <summary>A pass-through write: only where a grant allows it.</summary>
    public JsonObject WriteOutside(string token, string path, string text)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string full = CheckOutside(path);
        if (Encoding.UTF8.GetByteCount(text) > 2_000_000)
        {
            throw new AmpleException("AMP5610", "At most 2 MB of text at a time.");
        }

        if (!GrantCovers(task, full, true))
        {
            throw new AmpleException("AMP5611", $"There is no permission to write {full}. Ask first with ample_request_access (path, why, minutes); the person decides.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        bool existed = File.Exists(full);
        File.WriteAllText(full, text);
        task.Add("file", $"Wrote {full} with the permission of the person.");
        return new JsonObject { ["written"] = true, ["path"] = full, ["replaced"] = existed, ["bytes"] = Encoding.UTF8.GetByteCount(text) };
    }

    /// <summary>The model's remarks (what is missing, what could be better) as a file inside the project, no permission needed.</summary>
    public JsonObject Findings(string token, string name, string text)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string clean = Regex.Replace(string.IsNullOrWhiteSpace(name) ? "findings" : name.Trim(), "[^A-Za-z0-9_-]+", "-").Trim('-');
        if (clean.Length == 0)
        {
            clean = "findings";
        }

        string rel = $"notes/ai-findings/{clean}-{DateTime.UtcNow:yyyyMMdd-HHmm}.md";
        string full = Path.Combine(s.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (Encoding.UTF8.GetByteCount(text) > 500_000)
        {
            throw new AmpleException("AMP5610", "At most 500 KB of findings at a time.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        task.Add("file", $"Codex wrote its findings to {rel}.");
        return new JsonObject { ["written"] = true, ["path"] = rel, ["note"] = "The findings are in the project; tell the person where they are." };
    }

    private string? ProposalOrLive(AiTask task, string rel)
    {
        string staged = Path.GetFullPath(Path.Combine(task.Workspace, "project", rel.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(staged))
        {
            return staged;
        }

        string live = ProjectPath.Resolve(s.Root, rel);
        return File.Exists(live) ? live : null;
    }

    /// <summary>Turns a score of the project (MIDI or MusicXML) into a sequence in the proposal.</summary>
    public JsonObject ImportScore(string token, JsonElement body)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string rel = AiContext.Norm(body.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : string.Empty);
        if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[8..];
        }

        string? source = rel.Length == 0 ? null : ProposalOrLive(task, rel);
        if (source is null)
        {
            throw new AmpleException("AMP5243", $"There is no score file '{rel}' in the project. Give the path of a .mid, .musicxml or .xml file; a score from the web is fetched first with ample_fetch_url, and a fetched file (in workspace/assets/web) is put into the project with ample_copy_file (source=workspace/assets/web/<file>, destination=scores/<file>) before it is imported.");
        }

        string ext = Path.GetExtension(source).ToLowerInvariant();
        int perPattern = body.TryGetProperty("bars_per_pattern", out JsonElement bp) && int.TryParse(bp.ToString(), out int n) ? n : 8;
        string title = Path.GetFileNameWithoutExtension(source);
        Ample.Audio.Midi.ScoreImportResult result;
        try
        {
            result = ext is ".mid" or ".midi"
                ? Ample.Audio.Midi.ScoreImport.FromMidi(File.ReadAllBytes(source), title, perPattern, body.TryGetProperty("compress", out JsonElement compressValue) && (compressValue.ValueKind == JsonValueKind.True || compressValue.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)))
                : ext is ".musicxml" or ".xml"
                    ? Ample.Audio.Midi.ScoreImport.FromMusicXml(File.ReadAllText(source), title, perPattern)
                    : ext == ".mxl"
                        ? Ample.Audio.Midi.ScoreImport.FromMusicXml(UnzipMusicXml(source), title, perPattern)
                        : ScoreOmr.RecognizableExtensions.Contains(ext)
                            ? Ample.Audio.Midi.ScoreImport.FromMusicXml(ScoreOmr.Recognize(source, task.Workspace, s.Root), title, perPattern)
                            : ScoreConvert.ConvertibleExtensions.Contains(ext)
                                ? Ample.Audio.Midi.ScoreImport.FromMusicXml(ScoreConvert.Convert(source, task.Workspace, s.Root), title, perPattern)
                                : throw new AmpleException("AMP5285", "A score is a .mid, .musicxml, .xml or .mxl file, a PDF/image that Audiveris can recognize (optical music recognition - the result can be wrong, check the note count and warnings), or a MuseScore/Guitar Pro/Capella/Overture file that MuseScore can convert. A PDF can also be read as plain text with ample_file (its text) but that is not turned into notes.");
        }
        catch (Exception ex) when (ex is Ample.Audio.Midi.SmfFormatException or System.Xml.XmlException or FormatException or InvalidDataException)
        {
            throw new AmpleException("AMP5285", "The score could not be read: " + ex.Message);
        }

        string outRel = AiContext.Norm(body.TryGetProperty("out", out JsonElement o) && o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } given ? given : Path.ChangeExtension(rel, ".ampleseq"));
        if (outRel.StartsWith("project/", StringComparison.OrdinalIgnoreCase))
        {
            outRel = outRel[8..];
        }

        if (!outRel.EndsWith(".ampleseq", StringComparison.OrdinalIgnoreCase) && !outRel.EndsWith(".seq", StringComparison.OrdinalIgnoreCase))
        {
            outRel += ".ampleseq";
        }

        string root = Path.GetFullPath(Path.Combine(task.Workspace, "project")) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, outRel.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || outRel.StartsWith(ProjectLayout.AmpleFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new AmpleException("AMP5458", $"{outRel} is not a place in the proposal for a sequence.");
        }

        if (File.Exists(target) || File.Exists(ProjectPath.Resolve(s.Root, outRel)))
        {
            throw new AmpleException("AMP5243", $"{outRel} already exists: give another 'out', or change it with ample_edit.");
        }

        bool recognized = ScoreOmr.RecognizableExtensions.Contains(ext);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, result.SequenceText, new UTF8Encoding(false));
        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("file", $"imported the score {rel} to {outRel}{(recognized ? " (recognized with Audiveris)" : string.Empty)}: {result.Parts} part(s), {result.Notes} note(s), {result.Bars} bar(s) at {result.Tempo.ToString("0.#", CultureInfo.InvariantCulture)} BPM.");
        return new JsonObject
        {
            ["ok"] = true,
            ["path"] = outRel,
            ["parts"] = result.Parts,
            ["notes"] = result.Notes,
            ["bars"] = result.Bars,
            ["tempo"] = Math.Round(result.Tempo, 2),
            ["warnings"] = new JsonArray([.. result.Warnings.Select(w => (JsonNode)w)]),
            ["next"] = (recognized ? "This came from optical music recognition, not a machine-readable score: check the note count and a few bars against the source before trusting it. " : string.Empty)
                + "Add a sequencer block that names the file (file " + outRel + "), set the project tempo to " + Math.Round(result.Tempo).ToString(CultureInfo.InvariantCulture) + ", and give long scores the lane or length they need. Read the docs: ample_docs name=score-import.",
        };
    }

    /// <summary>The uncompressed MusicXML text inside a .mxl (zipped MusicXML) file.</summary>
    private static string UnzipMusicXml(string mxlPath)
    {
        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(mxlPath);
        System.IO.Compression.ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            ?? throw new AmpleException("AMP5285", "This .mxl file did not contain a readable MusicXML part.");
        using StreamReader reader = new(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>The text of a PDF of the project (PdfPig). A missing helper is written to the project log with how to fix it.</summary>
    public JsonObject PdfText(string token, string path) => PdfText(token, path, "project");

    public JsonObject PdfText(string token, string path, string location)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string rel = AiContext.Norm(path);
        string? source = null;
        if (location == "work")
        {
            if (!Path.GetExtension(rel).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || rel.Split('/').Any(x => x.Length > 0 && x[0] == '.')) throw new AmpleException("AMP5458", "There is no readable task-work PDF at that path.");
            source = ProjectPath.Resolve(task.Workspace, rel);
            string current = Path.GetFullPath(task.Workspace);
            foreach (string part in Path.GetRelativePath(current, source).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AmpleException("AMP5458", $"There is no readable task-work PDF '{rel}'.");
            }
            if (!File.Exists(source)) throw new AmpleException("AMP5458", $"There is no readable task-work PDF '{rel}'.");
        }
        else if (location != "project") throw new AmpleException("AMP5285", "Location must be project or work.");
        else if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[8..];
        }

        if (location == "project") source = ProposalOrLive(task, rel);
        if (source is null)
        {
            throw new AmpleException("AMP5243", $"There is no file '{rel}' in the project.");
        }

        try
        {
            using UglyToad.PdfPig.PdfDocument pdf = UglyToad.PdfPig.PdfDocument.Open(File.ReadAllBytes(source));
            StringBuilder text = new();
            int pages = Math.Min(pdf.NumberOfPages, 40);
            for (int page = 1; page <= pages && text.Length < 100_000; page++)
            {
                text.Append("--- page ").Append(page).Append(" ---\n").AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(pdf.GetPage(page)));
            }

            return new JsonObject { ["path"] = rel, ["pages"] = pdf.NumberOfPages, ["extractedPages"] = pages, ["text"] = text.ToString(0, Math.Min(text.Length, 100_000)), ["note"] = "The text of the PDF as AMPLE read it. Notation and images are not text: give the path to ample_import_score instead to recognize the music itself (optical music recognition)." };
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
        {
            const string what = "Reading the text of a PDF (the PdfPig files are missing)";
            const string hint = "The PdfPig library files are not next to the program. Rebuild AMPLE and copy the whole build folder (all files of bin/Debug/net9.0), or reinstall; ample doctor --env shows what else is missing.";
            ProjectLog.Append(s.Root, "missing", what, hint);
            return new JsonObject { ["ok"] = false, ["hint"] = hint, ["note"] = "Tell the person this is missing and recommend the hint; it is also in the project log (ample_project_log)." };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            throw new AmpleException("AMP5285", "The PDF could not be read: " + ex.Message);
        }
    }

    /// <summary>Asks Codex to compact its conversation now (before a big step, so it does not happen in the middle of it).</summary>
    public async Task<AiTask> CompactAsync(string id)
    {
        AiTask task = GetTask(id);
        if (task.Rpc is null || task.ThreadId is null || task.Status is "running" or "preparing" or "queued")
        {
            throw new AmpleException("AMP5458", "Codex can be asked to compact between its turns: wait until it is ready, or has finished.");
        }

        task.Compacting = true;
        task.Add("info", "Compacting the conversation of Codex (asked by the person).");
        try
        {
            await task.Rpc.RequestAsync("thread/compact/start", new JsonObject { ["threadId"] = task.ThreadId }, TimeSpan.FromSeconds(30), task.Cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AmpleException or InvalidOperationException)
        {
            task.Compacting = false;
            throw new AmpleException("AMP5458", "Codex could not compact now: " + ex.Message);
        }

        return task;
    }

    private static readonly string[] Efforts = ["default", "low", "medium", "high", "xhigh", "max", "ultra"];

    /// <summary>
    /// Changes the model and/or the reasoning effort of a running or waiting task, as Codex itself can: the next turn uses them. Codex is told in its next
    /// message, so "I made you smarter" means something to it, and ample_project shows the settings it works with now.
    /// </summary>
    public AiTask ChangeAssistant(string id, string? model, string? effort)
    {
        AiTask task = GetTask(id);
        if (effort is { Length: > 0 } && !Efforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
        {
            throw new AmpleException("AMP5285", "The reasoning effort must be one of: " + string.Join(", ", Efforts) + ".");
        }

        string before = DescribeAssistant(task);
        if (model is not null)
        {
            task.ModelNow = model.Length == 0 ? null : model;
        }

        if (effort is not null)
        {
            task.EffortNow = effort.Length == 0 ? null : effort.ToLowerInvariant();
        }

        string now = DescribeAssistant(task);
        if (now != before)
        {
            task.Add("info", $"The person changed the assistant: {before} -> {now}.");
            lock (task.QueuedMessages)
            {
                task.QueuedMessages.Add($"AMPLE: the person changed your settings while you were working ({before} -> {now}). They may call this making you smarter or faster; from your next turn on you work with {now}. Carry on with the request; there is nothing you need to do about it.");
            }
        }

        return task;
    }

    private static string DescribeAssistant(AiTask task)
    {
        AiPolicy policy = AiPolicy.Read();
        string model = task.ModelNow ?? task.Request.Model ?? policy.ModelToUse ?? "the default model";
        string effort = EffortOf(task, policy) ?? "the model's own reasoning default";
        return $"model {(model == "default" ? "Codex's default" : model)}, reasoning {effort}";
    }

    /// <summary>Adds what the assistant works with now (model and reasoning effort) to the project answer of the tool.</summary>
    public static JsonObject WithAssistantInfo(string token, JsonObject project)
    {
        if (ContextTokens.TryGetValue(token, out AiTask? task))
        {
            project["assistant"] = new JsonObject { ["settings"] = DescribeAssistant(task), ["note"] = "These are the model and the reasoning effort you work with now. The person can change them while you work; you are told when they do." };
            project["taskWorkFiles"] = new JsonArray([.. TaskWorkFiles(task).Select(path => (JsonNode)JsonValue.Create(path)!)]);
            project["taskWorkFilesNote"] = "A separate, read-only inventory of files in this Codex task's work folder. On path-based MCP calls, set location=work to read one; set location=project for normal project files, including any project work/ directory.";
        }

        return project;
    }

    private static IEnumerable<string> TaskWorkFiles(AiTask task)
    {
        if (!Directory.Exists(task.Workspace)) yield break;
        Stack<string> folders = new();
        folders.Push(task.Workspace);
        int count = 0;
        while (folders.Count > 0 && count < 250)
        {
            string folder = folders.Pop();
            foreach (string directory in Directory.EnumerateDirectories(folder))
            {
                string name = Path.GetFileName(directory);
                if ((name.Length > 0 && name[0] == '.') || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                folders.Push(directory);
            }
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string relative = Path.GetRelativePath(task.Workspace, file).Replace('\\', '/');
                string extension = Path.GetExtension(file);
                string name = Path.GetFileName(file);
                if ((name.Length > 0 && name[0] == '.') || name.Equals("rpc.log", StringComparison.OrdinalIgnoreCase) || (!WorkTextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && !extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                yield return relative;
                if (++count >= 250) yield break;
            }
        }
    }

    /// <summary>The project log for the assistant: what is missing and how to put it right.</summary>
    public JsonObject ProjectLogRead() => new()
    {
        ["entries"] = new JsonArray([.. ProjectLog.List(s.Root, 50).Select(e => (JsonNode)new JsonObject { ["at"] = e.At.ToString("O", CultureInfo.InvariantCulture), ["kind"] = e.Kind, ["what"] = e.What, ["hint"] = e.Hint })]),
        ["note"] = "When something here is missing for what the person asks, say so and recommend the hint.",
    };

    /// <summary>
    /// Finds the place outside the working folder that an approval request of Codex is about (a file change root, a working folder, or a path a command writes to),
    /// or null when the request stays inside.
    /// </summary>
    private string? OutsideTarget(AiTask task, string method, JsonObject p)
    {
        string workspace = Path.GetFullPath(task.Workspace.Length == 0 ? Path.Combine(s.Root, ProjectLayout.AmpleFolder, "ai", "work", task.Id) : task.Workspace);
        // a Windows path (C:...) is a place outside the working folder on any system: a server on Linux must not take it for a relative path
        bool Outside(string path) => path.Length > 0 && (Path.IsPathRooted(path) || Regex.IsMatch(path, @"^[A-Za-z]:[\\/]", RegexOptions.None, TimeSpan.FromSeconds(1))) && !Under(workspace, path);
        if (method.Contains("fileChange", StringComparison.Ordinal) && p["grantRoot"]?.GetValue<string>() is { } root && Outside(root))
        {
            return root;
        }

        if (method.Contains("commandExecution", StringComparison.Ordinal))
        {
            string command = p["command"]?.GetValue<string>() ?? string.Empty;
            if (Regex.IsMatch(command, @"(Set-Content|Add-Content|Out-File|New-Item|Copy-Item|Move-Item|Remove-Item|writeFile|WriteAllText|\bcopy\b|\bmove\b|\bdel\b|\bmkdir\b|>>?\s*[A-Za-z]:\\|\btee\b)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                foreach (Match m in Regex.Matches(command, @"[A-Za-z]:\\[^\s""'`;|)]+", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    if (Outside(m.Value))
                    {
                        return m.Value;
                    }
                }
            }
        }

        if (method.Contains("permissions", StringComparison.Ordinal))
        {
            string text = p.ToJsonString();
            if (text.Contains("write", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in Regex.Matches(text.Replace("\\\\", "\\", StringComparison.Ordinal), @"[A-Za-z]:\\[^\s""'`;|)\\]+(?:\\[^\s""'`;|)\\]+)*", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    if (Outside(m.Value))
                    {
                        return m.Value;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The emergency stop: the run waits at the request that goes outside, and the person decides: allow once, always for the project, or ask it to do it correctly.
    /// Returns the decision of the approval (accept, acceptForSession, decline).
    /// </summary>
    private bool IsAllowedOutside(AiTask task, string outside) => GrantCovers(task, Path.GetFullPath(outside), false);

    private void StopOutcome(AiTask task, string outside, string decision)
    {
        string full = Path.GetFullPath(outside);
        if (decision == "accept")
        {
            GrantToTask(task, full, "write", 10);
            task.Add("approval", $"Allowed once: {full} (for 10 minutes).");
        }
        else if (decision == "acceptForSession")
        {
            AddProjectGrant(full, "write");
            task.Add("approval", $"Allowed for this project: {full}");
        }
        else
        {
            task.Add("approval", $"Denied: Codex is told to do it another way ({full}).");
            lock (task.QueuedMessages)
            {
                task.QueuedMessages.Add($"AMPLE stopped you: you tried to write outside your working folder ({full}). Do not. Put what you wanted to write in ample_findings (inside the project), or ask with ample_request_access (path, why, minutes) and wait for the person's answer.");
            }
        }
    }
}
