using System.Text.Json;

namespace Ample.Plugins.Tests;

/// <summary>A plugin that needs the GPU and asks to be kept warm (setting keepWarmS) is reused by the next job instead of being started again.</summary>
[Trait("Category", "Plugins")]
public sealed class WarmPluginTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ample-warm-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task A_kept_warm_plugin_is_reused_until_it_is_released()
    {
        using PluginHost host = new();
        List<string> events = [];
        host.OnWarmEvent = events.Add;
        DiscoveredPlugin plugin = CreatePlugin();

        int first = await RunAsync(host, plugin, 60);
        int second = await RunAsync(host, plugin, 60);
        Assert.Equal(first, second);
        Assert.Single(host.WarmPlugins());

        Assert.Equal(1, host.ReleaseWarm("test"));
        Assert.Empty(host.WarmPlugins());
        int third = await RunAsync(host, plugin, 60);
        Assert.NotEqual(first, third);
        Assert.Contains(events, e => e.Contains("reused", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Contains("released (test)", StringComparison.Ordinal));
        host.ReleaseWarm("done");
    }

    [Fact]
    public async Task Without_keepWarmS_every_job_starts_a_fresh_process()
    {
        using PluginHost host = new();
        DiscoveredPlugin plugin = CreatePlugin();

        int first = await RunAsync(host, plugin, 0);
        int second = await RunAsync(host, plugin, 0);
        Assert.NotEqual(first, second);
        Assert.Empty(host.WarmPlugins());
    }

    [Fact]
    public async Task An_idle_kept_process_is_released_after_its_time()
    {
        using PluginHost host = new();
        DiscoveredPlugin plugin = CreatePlugin();

        await RunAsync(host, plugin, 1);
        Assert.Single(host.WarmPlugins());
        for (int i = 0; i < 40 && host.WarmPlugins().Count > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Empty(host.WarmPlugins());
    }

    [Fact]
    public async Task A_kept_process_is_released_when_video_memory_use_grows_while_it_is_idle()
    {
        long used = 2000;
        using PluginHost host = new() { PressureInterval = TimeSpan.FromMilliseconds(100), ReadGpu = () => new GpuTotals(8000 - Interlocked.Read(ref used), 8000, "test") };
        List<string> events = [];
        host.OnWarmEvent = events.Add;
        DiscoveredPlugin plugin = CreatePlugin();

        await RunAsync(host, plugin, 60);
        Assert.Single(host.WarmPlugins());
        Interlocked.Add(ref used, 2000);   // another program takes 2000 MB (the limit is 1024)
        for (int i = 0; i < 40 && host.WarmPlugins().Count > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Empty(host.WarmPlugins());
        Assert.Contains(events, e => e.Contains("grew by", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_kept_process_is_released_when_free_video_memory_gets_too_low()
    {
        long free = 6000;
        using PluginHost host = new() { PressureInterval = TimeSpan.FromMilliseconds(100), ReadGpu = () => new GpuTotals(Interlocked.Read(ref free), 8000, "test") };
        DiscoveredPlugin plugin = CreatePlugin();

        await RunAsync(host, plugin, 60);
        Interlocked.Exchange(ref free, 500);   // below the default minimum of 1000 MB
        for (int i = 0; i < 40 && host.WarmPlugins().Count > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Empty(host.WarmPlugins());
    }

    private DiscoveredPlugin CreatePlugin()
    {
        string plugins = Path.Combine(root, "plugins");
        string folder = Path.Combine(plugins, "warm");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(plugins, "sdk"));
        File.Copy(Path.Combine(Paths.PluginFolder, "sdk", "ample-plugin.mjs"), Path.Combine(plugins, "sdk", "ample-plugin.mjs"));
        string manifest = File.ReadAllText(Path.Combine(Paths.PluginFolder, "echo", "plugin.json"))
            .Replace("\"id\": \"echo\"", "\"id\": \"warm\"", StringComparison.Ordinal)
            .Replace("\"gpu\": false", "\"gpu\": true", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest);
        File.WriteAllText(Path.Combine(folder, "plugin.mjs"), """
            import fs from 'node:fs';
            import path from 'node:path';
            import { serve } from '../sdk/ample-plugin.mjs';
            serve({
              initialize: () => ({ protocol: 1, id: 'warm', version: '1.0.0', blocks: ['ai-voice'] }),
              render: async (params) => {
                fs.mkdirSync(path.join(params.jobDir, 'out'), { recursive: true });
                fs.writeFileSync(path.join(params.jobDir, 'out', 'vocal.wav'), 'x');
                return { outputs: [{ role: 'vocal', path: 'out/vocal.wav' }], meta: { pid: process.pid } };
              },
            });
            """);
        (IReadOnlyList<DiscoveredPlugin> found, IReadOnlyList<string> problems) = PluginHost.Discover([plugins]);
        Assert.Empty(problems);
        return found.Single(p => p.Manifest.Id == "warm");
    }

    private async Task<int> RunAsync(PluginHost host, DiscoveredPlugin plugin, int keepWarmSeconds)
    {
        string job = Path.Combine(root, "job-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(job);
        string parameters = JsonSerializer.Serialize(new { jobDir = job, settings = new { keepWarmS = keepWarmSeconds } });
        PluginResult result = await host.RenderAsync(plugin, new PluginJob(job, parameters, TimeSpan.FromSeconds(30)));
        using JsonDocument meta = JsonDocument.Parse(result.MetaJson);
        return meta.RootElement.GetProperty("pid").GetInt32();
    }
}
