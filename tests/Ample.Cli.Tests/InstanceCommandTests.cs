using System.Text.Json;

namespace Ample.Cli.Tests;

public sealed class InstanceCommandTests
{
    [Fact]
    public void Json_lists_only_active_instance_records_without_launch_tokens()
    {
        string home = Path.Combine(Path.GetTempPath(), "ample-cli-registry-" + Guid.NewGuid().ToString("N"));
        using StringWriter output = new();
        try
        {
            int exitCode = CliApplication.Run(["instances", "--home", home, "--json"], output);

            Assert.Equal(0, exitCode);
            using JsonDocument result = JsonDocument.Parse(output.ToString());
            Assert.Equal(JsonValueKind.Array, result.RootElement.ValueKind);
            Assert.Empty(result.RootElement.EnumerateArray());
            Assert.DoesNotContain("token", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, true);
        }
    }
}
