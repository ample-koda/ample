using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Storage.Security;

namespace Ample.Storage.BlackBox;

/// <summary>One AMPLE Redux recovery record.</summary>
/// <param name="Seq">Running number, never reused.</param>
/// <param name="Prev">SHA-256 (hex) of the previous record's text: the hash chain.</param>
/// <param name="At">Time from the injected clock.</param>
/// <param name="Actor"><c>user</c>, <c>plugin</c>, <c>system</c>, <c>cli</c> or <c>ui</c>.</param>
/// <param name="Kind">Command kind, for example <c>file.write</c>.</param>
/// <param name="Phase"><c>start</c>, <c>ok</c>, <c>failed</c>, <c>resolved</c> or <c>tombstone</c>.</param>
/// <param name="Correlation">Ties the records of one command together.</param>
/// <param name="PayloadJson">The full command payload as JSON text.</param>
/// <param name="PreImageJson">What was overwritten (so it can be restored), or null.</param>
/// <param name="Error">The error text of a failed command.</param>
public sealed record JournalRecord(long Seq, string Prev, DateTimeOffset At, string Actor, string Kind, string Phase, string Correlation, string PayloadJson, string? PreImageJson, string? Error)
{
    /// <summary>The fixed AMPLE Redux reference for this record.</summary>
    public string ArId => ReduxRecordReference.Padded(Seq);

    /// <summary>The compact AMPLE Redux reference for UI labels.</summary>
    public string CompactArId => ReduxRecordReference.Compact(Seq);
}

/// <summary>Journal options.</summary>
/// <param name="SegmentBytes">Size after which a new segment starts.</param>
/// <param name="MirrorRoot">Folder in the user's home for the mirror of the journal tail, or null to switch it off.</param>
/// <param name="MirrorRecords">How many records the mirror keeps.</param>
/// <param name="Cipher">The key to seal records with; null means: use the encryption of the project when it has one (see <see cref="ProjectEncryption"/>).</param>
public sealed record JournalOptions(long SegmentBytes = 4 * 1024 * 1024, string? MirrorRoot = null, int MirrorRecords = 200, DataCipher? Cipher = null);

/// <summary>A command that started but has no outcome record: it was interrupted.</summary>
/// <param name="Start">The start record (with the pre-image).</param>
public sealed record InterruptedCommand(JournalRecord Start);

/// <summary>The records read from one segment. <c>Torn</c> means an incomplete last line (a crash while writing); <c>Corrupt</c> means a damaged record before the end.</summary>
internal sealed record SegmentRead(List<JournalRecord> Records, List<string> Lines, bool Torn, bool Corrupt, int Remaining)
{
    public void Deconstruct(out List<JournalRecord> records, out List<string> lines, out bool torn)
    {
        records = Records;
        lines = Lines;
        torn = Torn || Corrupt;
    }
}

/// <summary>The result of verifying the journal.</summary>
/// <param name="Records">Records read.</param>
/// <param name="BadSeq">Sequence number of the first record that breaks the hash chain or fails its checksum, or null.</param>
/// <param name="TornTail">True when the last line is incomplete (a crash while writing).</param>
public sealed record VerifyReport(int Records, long? BadSeq, bool TornTail);

/// <summary>The black box: an append-only, write-ahead journal in <c>.ample/blackbox/</c>, separate from the history database. Records are JSON lines with a CRC32C and a SHA-256 hash chain; a torn last line is detected and dropped on open. A failing journal write blocks the command (fail closed).</summary>
public sealed class Journal : IDisposable
{
    private const string Zero = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string SequenceFile = "sequence.highwater";
    private readonly IFileSystem fs;
    private readonly IClock clock;
    private readonly string folder;
    private readonly string projectRoot;
    private readonly string projectId;
    private readonly JournalOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long nextSeq = 1;
    private long headSeq;
    private string lastHash = Zero;
    private string segment = string.Empty;
    private long segmentSize;
    private bool corruptTail;
    private readonly Queue<string> tail = new();

    private Journal(IFileSystem fs, IClock clock, string root, string projectId, JournalOptions options)
    {
        this.fs = fs;
        this.clock = clock;
        projectRoot = root;
        folder = Path.Combine(root, ProjectLayout.AmpleFolder, "blackbox");
        this.projectId = projectId;
        this.options = options;
    }

    /// <summary>Opens the journal of a project; a torn last line is repaired (the complete records stay).</summary>
    public static async ValueTask<Journal> OpenAsync(IFileSystem fs, IClock clock, string projectRoot, string projectId, JournalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        Journal j = new(fs, clock, projectRoot, projectId, options ?? new JournalOptions());
        fs.CreateDirectory(j.folder);
        DataCipher? cipher = j.Cipher();
        List<string> segments = [.. fs.GetFiles(j.folder).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        long observedSequence = await ReadSummaryHighWaterAsync(fs, j.folder, cancellationToken).ConfigureAwait(false);
        foreach (string seg in segments)
        {
            SegmentRead read = await ReadSegmentAsync(fs, seg, cipher, cancellationToken).ConfigureAwait(false);
            if (read.Records.Count > 0)
            {
                j.nextSeq = read.Records[^1].Seq + 1;
                j.headSeq = read.Records[^1].Seq;
                observedSequence = Math.Max(observedSequence, read.Records[^1].Seq);
                j.lastHash = Sha(read.Lines[^1]);
                foreach (string l in read.Lines.TakeLast(j.options.MirrorRecords))
                {
                    j.Remember(l);
                }
            }

            if (read.Torn && seg == segments[^1])
            {
                // Only an incomplete last line (a crash while writing) is repaired.
                await AtomicFile.WriteTextAsync(fs, seg, string.Concat(read.Lines.Select(l => (cipher?.SealLine(l) ?? l) + "\n")), cancellationToken).ConfigureAwait(false);
            }

            if (read.Corrupt && seg == segments[^1])
            {
                // A damaged record is evidence: the file is left untouched, new records go to a new segment with numbers that cannot clash.
                j.corruptTail = true;
                observedSequence = Math.Max(observedSequence, (read.Records.Count > 0 ? read.Records[^1].Seq : 0) + read.Remaining);
            }
        }

        string highWaterPath = Path.Combine(j.folder, SequenceFile);
        if (fs.FileExists(highWaterPath))
        {
            string highWaterText = (await fs.ReadAllTextAsync(highWaterPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (!long.TryParse(highWaterText, NumberStyles.None, CultureInfo.InvariantCulture, out long highWater) || highWater < 0 || highWater > ReduxRecordReference.MaximumSequence)
            {
                throw new AmpleException("AMP5230", "AMPLE Redux cannot allocate a safe record reference because its sequence marker is damaged.");
            }

            observedSequence = Math.Max(observedSequence, highWater);
        }

        if (observedSequence > ReduxRecordReference.MaximumSequence) throw new AmpleException("AMP5230", "AMPLE Redux has reached the maximum AR reference number.");
        j.nextSeq = Math.Max(j.nextSeq, observedSequence + 1);
        if (!fs.FileExists(highWaterPath)) await AtomicFile.WriteTextAsync(fs, highWaterPath, observedSequence.ToString(CultureInfo.InvariantCulture) + "\n", cancellationToken).ConfigureAwait(false);

        if (segments.Count > 0 && !j.corruptTail)
        {
            j.segment = segments[^1];
            j.segmentSize = Encoding.UTF8.GetByteCount(string.Concat((await ReadSegmentAsync(fs, j.segment, cipher, cancellationToken).ConfigureAwait(false)).Lines.Select(l => (cipher?.SealLine(l) ?? l) + "\n")));
        }

        return j;
    }

    /// <summary>Previews archive compaction while holding the journal writer lock.</summary>
    public async ValueTask<CompactionPreview> PreviewCompactionAsync(BlackBoxSettings settings, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await JournalRetention.PreviewAsync(fs, projectRoot, clock.UtcNow, settings, cancellationToken).ConfigureAwait(false)) with { ThroughSequence = headSeq }; }
        finally { gate.Release(); }
    }

    /// <summary>Compacts the archive while holding the journal writer lock.</summary>
    public async ValueTask<CompactionResult> CompactAsync(BlackBoxSettings settings, CancellationToken cancellationToken)
        => await CompactAsync(settings, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Compacts only if no journal records have been added since the displayed preview.</summary>
    public async ValueTask<CompactionResult> CompactAsync(BlackBoxSettings settings, long? expectedSequence, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedSequence.HasValue && headSeq != expectedSequence.Value) throw new AmpleException("AMP5285", "AMPLE Redux changed after the compaction preview; preview it again before compacting.");
            return await JournalRetention.CompactAsync(fs, projectRoot, clock.UtcNow, settings, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Appends a record and makes it durable before returning. Throws AMP5230 when the journal cannot be written.</summary>
    public async ValueTask<JournalRecord> AppendAsync(string actor, string kind, string phase, string correlation, string payloadJson, string? preImageJson = null, string? error = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (nextSeq > ReduxRecordReference.MaximumSequence) throw new AmpleException("AMP5230", "AMPLE Redux has reached the maximum AR reference number.");
            long reservedSequence = nextSeq;
            try
            {
                await AtomicFile.WriteTextAsync(fs, Path.Combine(folder, SequenceFile), reservedSequence.ToString(CultureInfo.InvariantCulture) + "\n", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AmpleException("AMP5230", "AMPLE Redux could not reserve the next AR reference, so the command was not run: " + ex.Message);
            }

            nextSeq++;
            JournalRecord record = new(reservedSequence, lastHash, clock.UtcNow, actor, kind, phase, correlation, payloadJson, preImageJson, error);
            string json = Serialize(record);
            string line = json + "\t" + Crc32c(Encoding.UTF8.GetBytes(json)).ToString("x8", CultureInfo.InvariantCulture);
            try
            {
                if (segment.Length == 0 || segmentSize >= options.SegmentBytes)
                {
                    segment = Path.Combine(folder, reservedSequence.ToString("D10", CultureInfo.InvariantCulture) + ".seg");
                    segmentSize = 0;
                }

                byte[] bytes = Encoding.UTF8.GetBytes((Cipher()?.SealLine(line) ?? line) + "\n");
                await using (Stream stream = fs.OpenAppend(segment))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                segmentSize += bytes.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AmpleException("AMP5230", "The black box journal could not be written, so the command was not run: " + ex.Message);
            }

            lastHash = Sha(line);
            headSeq = record.Seq;
            Remember(line);
            if (phase is "ok" or "failed" or "tombstone")
            {
                await MirrorAsync(cancellationToken).ConfigureAwait(false);
            }

            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads all records of all segments, oldest first (a torn last line is ignored).</summary>
    public async ValueTask<IReadOnlyList<JournalRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<JournalRecord> all = [];
            DataCipher? cipher = Cipher();
            foreach (string seg in fs.GetFiles(folder).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                all.AddRange((await ReadSegmentAsync(fs, seg, cipher, cancellationToken).ConfigureAwait(false)).Records);
            }

            return all;
        }
        finally { gate.Release(); }
    }

    /// <summary>Reads a newest-first, bounded page without holding the whole journal in memory.</summary>
    public async ValueTask<IReadOnlyList<JournalRecord>> ReadPageAsync(long throughSequence, long beforeSequence, Func<JournalRecord, bool> matches, int maximumResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (maximumResults is < 1 or > 201) throw new ArgumentOutOfRangeException(nameof(maximumResults));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<JournalRecord> page = [];
            DataCipher? cipher = Cipher();
            IEnumerable<string> segments = fs.GetFiles(folder)
                .Where(file => file.EndsWith(".seg", StringComparison.Ordinal))
                .OrderByDescending(file => long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) ? sequence : 0);
            foreach (string segmentPath in segments)
            {
                SegmentRead segmentRead = await ReadSegmentAsync(fs, segmentPath, cipher, cancellationToken).ConfigureAwait(false);
                foreach (JournalRecord record in segmentRead.Records.AsEnumerable().Reverse())
                {
                    if (record.Seq <= throughSequence && record.Seq < beforeSequence && matches(record)) page.Add(record);
                    if (page.Count == maximumResults) return page;
                }
            }

            return page;
        }
        finally { gate.Release(); }
    }

    /// <summary>Checks checksums and the hash chain of everything that is on disk.</summary>
    public async ValueTask<VerifyReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int count = 0;
            string? previousLine = null;
            bool torn = false;
            DataCipher? cipher = Cipher();
            foreach (string seg in fs.GetFiles(folder).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                string text = await fs.ReadAllTextAsync(seg, cancellationToken).ConfigureAwait(false);
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0)
                    {
                        continue;
                    }

                    string plain = Plain(cipher, lines[i]);
                    JournalRecord? record = Parse(plain);
                    bool lastLine = i >= lines.Length - 2 && seg == fs.GetFiles(folder).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Max();
                    if (record is null)
                    {
                        if (lastLine && !text.EndsWith('\n'))
                        {
                            torn = true;
                            continue;
                        }

                        return new VerifyReport(count, count + 1, torn);
                    }

                    if (previousLine is not null && record.Prev != Sha(previousLine))
                    {
                        return new VerifyReport(count, record.Seq, torn);
                    }

                    previousLine = plain;
                    count++;
                }
            }

            return new VerifyReport(count, null, torn);
        }
        finally { gate.Release(); }
    }

    /// <summary>Lists commands that started but never finished.</summary>
    public async ValueTask<IReadOnlyList<InterruptedCommand>> FindInterruptedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JournalRecord> all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> done = [.. all.Where(r => r.Phase is "ok" or "failed" or "resolved").Select(r => r.Correlation)];
        return [.. all.Where(r => r.Phase == "start" && !done.Contains(r.Correlation)).Select(r => new InterruptedCommand(r))];
    }

    /// <summary>Reads the mirror of a project's journal tail from the user's home, for the case that the project folder itself is gone.</summary>
    public static IReadOnlyList<JournalRecord> ReadMirror(IFileSystem fs, string mirrorRoot, string projectId, DataCipher? cipher = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        string path = Path.Combine(mirrorRoot, projectId, "tail.jsonl");
        return fs.FileExists(path) ? [.. fs.ReadAllTextAsync(path, CancellationToken.None).AsTask().Result.Split('\n').Select(l => Parse(Plain(cipher, l))).OfType<JournalRecord>()] : [];
    }

    internal static async ValueTask<SegmentRead> ReadSegmentAsync(IFileSystem fs, string path, DataCipher? cipher, CancellationToken cancellationToken)
    {
        string text = await fs.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        List<JournalRecord> records = [];
        List<string> lines = [];
        string[] all = [.. text.Split('\n').Where(l => l.Length > 0)];
        bool endsClean = text.Length == 0 || text.EndsWith('\n');
        for (int i = 0; i < all.Length; i++)
        {
            string plain = Plain(cipher, all[i]);
            JournalRecord? record = Parse(plain);
            if (record is null)
            {
                bool incompleteLast = i == all.Length - 1 && !endsClean;
                return new SegmentRead(records, lines, incompleteLast, !incompleteLast, all.Length - i);
            }

            records.Add(record);
            lines.Add(plain);
        }

        return new SegmentRead(records, lines, !endsClean, false, 0);
    }

    private static async ValueTask<long> ReadSummaryHighWaterAsync(IFileSystem fs, string folder, CancellationToken cancellationToken)
    {
        long maximum = 0;
        foreach (string summary in fs.GetFiles(folder).Where(f => f.EndsWith(".sum", StringComparison.Ordinal)))
        {
            string name = Path.GetFileNameWithoutExtension(summary);
            if (!long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out long firstSequence) || firstSequence is < 1 or > ReduxRecordReference.MaximumSequence)
            {
                throw new AmpleException("AMP5230", "AMPLE Redux found a summary without a valid AR sequence range.");
            }

            string[] lines = (await fs.ReadAllTextAsync(summary, cancellationToken).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            long count = 0;
            foreach (string line in lines.Where(line => !line.StartsWith('#')))
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 4 || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long groupCount) || groupCount < 1)
                {
                    throw new AmpleException("AMP5230", "AMPLE Redux found a damaged summary and cannot prove the next AR sequence.");
                }

                count = checked(count + groupCount);
            }

            if (count > 0) maximum = Math.Max(maximum, checked(firstSequence + count - 1));
        }

        return maximum;
    }

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();

    /// <summary>The number and the hash (of the plain line) of the newest record: what a signature covers.</summary>
    public (long Seq, string Hash) Head => (headSeq, lastHash);

    /// <summary>The hash of the plain line of every record on disk, by sequence number (to check signatures against).</summary>
    public async ValueTask<IReadOnlyDictionary<long, string>> LineHashesAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<long, string> hashes = [];
        DataCipher? cipher = Cipher();
        foreach (string seg in fs.GetFiles(folder).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            SegmentRead read = await ReadSegmentAsync(fs, seg, cipher, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < read.Records.Count; i++)
            {
                hashes[read.Records[i].Seq] = Sha(read.Lines[i]);
            }
        }

        return hashes;
    }

    private DataCipher? Cipher() => options.Cipher ?? ProjectEncryption.CipherFor(projectRoot);

    private static string Plain(DataCipher? cipher, string line)
    {
        if (!DataCipher.IsSealedLine(line))
        {
            return line;
        }

        try
        {
            return cipher is null ? line : cipher.OpenLine(line);
        }
        catch (AmpleException)
        {
            return line;
        }
    }

    private static string Serialize(JournalRecord r) => JsonSerializer.Serialize(new { seq = r.Seq, prev = r.Prev, at = r.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), actor = r.Actor, kind = r.Kind, phase = r.Phase, correlation = r.Correlation, payload = r.PayloadJson, preImage = r.PreImageJson, error = r.Error });

    private static JournalRecord? Parse(string line)
    {
        int tab = line.LastIndexOf('\t');
        if (tab < 0 || line.Length - tab - 1 != 8 || !uint.TryParse(line.AsSpan(tab + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc))
        {
            return null;
        }

        string json = line[..tab];
        if (Crc32c(Encoding.UTF8.GetBytes(json)) != crc)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            static string? Str(JsonElement e, string n) => e.GetProperty(n).ValueKind == JsonValueKind.Null ? null : e.GetProperty(n).GetString();
            return new JournalRecord(r.GetProperty("seq").GetInt64(), r.GetProperty("prev").GetString()!, DateTimeOffset.Parse(r.GetProperty("at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetProperty("actor").GetString()!, r.GetProperty("kind").GetString()!, r.GetProperty("phase").GetString()!, r.GetProperty("correlation").GetString()!, r.GetProperty("payload").GetString()!, Str(r, "preImage"), Str(r, "error"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Sha(string line) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(line)));

    private void Remember(string line)
    {
        tail.Enqueue(line);
        while (tail.Count > options.MirrorRecords)
        {
            tail.Dequeue();
        }
    }

    private async ValueTask MirrorAsync(CancellationToken cancellationToken)
    {
        if (options.MirrorRoot is null)
        {
            return;
        }

        try
        {
            string dir = Path.Combine(options.MirrorRoot, projectId);
            fs.CreateDirectory(dir);
            DataCipher? cipher = Cipher();
            await AtomicFile.WriteTextAsync(fs, Path.Combine(dir, "tail.jsonl"), string.Concat(tail.Select(l => (cipher?.SealLine(l) ?? l) + "\n")), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The mirror is a second copy; failing to write it must not block the work.
        }
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0x82F63B78u : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>Computes CRC32C (Castagnoli).</summary>
    public static uint Crc32c(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}
