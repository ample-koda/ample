using System.Text;
using System.Text.Json;
using Ample.Language;

namespace Ample.Server.Lite;

/// <summary>
/// The other side of an isolated Lite build: a program of its own (<c>Ample.Cli lite-worker</c>) that reads one request from its input (the entry, the limits and the files of the page),
/// builds it in a folder of its own with <see cref="LiteBuilder"/>, writes what it does as one JSON line at a time to its output and the WAV to <c>out.wav</c> in that folder. It holds no
/// session, no project and no connection; the server that started it can kill it at any moment (time, memory, the page going away) without any other part of the server being touched.
/// </summary>
public static class LiteWorker
{
    /// <summary>The first number of a request (the letters LITE), so a wrong program on the other end is noticed at once.</summary>
    public const int Magic = 0x4C495445;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes a request (what <see cref="RunAsync"/> reads).</summary>
    /// <param name="output">Where the worker reads from.</param>
    /// <param name="entry">The song to build.</param>
    /// <param name="files">The files of the project.</param>
    /// <param name="limits">The limits that hold for this build.</param>
    /// <param name="cancellationToken">Cancelled when the build is given up.</param>
    /// <returns>A task that ends when everything is written.</returns>
    public static async Task WriteRequestAsync(Stream output, string entry, IReadOnlyList<LiteFile> files, LiteLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(files);
        using MemoryStream buffer = new();
        using (BinaryWriter w = new(buffer, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(entry);
            w.Write(JsonSerializer.Serialize(limits, Json));
            w.Write(files.Count);
            foreach (LiteFile file in files)
            {
                w.Write(file.Path);
                w.Write(file.Data.Length);
                w.Write(file.Data);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Close();
    }

    /// <summary>Reads one request, builds it and reports.</summary>
    /// <param name="input">The request.</param>
    /// <param name="output">Where the JSON lines go (<c>log</c>, then <c>result</c> or <c>error</c>).</param>
    /// <param name="ampleRoot">The AMPLE folder (spec and so on).</param>
    /// <param name="work">The private folder of this build; <c>out.wav</c> is written into it.</param>
    /// <param name="cancellationToken">Cancelled when the worker has to stop.</param>
    /// <returns>0 when it ran to the end (also when the song was refused), 2 for a request that is not one.</returns>
    public static async Task<int> RunAsync(Stream input, Stream output, string ampleRoot, string work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using BinaryReader reader = new(input, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Magic) return 2;
        string entry = reader.ReadString();
        LiteLimits limits = JsonSerializer.Deserialize<LiteLimits>(reader.ReadString(), Json) ?? LiteLimits.Default;
        int count = reader.ReadInt32();
        if (count < 0 || count > limits.FilesAccepted + 8) return 2;
        List<LiteFile> files = new(count);
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            string path = reader.ReadString();
            int length = reader.ReadInt32();
            total += length;
            if (length < 0 || total > limits.BytesAccepted + (1024 * 1024)) return 2;
            files.Add(new LiteFile(path, reader.ReadBytes(length)));
        }

        StreamWriter lines = new(output, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        object gate = new();
        void Send(object message)
        {
            lock (gate) lines.WriteLine(JsonSerializer.Serialize(message, Json));
        }

        SpecData spec = Ample.Docs.SpecRoot.At(ampleRoot).LoadSpecData();
        LiteOutputs kept = new();
        try
        {
            LiteBuildResult result = await LiteBuilder.BuildAsync(spec, work, entry, files, limits, kept, cancellationToken, (text, fraction) => Send(new { t = "log", text, fraction })).ConfigureAwait(false);
            if (result.OutputId is not null && kept.Get(result.OutputId) is { } wav) await File.WriteAllBytesAsync(Path.Combine(work, "out.wav"), wav, cancellationToken).ConfigureAwait(false);
            Send(new { t = "result", result });
        }
        catch (LiteRejectedException rejected)
        {
            Send(new { t = "error", code = rejected.Code, status = rejected.Status, message = rejected.Message });
        }

        return 0;
    }
}
