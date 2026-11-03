using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ample.Core.Abstractions;

namespace Ample.Storage.Security;

/// <summary>
/// The vault: named secrets (API keys, tokens) kept outside every project, in one file of the user's library folder, sealed
/// with a key derived from a passphrase. Projects and plugin manifests only ever carry the <em>name</em> of a secret. The names are
/// visible in the file, the values are not. (The operating system credential stores are not used yet, see <c>docs/security.md</c>.)
/// </summary>
public sealed class PassphraseVault
{
    /// <summary>The environment variable that holds the passphrase of the vault for programs that cannot ask.</summary>
    public const string PassphraseVariable = "AMPLE_VAULT_PASSPHRASE";

    private static readonly byte[] CheckText = Encoding.UTF8.GetBytes("ample-vault-check");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string file;
    private readonly byte[] key;
    private VaultFile data;

    private PassphraseVault(string file, byte[] key, VaultFile data)
    {
        this.file = file;
        this.key = key;
        this.data = data;
    }

    /// <summary>The file of the vault in a library folder.</summary>
    public static string FileOf(string home) => Path.Combine(home, "vault.json");

    /// <summary>True when a vault exists in the library folder.</summary>
    public static bool Exists(string home) => File.Exists(FileOf(home));

    /// <summary>Creates a new vault (refused when there is one).</summary>
    public static PassphraseVault Create(string home, string passphrase, int iterations = Crypto.DefaultIterations)
    {
        if (Exists(home))
        {
            throw new AmpleException("AMP5311", "There is already a vault in " + home + ".");
        }

        CheckStrength(passphrase);
        Directory.CreateDirectory(home);
        byte[] salt = Crypto.Random(16);
        byte[] key = Crypto.Derive(passphrase, salt, iterations);
        VaultFile data = new() { Format = 1, Kdf = "pbkdf2-sha256", Iterations = iterations, Salt = Convert.ToBase64String(salt), Check = Convert.ToBase64String(Crypto.Seal(key, CheckText)) };
        PassphraseVault vault = new(FileOf(home), key, data);
        vault.Save();
        return vault;
    }

    /// <summary>Opens the vault.</summary>
    /// <exception cref="AmpleException">AMP5311 when there is none, the passphrase is wrong or the file is damaged.</exception>
    public static PassphraseVault Open(string home, string passphrase)
    {
        string file = FileOf(home);
        if (!File.Exists(file))
        {
            throw new AmpleException("AMP5311", "There is no vault yet. Create one with: ample vault set <name> (it asks for a new passphrase).");
        }

        VaultFile data;
        try
        {
            data = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(file), Options) ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new AmpleException("AMP5311", "The vault file is damaged: " + ex.Message);
        }

        byte[] key = Crypto.Derive(passphrase, Convert.FromBase64String(data.Salt), data.Iterations);
        try
        {
            _ = Crypto.Open(key, Convert.FromBase64String(data.Check));
        }
        catch (CryptographicException)
        {
            throw new AmpleException("AMP5311", "The passphrase does not open the vault.");
        }

        return new PassphraseVault(file, key, data);
    }

    /// <summary>The names of the secrets (never the values).</summary>
    public IReadOnlyList<string> Names => [.. data.Entries.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Stores or replaces a secret.</summary>
    public void Set(string name, string value)
    {
        CheckName(name);
        ArgumentNullException.ThrowIfNull(value);
        data.Entries[name] = Convert.ToBase64String(Crypto.Seal(key, Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(name)));
        Save();
    }

    /// <summary>Reads a secret, or null when there is none of that name.</summary>
    public string? Get(string name)
    {
        if (!data.Entries.TryGetValue(name, out string? sealedValue))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Crypto.Open(key, Convert.FromBase64String(sealedValue), Encoding.UTF8.GetBytes(name)));
        }
        catch (CryptographicException)
        {
            throw new AmpleException("AMP5311", $"The secret '{name}' in the vault is damaged.");
        }
    }

    /// <summary>Removes a secret; returns false when there was none.</summary>
    public bool Remove(string name)
    {
        bool removed = data.Entries.Remove(name);
        if (removed)
        {
            Save();
        }

        return removed;
    }

    /// <summary>A passphrase must be long enough to be worth sealing something with.</summary>
    public static void CheckStrength(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < 12)
        {
            throw new AmpleException("AMP5311", "Use a passphrase of at least 12 characters (a few words in a row is fine).");
        }
    }

    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
        {
            throw new AmpleException("AMP5311", "A secret name uses letters, digits, '_', '-' and '.', at most 80 characters.");
        }
    }

    private void Save()
    {
        string partial = file + ".part";
        File.WriteAllText(partial, JsonSerializer.Serialize(data, Options) + "\n");
        File.Move(partial, file, true);
    }

    private sealed class VaultFile
    {
        public int Format { get; set; }

        public string Kdf { get; set; } = string.Empty;

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;

        public string Check { get; set; } = string.Empty;

        [JsonPropertyName("entries")]
        public Dictionary<string, string> Entries { get; set; } = [];
    }
}
