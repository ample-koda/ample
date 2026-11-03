using System.Globalization;
using System.Text;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Storage.Security;

namespace Ample.Storage.BlackBox;

/// <summary>What a compaction did.</summary>
/// <param name="Thinned">Segments replaced by hourly summaries.</param>
/// <param name="Deleted">Old summaries removed.</param>
/// <param name="BudgetWarning">True when the journal uses 80% or more of its budget after compaction.</param>
/// <param name="TotalBytes">Bytes used by the Redux archive, including nested artifacts.</param>
public sealed record CompactionResult(int Thinned, int Deleted, bool BudgetWarning, long TotalBytes);

/// <summary>The exact impact of an explicit compaction, without changing the archive.</summary>
public sealed record CompactionPreview(int SegmentsToThin, int RecordsToThin, int SummariesToDelete, long BytesBefore, long BytesAfter, long BudgetBytes, long ThroughSequence);

/// <summary>Retention of AMPLE Redux: full detail for a number of days, then one summary line per hour and kind for a year, within a size budget. The newest segment is never thinned. The summaries that replace old segments hold only hours, kinds and counts and are not encrypted; the records themselves are, when the project is encrypted.</summary>
public static class JournalRetention
{
    /// <summary>Gets aggregate bytes used by journal segments, summaries and archived artifacts without reading their contents.</summary>
    public static ValueTask<long> StorageBytesAsync(IFileSystem fs, string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fs);
        return Size(fs, Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "blackbox"), cancellationToken);
    }

    /// <summary>Previews the exact record and byte impact of explicit compaction.</summary>
    public static async ValueTask<CompactionPreview> PreviewAsync(IFileSystem fs, string projectRoot, DateTimeOffset now, BlackBoxSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(settings);
        string folder = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "blackbox");
        List<string> segments = [.. fs.GetFiles(folder).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        HashSet<string> thin = new(StringComparer.Ordinal);
        Dictionary<string, List<JournalRecord>> loaded = new(StringComparer.Ordinal);
        long after = await Size(fs, folder, cancellationToken).ConfigureAwait(false);
        int records = 0, deleted = 0;
        foreach (string segment in segments.Take(Math.Max(0, segments.Count - 1)))
        {
            (List<JournalRecord> rows, _, _) = await Journal.ReadSegmentAsync(fs, segment, ProjectEncryption.CipherFor(projectRoot), cancellationToken).ConfigureAwait(false);
            loaded[segment] = rows;
            if (rows.Count > 0 && now - rows[^1].At > TimeSpan.FromDays(settings.FullDetailDays)) AddThin(segment, rows);
        }
        foreach (string segment in segments.Take(Math.Max(0, segments.Count - 1)))
        {
            if (after <= settings.BudgetBytes) break;
            List<JournalRecord> rows = loaded.TryGetValue(segment, out List<JournalRecord>? saved) ? saved : (await Journal.ReadSegmentAsync(fs, segment, ProjectEncryption.CipherFor(projectRoot), cancellationToken).ConfigureAwait(false)).Records;
            if (!thin.Contains(segment)) AddThin(segment, rows);
        }
        foreach (string summary in fs.GetFiles(folder).Where(f => f.EndsWith(".sum", StringComparison.Ordinal)))
        {
            string first = (await fs.ReadAllTextAsync(summary, cancellationToken).ConfigureAwait(false)).Split('\n')[0];
            if (!thin.Contains(Path.ChangeExtension(summary, ".seg")) && Expired(first, now, settings)) { after -= FileSize(fs, summary); deleted++; }
        }
        return new CompactionPreview(thin.Count, records, deleted, await Size(fs, folder, cancellationToken).ConfigureAwait(false), Math.Max(0, after), settings.BudgetBytes, 0);

        void AddThin(string segment, List<JournalRecord> rows)
        {
            thin.Add(segment);
            records += rows.Count;
            after -= FileSize(fs, segment);
            string sum = Path.ChangeExtension(segment, ".sum");
            if (fs.FileExists(sum)) after -= FileSize(fs, sum);
            string summary = Summary(rows);
            bool expires = rows.Count == 0 || now - rows[^1].At > TimeSpan.FromDays(settings.FullDetailDays + settings.ThinnedDays);
            if (!expires) after += Encoding.UTF8.GetByteCount(summary);
            else deleted++;
        }
    }

    /// <summary>Thins old segments and removes expired summaries.</summary>
    public static async ValueTask<CompactionResult> CompactAsync(IFileSystem fs, string projectRoot, DateTimeOffset now, BlackBoxSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(settings);
        string folder = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "blackbox");
        DataCipher? cipher = ProjectEncryption.CipherFor(projectRoot);
        int thinned = 0;
        int deleted = 0;
        List<string> segments = [.. fs.GetFiles(folder).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        foreach (string seg in segments.Take(Math.Max(0, segments.Count - 1)).ToList())
        {
            (List<JournalRecord> records, _, _) = await Journal.ReadSegmentAsync(fs, seg, cipher, cancellationToken).ConfigureAwait(false);
            if (records.Count > 0 && now - records[^1].At > TimeSpan.FromDays(settings.FullDetailDays))
            {
                await Thin(fs, seg, records, cancellationToken).ConfigureAwait(false);
                segments.Remove(seg);
                thinned++;
            }
        }

        long total = await Size(fs, folder, cancellationToken).ConfigureAwait(false);
        foreach (string seg in segments.Take(Math.Max(0, segments.Count - 1)).ToList())
        {
            if (total <= settings.BudgetBytes)
            {
                break;
            }

            (List<JournalRecord> records, _, _) = await Journal.ReadSegmentAsync(fs, seg, cipher, cancellationToken).ConfigureAwait(false);
            await Thin(fs, seg, records, cancellationToken).ConfigureAwait(false);
            thinned++;
            total = await Size(fs, folder, cancellationToken).ConfigureAwait(false);
        }

        foreach (string sum in fs.GetFiles(folder).Where(f => f.EndsWith(".sum", StringComparison.Ordinal)))
        {
            string first = (await fs.ReadAllTextAsync(sum, cancellationToken).ConfigureAwait(false)).Split('\n')[0];
            if (DateTimeOffset.TryParse(first.Replace("#last ", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset last) && now - last > TimeSpan.FromDays(settings.FullDetailDays + settings.ThinnedDays))
            {
                fs.DeleteFile(sum);
                deleted++;
            }
        }

        total = await Size(fs, folder, cancellationToken).ConfigureAwait(false);
        return new CompactionResult(thinned, deleted, total >= settings.BudgetBytes * 0.8, total);
    }

    private static async ValueTask Thin(IFileSystem fs, string segment, List<JournalRecord> records, CancellationToken cancellationToken)
    {
        await AtomicFile.WriteTextAsync(fs, Path.ChangeExtension(segment, ".sum"), Summary(records), cancellationToken).ConfigureAwait(false);
        fs.DeleteFile(segment);
    }

    private static string Summary(List<JournalRecord> records)
    {
        StringBuilder sb = new();
        sb.Append("#last ").Append(records.Count == 0 ? "0001-01-01T00:00:00Z" : records[^1].At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var g in records.GroupBy(r => (Hour: r.At.UtcDateTime.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture), r.Kind, r.Phase)).OrderBy(g => g.Key.Hour, StringComparer.Ordinal).ThenBy(g => g.Key.Kind, StringComparer.Ordinal)) sb.Append(g.Key.Hour).Append('\t').Append(g.Key.Kind).Append('\t').Append(g.Key.Phase).Append('\t').Append(g.Count().ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    private static long FileSize(IFileSystem fs, string file) { using Stream stream = fs.OpenRead(file); return stream.Length; }
    private static bool Expired(string first, DateTimeOffset now, BlackBoxSettings settings) => DateTimeOffset.TryParse(first.Replace("#last ", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset last) && now - last > TimeSpan.FromDays(settings.FullDetailDays + settings.ThinnedDays);

    private static ValueTask<long> Size(IFileSystem fs, string folder, CancellationToken cancellationToken)
    {
        long total = 0;
        Stack<string> pending = new();
        pending.Push(folder);
        while (pending.TryPop(out string? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string file in fs.GetFiles(current))
            {
                using Stream stream = fs.OpenRead(file);
                total = checked(total + stream.Length);
            }
            foreach (string directory in fs.GetDirectories(current)) pending.Push(directory);
        }

        return ValueTask.FromResult(total);
    }
}
