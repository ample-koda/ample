using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Ample.Cli.Tests;

public sealed class LspCommandTests
{
    private static string AmpleRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "spec")) && Directory.Exists(Path.Combine(dir.FullName, "spec-tests")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("AMPLE folder not found");
    }

    private static async Task SendAsync(Stream s, object message)
    {
        byte[] body = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(message));
        await s.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    private static async Task<JsonObject> ReceiveAsync(Stream s)
    {
        StringBuilder header = new();
        byte[] one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.Equal(1, await s.ReadAsync(one));
            header.Append((char)one[0]);
        }

        int length = int.Parse(header.ToString().Split("Content-Length:")[1].Split('\r')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            read += await s.ReadAsync(body.AsMemory(read));
        }

        return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(body))!;
    }

    [Fact]
    public async Task The_lsp_command_speaks_the_protocol_on_stdio_and_exits_zero_after_shutdown()
    {
        string dll = typeof(CliApplication).Assembly.Location;
        using Process p = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" lsp --root \"{AmpleRoot()}\"") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        await SendAsync(p.StandardInput.BaseStream, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
        JsonObject init = await ReceiveAsync(p.StandardOutput.BaseStream);
        Assert.Equal("ample-lsp", (string?)init["result"]!["serverInfo"]!["name"]);
        await SendAsync(p.StandardInput.BaseStream, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri = "file:///a.amplet", languageId = "amplet", version = 1, text = "midi-clip c {\n}\n" } } });
        JsonObject diag = await ReceiveAsync(p.StandardOutput.BaseStream);
        Assert.Equal("textDocument/publishDiagnostics", (string?)diag["method"]);
        await SendAsync(p.StandardInput.BaseStream, new { jsonrpc = "2.0", id = 2, method = "shutdown" });
        await ReceiveAsync(p.StandardOutput.BaseStream);
        await SendAsync(p.StandardInput.BaseStream, new { jsonrpc = "2.0", method = "exit" });
        Assert.True(p.WaitForExit(15000));
        Assert.Equal(0, p.ExitCode);
    }
}
