namespace Ample.Cli.Tests;

[Trait("Category", "Vault")]
[Collection("passphrase-environment")]   // these tests set AMPLE_VAULT_PASSPHRASE for the whole process: classes that do that share one collection and so never run at the same time
public sealed class SecurityCommandTests : IDisposable
{
    private const string Canary = "CANARY-cli-5d2b-do-not-leak";
    private const string Passphrase = "a long enough passphrase";
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-security-").FullName;
    private readonly TextReader input = Console.In;

    public void Dispose()
    {
        Console.SetIn(input);
        foreach (string v in new[] { "AMPLE_PASSPHRASE", "AMPLE_VAULT_PASSPHRASE", "AMPLE_NEW_PASSPHRASE" })
        {
            Environment.SetEnvironmentVariable(v, null);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void The_vault_takes_values_from_standard_input_and_lists_only_names()
    {
        string home = Path.Combine(folder, "home");
        Environment.SetEnvironmentVariable("AMPLE_VAULT_PASSPHRASE", Passphrase);
        Console.SetIn(new StringReader(Canary + "\n"));
        (int code, string text) = Run("vault", "set", "API_KEY", "--home", home);
        Assert.Equal(0, code);
        Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
        (code, text) = Run("vault", "list", "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("API_KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, File.ReadAllText(Path.Combine(home, "vault.json")), StringComparison.Ordinal);

        Environment.SetEnvironmentVariable("AMPLE_VAULT_PASSPHRASE", "the wrong passphrase");
        (code, text) = Run("vault", "list", "--home", home);
        Assert.Equal(1, code);
        Assert.Contains("AMP5311", text, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable("AMPLE_VAULT_PASSPHRASE", Passphrase);
        Assert.Contains("Removed", Run("vault", "remove", "API_KEY", "--home", home).Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_passphrase_in_the_environment_the_commands_say_so()
    {
        (int code, string text) = Run("vault", "list", "--home", Path.Combine(folder, "none"));
        Assert.Equal(1, code);
        Assert.Contains("AMPLE_VAULT_PASSPHRASE", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Encrypt_sign_and_verify_work_end_to_end()
    {
        string project = Path.Combine(folder, "song");
        string home = Path.Combine(folder, "home");
        Assert.Equal(0, Run("new", project).Code);
        Environment.SetEnvironmentVariable("AMPLE_PASSPHRASE", Passphrase);
        Environment.SetEnvironmentVariable("AMPLE_VAULT_PASSPHRASE", Passphrase);

        Assert.Contains("Not encrypted", Run("encrypt", "status", "--project", project).Output, StringComparison.Ordinal);
        (int code, string text) = Run("encrypt", "enable", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("Nothing was changed", text, StringComparison.Ordinal);
        Assert.Contains("encrypt this project", text, StringComparison.Ordinal);
        Assert.Contains("Not encrypted", Run("encrypt", "status", "--project", project).Output, StringComparison.Ordinal);

        (code, text) = Run("encrypt", "enable", "--project", project, "--accept", "encrypt this project", "--reason", "test");
        Assert.Equal(0, code);
        Assert.Contains("encrypted now", text, StringComparison.Ordinal);
        Assert.Contains("Encrypted since", Run("encrypt", "status", "--project", project).Output, StringComparison.Ordinal);

        string kit = Path.Combine(folder, "kit.txt");
        Assert.Equal(0, Run("encrypt", "recovery-kit", "--project", project, "--out", kit).Code);
        Assert.Contains("AMPLE recovery kit", File.ReadAllText(kit), StringComparison.Ordinal);

        Assert.Equal(0, Run("keys", "generate", "--home", home).Code);
        (code, text) = Run("sign", "--project", project, "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("Signed record", text, StringComparison.Ordinal);
        (code, text) = Run("verify", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("1 good", text, StringComparison.Ordinal);
        Assert.Contains("hash chain intact", text, StringComparison.Ordinal);

        string target = Path.Combine(folder, "backups");
        Assert.Equal(0, Run("backup", "create", "--target", target, "--project", project).Code);
        (code, text) = Run("backup", "verify", "--target", target);
        Assert.Equal(0, code);
        Assert.Contains("intact", text, StringComparison.Ordinal);

        string export = Path.Combine(folder, "song.ample");
        Assert.Equal(0, Run("export", export, "--project", project).Code);
        Assert.Equal(0, Run("sign", "--file", export, "--home", home).Code);
        Assert.Equal(0, Run("verify", "--file", export).Code);
        File.AppendAllText(export, "x");
        (code, text) = Run("verify", "--file", export);
        Assert.Equal(1, code);
        Assert.Contains("AMP5312", text, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable("AMPLE_PASSPHRASE", null);
        Ample.Storage.Security.ProjectEncryption.Forget(project);
        Assert.Contains("AMP5310", Run("verify", "--project", project).Output, StringComparison.Ordinal);
        Environment.SetEnvironmentVariable("AMPLE_PASSPHRASE", Passphrase);
        (code, text) = Run("encrypt", "disable", "--project", project, "--accept", "decrypt this project", "--reason", "test");
        Assert.Equal(0, code);
        Assert.Contains("not encrypted any more", text, StringComparison.Ordinal);
    }
}
