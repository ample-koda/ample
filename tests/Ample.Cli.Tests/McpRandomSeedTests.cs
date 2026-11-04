using System.Diagnostics;
using System.Text.Json;

namespace Ample.Cli.Tests;

public sealed class McpRandomSeedTests
{
    [Fact]
    public async Task Mcp_exposes_true_random_positive_seeds_without_server_access()
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tfm = Path.GetFileName(baseDir);
        string config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        string root = Environment.GetEnvironmentVariable("AMPLE_REPO_ROOT") ?? baseDir;
        while (!File.Exists(Path.Combine(root, "src", "Ample.Cli", "Ample.Cli.csproj"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("AMPLE repository not found.");
        string dll = Environment.GetEnvironmentVariable("AMPLE_CLI_DLL") ?? Path.Combine(root, "src", "Ample.Cli", "bin", config, tfm, "Ample.Cli.dll");
        Assert.True(File.Exists(dll), dll);
        ProcessStartInfo start = new("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("mcp");
        start.ArgumentList.Add("--url");
        start.ArgumentList.Add("http://127.0.0.1:1");
        using Process process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"ample_random_seed\",\"arguments\":{}}}");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"ample_random_seed\",\"arguments\":{}}}");
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        string[] lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using JsonDocument tools = JsonDocument.Parse(lines[0]);
        Assert.Contains(tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "ample_random_seed");
        JsonElement reduxSearch = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "ample_redux_search");
        Assert.True(reduxSearch.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        using JsonDocument first = JsonDocument.Parse(lines[1]);
        using JsonDocument second = JsonDocument.Parse(lines[2]);
        ulong seed1 = first.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("seed").GetUInt64();
        ulong seed2 = second.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("seed").GetUInt64();
        Assert.InRange(seed1, 1UL, ulong.MaxValue);
        Assert.InRange(seed2, 1UL, ulong.MaxValue);
        Assert.InRange(first.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("blockSeed").GetUInt64(), 1UL, ulong.MaxValue);
        Assert.NotEqual(seed1, seed2);
    }
}
