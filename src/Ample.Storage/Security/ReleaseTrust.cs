using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Ample.Core.Abstractions;

namespace Ample.Storage.Security;

/// <summary>
/// The public keys that AMPLE trusts for its own releases (<c>keys/*.pem</c>, built into the program). A release file is checked against
/// these keys, never against the public key that the <c>.sig</c> file carries: a forger can write any key into a signature file.
/// </summary>
public static class ReleaseTrust
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Keys = new(Load);

    /// <summary>The trusted keys: key id to base64 SubjectPublicKeyInfo.</summary>
    public static IReadOnlyDictionary<string, string> BuiltInKeys => Keys.Value;

    /// <summary>Checks <c>file</c> and <c>file.sig</c> against the built-in keys; returns the key id.</summary>
    /// <exception cref="AmpleException">AMP5312 when the signature is missing, wrong, from an unknown key, or the file changed.</exception>
    public static string VerifyFile(string file) => Signatures.VerifyFileWithKeys(file, BuiltInKeys);

    /// <summary>Reads PEM texts into the key table (the key id is the first 16 hex digits of the SHA-256 of the key, as <c>ample keys generate</c> makes it).</summary>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> pems)
    {
        Dictionary<string, string> table = [];
        foreach (string pem in pems)
        {
            PemFields fields = PemEncoding.Find(pem);
            byte[] der = Convert.FromBase64String(pem[fields.Base64Data]);
            table[Convert.ToHexStringLower(SHA256.HashData(der))[..16]] = Convert.ToBase64String(der);
        }

        return table;
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        Assembly assembly = typeof(ReleaseTrust).Assembly;
        List<string> pems = [];
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("release-keys/", StringComparison.Ordinal)))
        {
            using StreamReader reader = new(assembly.GetManifestResourceStream(name)!, Encoding.UTF8);
            pems.Add(reader.ReadToEnd());
        }

        return Parse(pems);
    }
}
