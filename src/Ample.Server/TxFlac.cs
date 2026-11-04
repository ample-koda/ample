using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;


namespace Ample.Server;

/// <summary>What the FLAC option of a Derived export found out about one rendered audio input.</summary>
internal sealed class FlacStem
{
    /// <summary>Gets or sets the file of the audio input in the package (derived/name.wav).</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the audio can be stored as FLAC at all (16 or 24 bit integer PCM).</summary>
    public bool Supported { get; set; }

    /// <summary>Gets or sets a value indicating whether the decoded file is byte for byte the original.</summary>
    public bool IdenticalFile { get; set; }

    /// <summary>Gets or sets a value indicating whether the samples (the data chunk) are the same, even if the file header differs.</summary>
    public bool IdenticalSamples { get; set; }

    /// <summary>Gets or sets the size of the WAV.</summary>
    public long WavSize { get; set; }

    /// <summary>Gets or sets the size of the FLAC.</summary>
    public long FlacSize { get; set; }

    /// <summary>Gets or sets the FLAC file kept for the export.</summary>
    public string FlacFile { get; set; } = string.Empty;
}

/// <summary>A listening test of the FLAC option: the final result built once with the original audio inputs and once with the audio inputs that went through FLAC.</summary>
internal sealed class FlacPreview
{
    /// <summary>Gets the id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>Gets or sets what the test belongs to (the saved song and the options); an approval is only valid for it.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder of the test files.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Gets or sets the master built with the original audio inputs.</summary>
    public string Before { get; set; } = string.Empty;

    /// <summary>Gets or sets the master built with the audio inputs that went through FLAC.</summary>
    public string After { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the two masters are byte for byte the same.</summary>
    public bool MastersIdentical { get; set; }

    /// <summary>Gets the stems.</summary>
    public List<FlacStem> Stems { get; } = [];

    /// <summary>Gets or sets a value indicating whether the user listened and approved.</summary>
    public bool Approved { get; set; }
}

/// <summary>FLAC for the rendered audio inputs of a Derived export, with ffmpeg. FLAC is lossless in principle, but nothing here promises that the decoded audio is the same file, so the user listens first.</summary>
internal static class TxFlac
{
    private static readonly ConcurrentDictionary<string, FlacPreview> Previews = new(StringComparer.Ordinal);

    internal static string Exe => Environment.GetEnvironmentVariable("AMPLE_FFMPEG") is { Length: > 0 } configured ? configured : "ffmpeg";

    /// <summary>Gets a test by id.</summary>
    public static FlacPreview Get(string id) => Previews.TryGetValue(id, out FlacPreview? p) ? p : throw new AmpleException("AMP5460", "This FLAC listening test does not exist (anymore). Listen again.");

    /// <summary>The key an approval belongs to.</summary>
    public static string KeyOf(TxPrepared p) => p.Request.Path + "|" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Doc.Saved))) + "|" + p.Request.WholeSets;

    /// <summary>The approved test for an export, or an error when there is none for exactly this state.</summary>
    public static FlacPreview Approved(string? id, TxPrepared p)
    {
        FlacPreview test = id is null ? throw Needed() : Get(id);
        if (!test.Approved || test.Key != KeyOf(p))
        {
            throw Needed();
        }

        return test;
    }

    private static AmpleException Needed() => new("AMP5460", "FLAC needs your approval first: listen to the result before (WAV) and after (FLAC) and approve it. The song changed since the last listening test if you did that already.");

    /// <summary>Whether the audio is something FLAC can hold: integer PCM of 16 or 24 bits.</summary>
    public static bool Supports(string wav)
    {
        (int format, int bits) = Format(wav);
        return format == 1 && bits is 16 or 24;
    }

    private static (int Format, int Bits) Format(string wav)
    {
        using FileStream f = new(wav, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] head = new byte[64];
        int n = f.Read(head, 0, head.Length);
        return n >= 36 ? (head[20] | (head[21] << 8), BitConverter.ToInt16(head, 34)) : (0, 0);
    }

    /// <summary>The SHA-256 of the samples only (the data chunk), so a different header does not count as different audio.</summary>
    public static string DataSha(string wav)
    {
        using FileStream f = new(wav, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] id = new byte[8];
        f.Seek(12, SeekOrigin.Begin);
        while (f.Read(id, 0, 8) == 8)
        {
            long size = BitConverter.ToUInt32(id, 4);
            if (id[0] == 'd' && id[1] == 'a' && id[2] == 't' && id[3] == 'a')
            {
                using SHA256 sha = SHA256.Create();
                byte[] buffer = new byte[1 << 20];
                long left = size;
                while (left > 0)
                {
                    int got = f.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (got <= 0)
                    {
                        break;
                    }

                    sha.TransformBlock(buffer, 0, got, null, 0);
                    left -= got;
                }

                sha.TransformFinalBlock([], 0, 0);
                return Convert.ToHexStringLower(sha.Hash!);
            }

            f.Seek(size + (size & 1), SeekOrigin.Current);
        }

        return string.Empty;
    }

    /// <summary>Checks that ffmpeg can be started.</summary>
    public static Task EnsureAvailableAsync(CancellationToken ct) => RunAsync(["-hide_banner", "-version"], ct);

    /// <summary>Encodes a WAV to FLAC.</summary>
    public static Task EncodeAsync(string wav, string flac, CancellationToken ct) => RunAsync(["-v", "error", "-y", "-i", wav, "-map_metadata", "-1", "-fflags", "+bitexact", "-c:a", "flac", "-compression_level", "8", flac], ct);

    /// <summary>Decodes a FLAC back to a WAV with the bit depth of the original.</summary>
    public static Task DecodeAsync(string flac, string wav, int bits, CancellationToken ct) => RunAsync(["-v", "error", "-y", "-i", flac, "-map_metadata", "-1", "-fflags", "+bitexact", "-c:a", bits == 24 ? "pcm_s24le" : "pcm_s16le", "-f", "wav", wav], ct);

    /// <summary>The bit depth of a WAV.</summary>
    public static int BitsOf(string wav) => Format(wav).Bits;

    private static async Task RunAsync(string[] arguments, CancellationToken ct)
    {
        ProcessStartInfo info = new(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        try
        {
            using Process process = Process.Start(info) ?? throw new AmpleException("AMP5461", "FLAC needs ffmpeg. Install ffmpeg or set AMPLE_FFMPEG to its executable path.");
            Task<string> err = process.StandardError.ReadToEndAsync(ct);
            _ = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new AmpleException("AMP5461", "ffmpeg could not convert the audio: " + (await err.ConfigureAwait(false)).Trim().Split('\n').FirstOrDefault());
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new AmpleException("AMP5461", "FLAC needs ffmpeg. Install ffmpeg or set AMPLE_FFMPEG to its executable path.");
        }
    }

    /// <summary>
    /// Makes the listening test: every audio input goes through FLAC and back, and the final result is built with the originals ("before") and with what came back ("after").
    /// When every file came back byte for byte the same there is nothing to hear and "after" is "before".
    /// </summary>
    public static async Task<FlacPreview> PreviewAsync(ProjectSession s, TxPrepared p, CancellationToken ct)
    {
        FlacPreview test = new() { Key = KeyOf(p) };
        test.Folder = Path.Combine(s.Root, ProjectLayout.AmpleFolder, "tmp", "flac-" + test.Id);
        Directory.CreateDirectory(test.Folder);
        string decoded = Path.Combine(test.Folder, "decoded");
        Directory.CreateDirectory(decoded);
        Dictionary<string, string> original = new(StringComparer.Ordinal);
        Dictionary<string, string> back = new(StringComparer.Ordinal);
        foreach (TxDerivedAsset a in p.DerivedAssets)
        {
            FlacStem stem = new() { File = a.File, WavSize = new FileInfo(a.TempFile).Length, Supported = Supports(a.TempFile) };
            original[a.File] = a.TempFile;
            if (!stem.Supported)
            {
                back[a.File] = a.TempFile;
                test.Stems.Add(stem);
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(a.File);
            stem.FlacFile = Path.Combine(test.Folder, name + ".flac");
            string wav = Path.Combine(decoded, name + ".wav");
            await EncodeAsync(a.TempFile, stem.FlacFile, ct).ConfigureAwait(false);
            await DecodeAsync(stem.FlacFile, wav, BitsOf(a.TempFile), ct).ConfigureAwait(false);
            stem.FlacSize = new FileInfo(stem.FlacFile).Length;
            stem.IdenticalFile = File.ReadAllBytes(a.TempFile).AsSpan().SequenceEqual(File.ReadAllBytes(wav));
            stem.IdenticalSamples = stem.IdenticalFile || DataSha(a.TempFile) == DataSha(wav);
            back[a.File] = wav;
            test.Stems.Add(stem);
        }

        test.Before = await AiService.BuildStagedMasterAsync(s, Path.Combine(test.Folder, "before"), p.Request.Path, p.SongText, original, Path.Combine(test.Folder, "before.wav"), ct).ConfigureAwait(false);
        if (test.Stems.All(x => !x.Supported || x.IdenticalFile))
        {
            test.After = test.Before;
            test.MastersIdentical = true;
        }
        else
        {
            test.After = await AiService.BuildStagedMasterAsync(s, Path.Combine(test.Folder, "after"), p.Request.Path, p.SongText, back, Path.Combine(test.Folder, "after.wav"), ct).ConfigureAwait(false);
            test.MastersIdentical = File.ReadAllBytes(test.Before).AsSpan().SequenceEqual(File.ReadAllBytes(test.After));
        }

        Previews[test.Id] = test;
        return test;
    }

    /// <summary>Marks a test as listened to and approved.</summary>
    public static FlacPreview Approve(string id)
    {
        FlacPreview test = Get(id);
        test.Approved = true;
        return test;
    }
}
