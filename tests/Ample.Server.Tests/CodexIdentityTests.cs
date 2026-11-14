using System.Diagnostics;
using System.Text.Json.Nodes;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The Codex model catalog belongs to the identity that runs the task (the server account, the project identity or Koda's own login), and a sign-in that fails
/// ends in a terminal state, releases its Codex process and can be started again at once. A fake <c>codex app-server</c> (a node script) stands in for Codex; AMPLE_HOME
/// points at a private folder while a test runs, so nothing of the real user is read or changed.</summary>
public sealed class CodexIdentityTests : IAsyncLifetime
{
    private const string FakeCodex = """
        const fs = require('fs'), path = require('path');
        const home = process.env.AMPLE_HOME;
        const args = process.argv.slice(2);
        if (args[0] === '--version') { console.log('codex-cli 0.0.0'); process.exit(0); }
        const who = process.env.CODEX_HOME ? path.basename(process.env.CODEX_HOME) : 'server';
        const mode = () => { try { return fs.readFileSync(path.join(home, 'fake-mode.txt'), 'utf8').trim(); } catch { return 'ok'; } };
        fs.appendFileSync(path.join(home, 'spawn.log'), 'spawn ' + process.pid + ' ' + who + '\n');
        const send = (o) => console.log(JSON.stringify({ jsonrpc: '2.0', ...o }));
        require('readline').createInterface({ input: process.stdin }).on('line', (l) => {
          const m = JSON.parse(l);
          if (m.id === undefined) return;
          const fail = (text) => send({ id: m.id, error: { message: text } });
          if (m.method === 'initialize') return mode() === 'init-fails' ? fail('init refused') : send({ id: m.id, result: {} });
          if (m.method === 'model/list') { fs.appendFileSync(path.join(home, 'spawn.log'), 'models ' + who + '\n'); return send({ id: m.id, result: { data: [{ model: 'model-of-' + who }] } }); }
          if (m.method === 'account/read') return send({ id: m.id, result: { account: { type: 'apikey' } } });
          if (m.method === 'account/login/start') {
            if (mode() === 'login-fails') return fail('login refused');
            if (mode() === 'no-code') return send({ id: m.id, result: {} });
            return send({ id: m.id, result: { userCode: 'ABCD-1234', verificationUrl: 'https://example.invalid/device', loginId: '1' } });
          }
          send({ id: m.id, result: {} });
        });
        """;

    private readonly string temp = Path.Combine(Path.GetTempPath(), "ample-codexid-" + Guid.NewGuid().ToString("N"));
    private string? oldHome;
    private string projectFolder = string.Empty;
    private ProjectSession session = null!;

    private string Home => Path.Combine(temp, "home");

    public async Task InitializeAsync()
    {
        oldHome = Environment.GetEnvironmentVariable("AMPLE_HOME");
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("AMPLE_HOME", Home);
        string script = Path.Combine(temp, "fake-codex.js");
        File.WriteAllText(script, FakeCodex);
        File.WriteAllText(Path.Combine(Home, "ai-policy.json"), new JsonObject { ["codexPath"] = script }.ToJsonString());
        string? ampleRoot = AppContext.BaseDirectory;
        while (ampleRoot is not null && !File.Exists(Path.Combine(ampleRoot, "global.json"))) ampleRoot = Path.GetDirectoryName(ampleRoot);
        projectFolder = Path.Combine(temp, "project");
        using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), projectFolder, "codex-identity", null, CancellationToken.None)) { }
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "song.amplet"), "project codex-identity\n");
        session = await ProjectSession.OpenAsync(new ServerOptions(projectFolder, ampleRoot ?? throw new InvalidOperationException("AMPLE root not found."), LibraryHome: temp + "-library"), new EventBus(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await session.DisposeAsync();
        Environment.SetEnvironmentVariable("AMPLE_HOME", oldHome);
        foreach (string path in new[] { temp, temp + "-library" })
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { /* a child may still hold a file */ }
        }
    }

    private void Mode(string mode) => File.WriteAllText(Path.Combine(Home, "fake-mode.txt"), mode);

    private string[] Log() => File.Exists(Path.Combine(Home, "spawn.log")) ? File.ReadAllLines(Path.Combine(Home, "spawn.log")) : [];

    private int Count(string prefix) => Log().Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

    private string KodaName => session.Manifest.Id.ToString("D") + "-koda";

    private void LinkKoda()
    {
        string folder = Path.Combine(Home, "codex-homes", KodaName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "auth.json"), "{}");
    }

    private void UseDeviceIdentity()
    {
        string file = Path.Combine(projectFolder, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);
        JsonObject project = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        project["settings"]!["ai"]!["assistant"] = "codex-device";
        File.WriteAllText(file, project.ToJsonString());
        Assert.Equal("codex-device", session.Ai.Settings().Assistant);
    }

    private static async Task<bool> Ended(IEnumerable<int> pids)
    {
        for (int i = 0; i < 100; i++)
        {
            if (pids.All(pid => { try { using Process p = Process.GetProcessById(pid); return p.HasExited; } catch (ArgumentException) { return true; } }))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private IEnumerable<int> Pids() => Log().Where(l => l.StartsWith("spawn ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task Models_come_from_the_identity_that_runs_the_task_and_are_forgotten_on_unlink()
    {
        JsonObject server = await session.Ai.ModelsAsync(CancellationToken.None);
        Assert.Equal("model-of-server", server["models"]![0]!["id"]!.GetValue<string>());
        JsonObject kodaShared = await session.Ai.ModelsAsync(CancellationToken.None, koda: true);
        Assert.Equal("model-of-server", kodaShared["models"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(1, Count("models server"));

        LinkKoda();
        JsonObject koda = await session.Ai.ModelsAsync(CancellationToken.None, koda: true);
        Assert.Equal("model-of-" + KodaName, koda["models"]![0]!["id"]!.GetValue<string>());
        JsonObject again = await session.Ai.ModelsAsync(CancellationToken.None);
        Assert.Equal("model-of-server", again["models"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(1, Count("models server"));
        Assert.Equal(1, Count("models " + KodaName));

        await session.Ai.UnlinkKodaAsync();
        JsonObject afterUnlink = await session.Ai.ModelsAsync(CancellationToken.None, koda: true);
        Assert.Equal("model-of-server", afterUnlink["models"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(2, Count("models server"));
    }

    [Theory]
    [InlineData("init-fails")]
    [InlineData("login-fails")]
    [InlineData("no-code")]
    public async Task A_koda_sign_in_that_cannot_start_fails_releases_codex_and_can_be_retried_at_once(string mode)
    {
        Mode(mode);
        JsonObject failed = await session.Ai.StartKodaLoginAsync(CancellationToken.None);
        Assert.Equal("failed", failed["login"]!["state"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(failed["login"]!["error"]!.GetValue<string>()));
        Assert.True(await Ended(Pids()), "the Codex process of the failed sign-in must be gone");

        Mode("ok");
        JsonObject waiting = await session.Ai.StartKodaLoginAsync(CancellationToken.None);
        Assert.Equal("waiting", waiting["login"]!["state"]!.GetValue<string>());
        Assert.Equal("ABCD-1234", waiting["login"]!["userCode"]!.GetValue<string>());
        await session.Ai.CancelKodaLoginAsync();
        Assert.True(await Ended(Pids()));
    }

    [Theory]
    [InlineData("init-fails")]
    [InlineData("login-fails")]
    [InlineData("no-code")]
    public async Task A_project_identity_sign_in_that_cannot_start_fails_releases_codex_and_can_be_retried_at_once(string mode)
    {
        UseDeviceIdentity();
        Mode(mode);
        JsonObject failed = await session.Ai.StartDeviceLoginAsync(CancellationToken.None);
        Assert.Equal("failed", failed["state"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(failed["error"]!.GetValue<string>()));
        Assert.True(await Ended(Pids()), "the Codex process of the failed sign-in must be gone");

        Mode("ok");
        JsonObject waiting = await session.Ai.StartDeviceLoginAsync(CancellationToken.None);
        Assert.Equal("waiting", waiting["state"]!.GetValue<string>());
        await session.Ai.CancelDeviceLoginAsync();
        Assert.True(await Ended(Pids()));
    }
}
