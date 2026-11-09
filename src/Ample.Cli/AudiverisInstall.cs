using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ample.Storage.Projects;

namespace Ample.Cli;

/// <summary>
/// <c>ample packs install audiveris</c>: the optical music recognition (Audiveris, audiveris.org, AGPL, a separate program) that turns a PDF or a picture of sheet music into notes.
/// It is not part of AMPLE: the installer of the pinned upstream release (5.11.0) is downloaded from GitHub, checked against a fixed SHA-256 and unpacked for this person alone
/// into <c>&lt;AMPLE home&gt;/audiveris</c> (no administrator rights): <c>dpkg-deb -x</c> on Ubuntu, an administrative extraction of the MSI on Windows. Nothing is installed system-wide.
/// </summary>
internal static class AudiverisInstall
{
    internal const string Version = "5.11.0";
    internal const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>The pinned installer for this machine (name and SHA-256 as GitHub published them for the release), or null with the reason.</summary>
    internal static (string Asset, string Sha256, string Kind)? AssetFor(out string why)
    {
        why = string.Empty;
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            why = "Audiveris is offered here for x64 machines only (Windows and Ubuntu).";
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return ($"Audiveris-{Version}-windowsConsole-x86_64.msi", "5f1b4e96a12c53c7da426814b76e599363c4181e291855996e0a6878dda95f71", "msi");
        }

        if (OperatingSystem.IsLinux())
        {
            string release = File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : string.Empty;
            if (release.Contains("VERSION_ID=\"24.04\"", StringComparison.Ordinal)) return ($"Audiveris-{Version}-ubuntu24.04-x86_64.deb", "f20113aaa33b3149ec8d6a09b2a7963360e65fafd92d69389987a85bbc3ec7a3", "deb");
            if (release.Contains("VERSION_ID=\"22.04\"", StringComparison.Ordinal)) return ($"Audiveris-{Version}-ubuntu22.04-x86_64.deb", "ae714594f40e54b1a4951fc3f914f08ae38fe5d07b7f2283b1a904fdb6e0a318", "deb");
            why = "Audiveris is offered here for Ubuntu 22.04 and 24.04 (x64).";
            return null;
        }

        why = "Audiveris is offered here for Windows and Ubuntu only.";
        return null;
    }

    internal static async Task<int> RunAsync(TextWriter output, string home, Action<int, string>? progress, CancellationToken token)
    {
        (string Asset, string Sha256, string Kind)? asset = AssetFor(out string why);
        if (asset is null)
        {
            output.WriteLine(why);
            return 2;
        }

        string final = Path.Combine(Path.GetFullPath(home), "audiveris");
        if (AudiverisLocator.FindExe(home) is { } existing)
        {
            output.WriteLine($"Audiveris is already installed: {existing}");
            progress?.Invoke(100, "Audiveris is already installed");
            return 0;
        }

        string stage = Path.Combine(Path.GetFullPath(home), "av-installing-" + Environment.ProcessId);   // short: a long path makes the extraction of the MSI fail
        string file = Path.Combine(Path.GetTempPath(), "ample-" + asset.Value.Asset);
        try
        {
            string url = $"https://github.com/Audiveris/audiveris/releases/download/{Version}/{asset.Value.Asset}";
            progress?.Invoke(2, $"Downloading Audiveris {Version} from github.com");
            using (HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ample-pack-install");
                using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    output.WriteLine($"The download failed ({(int)response.StatusCode}): {url}");
                    return 1;
                }

                long total = response.Content.Headers.ContentLength ?? 0;
                if (total > MaxBytes)
                {
                    output.WriteLine("The download is larger than expected; it is not used.");
                    return 1;
                }

                await using Stream source = await response.Content.ReadAsStreamAsync(token);
                await using FileStream target = File.Create(file);
                byte[] buffer = new byte[256 * 1024];
                long done = 0;
                int last = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    done += read;
                    if (done > MaxBytes)
                    {
                        output.WriteLine("The download is larger than expected; it is not used.");
                        return 1;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    int percent = total > 0 ? (int)(2 + 78L * done / total) : -1;
                    if (percent != last) { last = percent; progress?.Invoke(percent, $"Downloading Audiveris ({done / 1024 / 1024} MB)"); }
                }
            }

            progress?.Invoke(81, "Checking the download");
            string actual;
            await using (FileStream check = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(check, token)).ToLowerInvariant();
            if (!string.Equals(actual, asset.Value.Sha256, StringComparison.Ordinal))
            {
                output.WriteLine("The download does not match the checksum that is built into AMPLE; it is not used.");
                return 1;
            }

            progress?.Invoke(84, "Unpacking Audiveris for you (no administrator rights needed)");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            ProcessStartInfo start = asset.Value.Kind == "deb"
                ? new ProcessStartInfo("dpkg-deb") { ArgumentList = { "-x", file, stage } }
                : new ProcessStartInfo("msiexec.exe") { ArgumentList = { "/a", file, "/qn", "TARGETDIR=" + stage } };
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardError = true;
            start.RedirectStandardOutput = true;
            try
            {
                using Process process = Process.Start(start) ?? throw new InvalidOperationException("The unpacking program did not start.");
                _ = process.StandardOutput.ReadToEndAsync(token);
                Task<string> errors = process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0)
                {
                    output.WriteLine($"Unpacking failed (exit code {process.ExitCode}): {(await errors).Trim()}");
                    return 1;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                output.WriteLine(asset.Value.Kind == "deb" ? "dpkg-deb was not found: it comes with Ubuntu and Debian (the package dpkg)." : "msiexec was not found.");
                return 1;
            }

            string exeName = OperatingSystem.IsWindows() ? "Audiveris.exe" : "Audiveris";
            string? found = Directory.EnumerateFiles(stage, exeName, SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
            if (found is null)
            {
                output.WriteLine("The unpacked Audiveris has no program to start; it is not used.");
                return 1;
            }

            progress?.Invoke(95, "Finishing");
            if (Directory.Exists(final)) Directory.Delete(final, true);   // an earlier, damaged attempt of this very pack
            Directory.Move(stage, final);
            string relative = Path.GetRelativePath(stage, found);
            await File.WriteAllTextAsync(Path.Combine(final, "installed.json"), JsonSerializer.Serialize(new { version = Version, exe = relative, installed = DateTimeOffset.UtcNow }), token);
            progress?.Invoke(100, "Audiveris is ready");
            output.WriteLine($"Audiveris {Version} is installed for you in {final}. A PDF or picture of sheet music can now be imported (ample_import_score).");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            output.WriteLine("Audiveris could not be installed: " + ex.Message);
            return 1;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { /* the temp file goes with the next clean-up */ }
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (IOException) { /* left behind: the next attempt removes it */ }
        }
    }
}
