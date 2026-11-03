namespace Ample.Core.Abstractions;

/// <summary>Provides the file operations used by AMPLE. All project I/O goes through this interface so it can be faked and fault-injected in tests.</summary>
public interface IFileSystem
{
    /// <summary>Determines whether a file exists.</summary>
    bool FileExists(string path);

    /// <summary>Determines whether a directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Reads an entire UTF-8 text file.</summary>
    ValueTask<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads an entire file.</summary>
    ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes an entire UTF-8 text file (not atomic; use the atomic helper for project files).</summary>
    ValueTask WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken);

    /// <summary>Creates or replaces a file and flushes its content to the storage device before returning.</summary>
    ValueTask WriteAllBytesDurableAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken);

    /// <summary>Opens a file for reading as a stream (for large files).</summary>
    Stream OpenRead(string path);

    /// <summary>Creates or replaces a file for writing as a stream; disposing the stream flushes the content to the storage device.</summary>
    Stream CreateDurableWriteStream(string path);

    /// <summary>Opens a file for appending (created when missing). Flushing the stream makes the appended bytes durable on the storage device.</summary>
    Stream OpenAppend(string path);

    /// <summary>Creates a directory and its parents.</summary>
    void CreateDirectory(string path);

    /// <summary>Moves a file; with <paramref name="overwrite"/> an existing destination is replaced in one step.</summary>
    void Move(string source, string destination, bool overwrite);

    /// <summary>Moves a directory without replacing an existing destination.</summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Deletes a file; a missing file is not an error.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes a directory.</summary>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>Lists the files directly inside a directory, sorted ordinally.</summary>
    IReadOnlyList<string> GetFiles(string directory);

    /// <summary>Lists the directories directly inside a directory, sorted ordinally.</summary>
    IReadOnlyList<string> GetDirectories(string directory);

    /// <summary>Flushes directory metadata (a completed rename) to the storage device where the platform supports it; otherwise does nothing.</summary>
    void FlushDirectory(string directory);

    /// <summary>Gets the free bytes on the volume that holds the path.</summary>
    long GetFreeSpace(string path);

    /// <summary>Returns the target of a symbolic link or junction at the path, or null when the path is not a link.</summary>
    string? GetLinkTarget(string path);

    /// <summary>Takes an exclusive operating-system lock on a file (created when missing). Returns null when another holder has it. Disposing releases the lock.</summary>
    IDisposable? TryLockFile(string path);
}
