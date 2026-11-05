using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ample.Storage.Security;

namespace Ample.Cli;

/// <summary>
/// <c>ample packs install koda-browser</c>: the optional browser of the assistant. The pack is the Playwright driver (a Node program for THIS platform and
/// the Playwright package folder), built by the release and listed with its SHA-256 in the release manifest. The manifest is checked against the release keys
/// that are built into the program before anything else is downloaded; the zip must match the manifest and must hold exactly the expected shape.
/// Playwright then downloads Chromium itself (its own integrity checks apply to that part).
/// </summary>
internal static class KodaBrowserInstall
{
    internal const string AssetPrefix = "ample-koda-browser-";
    internal const long MaxPackBytes = 400L * 1024 * 1024;
    internal const long MaxUnpackedBytes = 1024L * 1024 * 1024;

    /// <summary>The platform names of the packs that are built; null where there is none.</summary>
    internal static string? CurrentPlatform() =>
        RuntimeInformation.OSArchitecture != Architecture.X64 ? null
        : OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsLinux() ? "linux-x64"
        : null;

    /// <summary>The folder name Playwright uses below <c>.playwright/node</c> for a platform.</summary>
    internal static string NodeFolderOf(string platform) => platform == "win-x64" ? "win32_x64" : platform;

    internal static string NodeFileOf(string platform) => platform == "win-x64" ? "node.exe" : "node";

    internal static string AssetNameOf(string platform) => $"{AssetPrefix}{platform}.zip";

    /// <summary>
    /// Installs the pack. <paramref name="baseUrl"/> is the folder that holds the manifest and the pack (an https address of a release, or, for tests, a file or
    /// loopback address). <paramref name="verifyManifest"/> returns the signer's key id or throws; the default checks the keys built into the program.
    /// </summary>
    internal static async Task<int> RunAsync(TextWriter output, string home, string version, string baseUrl, bool skipChromium, Func<string, string>? verifyManifest, CancellationToken token, Action<int, string>? progress = null)
    {
        verifyManifest ??= ReleaseTrust.VerifyFile;
        if (Environment.IsPrivilegedProcess)
        {
            output.WriteLine("Do not run this as an administrator or as root: the browser belongs to one person. Run it as yourself.");
            return 2;
        }

        string? platform = CurrentPlatform();
        if (platform is null)
        {
            output.WriteLine("There is no koda-browser pack for this platform yet (Windows x64 and Linux x64 are built).");
            return 2;
        }

        string temp = Path.Combine(Path.GetTempPath(), "ample-koda-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string final = Path.Combine(home, "koda-browser");
        string stage = final + ".installing-" + Environment.ProcessId;
        try
        {
            // 1. the manifest, checked against the keys built into the program BEFORE anything else is downloaded
            string manifest = Path.Combine(temp, $"ample-{version}.manifest.json");
            progress?.Invoke(5, $"Connecting to {new Uri(baseUrl).Host} for the release manifest");
            if (!await TryFetchAsync(output, $"{baseUrl.TrimEnd('/')}/ample-{version}.manifest.json", manifest, 1024 * 1024, token)
                || !await TryFetchAsync(output, $"{baseUrl.TrimEnd('/')}/ample-{version}.manifest.json.sig", manifest + ".sig", 64 * 1024, token))
            {
                return 1;
            }

            progress?.Invoke(10, "Checking the signature of the release manifest");
            try
            {
                string signer = verifyManifest(manifest);
                output.WriteLine($"The release manifest is signed by key {signer}.");
            }
            catch (Ample.Core.Abstractions.AmpleException ex)
            {
                output.WriteLine("The release manifest is not signed by an AMPLE release key: " + ex.Message + " Nothing was installed.");
                return 1;
            }

            // 2. the pack, as the manifest lists it
            string name = AssetNameOf(platform);
            (string? sha256, long size) = FindArtefact(manifest, name);
            if (sha256 is null)
            {
                output.WriteLine($"{name} is not listed in the release manifest of {version}. Nothing was installed.");
                return 1;
            }

            if (size <= 0 || size > MaxPackBytes)
            {
                output.WriteLine($"{name} has an unreasonable size in the manifest ({size} bytes). Nothing was installed.");
                return 1;
            }

            string zip = Path.Combine(temp, name);
            output.WriteLine($"Fetching {name} ({size / 1024 / 1024} MB)...");
            progress?.Invoke(15, $"Downloading the browser driver ({size / 1024 / 1024} MB)");
            if (!await TryFetchAsync(output, $"{baseUrl.TrimEnd('/')}/{name}", zip, MaxPackBytes, token)) return 1;
            progress?.Invoke(35, "Checking the SHA-256 of the browser driver");
            string actual = Convert.ToHexStringLower(await HashAsync(zip, token));
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("The download does not match the manifest (SHA-256). Nothing was installed.");
                return 1;
            }

            // 3. unpack into a staging folder next to the final one, with every entry checked first
            progress?.Invoke(40, "Unpacking the browser driver");
            Directory.CreateDirectory(home);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            string? problem = Unpack(zip, stage, platform);
            if (problem is not null)
            {
                output.WriteLine(problem + " Nothing was installed.");
                return 1;
            }

            // 4. switch: the old pack (if any) is moved aside first and removed only when the new one is in place
            string? old = null;
            if (Directory.Exists(final))
            {
                old = final + ".old-" + Environment.ProcessId;
                Directory.Move(final, old);
            }

            Directory.Move(stage, final);
            if (old is not null) Directory.Delete(old, recursive: true);
            output.WriteLine($"Installed the driver: {final}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            output.WriteLine("The koda-browser pack could not be installed: " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            }
            catch (IOException)
            {
                // a locked temporary file must not hide the result
            }
        }

        if (!skipChromium) progress?.Invoke(50, "Installing Chromium (this is the long step, a few minutes)");
        int result = skipChromium ? 0 : await InstallChromiumAsync(output, final, platform, token);
        if (result == 0) progress?.Invoke(100, "koda-browser is installed");
        return result;
    }

    /// <summary>Reads the sha256 and size of one artefact from a manifest; (null, 0) when it is not there. The order of the fields does not matter.</summary>
    internal static (string? Sha256, long Size) FindArtefact(string manifestPath, string name)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("artefacts", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return (null, 0);
            foreach (JsonElement a in list.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out JsonElement n) && n.GetString() == name
                    && a.TryGetProperty("sha256", out JsonElement h) && h.GetString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
                    && a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long size))
                {
                    return (hash, size);
                }
            }
        }
        catch (JsonException)
        {
            return (null, 0);
        }

        return (null, 0);
    }

    /// <summary>
    /// Unpacks the pack zip into <paramref name="stage"/> after checking every entry: nothing outside <c>.playwright/</c>, no absolute path, no drive, no '..', a bounded
    /// size, and the expected files present. Returns the reason when the zip is refused (and leaves nothing behind), else null.
    /// </summary>
    internal static string? Unpack(string zipPath, string stage, string platform)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = entry.FullName;
            string[] parts = path.Split('/', '\\');
            if (path.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':') || parts.Contains("..") || path.Contains('\0'))
            {
                return $"The zip holds an unsafe path ('{path}').";
            }

            if (parts[0] != ".playwright") return $"The zip holds a file outside .playwright ('{path}').";
            total += entry.Length;
            if (total > MaxUnpackedBytes) return "The zip would unpack to more than 1 GB.";
        }

        string node = $".playwright/node/{NodeFolderOf(platform)}/{NodeFileOf(platform)}";
        if (archive.GetEntry(node) is null || archive.GetEntry(".playwright/package/package.json") is null)
        {
            return "The zip does not hold the expected driver (the Node program for this platform and the Playwright package).";
        }

        string stageFull = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            Directory.CreateDirectory(stage);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                if (!target.StartsWith(stageFull, StringComparison.OrdinalIgnoreCase)) return $"The zip holds a path outside its folder ('{entry.FullName}').";
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(stage, node.Replace('/', Path.DirectorySeparatorChar)), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return null;
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static async Task<bool> TryFetchAsync(TextWriter output, string url, string destination, long maxBytes, CancellationToken token)
    {
        try
        {
            Uri uri = new(url);
            if (uri.IsFile)
            {
                FileInfo source = new(uri.LocalPath);
                if (!source.Exists || source.Length > maxBytes)
                {
                    output.WriteLine($"{Path.GetFileName(uri.LocalPath)} was not found or is too large.");
                    return false;
                }

                File.Copy(source.FullName, destination, overwrite: true);
                return true;
            }

            if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            {
                output.WriteLine($"Only https addresses are fetched: {uri.GetLeftPart(UriPartial.Path)}");
                return false;
            }

            using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(20) };
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                output.WriteLine($"Download failed ({(int)response.StatusCode}) for {Path.GetFileName(uri.LocalPath)}.");
                return false;
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                output.WriteLine($"{Path.GetFileName(uri.LocalPath)} is larger than allowed.");
                return false;
            }

            long total = 0;
            await using Stream source2 = await response.Content.ReadAsStreamAsync(token);
            await using FileStream file = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await source2.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    output.WriteLine($"{Path.GetFileName(uri.LocalPath)} is larger than allowed.");
                    return false;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), token);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or IOException or TaskCanceledException)
        {
            output.WriteLine($"Download failed for {Path.GetFileName(url)}: {ex.Message}");
            return false;
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken token)
    {
        await using FileStream stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, token);
    }

    private static async Task<int> InstallChromiumAsync(TextWriter output, string pack, string platform, CancellationToken token)
    {
        string node = Path.Combine(pack, ".playwright", "node", NodeFolderOf(platform), NodeFileOf(platform));
        string cli = Path.Combine(pack, ".playwright", "package", "cli.js");
        if (!File.Exists(cli))
        {
            output.WriteLine("The pack has no cli.js to install Chromium with.");
            return 1;
        }

        output.WriteLine("Letting Playwright download Chromium (it checks its own download)...");
        ProcessStartInfo start = new(node) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("install");
        start.ArgumentList.Add("chromium");
        using Process? process = Process.Start(start);
        if (process is null)
        {
            output.WriteLine("Chromium could not be installed: the Node program did not start.");
            return 1;
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(token);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            output.WriteLine("Chromium did not finish downloading in 20 minutes. Run the command again later.");
            return 1;
        }

        string tail = string.Join('\n', (await standardOutput + await standardError).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(3));
        if (process.ExitCode != 0)
        {
            output.WriteLine("Chromium could not be installed (" + tail + "). The driver is in place; run the command again when you are online.");
            return 1;
        }

        output.WriteLine("Chromium is installed.");
        return 0;
    }
}
