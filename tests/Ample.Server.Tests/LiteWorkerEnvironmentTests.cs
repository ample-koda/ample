using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>The build worker of Remix runs what a visitor uploaded; it does not get the secrets of the server in its environment.</summary>
public sealed class LiteWorkerEnvironmentTests
{
    [Fact]
    public void Secrets_are_taken_out_and_the_rest_stays()
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/var/lib/ample-remix",
            ["DOTNET_gcServer"] = "0",
            ["AMPLE_FFMPEG"] = "/usr/bin/ffmpeg",
            ["AMPLE_REMIX_ADMIN_KEY"] = "admin",
            ["AMPLE_BRAVE_API_KEY"] = "brave",
            ["GITHUB_TOKEN"] = "gh",
            ["Some_Secret_Value"] = "s",
            ["DB_PASSWORD"] = "p",
            ["GPG_PASSPHRASE"] = "g",
            ["aws_credentials"] = "c",
        };

        LiteIsolation.RemoveSecrets(environment);

        Assert.Equal(["AMPLE_FFMPEG", "DOTNET_gcServer", "HOME", "PATH"], environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_real_environment_of_a_process_start_is_cleaned_too()
    {
        System.Diagnostics.ProcessStartInfo info = new("dotnet");
        info.Environment["AMPLE_REMIX_ADMIN_KEY"] = "admin";
        info.Environment["AMPLE_TEST_VISIBLE"] = "yes";
        LiteIsolation.RemoveSecrets(info.Environment);
        Assert.False(info.Environment.ContainsKey("AMPLE_REMIX_ADMIN_KEY"));
        Assert.Equal("yes", info.Environment["AMPLE_TEST_VISIBLE"]);
    }
}
