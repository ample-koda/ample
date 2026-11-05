using System.CommandLine;
using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Engine.Security;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Security;

namespace Ample.Cli;

/// <summary>The <c>ample vault</c>, <c>keys</c>, <c>sign</c>, <c>verify</c> and <c>encrypt</c> commands. Passphrases and secret values are never arguments: they come from the environment or from standard input.</summary>
internal static class SecurityCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output) => [Vault(output), Keys(output), Sign(output), Verify(output), Encrypt(output)];

    private static Option<string?> HomeOption() => new("--home") { Description = "The library folder (default: AMPLE_HOME, else the local application data of the user)." };

    private static Option<string?> ProjectOption() => new("--project") { Description = "The project folder (default: the current folder)." };

    private static string Home(string? home) => home ?? Ample.Storage.Library.Library.DefaultHome();

    private static string Env(string name, string what) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new AmpleException("AMP5311", $"Give {what} in the environment variable {name}; it is never taken from an argument.");

    private static async Task<int> Guard(TextWriter output, Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<(OpenProject Project, string Root)> OpenAsync(string? folder, bool writer, CancellationToken token)
    {
        string root = Path.GetFullPath(folder ?? Directory.GetCurrentDirectory());
        return (await ProjectStore.OpenAsync(Fs, Clock, root, readOnly: !writer, token), root);
    }

    private static Command Vault(TextWriter output)
    {
        Command root = new("vault", "Secrets (API keys, tokens) kept outside every project. Plugins get only the secrets their manifest names.");
        Argument<string> name = new("name") { Description = "The name of the secret (letters, digits, _ - .)." };
        Option<string?> homeSet = HomeOption();
        Command set = new("set", $"Stores a secret. The value is read from standard input; the passphrase of the vault from {PassphraseVault.PassphraseVariable} (a new vault is made when there is none).") { name, homeSet };
        set.SetAction((parse, token) => Guard(output, async () =>
        {
            string home = Home(parse.GetValue(homeSet));
            string passphrase = Env(PassphraseVault.PassphraseVariable, "the passphrase of the vault");
            string value = (await Console.In.ReadToEndAsync(token)).TrimEnd('\r', '\n');
            if (value.Length == 0)
            {
                throw new AmpleException("AMP5311", "Give the secret on standard input, for example: echo the-secret | ample vault set <name>");
            }

            PassphraseVault vault = PassphraseVault.Exists(home) ? PassphraseVault.Open(home, passphrase) : PassphraseVault.Create(home, passphrase);
            vault.Set(parse.GetValue(name)!, value);
            output.WriteLine($"Stored '{parse.GetValue(name)}' in the vault.");
            return 0;
        }));

        Option<string?> homeList = HomeOption();
        Command list = new("list", "Lists the names of the secrets (never the values).") { homeList };
        list.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            PassphraseVault vault = PassphraseVault.Open(Home(parse.GetValue(homeList)), Env(PassphraseVault.PassphraseVariable, "the passphrase of the vault"));
            foreach (string n in vault.Names)
            {
                output.WriteLine(n);
            }

            return Task.FromResult(0);
        }));

        Argument<string> removeName = new("name") { Description = "The name of the secret." };
        Option<string?> homeRemove = HomeOption();
        Command remove = new("remove", "Removes a secret.") { removeName, homeRemove };
        remove.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            PassphraseVault vault = PassphraseVault.Open(Home(parse.GetValue(homeRemove)), Env(PassphraseVault.PassphraseVariable, "the passphrase of the vault"));
            output.WriteLine(vault.Remove(parse.GetValue(removeName)!) ? "Removed." : "There was no such secret.");
            return Task.FromResult(0);
        }));

        foreach (Command c in new[] { set, list, remove })
        {
            root.Subcommands.Add(c);
        }

        return root;
    }

    private static Command Keys(TextWriter output)
    {
        Command root = new("keys", "The signing keys of the user (ECDSA P-256). The private key is sealed with the passphrase of the vault.");
        Option<string?> homeGen = HomeOption();
        Command generate = new("generate", "Makes a signing key.") { homeGen };
        generate.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            SigningKeyInfo k = new SigningKeys(Home(parse.GetValue(homeGen))).Generate(Env(PassphraseVault.PassphraseVariable, "the passphrase for the key"), Clock.UtcNow);
            output.WriteLine($"Key {k.KeyId} made and active.");
            return Task.FromResult(0);
        }));

        Option<string?> homeList = HomeOption();
        Command list = new("list", "Lists the keys.") { homeList };
        list.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            foreach (SigningKeyInfo k in new SigningKeys(Home(parse.GetValue(homeList))).List())
            {
                output.WriteLine($"{k.KeyId}  {k.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  {(k.Retired is null ? "active" : "retired " + k.Retired.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
            }

            return Task.FromResult(0);
        }));

        Argument<string> exportId = new("key") { Description = "The key id." };
        Option<string?> homeExport = HomeOption();
        Command export = new("export", "Prints the public key (PEM) for someone who verifies your signatures.") { exportId, homeExport };
        export.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            output.Write(new SigningKeys(Home(parse.GetValue(homeExport))).ExportPublic(parse.GetValue(exportId)!));
            return Task.FromResult(0);
        }));

        Option<string?> homeRotate = HomeOption();
        Command rotate = new("rotate", "Retires the active key (it keeps verifying old signatures, but cannot sign any more) and makes a new one.") { homeRotate };
        rotate.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            SigningKeyInfo k = new SigningKeys(Home(parse.GetValue(homeRotate))).Rotate(Env(PassphraseVault.PassphraseVariable, "the passphrase for the new key"), Clock.UtcNow);
            output.WriteLine($"New key {k.KeyId} is active; the previous one is retired.");
            return Task.FromResult(0);
        }));

        foreach (Command c in new[] { generate, list, export, rotate })
        {
            root.Subcommands.Add(c);
        }

        return root;
    }

    private static Command Sign(TextWriter output)
    {
        Option<string?> project = ProjectOption();
        Option<string?> home = HomeOption();
        Option<string?> file = new("--file") { Description = "Sign this file (an export) instead of the black box; the signature goes next to it as <file>.sig." };
        Command sign = new("sign", "Signs the newest record of the black box (or a file) with your signing key. The passphrase of the key is read from " + PassphraseVault.PassphraseVariable + ".") { project, home, file };
        sign.SetAction((parse, token) => Guard(output, async () =>
        {
            SigningKeys keys = new(Home(parse.GetValue(home)));
            string passphrase = Env(PassphraseVault.PassphraseVariable, "the passphrase of the signing key");
            if (parse.GetValue(file) is { } f)
            {
                output.WriteLine("Wrote " + Signatures.SignFile(Path.GetFullPath(f), keys, passphrase, Clock.UtcNow));
                return 0;
            }

            (OpenProject open, string root) = await OpenAsync(parse.GetValue(project), true, token);
            using (open)
            {
                using Journal journal = await Journal.OpenAsync(Fs, Clock, root, open.Manifest.Id.ToString("D"), cancellationToken: token);
                (long seq, string keyId) = Signatures.SignHead(journal, root, open.Manifest.Id, keys, passphrase, Clock.UtcNow);
                output.WriteLine($"Signed record {seq.ToString(CultureInfo.InvariantCulture)} with key {keyId}.");
            }

            return 0;
        }));
        return sign;
    }

    private static Command Verify(TextWriter output)
    {
        Option<string?> project = ProjectOption();
        Option<string?> file = new("--file") { Description = "Check the signature of this file (<file>.sig) instead of the project." };
        Option<string?> trust = new("--trust") { Description = "Only accept signatures of this key id." };
        Option<bool> release = new("--release") { Description = "With --file: accept only the keys that are built into AMPLE (the release keys); the key written in the signature file is ignored." };
        Command verify = new("verify", "Checks the hash chain of the black box and every signature of the project (or the signature of a file). Exit code 1 when anything is wrong.") { project, file, trust, release };
        verify.SetAction((parse, token) => Guard(output, async () =>
        {
            if (parse.GetValue(file) is { } f)
            {
                string signer = parse.GetValue(release) ? ReleaseTrust.VerifyFile(Path.GetFullPath(f)) : Signatures.VerifyFile(Path.GetFullPath(f), parse.GetValue(trust));
                output.WriteLine($"The file is intact and signed by key {signer}{(parse.GetValue(release) ? " (a release key built into AMPLE)" : string.Empty)}.");
                return 0;
            }

            (OpenProject open, string root) = await OpenAsync(parse.GetValue(project), false, token);
            using (open)
            {
                using Journal journal = await Journal.OpenAsync(Fs, Clock, root, open.Manifest.Id.ToString("D"), cancellationToken: token);
                VerifyReport chain = await journal.VerifyAsync(token);
                output.WriteLine(chain.BadSeq is null ? $"Black box: {chain.Records.ToString(CultureInfo.InvariantCulture)} record(s), hash chain intact." : $"Black box: record {chain.BadSeq.Value.ToString(CultureInfo.InvariantCulture)} is damaged or was changed.");
                SignatureReport signatures = await Signatures.VerifyHeadsAsync(journal, root, open.Manifest.Id, parse.GetValue(trust), token);
                output.WriteLine($"Signatures: {signatures.Checked.ToString(CultureInfo.InvariantCulture)} good, {signatures.Unverifiable.ToString(CultureInfo.InvariantCulture)} no longer checkable (thinned), {signatures.Problems.Count.ToString(CultureInfo.InvariantCulture)} problem(s).");
                foreach (string p in signatures.Problems)
                {
                    output.WriteLine("AMP5312: " + p);
                }

                return chain.BadSeq is null && signatures.Ok ? 0 : 1;
            }
        }));
        return verify;
    }

    private static Command Encrypt(TextWriter output)
    {
        Command root = new("encrypt", $"Optional encryption at rest of the black box and of backups (off by default). The passphrase comes from {ProjectEncryption.PassphraseVariable}.");
        Option<string?> statusProject = ProjectOption();
        Command status = new("status", "Says whether the project is encrypted.") { statusProject };
        status.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            string path = Path.GetFullPath(parse.GetValue(statusProject) ?? Directory.GetCurrentDirectory());
            EncryptionConfig? config = ProjectEncryption.Read(path);
            output.WriteLine(config is null ? "Not encrypted." : $"Encrypted since {config.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({config.Cipher}, key from a passphrase by {config.Kdf}, {config.Iterations.ToString(CultureInfo.InvariantCulture)} iterations).");
            return Task.FromResult(0);
        }));

        Command enable = ActCommand(output, "enable", "Switches encryption on (a dangerous act: run it without --accept for the dry run). The new passphrase is read from " + ProjectEncryption.PassphraseVariable + ".", true);
        Command disable = ActCommand(output, "disable", "Switches encryption off again (a dangerous act: run it without --accept for the dry run).", false);

        Option<string?> kitProject = ProjectOption();
        Option<string> kitOut = new("--out") { Description = "The file to write the recovery kit to (it contains the key: keep it offline).", Required = true };
        Command kit = new("recovery-kit", "Writes the recovery kit of an encrypted project.") { kitProject, kitOut };
        kit.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            string path = Path.GetFullPath(parse.GetValue(kitProject) ?? Directory.GetCurrentDirectory());
            File.WriteAllText(parse.GetValue(kitOut)!, ProjectEncryption.RecoveryKit(path, Env(ProjectEncryption.PassphraseVariable, "the passphrase of the project")));
            output.WriteLine("Wrote the recovery kit. It contains the key itself: keep it offline and safe.");
            return Task.FromResult(0);
        }));

        Option<string?> passProject = ProjectOption();
        Command passphrase = new("passphrase", $"Changes the passphrase (nothing is re-encrypted). The old one is read from {ProjectEncryption.PassphraseVariable}, the new one from AMPLE_NEW_PASSPHRASE.") { passProject };
        passphrase.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            string path = Path.GetFullPath(parse.GetValue(passProject) ?? Directory.GetCurrentDirectory());
            ProjectEncryption.ChangePassphrase(path, Env(ProjectEncryption.PassphraseVariable, "the old passphrase"), Env("AMPLE_NEW_PASSPHRASE", "the new passphrase"));
            output.WriteLine("The passphrase was changed. Backup folders get the new wrapping with the next backup.");
            return Task.FromResult(0);
        }));

        foreach (Command c in new[] { status, enable, disable, kit, passphrase })
        {
            root.Subcommands.Add(c);
        }

        return root;
    }

    private static Command ActCommand(TextWriter output, string name, string description, bool enabling)
    {
        Option<string?> project = ProjectOption();
        Option<string?> accept = new("--accept") { Description = "The words the dry run asked you to type; without it nothing changes." };
        Option<string?> reason = new("--reason") { Description = "Why (kept in the black box)." };
        Command command = new(name, description) { project, accept, reason };
        command.SetAction((parse, token) => Guard(output, async () =>
        {
            (OpenProject open, string root) = await OpenAsync(parse.GetValue(project), true, token);
            using (open)
            {
                DangerousAct act = enabling ? new EnableEncryptionAct(Env(ProjectEncryption.PassphraseVariable, "the new passphrase"), Clock.UtcNow) : new DisableEncryptionAct();
                using Journal journal = await Journal.OpenAsync(Fs, Clock, root, open.Manifest.Id.ToString("D"), cancellationToken: token);
                CommandContext context = new(Fs, root, Clock);
                DryRunResult dry = await act.DryRunAsync(context, token);
                foreach (string line in dry.Impact)
                {
                    output.WriteLine(line);
                }

                output.WriteLine(dry.BackupSuggestion);
                if (parse.GetValue(accept) is null)
                {
                    output.WriteLine($"Nothing was changed. To do it, run again with --accept \"{act.Acceptance}\" --reason \"...\".");
                    return 0;
                }

                await (enabling ? ((EnableEncryptionAct)act).RunAsync(journal, context, "cli", parse.GetValue(accept)!, parse.GetValue(reason) ?? string.Empty, token) : ((DisableEncryptionAct)act).RunAsync(journal, context, "cli", parse.GetValue(accept)!, parse.GetValue(reason) ?? string.Empty, token));
                output.WriteLine(enabling ? "The project is encrypted now. Write the recovery kit: ample encrypt recovery-kit --out <file>" : "The project is not encrypted any more.");
            }

            return 0;
        }));
        return command;
    }
}
