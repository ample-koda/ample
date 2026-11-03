using Ample.Storage.Security;

namespace Ample.Plugins;

/// <summary>Gives plugins the secrets they declared from the user's vault. A plugin only receives the secrets named in its manifest.</summary>
public sealed class VaultSecretProvider(PassphraseVault vault) : ISecretProvider
{
    /// <summary>Opens the vault of a library folder when there is one and its passphrase is in <see cref="PassphraseVault.PassphraseVariable"/>; otherwise null (plugins then get no secrets).</summary>
    public static VaultSecretProvider? FromEnvironment(string home)
    {
        string? passphrase = Environment.GetEnvironmentVariable(PassphraseVault.PassphraseVariable);
        return string.IsNullOrEmpty(passphrase) || !PassphraseVault.Exists(home) ? null : new VaultSecretProvider(PassphraseVault.Open(home, passphrase));
    }

    /// <inheritdoc />
    public string? Lookup(string name) => vault.Get(name);
}
