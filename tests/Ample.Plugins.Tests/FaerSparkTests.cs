using System.Net;
using System.Net.Sockets;
using Ample.Core.Abstractions;

namespace Ample.Plugins.Tests;

public sealed class FaerSparkTests
{
    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task A_server_that_already_answers_is_used_and_never_owned()
    {
        int port = FreePort();
        using HttpListener server = new();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext c = await server.GetContextAsync();
                c.Response.StatusCode = 200;
                c.Response.Close();
            }
        });
        using FaerSpark spark = new(string.Empty, string.Empty, port);
        await using (await spark.AcquireAsync(CancellationToken.None))
        {
            Assert.False(spark.Owns);
        }
    }

    [Fact]
    public async Task Without_a_server_and_without_an_executable_the_build_is_told_what_to_set()
    {
        using FaerSpark spark = new(string.Empty, string.Empty, FreePort());
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(() => spark.AcquireAsync(CancellationToken.None));
        Assert.Equal("AMP5381", ex.DiagnosticCode);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("cli", null, false)]
    [InlineData("server", "http://127.0.0.1:9999", false)]
    [InlineData("server-then-cli", "http://127.0.0.1:8085", true)]
    public void Only_a_song_that_uses_the_local_server_asks_for_it(string? backend, string? endpoint, bool expected)
    {
        Dictionary<string, string> settings = [];
        if (backend is not null) settings["backend"] = backend;
        if (endpoint is not null) settings["endpoint"] = endpoint;
        using FaerSpark spark = new(string.Empty, string.Empty, 8085);
        Assert.Equal(expected, spark.Serves(settings));
    }

    private static HttpListener Serve(int port)
    {
        HttpListener server = new();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (server.IsListening)
                {
                    HttpListenerContext c = await server.GetContextAsync();
                    c.Response.StatusCode = 200;
                    c.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // The test ended.
            }
        });
        return server;
    }

    [Fact]
    public async Task A_live_lease_of_another_manager_is_seen_and_a_dead_one_is_removed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-spark-" + Guid.NewGuid().ToString("N"));
        int port = FreePort();
        using HttpListener server = Serve(port);
        using FaerSpark a = new(string.Empty, string.Empty, port, null, folder);
        using FaerSpark b = new(string.Empty, string.Empty, port, null, folder);
        Assert.False(b.OthersHoldLease());
        IAsyncDisposable lease = await a.AcquireAsync(CancellationToken.None);
        Assert.True(b.OthersHoldLease());
        Assert.False(a.OthersHoldLease());
        await lease.DisposeAsync();
        Assert.False(b.OthersHoldLease());

        string stale = Path.Combine(folder, $"lease-{port}-dead.json");
        await File.WriteAllTextAsync(stale, "2147483000");
        Assert.False(b.OthersHoldLease());
        Assert.False(File.Exists(stale));
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_start_a_server_and_dispose_is_repeatable()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-spark-" + Guid.NewGuid().ToString("N"));
        using CancellationTokenSource cts = new();
        FaerSpark spark = new(string.Empty, string.Empty, FreePort(), null, folder);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => spark.AcquireAsync(cts.Token));
        spark.Dispose();
        spark.Dispose();
    }
}
