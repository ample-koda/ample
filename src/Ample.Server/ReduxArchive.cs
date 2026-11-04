using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;

namespace Ample.Server;

internal sealed record ReduxArchiveQuery(string Scope, string? Reference, string? TaskId, string? SessionId, string? Path, string? Block, string? Field, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? Text, int Limit, string? Cursor);
internal sealed record ReduxArchivePage(IReadOnlyList<JournalRecord> Records, string? NextCursor, long ThroughSequence);
internal sealed record ReduxArchiveCursor(long Through, long Before, string FilterHash);

internal static class ReduxArchive
{
    private const int MaximumPageSize = 200;

    public static async ValueTask<ReduxArchivePage> SearchAsync(Journal journal, ReduxArchiveQuery query, CancellationToken cancellationToken)
    {
        string filterHash = FilterHash(query);
        long through = journal.Head.Seq;
        long before = through + 1;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            ReduxArchiveCursor cursor = DecodeCursor(query.Cursor);
            if (cursor.FilterHash != filterHash) throw new AmpleException("AMP5285", "The AMPLE Redux cursor belongs to different filters.");
            through = cursor.Through;
            before = cursor.Before;
        }

        long? targetSequence = null;
        if (!string.IsNullOrWhiteSpace(query.Reference))
        {
            if (!ReduxRecordReference.TryParse(query.Reference, out long sequence)) throw new AmpleException("AMP5285", "The AMPLE Redux reference is invalid.");
            targetSequence = sequence;
        }

        int limit = Math.Clamp(query.Limit, 1, MaximumPageSize);
        IReadOnlyList<JournalRecord> matches = await journal.ReadPageAsync(through, before, record => (!targetSequence.HasValue || record.Seq == targetSequence.Value) && (!query.FromUtc.HasValue || record.At >= query.FromUtc.Value) && (!query.ToUtc.HasValue || record.At <= query.ToUtc.Value) && Match(record, query), limit + 1, cancellationToken).ConfigureAwait(false);
        bool hasMore = matches.Count > limit;
        JournalRecord[] page = matches.Take(limit).ToArray();
        string? next = hasMore && page.Length > 0 ? EncodeCursor(new ReduxArchiveCursor(through, page[^1].Seq, filterHash)) : null;
        return new ReduxArchivePage(page, next, through);
    }

    private static bool Match(JournalRecord record, ReduxArchiveQuery query)
    {
        if (!Contains(record.Correlation, query.TaskId) && !HasRecordValue(record, ["task", "taskId", "task_id"], query.TaskId)) return false;
        if (!Contains(record.Correlation, query.SessionId) && !HasRecordValue(record, ["session", "sessionId", "session_id"], query.SessionId)) return false;
        if (!HasRecordValue(record, ["path", "file", "song"], query.Path)) return false;
        if (!HasRecordValue(record, ["block", "blockId", "block_id"], query.Block)) return false;
        if (!HasRecordValue(record, ["field", "parameter", "parameterName", "name"], query.Field)) return false;
        return string.IsNullOrWhiteSpace(query.Text) || Contains(record.Kind, query.Text) || Contains(record.Actor, query.Text) || Contains(record.PayloadJson, query.Text) || Contains(record.PreImageJson, query.Text);
    }

    private static bool HasRecordValue(JournalRecord record, string[] names, string? expected) => HasValue(record.PayloadJson, names, expected) || (record.PreImageJson is not null && HasValue(record.PreImageJson, names, expected));

    private static bool HasValue(string json, string[] names, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return HasValue(document.RootElement, names, expected);
        }
        catch (JsonException)
        {
            return Contains(json, expected);
        }
    }

    private static bool HasValue(JsonElement element, string[] names, string expected)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && Contains(property.Value.ToString(), expected)) return true;
                if (HasValue(property.Value, names, expected)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray()) if (HasValue(child, names, expected)) return true;
        }

        return false;
    }

    private static bool Contains(string? value, string? query) => string.IsNullOrWhiteSpace(query) || value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static string FilterHash(ReduxArchiveQuery query)
    {
        string value = JsonSerializer.Serialize(new { query.Scope, query.Reference, query.TaskId, query.SessionId, query.Path, query.Block, query.Field, query.FromUtc, query.ToUtc, query.Text });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string EncodeCursor(ReduxArchiveCursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ReduxArchiveCursor DecodeCursor(string encoded)
    {
        if (encoded.Length > 512) throw new AmpleException("AMP5285", "The AMPLE Redux cursor is invalid.");
        try
        {
            string padded = encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4);
            return JsonSerializer.Deserialize<ReduxArchiveCursor>(Convert.FromBase64String(padded)) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new AmpleException("AMP5285", "The AMPLE Redux cursor is invalid.");
        }
    }
}
