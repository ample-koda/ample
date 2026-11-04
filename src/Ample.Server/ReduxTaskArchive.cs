using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Storage.BlackBox;

namespace Ample.Server;

/// <summary>Stores assistant task summaries and content-addressed, redacted proposal text in AMPLE Redux.</summary>
internal sealed class ReduxTaskArchive(string projectRoot, Journal journal)
{
    private static readonly Regex SecretAssignment = new("(?i)((?:\"?)(?:password|passwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret|authorization)(?:\"?)\\s*[:=]\\s*)(\"[^\"]*\"|'[^']*'|[^\\s,;]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Bearer = new("(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GithubToken = new("\\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,})\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string artifacts = Path.Combine(projectRoot, ".ample", "blackbox", "artifacts");

    public static string Redact(string text)
    {
        string redacted = Bearer.Replace(text, "Bearer [REDACTED]");
        redacted = SecretAssignment.Replace(redacted, match => match.Groups[1].Value + (match.Groups[2].Value[0] is '\"' or '\'' ? match.Groups[2].Value[..1] + "[REDACTED]" + match.Groups[2].Value[..1] : "\"[REDACTED]\""));
        return GithubToken.Replace(redacted, "[REDACTED]");
    }

    public void RecordTask(AiTask task, string status) => RecordTask(task, status, null);

    public void RecordTask(AiTask task, string status, string? historyRevision)
    {
        var changes = task.Changes.Select(change => new
        {
            path = Redact(change.Path),
            change.Status,
            change.Added,
            change.Removed,
            why = Redact(change.Why ?? string.Empty),
            before = StoreArtifact(change.Before),
            after = StoreArtifact(change.After),
        }).ToArray();
        var clips = task.ClipsNow().Select(clip => new
        {
            clip.Id,
            label = Redact(clip.Label),
            clip.Block,
            clip.Source,
            clip.From,
            clip.To,
            clip.Seconds,
            clip.AudioHash,
            clip.At,
            clip.Trial,
            why = Redact(clip.Why),
            clip.MediaType,
            settings = Redact(clip.Settings.ToJsonString()),
        }).ToArray();
        string payload = JsonSerializer.Serialize(new
        {
            taskId = task.Id,
            sessionId = task.SessionId,
            status,
            historyRevision,
            request = Redact(task.Request.Prompt),
            song = Redact(task.Request.Song),
            kind = task.Request.Kind,
            scope = task.Request.Scope,
            path = Redact(task.Request.Path),
            task.Request.FromLine,
            task.Request.ToLine,
            block = task.Request.Block is null ? null : Redact(task.Request.Block),
            model = task.Request.Model is null ? null : Redact(task.Request.Model),
            effort = task.Request.Effort,
            hums = task.Request.Hums,
            offered = task.Request.Offered,
            summary = Redact(task.Summary),
            error = task.Error is null ? null : Redact(task.Error),
            applied = task.Applied,
            identity = task.Identity,
            mode = task.Mode,
            changes,
            clips,
        });
        journal.AppendAsync("assistant", "assistant.task", status, task.Id, payload, cancellationToken: CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public void RecordConversation(AiTask task, AiLog entry)
    {
        string payload = JsonSerializer.Serialize(new
        {
            taskId = task.Id,
            sessionId = task.SessionId,
            at = entry.At,
            kind = entry.Kind,
            text = Redact(entry.Text),
            path = entry.Path is null ? null : Redact(entry.Path),
            why = entry.Why is null ? null : Redact(entry.Why),
            details = entry.Details is null ? null : Redact(entry.Details.ToJsonString()),
        });
        journal.AppendAsync("assistant", "assistant.conversation", "message", task.Id, payload, cancellationToken: CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private object? StoreArtifact(string? content)
    {
        if (content is null) return null;
        string redacted = Redact(content);
        byte[] bytes = Encoding.UTF8.GetBytes(redacted);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string path = Path.Combine(artifacts, hash[..2], hash + ".txt");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }

        return new { sha256 = hash, bytes = bytes.Length, lines = redacted.Length == 0 ? 0 : redacted.Count(character => character == '\n') + 1 };
    }
}
