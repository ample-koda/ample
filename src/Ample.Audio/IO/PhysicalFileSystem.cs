using System.Text;
using Ample.Core.Abstractions;

namespace Ample.Audio.IO;

/// <summary>The real file system.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public async ValueTask<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) => await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) => await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask WriteAllBytesDurableAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <inheritdoc />
    public Stream CreateDurableWriteStream(string path) => new DurableStream(path);

    /// <inheritdoc />
    public Stream OpenAppend(string path) => new AppendStream(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);

    /// <inheritdoc />
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    /// <inheritdoc />
    public IReadOnlyList<string> GetFiles(string directory) => [.. Directory.GetFiles(directory).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyList<string> GetDirectories(string directory) => [.. Directory.GetDirectories(directory).Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // Directory flushing is best effort on platforms that do not allow opening directories.
        }
    }

    /// <inheritdoc />
    public long GetFreeSpace(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? Path.GetFullPath(path);
        return new DriveInfo(root).AvailableFreeSpace;
    }

    /// <inheritdoc />
    public string? GetLinkTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }

    /// <inheritdoc />
    public IDisposable? TryLockFile(string path)
    {
        try
        {
            return new HeldLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class DurableStream(string path) : FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough | FileOptions.Asynchronous)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush(flushToDisk: true);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class AppendStream(string path) : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)
    {
        public override void Flush() => Flush(flushToDisk: true);
    }

    private sealed class HeldLock(FileStream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }
}
