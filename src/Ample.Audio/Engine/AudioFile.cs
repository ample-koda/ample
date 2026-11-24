using System.ComponentModel;
using System.Diagnostics;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>
/// Reads an audio file of the project as WAV bytes. A WAV file is read as it is. Another format (mp3, flac, ogg, aiff, ...) is decoded once through ffmpeg
/// (it must be on the PATH) into 16-bit WAV; the file itself, not the decoded copy, is what the plan hashes, so the decoder is a tool of the machine,
/// like the other tools whose output a take records.
/// </summary>
public static class AudioFile
{
    /// <summary>Gets whether the bytes start like a RIFF/WAVE file.</summary>
    internal static bool IsWav(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E';

    /// <summary>
    /// Reads only the frames of the stretch that is used (start and end in seconds) of a 16-bit PCM WAV file that already has the wanted sample rate, as a small WAV of that stretch,
    /// and says where the stretch begins in the file. Anything else (another format, another rate that would need resampling with its context, no stretch asked for, the whole file) is read as
    /// <see cref="ReadWav"/> does, with a window start of 0 (issue 293). The stretch is cut with the same rounding as <see cref="AudioCut.Range"/>.
    /// </summary>
    internal static (byte[] Wav, int WindowStart, bool Windowed) ReadWavWindow(string path, int sampleRate, string? start, string? end)
    {
        double? startSeconds = AudioCut.Seconds(start), endSeconds = AudioCut.Seconds(end);
        bool asked = startSeconds is > 0 || endSeconds is > 0;
        if (!asked || !TryReadHeader(path, out WavHeader header) || header.Rate != sampleRate)
        {
            return (ReadWav(path), 0, false);
        }

        long total = header.DataBytes / header.BlockAlign;
        long from = Math.Min(total, (long)Math.Round((startSeconds ?? 0) * sampleRate));
        long to = endSeconds is double stop && stop > 0 ? Math.Min(total, (long)Math.Round(stop * sampleRate)) : total;
        to = Math.Max(from, to);
        if ((from == 0 && to == total) || to == from)
        {
            return (ReadWav(path), 0, false);
        }

        byte[] data = new byte[(to - from) * header.BlockAlign];
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(header.DataStart + (from * header.BlockAlign), SeekOrigin.Begin);
            stream.ReadExactly(data);
        }

        short[] samples = new short[data.Length / 2];
        Buffer.BlockCopy(data, 0, samples, 0, data.Length);
        if (header.Channels == 1)
        {
            return (Wav.Encode(samples, sampleRate), (int)from, true);
        }

        short[] left = new short[samples.Length / 2], right = new short[samples.Length / 2];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = samples[2 * i];
            right[i] = samples[(2 * i) + 1];
        }

        return (StereoWav.Encode(new StereoBuffer(left, right), sampleRate), (int)from, true);
    }

    private readonly record struct WavHeader(int Channels, int Rate, int BlockAlign, long DataStart, long DataBytes);

    /// <summary>The format and the place of the data of a 16-bit PCM WAV file with one or two channels, without reading the data; false for anything else.</summary>
    private static bool TryReadHeader(string path, out WavHeader header)
    {
        header = default;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> riff = stackalloc byte[12];
            if (stream.Read(riff) < 12 || !IsWav(riff))
            {
                return false;
            }

            int channels = 0, rate = 0, bits = 0, tag = 0;
            Span<byte> chunk = stackalloc byte[8];
            while (stream.Position + 8 <= stream.Length && stream.Read(chunk) == 8)
            {
                string id = System.Text.Encoding.ASCII.GetString(chunk[..4]);
                long size = BitConverter.ToUInt32(chunk[4..]);
                long body = stream.Position;
                if (id == "fmt " && size >= 16)
                {
                    byte[] format = new byte[16];
                    stream.ReadExactly(format);
                    tag = BitConverter.ToUInt16(format, 0);
                    channels = BitConverter.ToUInt16(format, 2);
                    rate = BitConverter.ToInt32(format, 4);
                    bits = BitConverter.ToUInt16(format, 14);
                }
                else if (id == "data")
                {
                    long available = stream.Length - body;
                    long bytes = Math.Min(size, available);
                    int blockAlign = channels * 2;
                    if (tag != 1 || bits != 16 || channels is < 1 or > 2 || rate <= 0 || bytes < blockAlign)
                    {
                        return false;
                    }

                    header = new WavHeader(channels, rate, blockAlign, body, bytes - (bytes % blockAlign));
                    return true;
                }

                stream.Seek(body + size + (size & 1), SeekOrigin.Begin);
            }
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Reads the file as WAV bytes (decoding it with ffmpeg when it is not a WAV file).</summary>
    /// <exception cref="AmpleException">AMP5350 when the file cannot be decoded.</exception>
    internal static byte[] ReadWav(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (IsWav(bytes))
        {
            return bytes;
        }

        string temp = Path.Combine(Path.GetTempPath(), "ample-decode-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            ProcessStartInfo start = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file", "-i", path, "-vn", "-c:a", "pcm_s16le", "-f", "wav", temp })
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start) ?? throw new AmpleException("AMP5350", $"The audio file '{Path.GetFileName(path)}' is not a WAV file and ffmpeg could not be started to decode it.");
            Task<string> error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { /* it ended meanwhile */ }
                throw new AmpleException("AMP5350", $"Decoding '{Path.GetFileName(path)}' took too long.");
            }

            if (process.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length < 44)
            {
                string why = error.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown format";
                throw new AmpleException("AMP5350", $"The audio file '{Path.GetFileName(path)}' cannot be decoded: {why}.");
            }

            return File.ReadAllBytes(temp);
        }
        catch (Win32Exception)
        {
            throw new AmpleException("AMP5350", $"The audio file '{Path.GetFileName(path)}' is not a WAV file, and ffmpeg is needed to read other formats but was not found on the PATH. Convert the file to WAV or install ffmpeg.");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* a temp file; the system cleans it */ }
        }
    }

    /// <summary>The ffmpeg arguments that encode a WAV into the format named by the extension of <paramref name="target"/> (mp3 or flac), or null when it is not one of those.</summary>
    public static string[]? EncodeArguments(string wavPath, string target) => Path.GetExtension(target).ToLowerInvariant() switch
    {
        ".mp3" => ["-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file", "-i", wavPath, "-vn", "-c:a", "libmp3lame", "-q:a", "2", target],
        ".flac" => ["-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file", "-i", wavPath, "-vn", "-c:a", "flac", target],
        _ => null,
    };

    /// <summary>Writes WAV bytes to <paramref name="target"/>: as they are, or encoded through ffmpeg to mp3 or flac when the extension says so (ffmpeg must be on the PATH).</summary>
    /// <exception cref="AmpleException">AMP5351 when ffmpeg is missing or fails.</exception>
    public static void WriteAs(byte[] wav, string target)
    {
        string full = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = Path.Combine(Path.GetTempPath(), "ample-encode-" + Guid.NewGuid().ToString("N") + ".wav");
        string[]? arguments = EncodeArguments(temp, full);
        if (arguments is null)
        {
            File.WriteAllBytes(full, wav);
            return;
        }

        try
        {
            File.WriteAllBytes(temp, wav);
            ProcessStartInfo start = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new AmpleException("AMP5351", "ffmpeg could not be started to write " + Path.GetFileName(full) + ".");
            Task<string> error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { /* it ended meanwhile */ }
                throw new AmpleException("AMP5351", "Writing " + Path.GetFileName(full) + " took too long.");
            }

            if (process.ExitCode != 0 || !File.Exists(full))
            {
                string why = error.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown error";
                throw new AmpleException("AMP5351", "ffmpeg could not write " + Path.GetFileName(full) + ": " + why + ".");
            }
        }
        catch (Win32Exception)
        {
            throw new AmpleException("AMP5351", "ffmpeg is needed to write mp3 or flac but was not found on the PATH. Write a .wav, or install ffmpeg.");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* a temp file; the system cleans it */ }
        }
    }
}
