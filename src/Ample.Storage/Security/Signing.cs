using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;

namespace Ample.Storage.Security;

/// <summary>A signing key of the user.</summary>
/// <param name="KeyId">The first 16 hex digits of the SHA-256 of the public key.</param>
/// <param name="PublicKey">The public key (SubjectPublicKeyInfo, base64).</param>
/// <param name="Created">When it was made.</param>
/// <param name="Retired">When it was retired by a rotation (a retired key still verifies old signatures but cannot sign), or null.</param>
public sealed record SigningKeyInfo(string KeyId, string PublicKey, DateTimeOffset Created, DateTimeOffset? Retired);

/// <summary>
/// The signing keys of the user, in <c>keys/</c> of the library folder: ECDSA P-256 with SHA-256 (from the .NET platform), the private key sealed
/// with a key made from a passphrase. One key is the active one; a rotation retires it and makes a new one.
/// </summary>
public sealed class SigningKeys
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string folder;

    /// <summary>Creates the key store of a library folder.</summary>
    public SigningKeys(string home) => folder = Path.Combine(home, "keys");

    /// <summary>Makes a new key and makes it the active one. A previous active key is retired (see <see cref="Rotate"/>).</summary>
    public SigningKeyInfo Generate(string passphrase, DateTimeOffset now, int iterations = Crypto.DefaultIterations)
    {
        PassphraseVault.CheckStrength(passphrase);
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKey = ecdsa.ExportSubjectPublicKeyInfo();
        string keyId = Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];
        byte[] salt = Crypto.Random(16);
        byte[] kek = Crypto.Derive(passphrase, salt, iterations);
        Directory.CreateDirectory(folder);
        KeyFile file = new() { KeyId = keyId, Algorithm = "ecdsa-p256-sha256", PublicKey = Convert.ToBase64String(publicKey), Created = now, Iterations = iterations, Salt = Convert.ToBase64String(salt), PrivateKey = Convert.ToBase64String(Crypto.Seal(kek, ecdsa.ExportPkcs8PrivateKey(), Encoding.UTF8.GetBytes(keyId))) };
        Write(file);
        File.WriteAllText(Path.Combine(folder, "active.txt"), keyId + "\n");
        return Info(file);
    }

    /// <summary>The key that signs now, or null when there is none.</summary>
    public SigningKeyInfo? Active
    {
        get
        {
            string marker = Path.Combine(folder, "active.txt");
            return File.Exists(marker) ? List().FirstOrDefault(k => k.KeyId == File.ReadAllText(marker).Trim() && k.Retired is null) : null;
        }
    }

    /// <summary>All keys, newest first.</summary>
    public IReadOnlyList<SigningKeyInfo> List() => !Directory.Exists(folder) ? [] : [.. Directory.EnumerateFiles(folder, "*.json").Select(Read).Select(Info).OrderByDescending(k => k.Created)];

    /// <summary>Retires the active key (its private half is removed) and makes a new active key.</summary>
    public SigningKeyInfo Rotate(string passphrase, DateTimeOffset now, int iterations = Crypto.DefaultIterations)
    {
        SigningKeyInfo? current = Active;
        SigningKeyInfo made = Generate(passphrase, now, iterations);
        if (current is not null)
        {
            KeyFile old = Read(Path.Combine(folder, current.KeyId + ".json"));
            old.PrivateKey = string.Empty;
            old.Retired = now;
            Write(old);
        }

        return made;
    }

    /// <summary>Signs data with the active key.</summary>
    /// <exception cref="AmpleException">AMP5313 when there is no active key or the passphrase is wrong.</exception>
    public (string KeyId, string PublicKey, string Signature) Sign(string passphrase, byte[] data)
    {
        SigningKeyInfo active = Active ?? throw new AmpleException("AMP5313", "There is no signing key yet. Make one with: ample keys generate");
        KeyFile file = Read(Path.Combine(folder, active.KeyId + ".json"));
        byte[] kek = Crypto.Derive(passphrase, Convert.FromBase64String(file.Salt), file.Iterations);
        byte[] pkcs8;
        try
        {
            pkcs8 = Crypto.Open(kek, Convert.FromBase64String(file.PrivateKey), Encoding.UTF8.GetBytes(file.KeyId));
        }
        catch (CryptographicException)
        {
            throw new AmpleException("AMP5313", "The passphrase does not open the signing key.");
        }

        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return (file.KeyId, file.PublicKey, Convert.ToBase64String(ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    /// <summary>The public key of a key as PEM text, to hand to someone who verifies.</summary>
    public string ExportPublic(string keyId)
    {
        SigningKeyInfo info = List().FirstOrDefault(k => k.KeyId == keyId) ?? throw new AmpleException("AMP5313", $"There is no signing key '{keyId}'.");
        return PemEncoding.WriteString("PUBLIC KEY", Convert.FromBase64String(info.PublicKey)) + "\n";
    }

    /// <summary>Checks a signature against a public key (base64 SubjectPublicKeyInfo).</summary>
    public static bool Verify(string publicKey, byte[] data, string signature)
    {
        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static SigningKeyInfo Info(KeyFile f) => new(f.KeyId, f.PublicKey, f.Created, f.Retired);

    private static KeyFile Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path), Options) ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new AmpleException("AMP5313", $"The key file '{Path.GetFileName(path)}' is damaged: {ex.Message}");
        }
    }

    private void Write(KeyFile file)
    {
        string path = Path.Combine(folder, file.KeyId + ".json");
        File.WriteAllText(path + ".part", JsonSerializer.Serialize(file, Options) + "\n");
        File.Move(path + ".part", path, true);
    }

    private sealed class KeyFile
    {
        public string KeyId { get; set; } = string.Empty;

        public string Algorithm { get; set; } = string.Empty;

        public string PublicKey { get; set; } = string.Empty;

        public DateTimeOffset Created { get; set; }

        public DateTimeOffset? Retired { get; set; }

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;

        public string PrivateKey { get; set; } = string.Empty;
    }
}

/// <summary>The result of checking the signatures of a project.</summary>
/// <param name="Checked">Signatures that were checked and are good.</param>
/// <param name="Unverifiable">Signatures whose records were thinned away by the retention (no proof either way).</param>
/// <param name="Problems">What is wrong: a signature that does not match, a record that was changed, a journal that is shorter than what was signed.</param>
public sealed record SignatureReport(int Checked, int Unverifiable, IReadOnlyList<string> Problems)
{
    /// <summary>True when nothing is wrong.</summary>
    public bool Ok => Problems.Count == 0;
}

/// <summary>Signatures over the head of the black box (its newest record and the hash of that record) and over files such as exports.</summary>
public static class Signatures
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions Indented = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static string FileHash(string file)
    {
        using FileStream s = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>The file with the signatures of a project.</summary>
    public static string LogOf(string projectRoot) => Path.Combine(projectRoot, ".ample", "signatures.jsonl");

    private static byte[] Message(Guid projectId, long seq, string head, DateTimeOffset at) => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"ample-sig1|{projectId:D}|{seq}|{head}|{at.UtcDateTime:O}"));

    /// <summary>Signs the current head of the black box and appends the signature to the project's signature log.</summary>
    public static (long Seq, string KeyId) SignHead(Journal journal, string projectRoot, Guid projectId, SigningKeys keys, string passphrase, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(keys);
        (long seq, string head) = journal.Head;
        if (seq < 1)
        {
            throw new AmpleException("AMP5312", "The black box is empty: there is nothing to sign yet.");
        }

        (string keyId, string publicKey, string signature) = keys.Sign(passphrase, Message(projectId, seq, head, now));
        string line = JsonSerializer.Serialize(new { keyId, publicKey, seq, head, at = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), signature }, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(LogOf(projectRoot))!);
        File.AppendAllText(LogOf(projectRoot), line + "\n");
        return (seq, keyId);
    }

    /// <summary>Checks every signature of the project against the black box on disk.</summary>
    /// <param name="journal">The journal.</param>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="projectId">The project id (it is part of what was signed).</param>
    /// <param name="trustedKeyId">When given, signatures of any other key count as a problem.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async ValueTask<SignatureReport> VerifyHeadsAsync(Journal journal, string projectRoot, Guid projectId, string? trustedKeyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        List<string> problems = [];
        int good = 0;
        int unverifiable = 0;
        string log = LogOf(projectRoot);
        if (!File.Exists(log))
        {
            return new SignatureReport(0, 0, []);
        }

        IReadOnlyDictionary<long, string> hashes = await journal.LineHashesAsync(cancellationToken).ConfigureAwait(false);
        long newest = hashes.Count == 0 ? 0 : hashes.Keys.Max();
        int number = 0;
        foreach (string text in File.ReadAllLines(log).Where(l => l.Length > 0))
        {
            number++;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement r = doc.RootElement;
                string keyId = r.GetProperty("keyId").GetString()!;
                long seq = r.GetProperty("seq").GetInt64();
                string head = r.GetProperty("head").GetString()!;
                DateTimeOffset at = DateTimeOffset.Parse(r.GetProperty("at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (trustedKeyId is not null && keyId != trustedKeyId)
                {
                    problems.Add($"Signature {number} was made by key {keyId}, not by the key you trust ({trustedKeyId}).");
                    continue;
                }

                if (!SigningKeys.Verify(r.GetProperty("publicKey").GetString()!, Message(projectId, seq, head, at), r.GetProperty("signature").GetString()!))
                {
                    problems.Add($"Signature {number} (record {seq}) is not a valid signature of key {keyId}: the signature log was changed.");
                }
                else if (hashes.TryGetValue(seq, out string? actual))
                {
                    if (actual == head)
                    {
                        good++;
                    }
                    else
                    {
                        problems.Add($"Record {seq} of the black box is not what was signed: it was changed after signature {number}.");
                    }
                }
                else if (seq > newest)
                {
                    problems.Add($"The black box ends at record {newest}, but record {seq} was signed: records were cut off.");
                }
                else
                {
                    unverifiable++;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                problems.Add($"Line {number} of the signature log is damaged.");
            }
        }

        return new SignatureReport(good, unverifiable, problems);
    }

    /// <summary>Signs a file (an export, a backup manifest) and writes the signature next to it as <c>file.sig</c>.</summary>
    public static string SignFile(string file, SigningKeys keys, string passphrase, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string hash = FileHash(file);
        (string keyId, string publicKey, string signature) = keys.Sign(passphrase, Encoding.UTF8.GetBytes($"ample-file-sig1|{hash}"));
        string sig = file + ".sig";
        File.WriteAllText(sig, JsonSerializer.Serialize(new { keyId, publicKey, sha256 = hash, at = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), signature }, Indented) + "\n");
        return sig;
    }

    /// <summary>
    /// Checks a signed file against a table of trusted keys (key id to base64 SubjectPublicKeyInfo) and returns the key id. The public key
    /// written in the <c>.sig</c> file is ignored: only a key the caller trusts counts.
    /// </summary>
    /// <exception cref="AmpleException">AMP5312 when the signature is missing, damaged, from an unknown key, wrong, or the file changed.</exception>
    public static string VerifyFileWithKeys(string file, IReadOnlyDictionary<string, string> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        string sig = file + ".sig";
        if (!File.Exists(sig))
        {
            throw new AmpleException("AMP5312", $"There is no signature file '{Path.GetFileName(sig)}' next to the file.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sig));
            JsonElement r = doc.RootElement;
            string keyId = r.GetProperty("keyId").GetString()!;
            if (!trusted.TryGetValue(keyId, out string? publicKey))
            {
                throw new AmpleException("AMP5312", $"The file was signed by key {keyId}, which AMPLE does not trust.");
            }

            string hash = FileHash(file);
            if (r.GetProperty("sha256").GetString() != hash)
            {
                throw new AmpleException("AMP5312", "The file has changed since it was signed.");
            }

            return SigningKeys.Verify(publicKey, Encoding.UTF8.GetBytes($"ample-file-sig1|{hash}"), r.GetProperty("signature").GetString()!)
                ? keyId
                : throw new AmpleException("AMP5312", "The signature is not valid.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AmpleException("AMP5312", "The signature file is damaged.");
        }
    }

    /// <summary>Checks a signed file; returns the key id of the signer, or throws.</summary>
    /// <exception cref="AmpleException">AMP5312 when the signature is missing, wrong or the file changed.</exception>
    public static string VerifyFile(string file, string? trustedKeyId = null)
    {
        string sig = file + ".sig";
        if (!File.Exists(sig))
        {
            throw new AmpleException("AMP5312", $"There is no signature file '{Path.GetFileName(sig)}' next to the file.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sig));
            JsonElement r = doc.RootElement;
            string hash = FileHash(file);
            string keyId = r.GetProperty("keyId").GetString()!;
            if (trustedKeyId is not null && keyId != trustedKeyId)
            {
                throw new AmpleException("AMP5312", $"The file was signed by key {keyId}, not by the key you trust ({trustedKeyId}).");
            }

            if (r.GetProperty("sha256").GetString() != hash)
            {
                throw new AmpleException("AMP5312", "The file has changed since it was signed.");
            }

            return SigningKeys.Verify(r.GetProperty("publicKey").GetString()!, Encoding.UTF8.GetBytes($"ample-file-sig1|{hash}"), r.GetProperty("signature").GetString()!)
                ? keyId
                : throw new AmpleException("AMP5312", "The signature is not valid.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AmpleException("AMP5312", "The signature file is damaged.");
        }
    }
}
