using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.VisualStudio.Remote;

namespace Ample.Server.Tests;

public sealed class RemoteSyncTests
{
    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }

    [Fact]
    public async Task A_song_is_built_on_the_server_and_its_master_comes_back_as_a_wav()
    {
        string server = Path.Combine(Path.GetTempPath(), "ample-rbuild-" + Guid.NewGuid().ToString("N"));
        string home = server + "-home";
        string wav = Path.Combine(server + "-out", "play.wav");
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        await File.WriteAllTextAsync(
            Path.Combine(server, "tune.amplet"),
            "composition \"t\" { ample 1  tempo 100  master-seed 3 }\n\nmidi-clip motif at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:64:100\"\n}\n\n"
            + "piano lead at 1 for 2 {\n  from motif.notes -> notes\n}\n");
        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient client = new(new RemoteOptions { Url = running.Url, User = "koda", PasswordProvider = () => "a-long-password" });
            await RemoteBuild.BuildToFileAsync(client, "tune.amplet", wav);
            byte[] bytes = await File.ReadAllBytesAsync(wav);
            Assert.True(bytes.Length > 1000);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        }
        finally
        {
            foreach (string d in new[] { server, server + "-out", home })
            {
                try { Directory.Delete(d, true); }
                catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task A_pull_brings_text_and_binary_files_a_push_sends_local_changes_and_a_change_on_both_sides_never_loses_the_local_file()
    {
        string server = Path.Combine(Path.GetTempPath(), "ample-rsync-s-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(Path.GetTempPath(), "ample-rsync-c-" + Guid.NewGuid().ToString("N"));
        string home = server + "-home";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        byte[] kick = [0, 1, 2, 3, 250, 251, 252, 253];
        Directory.CreateDirectory(Path.Combine(server, "samples"));
        await File.WriteAllBytesAsync(Path.Combine(server, "samples", "kick.wav"), kick);
        await File.WriteAllTextAsync(Path.Combine(server, "song.amplet"), "project demo\none\n");
        await File.WriteAllTextAsync(Path.Combine(server, "notes.txt"), "server notes\n");
        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient client = new(new RemoteOptions { Url = running.Url, User = "koda", PasswordProvider = () => "a-long-password" });
            RemoteSync sync = new(client, cache);

            // pull: everything comes, binary too (byte for byte)
            SyncResult pulled = await sync.PullAsync();
            Assert.Contains("song.amplet", pulled.Fetched);
            Assert.Contains("samples/kick.wav", pulled.Fetched);
            Assert.Equal(kick, await File.ReadAllBytesAsync(Path.Combine(cache, "samples", "kick.wav")));
            Assert.Empty(pulled.Conflicts);
            Assert.Empty((await sync.PullAsync()).Fetched);   // nothing changed: nothing to do

            // a local change goes to the server, an untouched file does not
            await File.WriteAllTextAsync(Path.Combine(cache, "song.amplet"), "project demo\none\ntwo\n");
            await File.WriteAllTextAsync(Path.Combine(cache, "new.txt"), "a new file\n");
            SyncResult pushed = await sync.PushAsync();
            Assert.Equal(["new.txt", "song.amplet"], pushed.Pushed.Order().ToList());
            Assert.Equal("project demo\none\ntwo\n", await File.ReadAllTextAsync(Path.Combine(server, "song.amplet")));
            Assert.Equal("a new file\n", await File.ReadAllTextAsync(Path.Combine(server, "new.txt")));

            // a change on the server only comes here
            await File.WriteAllTextAsync(Path.Combine(server, "notes.txt"), "server notes 2\n");
            SyncResult next = await sync.PullAsync();
            Assert.Equal(["notes.txt"], next.Fetched);
            Assert.Equal("server notes 2\n", await File.ReadAllTextAsync(Path.Combine(cache, "notes.txt")));

            // a change on both sides: the push refuses it, the pull puts the local file aside and takes the server's
            await File.WriteAllTextAsync(Path.Combine(server, "notes.txt"), "server notes 3\n");
            await File.WriteAllTextAsync(Path.Combine(cache, "notes.txt"), "my notes\n");
            SyncResult refused = await sync.PushAsync();
            Assert.Equal(["notes.txt"], refused.Conflicts);
            Assert.Equal("server notes 3\n", await File.ReadAllTextAsync(Path.Combine(server, "notes.txt")));
            SyncResult resolved = await sync.PullAsync();
            Assert.Equal(["notes.txt"], resolved.Conflicts);
            Assert.NotNull(resolved.ConflictId);
            Assert.Equal("my notes\n", await File.ReadAllTextAsync(Path.Combine(cache, "_conflicts", resolved.ConflictId!, "notes.txt")));
            Assert.Equal("server notes 3\n", await File.ReadAllTextAsync(Path.Combine(cache, "notes.txt")));
        }
        finally
        {
            foreach (string d in new[] { server, cache, home })
            {
                try { Directory.Delete(d, true); }
                catch (IOException) { }
            }
        }
    }
}
