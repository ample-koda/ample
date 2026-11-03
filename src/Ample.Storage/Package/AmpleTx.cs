using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;

namespace Ample.Storage.Package;

/// <summary>
/// The <c>.ampletx</c> file: one text file that holds a portable snapshot of a project. The head says what it is (<c>#AMPLETX 1</c>, <c>#kind full|derived</c>) and carries the manifest as one JSON line; then every file follows as an
/// entry, text files line by line (each line starts with <c>|</c>) and binary files as Base64 lines. The format never changes the bytes of a file: every entry names its SHA-256 and size, and reading it back checks both.
/// </summary>
public static class AmpleTx
{
    /// <summary>The format version this code writes and reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>The roles an entry can have.</summary>
    public static class Roles
    {
        /// <summary>The song text (<c>.amplet</c>).</summary>
        public const string Song = "song";

        /// <summary>A file the song reads (a sample, a sequence, lyrics, a MIDI file, a SoundFont, a module).</summary>
        public const string Source = "source";

        /// <summary>A rendered audio file that stands in for a part of the graph (Derived export).</summary>
        public const string Derived = "derived";

        /// <summary>The final master of the saved state, for instant playback and comparison.</summary>
        public const string Master = "master-preview";

        /// <summary>Project settings that influence the sound (plugin defaults).</summary>
        public const string Settings = "settings";
    }

    /// <summary>The bytes of 57 binary bytes become one line of 76 Base64 characters.</summary>
    internal const int LineBytes = 57;
}

/// <summary>What the writer is asked to put in the package.</summary>
/// <param name="Path">The path in the package (project-relative with <c>/</c>; packs as <c>pack/...</c>).</param>
/// <param name="Role">One of <see cref="AmpleTx.Roles"/>.</param>
/// <param name="MediaType">The media type, for example <c>audio/wav</c> or <c>text/plain</c>.</param>
/// <param name="OriginalName">The file name where it came from.</param>
/// <param name="OriginalPath">The path where it came from (relative to the project, or a pack reference).</param>
/// <param name="Text">The text, when the content is held in memory as text.</param>
/// <param name="Bytes">The bytes, when held in memory.</param>
/// <param name="File">A file on disk to read, when the content is big.</param>
/// <param name="Info">Extra facts for this entry (how it was made, what it replaced), shown when the package is inspected.</param>
public sealed record TxSource(string Path, string Role, string MediaType, string OriginalName, string OriginalPath, string? Text = null, byte[]? Bytes = null, string? File = null, JsonObject? Info = null);

/// <summary>An entry of a package as it is read back.</summary>
public sealed class TxEntry
{
    /// <summary>Gets the path in the package.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the role.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Gets <c>text</c> or <c>base64</c>.</summary>
    public string Encoding { get; init; } = "text";

    /// <summary>Gets the SHA-256 of the original bytes, lower-case hex.</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Gets the size of the original bytes.</summary>
    public long Size { get; init; }

    /// <summary>Gets the media type.</summary>
    public string MediaType { get; init; } = string.Empty;

    /// <summary>Gets the original file name.</summary>
    public string OriginalName { get; init; } = string.Empty;

    /// <summary>Gets the original path.</summary>
    public string OriginalPath { get; init; } = string.Empty;

    /// <summary>Gets whether a text entry ended with a line break.</summary>
    public bool TrailingNewline { get; init; }

    /// <summary>Gets the number of bytes of the body in the package file.</summary>
    public long BodyBytes { get; init; }

    /// <summary>Gets extra facts about the entry.</summary>
    public JsonObject? Info { get; init; }

    /// <summary>Gets the offset of the body in the package file.</summary>
    public long BodyOffset { get; internal set; }
}

/// <summary>Writes a package.</summary>
public static class TxWriter
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Writes the package file. The entries come in a fixed order (role, then path), so the same project gives the same file.</summary>
    /// <returns>The SHA-256 of the written package file.</returns>
    public static string Write(string outputPath, string kind, JsonObject manifest, IReadOnlyList<TxSource> sources)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sources);
        List<(TxSource Source, TxEntry Entry, byte[]? Held)> prepared = [];
        foreach (TxSource s in sources.OrderBy(x => RoleOrder(x.Role)).ThenBy(x => x.Path, StringComparer.Ordinal))
        {
            prepared.Add(Prepare(s));
        }

        if (prepared.Select(p => p.Entry.Path).Distinct(StringComparer.Ordinal).Count() != prepared.Count)
        {
            throw new AmpleException("AMP5420", "A package cannot hold two files with the same path.");
        }

        JsonObject head = (JsonObject)manifest.DeepClone();
        head["packageFormat"] = AmpleTx.FormatVersion;
        head["kind"] = kind;
        head["entries"] = new JsonArray(prepared.Select(p => (JsonNode)EntryJson(p.Entry)).ToArray());
        string temporary = outputPath + ".part";
        string result;
        using (SHA256 hash = SHA256.Create())
        {
            using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (CryptoStream crypto = new(file, hash, CryptoStreamMode.Write, leaveOpen: true))
            {
                void Line(string text) { byte[] b = Utf8.GetBytes(text + "\n"); crypto.Write(b, 0, b.Length); }
                Line($"#AMPLETX {AmpleTx.FormatVersion}");
                Line("#kind " + kind);
                Line("#manifest " + head.ToJsonString());
                foreach ((TxSource source, TxEntry entry, byte[]? held) in prepared)
                {
                    Line("#entry " + EntryJson(entry).ToJsonString());
                    if (entry.Encoding == "text")
                    {
                        string text = source.Text ?? Utf8.GetString(held!);
                        string[] lines = text.Split('\n');
                        int count = entry.TrailingNewline ? lines.Length - 1 : lines.Length;
                        for (int i = 0; i < count; i++)
                        {
                            Line("|" + lines[i]);
                        }
                    }
                    else
                    {
                        using Stream content = held is not null ? new MemoryStream(held) : new FileStream(source.File!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                        byte[] buffer = new byte[AmpleTx.LineBytes * 1024];
                        int read;
                        while ((read = ReadFull(content, buffer)) > 0)
                        {
                            StringBuilder chunk = new(read / 3 * 4 + read / AmpleTx.LineBytes * 2 + 8);
                            for (int at = 0; at < read; at += AmpleTx.LineBytes)
                            {
                                chunk.Append(Convert.ToBase64String(buffer, at, Math.Min(AmpleTx.LineBytes, read - at))).Append('\n');
                            }

                            byte[] bytes = Utf8.GetBytes(chunk.ToString());
                            crypto.Write(bytes, 0, bytes.Length);
                        }
                    }

                    Line("#end");
                }

                Line("#endpackage");
                crypto.FlushFinalBlock();
                file.Flush(true);
            }

            result = Convert.ToHexStringLower(hash.Hash!);
        }

        File.Move(temporary, outputPath, true);
        return result;
    }

    private static int RoleOrder(string role) => role switch { AmpleTx.Roles.Song => 0, AmpleTx.Roles.Settings => 1, AmpleTx.Roles.Source => 2, AmpleTx.Roles.Derived => 3, AmpleTx.Roles.Master => 4, _ => 5 };

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static JsonObject EntryJson(TxEntry e)
    {
        JsonObject o = new()
        {
            ["path"] = e.Path,
            ["role"] = e.Role,
            ["encoding"] = e.Encoding,
            ["sha256"] = e.Sha256,
            ["size"] = e.Size,
            ["mediaType"] = e.MediaType,
            ["originalName"] = e.OriginalName,
            ["originalPath"] = e.OriginalPath,
            ["trailingNewline"] = e.TrailingNewline,
            ["bodyBytes"] = e.BodyBytes,
        };
        if (e.Info is not null)
        {
            o["info"] = e.Info.DeepClone();
        }

        return o;
    }

    private static (TxSource, TxEntry, byte[]?) Prepare(TxSource s)
    {
        byte[]? held = s.Bytes;
        if (s.Text is not null)
        {
            held = Utf8.GetBytes(s.Text);
        }

        long size;
        string sha;
        if (held is not null)
        {
            size = held.Length;
            sha = Convert.ToHexStringLower(SHA256.HashData(held));
        }
        else
        {
            using FileStream stream = new(s.File ?? throw new AmpleException("AMP5420", "A package file needs content."), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            size = stream.Length;
            sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        bool asText = held is not null && IsPlainText(held);
        long body;
        bool trailing = false;
        if (asText)
        {
            string text = Utf8.GetString(held!);
            trailing = text.EndsWith('\n');
            string[] lines = text.Split('\n');
            int count = trailing ? lines.Length - 1 : lines.Length;
            body = 0;
            for (int i = 0; i < count; i++)
            {
                body += 1 + Utf8.GetByteCount(lines[i]) + 1;
            }
        }
        else
        {
            long lines = (size + AmpleTx.LineBytes - 1) / AmpleTx.LineBytes;
            body = ((size + 2) / 3 * 4) + lines;
        }

        TxEntry entry = new()
        {
            Path = s.Path,
            Role = s.Role,
            Encoding = asText ? "text" : "base64",
            Sha256 = sha,
            Size = size,
            MediaType = s.MediaType,
            OriginalName = s.OriginalName,
            OriginalPath = s.OriginalPath,
            TrailingNewline = trailing,
            BodyBytes = body,
            Info = s.Info,
        };
        return (s, entry, held);
    }

    /// <summary>Text that can be stored line by line without changing a byte: valid UTF-8, no byte order mark, no carriage returns, no control characters other than tab.</summary>
    private static bool IsPlainText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return false;
        }

        foreach (byte b in bytes)
        {
            if (b == '\r' || b == 0 || (b < 0x20 && b != '\n' && b != '\t'))
            {
                return false;
            }
        }

        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            return Utf8.GetBytes(text).AsSpan().SequenceEqual(bytes);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>A package that was read: its head, its manifest and its entries (the bodies are read on demand).</summary>
public sealed class TxPackage
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private TxPackage(string path)
    {
        FilePath = path;
    }

    /// <summary>Gets the package file.</summary>
    public string FilePath { get; }

    /// <summary>Gets the format version of the package.</summary>
    public int Version { get; private set; }

    /// <summary><c>full</c> or <c>derived</c>.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>Gets the manifest.</summary>
    public JsonObject Manifest { get; private set; } = [];

    /// <summary>Gets the entries.</summary>
    public IReadOnlyList<TxEntry> Entries { get; private set; } = [];

    /// <summary>Opens a package and reads its index (the bodies are skipped, so this is fast for big packages).</summary>
    public static TxPackage Open(string path)
    {
        TxPackage package = new(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long position = 0;
        string Read()
        {
            List<byte> line = [];
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                position++;
                if (b == '\n')
                {
                    return Utf8.GetString([.. line]);
                }

                line.Add((byte)b);
            }

            throw new AmpleException("AMP5421", "The package ends too early; it is not a complete .ampletx file.");
        }

        string first = Read();
        if (!first.StartsWith("#AMPLETX ", StringComparison.Ordinal) || !int.TryParse(first.AsSpan(9), out int version))
        {
            throw new AmpleException("AMP5421", "This is not an .ampletx file (it does not start with #AMPLETX).");
        }

        package.Version = version;
        if (version > AmpleTx.FormatVersion)
        {
            throw new AmpleException("AMP5422", $"This package uses .ampletx format {version}; this AMPLE reads format {AmpleTx.FormatVersion} and older.");
        }

        string kindLine = Read();
        if (!kindLine.StartsWith("#kind ", StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5421", "The package has no #kind line.");
        }

        package.Kind = kindLine[6..].Trim();
        string manifestLine = Read();
        if (!manifestLine.StartsWith("#manifest ", StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5421", "The package has no #manifest line.");
        }

        package.Manifest = JsonNode.Parse(manifestLine[10..]) as JsonObject ?? throw new AmpleException("AMP5421", "The manifest of the package is not valid.");
        List<TxEntry> entries = [];
        while (true)
        {
            string line = Read();
            if (line == "#endpackage")
            {
                break;
            }

            if (!line.StartsWith("#entry ", StringComparison.Ordinal))
            {
                throw new AmpleException("AMP5421", "The package has a line that is not an entry head: " + (line.Length > 40 ? line[..40] : line));
            }

            JsonObject o = JsonNode.Parse(line[7..])!.AsObject();
            TxEntry entry = new()
            {
                Path = o["path"]!.GetValue<string>(),
                Role = o["role"]!.GetValue<string>(),
                Encoding = o["encoding"]!.GetValue<string>(),
                Sha256 = o["sha256"]!.GetValue<string>(),
                Size = o["size"]!.GetValue<long>(),
                MediaType = o["mediaType"]?.GetValue<string>() ?? string.Empty,
                OriginalName = o["originalName"]?.GetValue<string>() ?? string.Empty,
                OriginalPath = o["originalPath"]?.GetValue<string>() ?? string.Empty,
                TrailingNewline = o["trailingNewline"]?.GetValue<bool>() ?? false,
                BodyBytes = o["bodyBytes"]!.GetValue<long>(),
                Info = o["info"] as JsonObject,
                BodyOffset = position,
            };
            stream.Seek(entry.BodyBytes, SeekOrigin.Current);
            position += entry.BodyBytes;
            if (Read() != "#end")
            {
                throw new AmpleException("AMP5421", $"The entry {entry.Path} does not end where it should: the package is damaged.");
            }

            entries.Add(entry);
        }

        package.Entries = entries;
        return package;
    }

    /// <summary>Finds an entry by its path.</summary>
    public TxEntry? Find(string path) => Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));

    /// <summary>Reads the bytes of an entry into a stream and checks the size and the SHA-256.</summary>
    public void CopyTo(TxEntry entry, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        using FileStream stream = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        stream.Seek(entry.BodyOffset, SeekOrigin.Begin);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        void Put(byte[] data, int count)
        {
            hash.AppendData(data, 0, count);
            destination.Write(data, 0, count);
            written += count;
        }

        byte[] buffer = new byte[1 << 16];
        long left = entry.BodyBytes;
        if (entry.Encoding == "text")
        {
            byte[] raw = new byte[(int)left];
            ReadExactly(stream, raw);
            string[] lines = Utf8.GetString(raw).Split('\n', StringSplitOptions.None);
            // every body line is "|text" followed by a line break; the split leaves one empty piece at the end
            List<string> pieces = [];
            for (int i = 0; i < lines.Length - 1; i++)
            {
                pieces.Add(lines[i].StartsWith('|') ? lines[i][1..] : throw new AmpleException("AMP5421", $"The text entry {entry.Path} has a line without its | mark."));
            }

            string joined = string.Join('\n', pieces) + (entry.TrailingNewline && pieces.Count > 0 ? "\n" : string.Empty);
            byte[] bytes = Utf8.GetBytes(joined);
            Put(bytes, bytes.Length);
        }
        else
        {
            StringBuilder pending = new();
            while (left > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0)
                {
                    throw new AmpleException("AMP5421", $"The entry {entry.Path} ends too early.");
                }

                left -= read;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != '\n' && buffer[i] != '\r')
                    {
                        pending.Append((char)buffer[i]);
                    }
                }

                int usable = pending.Length / 4 * 4;
                if (usable > 0)
                {
                    byte[] decoded = Convert.FromBase64String(pending.ToString(0, usable));
                    pending.Remove(0, usable);
                    Put(decoded, decoded.Length);
                }
            }

            if (pending.Length > 0)
            {
                byte[] decoded = Convert.FromBase64String(pending.ToString());
                Put(decoded, decoded.Length);
            }

        }

        string sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (written != entry.Size || !string.Equals(sha, entry.Sha256, StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5423", $"The file {entry.Path} in the package does not match its SHA-256 and size: the package is damaged.");
        }
    }

    /// <summary>Reads an entry that is text.</summary>
    public string ReadText(TxEntry entry)
    {
        using MemoryStream memory = new();
        CopyTo(entry, memory);
        return Utf8.GetString(memory.ToArray());
    }

    /// <summary>Reads a whole entry into memory (for small files only).</summary>
    public byte[] ReadBytes(TxEntry entry)
    {
        using MemoryStream memory = new();
        CopyTo(entry, memory);
        return memory.ToArray();
    }

    /// <summary>Gets the SHA-256 of the package file.</summary>
    public string FileSha256()
    {
        using FileStream stream = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                throw new AmpleException("AMP5421", "The package ends too early.");
            }

            total += read;
        }
    }

    /// <summary>Makes the plain package file out of a <c>.ampletx.zip</c> (the package that is inside it is written to <paramref name="folder"/>); a plain package is returned as it is.</summary>
    public static string Unwrap(string path, string folder)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(path);
        System.IO.Compression.ZipArchiveEntry inner = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".ampletx", StringComparison.OrdinalIgnoreCase)) ?? throw new AmpleException("AMP5421", "The zip does not contain an .ampletx file.");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, inner.Name);
        System.IO.Compression.ZipFileExtensions.ExtractToFile(inner, target, true);
        return target;
    }

    /// <summary>Puts the package in a normal zip next to it, with the highest compression (the package itself stays exactly as it is).</summary>
    public static string Compress(string packagePath)
    {
        string zipPath = packagePath + ".zip";
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, packagePath, Path.GetFileName(packagePath), System.IO.Compression.CompressionLevel.SmallestSize);
        return zipPath;
    }
}
