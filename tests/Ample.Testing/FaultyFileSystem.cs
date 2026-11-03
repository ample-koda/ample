using Ample.Core.Abstractions;

namespace Ample.Testing;

/// <summary>Wraps a file system and fails the Nth mutating operation (write, move, delete, flush), to prove that a crash at every step leaves consistent files. A failing durable write first leaves a truncated file behind, like a power cut half way.</summary>
public sealed class FaultyFileSystem(IFileSystem inner, int failAt) : IFileSystem
{
    private int count;

    /// <summary>Gets the number of mutating operations seen so far.</summary>
    public int Operations => count;

    /// <summary>Gets a value indicating whether the planned failure happened.</summary>
    public bool Failed { get; private set; }

    /// <inheritdoc />
    public bool FileExists(string path) => inner.FileExists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    /// <inheritdoc />
    public ValueTask<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) => inner.ReadAllTextAsync(path, cancellationToken);

    /// <inheritdoc />
    public ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) => inner.ReadAllBytesAsync(path, cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        Step();
        return inner.WriteAllTextAsync(path, contents, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask WriteAllBytesDurableAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        try
        {
            Step();
        }
        catch (IOException)
        {
            await inner.WriteAllBytesDurableAsync(path, contents[..(contents.Length / 2)], cancellationToken);
            throw;
        }

        await inner.WriteAllBytesDurableAsync(path, contents, cancellationToken);
    }

    /// <inheritdoc />
    public Stream OpenRead(string path) => inner.OpenRead(path);

    /// <inheritdoc />
    public Stream CreateDurableWriteStream(string path)
    {
        Step();
        return inner.CreateDurableWriteStream(path);
    }

    /// <inheritdoc />
    public Stream OpenAppend(string path)
    {
        Step();
        return inner.OpenAppend(path);
    }

    /// <inheritdoc />
    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    /// <inheritdoc />
    public void Move(string source, string destination, bool overwrite)
    {
        Step();
        inner.Move(source, destination, overwrite);
    }

    /// <inheritdoc />
    public void MoveDirectory(string source, string destination)
    {
        Step();
        inner.MoveDirectory(source, destination);
    }

    /// <inheritdoc />
    public void DeleteFile(string path) => inner.DeleteFile(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(path, recursive);

    /// <inheritdoc />
    public IReadOnlyList<string> GetFiles(string directory) => inner.GetFiles(directory);

    /// <inheritdoc />
    public IReadOnlyList<string> GetDirectories(string directory) => inner.GetDirectories(directory);

    /// <inheritdoc />
    public void FlushDirectory(string directory)
    {
        Step();
        inner.FlushDirectory(directory);
    }

    /// <inheritdoc />
    public long GetFreeSpace(string path) => inner.GetFreeSpace(path);

    /// <inheritdoc />
    public string? GetLinkTarget(string path) => inner.GetLinkTarget(path);

    /// <inheritdoc />
    public IDisposable? TryLockFile(string path) => inner.TryLockFile(path);

    private void Step()
    {
        count++;
        if (count == failAt)
        {
            Failed = true;
            throw new IOException($"Injected failure at operation {count}.");
        }
    }
}
