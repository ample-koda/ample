using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Storage.Security;

/// <summary>What <c>.ample/encryption.json</c> says: how the data key is wrapped. It holds no secret by itself.</summary>
/// <param name="Format">1.</param>
/// <param name="Cipher"><c>aes-256-gcm</c>.</param>
/// <param name="Kdf"><c>pbkdf2-sha256</c>.</param>
/// <param name="Iterations">The PBKDF2 iterations.</param>
/// <param name="Salt">Base64.</param>
/// <param name="WrappedKey">The data key sealed with the key made from the passphrase (base64).</param>
/// <param name="KeyCheck">HMAC-SHA256 of a fixed text under the data key (hex): tells a right passphrase from a damaged file.</param>
/// <param name="Created">When encryption was switched on.</param>
public sealed record EncryptionConfig(int Format, string Cipher, string Kdf, int Iterations, string Salt, string WrappedKey, string KeyCheck, DateTimeOffset Created);

/// <summary>Optional encryption at rest of the black box and of backups. Off by default, switched on and off by dangerous acts, the key kept as a data key that a passphrase unwraps.</summary>
public static class ProjectEncryption
{
    /// <summary>The environment variable that holds the passphrase of an encrypted project for programs that cannot ask.</summary>
    public const string PassphraseVariable = "AMPLE_PASSPHRASE";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly ConcurrentDictionary<string, DataCipher> Unlocked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a program that has no passphrase yet can ask for one (the default reads <see cref="PassphraseVariable"/>).</summary>
    public static Func<string, string?> PassphraseSource { get; set; } = _ => Environment.GetEnvironmentVariable(PassphraseVariable);

    /// <summary>The file of a project.</summary>
    public static string FileOf(string projectRoot) => Path.Combine(projectRoot, ".ample", "encryption.json");

    /// <summary>True when the project is encrypted.</summary>
    public static bool IsEnabled(string projectRoot) => File.Exists(FileOf(projectRoot));

    /// <summary>Reads the configuration, or null when the project is not encrypted.</summary>
    public static EncryptionConfig? Read(string projectRoot) => ReadFile(FileOf(projectRoot));

    /// <summary>Reads a configuration file (a project's or a backup target's), or null when there is none.</summary>
    public static EncryptionConfig? ReadFile(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EncryptionConfig>(File.ReadAllText(file), Options) ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new AmpleException("AMP5310", "The encryption settings file is damaged: " + ex.Message + " Restore .ample/encryption.json from your recovery kit or a backup.");
        }
    }

    /// <summary>Writes a configuration file.</summary>
    public static void WriteFile(string file, EncryptionConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string partial = file + ".part";
        File.WriteAllText(partial, JsonSerializer.Serialize(config, Options) + "\n");
        File.Move(partial, file, true);
    }

    /// <summary>Makes a new data key and wraps it with a passphrase.</summary>
    public static (EncryptionConfig Config, DataCipher Cipher) Create(string passphrase, DateTimeOffset now, int iterations = Crypto.DefaultIterations)
    {
        PassphraseVault.CheckStrength(passphrase);
        byte[] dataKey = Crypto.Random(Crypto.KeyBytes);
        return (Wrap(dataKey, passphrase, now, iterations), new DataCipher(dataKey));
    }

    /// <summary>Wraps an existing data key with a (new) passphrase.</summary>
    public static EncryptionConfig Wrap(byte[] dataKey, string passphrase, DateTimeOffset created, int iterations = Crypto.DefaultIterations)
    {
        byte[] salt = Crypto.Random(16);
        byte[] kek = Crypto.Derive(passphrase, salt, iterations);
        return new EncryptionConfig(1, "aes-256-gcm", "pbkdf2-sha256", iterations, Convert.ToBase64String(salt), Convert.ToBase64String(Crypto.Seal(kek, dataKey, "ample-data-key"u8)), Check(dataKey), created);
    }

    /// <summary>Sets a new passphrase: the data key stays, only its wrapping changes (so nothing is re-encrypted).</summary>
    public static void ChangePassphrase(string projectRoot, string oldPassphrase, string newPassphrase, int iterations = Crypto.DefaultIterations)
    {
        EncryptionConfig config = Read(projectRoot) ?? throw new AmpleException("AMP5314", "This project is not encrypted.");
        PassphraseVault.CheckStrength(newPassphrase);
        DataCipher cipher = Unwrap(config, oldPassphrase);
        WriteFile(FileOf(projectRoot), Wrap(cipher.Key, newPassphrase, config.Created, iterations));
    }

    /// <summary>Unwraps the data key with the passphrase.</summary>
    /// <exception cref="AmpleException">AMP5310 when the passphrase is wrong.</exception>
    public static DataCipher Unwrap(EncryptionConfig config, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(config);
        byte[] kek = Crypto.Derive(passphrase, Convert.FromBase64String(config.Salt), config.Iterations);
        byte[] dataKey;
        try
        {
            dataKey = Crypto.Open(kek, Convert.FromBase64String(config.WrappedKey), "ample-data-key"u8);
        }
        catch (CryptographicException)
        {
            throw new AmpleException("AMP5310", "The passphrase does not open this project's encryption key.");
        }

        return Check(dataKey) == config.KeyCheck ? new DataCipher(dataKey) : throw new AmpleException("AMP5310", "The key of this project did not pass its check: the settings file is damaged.");
    }

    /// <summary>Remembers an unlocked key for this process.</summary>
    public static void Remember(string projectRoot, DataCipher cipher) => Unlocked[Path.GetFullPath(projectRoot)] = cipher;

    /// <summary>Forgets the key of a project (locks it again for this process).</summary>
    public static void Forget(string projectRoot) => Unlocked.TryRemove(Path.GetFullPath(projectRoot), out _);

    /// <summary>The cipher of a project: null when it is not encrypted; otherwise the unlocked key, or the passphrase from <see cref="PassphraseSource"/>.</summary>
    /// <exception cref="AmpleException">AMP5310 when the project is encrypted and no passphrase is available or it is wrong.</exception>
    public static DataCipher? CipherFor(string projectRoot)
    {
        string full = Path.GetFullPath(projectRoot);
        EncryptionConfig? config = Read(full);
        if (config is null)
        {
            Unlocked.TryRemove(full, out _);
            return null;
        }

        if (Unlocked.TryGetValue(full, out DataCipher? known))
        {
            return known;
        }

        return Unlock(full, config);
    }

    /// <summary>The cipher of an encrypted backup target (or null when it holds no key file).</summary>
    public static DataCipher? CipherForTarget(string targetFolder)
    {
        string full = Path.GetFullPath(targetFolder);
        EncryptionConfig? config = ReadFile(Path.Combine(full, "encryption.json"));
        return config is null ? null : Unlocked.TryGetValue(full, out DataCipher? known) ? known : Unlock(full, config);
    }

    /// <summary>The recovery kit: everything needed to open the data again if the project folder is lost, as text for a safe place. It contains the key itself, so it is as secret as the passphrase.</summary>
    public static string RecoveryKit(string projectRoot, string passphrase)
    {
        EncryptionConfig config = Read(projectRoot) ?? throw new AmpleException("AMP5310", "This project is not encrypted.");
        DataCipher cipher = Unwrap(config, passphrase);
        StringBuilder sb = new();
        sb.AppendLine("AMPLE recovery kit");
        sb.AppendLine("==================");
        sb.AppendLine("Keep this file somewhere safe and offline. Whoever has it can open the encrypted black box and backups of the project");
        sb.AppendLine("without the passphrase. Without the passphrase AND without this kit the encrypted data cannot be recovered by anyone.");
        sb.AppendLine();
        sb.AppendLine("project folder: " + Path.GetFullPath(projectRoot));
        sb.AppendLine("created: " + config.Created.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine("cipher: " + config.Cipher);
        sb.AppendLine("data-key (base64): " + Convert.ToBase64String(cipher.Key));
        sb.AppendLine("encryption.json:");
        sb.AppendLine(JsonSerializer.Serialize(config, Options));
        return sb.ToString();
    }

    /// <summary>Reads the data key out of a recovery kit (for tests and for the tool that restores from a kit).</summary>
    public static DataCipher FromRecoveryKit(string kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        string? line = kit.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data-key (base64): ", StringComparison.Ordinal));
        if (line is null)
        {
            throw new AmpleException("AMP5310", "This is not a recovery kit of AMPLE.");
        }

        try
        {
            return new DataCipher(Convert.FromBase64String(line["data-key (base64): ".Length..]));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new AmpleException("AMP5310", "The key in this recovery kit is damaged.");
        }
    }

    private static DataCipher Unlock(string key, EncryptionConfig config)
    {
        string passphrase = PassphraseSource(key) is { Length: > 0 } p ? p : throw new AmpleException("AMP5310", $"This is encrypted. Give the passphrase in the environment variable {PassphraseVariable} (or unlock it in the program that opened it).");
        DataCipher cipher = Unwrap(config, passphrase);
        Unlocked[key] = cipher;
        return cipher;
    }

    private static string Check(byte[] dataKey) => Convert.ToHexStringLower(HMACSHA256.HashData(dataKey, "ample-key-check"u8));
}
