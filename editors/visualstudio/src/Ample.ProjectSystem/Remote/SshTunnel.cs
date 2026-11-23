using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Ample.VisualStudio.Remote;

/// <summary>
/// An SSH tunnel: <c>ssh -N -L &lt;free local port&gt;:127.0.0.1:&lt;server port&gt; &lt;host&gt;</c>, started by this class and stopped when it is disposed.
/// It uses the ssh.exe of Windows (OpenSSH) with the person's own keys; it never asks for a password (BatchMode), so a missing key shows as an error
/// message, never as a hidden prompt.
/// </summary>
public sealed class SshTunnel : System.IDisposable
{
    private readonly Process process;

    private SshTunnel(Process process, int localPort)
    {
        this.process = process;
        LocalUrl = "http://127.0.0.1:" + localPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the address on this machine that leads to the server's AMPLE.</summary>
    public string LocalUrl { get; }

    /// <summary>Gets whether the ssh process is still running.</summary>
    public bool Alive => !process.HasExited;

    /// <param name="host">The SSH target, for example user and host of your server.</param>
    /// <param name="remoteUrl">The address of the AMPLE on the server as the server sees it, for example http://127.0.0.1:55077.</param>
    /// <param name="cancellationToken">Cancels the wait for the tunnel.</param>
    public static async System.Threading.Tasks.Task<SshTunnel> StartAsync(string host, string remoteUrl, System.Threading.CancellationToken cancellationToken)
    {
        System.Uri remote = new(remoteUrl);
        int localPort = FreePort();
        ProcessStartInfo start = new("ssh", "-N -o ExitOnForwardFailure=yes -o BatchMode=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -L "
            + localPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + remote.Host + ":" + remote.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + host)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        Process process = Process.Start(start) ?? throw new System.IO.IOException("ssh did not start (is the OpenSSH client installed?).");
        System.Text.StringBuilder error = new();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        for (int i = 0; i < 150; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new System.IO.IOException("The SSH tunnel stopped: " + error.ToString().Trim());
            }

            if (CanConnect(localPort))
            {
                return new SshTunnel(process, localPort);
            }

            await System.Threading.Tasks.Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        try { process.Kill(); } catch (System.InvalidOperationException) { }
        throw new System.IO.IOException("The SSH tunnel did not come up in 15 seconds: " + error.ToString().Trim());
    }

    /// <summary>Stops the ssh process.</summary>
    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(); } catch (System.InvalidOperationException) { }
        process.Dispose();
    }

    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using TcpClient client = new();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
