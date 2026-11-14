using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Ample.Server.Tests;

/// <summary>The deadline of a Codex request covers queueing, writing and waiting for the answer; a failed or timed-out send leaves no pending request and no live connection.</summary>
public sealed class CodexRpcDeadlineTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ample-codexrpc-" + Guid.NewGuid().ToString("N"));

    public CodexRpcDeadlineTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (IOException) { /* a child may still hold the folder */ }
    }

    private string Fake(string script)
    {
        string path = Path.Combine(folder, "fake-codex-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(path, script);
        return path;
    }

    private CodexRpc Start(string script) => CodexRpc.Start(Fake(script), Path.Combine(folder, "home"), folder, []);

    private static int PendingCount(CodexRpc rpc) => ((ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>>)typeof(CodexRpc).GetField("pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rpc)!).Count;

    [Fact]
    public async Task A_request_that_answers_works_and_leaves_nothing_pending()
    {
        await using CodexRpc rpc = Start("require('readline').createInterface({input:process.stdin}).on('line',l=>{const m=JSON.parse(l);console.log(JSON.stringify({jsonrpc:'2.0',id:m.id,result:{ok:true}}));});");
        JsonNode? result = await rpc.RequestAsync("ping", null, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.Equal(0, PendingCount(rpc));
    }

    [Fact]
    public async Task A_write_that_blocks_on_a_child_that_never_reads_times_out_closes_the_connection_and_cleans_up()
    {
        await using CodexRpc rpc = Start("setTimeout(()=>{},120000);");
        JsonObject big = new() { ["blob"] = new string('x', 16 * 1024 * 1024) };
        Stopwatch watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => rpc.RequestAsync("big", big, TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the deadline must cover the write: " + watch.Elapsed);
        Assert.Equal(0, PendingCount(rpc));
        for (int i = 0; i < 100 && !rpc.Exited; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(rpc.Exited);
    }

    [Fact]
    public async Task A_send_to_a_child_that_has_already_ended_fails_and_cleans_up_the_pending_request()
    {
        await using CodexRpc rpc = Start("process.exit(0);");
        for (int i = 0; i < 100 && !rpc.Exited; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(rpc.Exited);
        await Assert.ThrowsAnyAsync<Exception>(() => rpc.RequestAsync("late", new JsonObject { ["blob"] = new string('x', 1024 * 1024) }, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(0, PendingCount(rpc));
    }
}
