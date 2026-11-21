using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>
/// The corpus of the end-to-end parity test and the answers of the C program for it (<c>reference/m7</c>): 6,970 texts
/// (the 6,278 of the M3 corpus, 600 generated prose sentences, 14 long texts of 500 to 1,600 tokens, odd input),
/// and for every text either the number of samples and the SHA-256 of the 16-bit samples, or "EMPTY" (no waveform) or "CRASH" (the C program died or hung).
/// </summary>
internal static class EndToEndFixtures
{
    internal sealed record Reference(string Status, int Samples, string Sha256, int Signal);

    internal static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal static List<byte[]> Corpus()
    {
        List<byte[]> texts = [];
        foreach (string line in ReadGz("flite-m7-corpus.txt.gz"))
        {
            texts.Add(Unescape(line));
        }

        return texts;
    }

    /// <summary>The multi-sentence corpus of the splitting test (<c>reference/m8</c>): 501 texts.</summary>
    internal static List<byte[]> SentenceCorpus() => [.. ReadGz("flite-m8-sentences.txt.gz").Select(Unescape)];

    internal static List<Reference> References() => References("flite-m7-pcm.tsv.gz");

    /// <summary>The answers of the C program for a corpus: one line per text, <c>n TAB OK TAB samples TAB rate TAB sha256</c>, <c>EMPTY</c> or <c>CRASH</c>.</summary>
    /// <param name="name">The gzip file in the fixtures folder.</param>
    internal static List<Reference> References(string name)
    {
        List<Reference> list = [];
        foreach (string line in ReadGz(name))
        {
            string[] f = line.Split('\t');
            list.Add(f[1] switch
            {
                "OK" => new Reference("OK", int.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[4], 0),
                "EMPTY" => new Reference("EMPTY", 0, string.Empty, 0),
                _ => new Reference("CRASH", 0, string.Empty, int.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture)),
            });
        }

        return list;
    }

    /// <summary>The SHA-256 of the samples as little-endian 16-bit values, in lower case hex, as the C dumper writes it.</summary>
    internal static string Sha256(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[2 * i] = (byte)(samples[i] & 0xff);
            bytes[(2 * i) + 1] = (byte)((samples[i] >> 8) & 0xff);
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Decodes the escapes of the corpus files: <c>\xHH</c> is one byte, a doubled backslash is one backslash.</summary>
    internal static byte[] Unescape(string line)
    {
        List<byte> bytes = new(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] == '\\')
            {
                bytes.Add((byte)'\\');
                i++;
            }
            else if (c == '\\' && i + 3 < line.Length && line[i + 1] == 'x' && Uri.IsHexDigit(line[i + 2]) && Uri.IsHexDigit(line[i + 3]))
            {
                bytes.Add(Convert.ToByte(line.Substring(i + 2, 2), 16));
                i += 3;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        // a NUL ends the C string
        int nul = bytes.IndexOf(0);
        return nul >= 0 ? [.. bytes.Take(nul)] : [.. bytes];
    }

    internal static string Printable(byte[] text, int max = 90)
    {
        string s = Encoding.Latin1.GetString(text.Take(max).ToArray());
        return string.Concat(s.Select(ch => ch < 0x20 || ch >= 0x7f ? $"\\x{(int)ch:x2}" : ch.ToString()));
    }

    internal static IEnumerable<string> ReadGz(string name)
    {
        using FileStream file = File.OpenRead(Path(name));
        using GZipStream gz = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(gz, Encoding.Latin1);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }
}
