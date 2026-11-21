using System.Reflection;
using System.Security.Cryptography;

namespace Ample.Speech.Flite.Data;

/// <summary>The embedded data files of the Flite engine and their identity.</summary>
public static class FliteDataFiles
{
    /// <summary>The names of the four data files, in the order that the identity hash is made over.</summary>
    public static IReadOnlyList<string> Names { get; } = ["flite-kal16-voice.bin", "flite-cmu-lexicon.bin", "flite-cmu-lts.bin", "flite-usenglish.bin"];

    /// <summary>The SHA-256 of the four embedded files, one after the other, as lower-case hex; it must equal <see cref="FliteDataIdentity.DataSha256"/>.</summary>
    public static string ComputeSha256()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Assembly assembly = typeof(FliteDataFiles).Assembly;
        foreach (string name in Names)
        {
            using Stream stream = assembly.GetManifestResourceStream("Ample.Speech.Flite.Data." + name) ?? throw new InvalidOperationException($"The data file '{name}' is not part of the assembly.");
            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
