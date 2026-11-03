using System.Text;
using Ample.Core.Abstractions;

namespace Ample.Audio.IO;

/// <summary>Writes files so that a crash or an I/O error at any step leaves either the complete old file or the complete new file, never a partial one: write a temporary file in the same folder, flush it, rename it over the target, flush the folder.</summary>
public static class AtomicFile
{
    /// <summary>The suffix of temporary files; <c>ample doctor</c> reports leftovers.</summary>
    public const string TempSuffix = ".ample-tmp";

    /// <summary>Atomically replaces a file with UTF-8 text (no byte order mark).</summary>
    public static async ValueTask WriteTextAsync(IFileSystem fs, string path, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("The path has no directory.", nameof(path));
        string temp = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + TempSuffix);
        try
        {
            await fs.WriteAllBytesDurableAsync(temp, new UTF8Encoding(false).GetBytes(text), cancellationToken).ConfigureAwait(false);
            fs.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(fs, temp);
            throw;
        }

        fs.FlushDirectory(directory);
    }

    private static void TryDelete(IFileSystem fs, string path)
    {
        try
        {
            fs.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The leftover temporary file is reported by the doctor; the original failure is more important.
        }
    }
}
