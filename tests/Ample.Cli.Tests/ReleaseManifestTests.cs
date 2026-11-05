using Ample.Storage.Security;

namespace Ample.Cli.Tests;

[Collection("passphrase-environment")]   // sets AMPLE_VAULT_PASSPHRASE for the whole process: shares a collection with SecurityCommandTests
public sealed class ReleaseManifestTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-release-").FullName;

    public void Dispose()
    {
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
    public void The_program_has_both_release_keys_built_in()
    {
        Assert.Contains("d0ebf7d9c4d8b7ce", ReleaseTrust.BuiltInKeys.Keys);
        Assert.Contains("597b43377806b15c", ReleaseTrust.BuiltInKeys.Keys);
    }

    [Fact]
    public void Release_verification_ignores_the_key_inside_the_signature_file_and_accepts_only_built_in_keys()
    {
        string home = Path.Combine(folder, "home");
        string file = Path.Combine(folder, "a.zip");
        File.WriteAllText(file, "payload");
        Environment.SetEnvironmentVariable("AMPLE_VAULT_PASSPHRASE", "a-long-enough-test-passphrase-1234");
        try
        {
            Assert.Equal(0, Run("keys", "generate", "--home", home).Code);
            Assert.Equal(0, Run("sign", "--file", file, "--home", home).Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMPLE_VAULT_PASSPHRASE", null);
        }

        // Signed by a key AMPLE does not trust: the ordinary check passes (the key is in the sig file), the release check refuses.
        Assert.Equal(0, Run("verify", "--file", file).Code);
        (int code, string text) = Run("verify", "--file", file, "--release");
        Assert.Equal(1, code);
        Assert.Contains("does not trust", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_lists_the_artefacts_with_size_and_sha256_and_rejects_a_bad_channel()
    {
        string file = Path.Combine(folder, "ample-0.9.2-linux-x64.zip");
        File.WriteAllText(file, "zip");
        string spec = Path.Combine(folder, "version.json");
        File.WriteAllText(spec, "{\"specVersion\":\"1.1\",\"status\":\"provisional\"}");
        string output = Path.Combine(folder, "m.json");

        (int code, _) = Run("release", "manifest", "--version", "0.9.2", "--spec-version-file", spec, "--out", output, file);
        Assert.Equal(0, code);
        string json = File.ReadAllText(output);
        Assert.Contains("\"channel\": \"stable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"reads\": \"1.1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"ample-0.9.2-linux-x64.zip\"", json, StringComparison.Ordinal);
        Assert.Contains("\"size\": 3", json, StringComparison.Ordinal);

        Assert.Equal(2, Run("release", "manifest", "--version", "1.3.0-rc.1", "--channel", "stable", "--spec-version-file", spec, "--out", output).Code);
    }
}
