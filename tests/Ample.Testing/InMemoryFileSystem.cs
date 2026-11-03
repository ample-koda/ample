using System.Text;
using Ample.Core.Abstractions;

namespace Ample.Testing;

/// <summary>Provides an in-memory file system for tests: files as bytes, directories, links, locks and a configurable amount of free space.</summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private readonly HashSet<string> directories = new(StringComparer.Ordinal);
    private readonly HashSet<string> locks = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> links = new(StringComparer.Ordinal);

    /// <summary>Registers a symbolic link: the path leads to the target.</summary>
    public void AddLink(string path, string target) => links[Norm(path)] = target;

    /// <summary>Gets or sets the free space reported for every path.</summary>
    public long FreeSpace { get; set; } = long.MaxValue;

    /// <inheritdoc />
    public bool FileExists(string path) => files.ContainsKey(Norm(path));

    /// <inheritdoc />
    public bool DirectoryExists(string path) => directories.Contains(Norm(path));

    /// <inheritdoc />
    public ValueTask<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Encoding.UTF8.GetString(files[Norm(path)]));
    }

    /// <inheritdoc />
    public ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(files[Norm(path)]);
    }

    /// <inheritdoc />
    public ValueTask WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Put(path, Encoding.UTF8.GetBytes(contents));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteAllBytesDurableAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Put(path, contents.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public Stream OpenRead(string path) => new MemoryStream(files[Norm(path)], writable: false);

    /// <inheritdoc />
    public Stream CreateDurableWriteStream(string path)
    {
        string p = Norm(path);
        CreateDirectory(Path.GetDirectoryName(p) ?? string.Empty);
        files[p] = [];
        return new CommitOnDispose(data => files[p] = data);
    }

    /// <inheritdoc />
    public Stream OpenAppend(string path)
    {
        string p = Norm(path);
        CreateDirectory(Path.GetDirectoryName(p) ?? string.Empty);
        byte[] existing = files.GetValueOrDefault(p) ?? [];
        files[p] = existing;
        return new AppendTo(existing, data => files[p] = data);
    }

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        for (string? p = Norm(path); !string.IsNullOrEmpty(p) && directories.Add(p); p = Path.GetDirectoryName(p))
        {
        }
    }

    /// <inheritdoc />
    public void Move(string source, string destination, bool overwrite)
    {
        string from = Norm(source), to = Norm(destination);
        if (!files.TryGetValue(from, out byte[]? data))
        {
            throw new FileNotFoundException("No such file.", source);
        }

        if (files.ContainsKey(to) && !overwrite)
        {
            throw new IOException("The destination exists.");
        }

        files.Remove(from);
        files[to] = data;
    }

    /// <inheritdoc />
    public void MoveDirectory(string source, string destination)
    {
        string from = Norm(source), to = Norm(destination);
        if (!directories.Contains(from)) throw new DirectoryNotFoundException(source);
        if (directories.Contains(to) || files.ContainsKey(to)) throw new IOException("The destination exists.");
        string[] movedDirectories = directories.Where(path => path == from || path.StartsWith(from + "/", StringComparison.Ordinal)).ToArray();
        string[] movedFiles = files.Keys.Where(path => path.StartsWith(from + "/", StringComparison.Ordinal)).ToArray();
        foreach (string path in movedDirectories) directories.Remove(path);
        foreach (string path in movedFiles) { byte[] data = files[path]; files.Remove(path); files[to + path[from.Length..]] = data; }
        foreach (string path in movedDirectories) directories.Add(to + path[from.Length..]);
    }

    /// <inheritdoc />
    public void DeleteFile(string path) => files.Remove(Norm(path));

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive)
    {
        string p = Norm(path);
        foreach (string f in files.Keys.Where(k => k.StartsWith(p + "/", StringComparison.Ordinal)).ToList())
        {
            files.Remove(f);
        }

        foreach (string d in directories.Where(k => k == p || k.StartsWith(p + "/", StringComparison.Ordinal)).ToList())
        {
            directories.Remove(d);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetFiles(string directory) => [.. files.Keys.Where(k => Path.GetDirectoryName(k)?.Replace('\\', '/') == Norm(directory)).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyList<string> GetDirectories(string directory) => [.. directories.Where(k => Path.GetDirectoryName(k)?.Replace('\\', '/') == Norm(directory)).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public void FlushDirectory(string directory)
    {
    }

    /// <inheritdoc />
    public long GetFreeSpace(string path) => FreeSpace;

    /// <inheritdoc />
    public string? GetLinkTarget(string path) => links.GetValueOrDefault(Norm(path));

    /// <inheritdoc />
    public IDisposable? TryLockFile(string path)
    {
        string p = Norm(path);
        if (!locks.Add(p))
        {
            return null;
        }

        Put(p, files.GetValueOrDefault(p) ?? []);
        return new Release(() => locks.Remove(p));
    }

    /// <summary>Lists every file path (for assertions).</summary>
    public IReadOnlyList<string> AllFiles => [.. files.Keys.Order(StringComparer.Ordinal)];

    private static string Norm(string path) => path.Replace('\\', '/').TrimEnd('/');

    private void Put(string path, byte[] data)
    {
        string p = Norm(path);
        CreateDirectory(Path.GetDirectoryName(p) ?? string.Empty);
        files[p] = data;
    }

    private sealed class CommitOnDispose(Action<byte[]> commit) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                commit(ToArray());
            }

            base.Dispose(disposing);
        }
    }

    private sealed class AppendTo : MemoryStream
    {
        private readonly byte[] original;
        private readonly Action<byte[]> commit;

        public AppendTo(byte[] original, Action<byte[]> commit)
        {
            this.original = original;
            this.commit = commit;
        }

        public override void Flush() => commit([.. original, .. ToArray()]);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
