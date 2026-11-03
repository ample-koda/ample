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
            foreach (string argument in new[] { "-v", "error", "-nostdin", "-y", "-i", path, "-vn", "-c:a", "pcm_s16le", "-f", "wav", temp })
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
        ".mp3" => ["-v", "error", "-nostdin", "-y", "-i", wavPath, "-vn", "-c:a", "libmp3lame", "-q:a", "2", target],
        ".flac" => ["-v", "error", "-nostdin", "-y", "-i", wavPath, "-vn", "-c:a", "flac", target],
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
