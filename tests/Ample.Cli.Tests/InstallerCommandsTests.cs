using System.Net;

namespace Ample.Cli.Tests;

public sealed class InstallerCommandsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-installer-").FullName;

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
    public void Packs_install_refuses_a_download_that_does_not_match_the_pinned_digest_and_leaves_nothing_behind()
    {
        using HttpListener listener = new();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch (HttpListenerException) { return; } catch (ObjectDisposedException) { return; }
                byte[] body = "not the real soundfont"u8.ToArray();
                c.Response.OutputStream.Write(body);
                c.Response.Close();
            }
        });

        (int code, string text) = Run("packs", "install", "soundfont", "--home", folder, "--from", $"http://127.0.0.1:{port}/");
        listener.Stop();

        Assert.Equal(1, code);
        Assert.Contains("does not match the pinned digest", text, StringComparison.Ordinal);
        string pack = Path.Combine(folder, "audio-packs", "licensed", "musescore-general");
        Assert.False(File.Exists(Path.Combine(pack, "MuseScore_General.sf2")));
        Assert.Empty(Directory.GetFiles(pack, "*.part"));
    }

    [Fact]
    public void Packs_install_leaves_an_existing_file_with_another_digest_untouched()
    {
        string pack = Path.Combine(folder, "audio-packs", "licensed", "musescore-general");
        Directory.CreateDirectory(pack);
        string font = Path.Combine(pack, "MuseScore_General.sf2");
        File.WriteAllText(font, "mine");

        (int code, string text) = Run("packs", "install", "soundfont", "--home", folder, "--from", "http://127.0.0.1:9/");

        Assert.Equal(1, code);
        Assert.Contains("leaving it untouched", text, StringComparison.Ordinal);
        Assert.Equal("mine", File.ReadAllText(font));
    }

    [Fact]
    public void Doctor_assistant_reports_the_state_of_codex_and_never_fails()
    {
        (int code, string text) = Run("doctor", "--assistant");
        Assert.Equal(0, code);
        Assert.Contains("assistant:", text, StringComparison.Ordinal);
    }

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
