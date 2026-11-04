using System.Text;

namespace Ample.Cli;

/// <summary>Writes a small rolling UTF-8 log, retaining the current file and two one-megabyte backups.</summary>
internal sealed class RotatingLogWriter : TextWriter
{
    private const long MaxBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly string path;
    private readonly TextWriter? mirror;
    private StreamWriter writer;
    private long bytes;

    public RotatingLogWriter(string path, TextWriter? mirror)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        this.mirror = mirror;
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        if (File.Exists(this.path) && new FileInfo(this.path).Length >= MaxBytes) Rotate();
        bytes = File.Exists(this.path) ? new FileInfo(this.path).Length : 0;
        writer = Open();
    }

    public override Encoding Encoding => new UTF8Encoding(false);

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (gate)
        {
            int length = Encoding.GetByteCount(value);
            if (bytes > 0 && bytes + length > MaxBytes) Rotate();
            writer.Write(value);
            writer.Flush();
            mirror?.Write(value);
            mirror?.Flush();
            bytes += length;
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write(NewLine);
    }

    public override void Flush()
    {
        lock (gate)
        {
            writer.Flush();
            mirror?.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate) writer.Dispose();
        }

        base.Dispose(disposing);
    }

    private StreamWriter Open() => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };

    private void Rotate()
    {
        writer?.Dispose();
        string first = path + ".1";
        string second = path + ".2";
        if (File.Exists(second)) File.Delete(second);
        if (File.Exists(first)) File.Move(first, second);
        if (File.Exists(path)) File.Move(path, first);
        bytes = 0;
        writer = Open();
    }
}
