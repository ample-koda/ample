using System.Security.Cryptography;

namespace Ample.Cli;

/// <summary>
/// <c>ample packs install soundfont</c>: fetches the pinned General MIDI SoundFont (MuseScore General, MIT) into the user-level
/// audio-packs folder and verifies its SHA-256. No Node is needed. An existing file is verified and kept; a file that does not match
/// the pinned digest is never overwritten.
/// </summary>
internal static class PackInstall
{
    internal const string BaseUrl = "https://ftp.osuosl.org/pub/musescore/soundfont/MuseScore_General/";
    /// <summary>The AMPLE mirror of the same three files (get.ample-koda.dev); <c>--mirror</c> uses it. The SHA-256 above is still checked.</summary>
    internal const string MirrorUrl = "https://get.ample-koda.dev/mirror/musescore-general/";
    internal const string Sha256 = "ee51d2c4b1525e70f19a45909c4fd7a2e26d91d115fa89dbf5a6bc413d8b9bf3";
    internal const long MaxBytes = 512L * 1024 * 1024;
    private static readonly string[] Assets = ["MuseScore_General.sf2", "MuseScore_General_License.md", "MuseScore_General_Sample_Sources.csv"];

    internal static string TargetFolder(string home) => Path.Combine(home, "audio-packs", "licensed", "musescore-general");

    // progress (optional): told what is happening, as (percent of the font, or -1 when it is only a step; text). Used by --progress so an installer can show it.
    internal static async Task<int> SoundFontAsync(TextWriter output, string home, string? baseUrl, CancellationToken token, Action<int, string>? progress = null)
    {
        string folder = TargetFolder(home);
        Directory.CreateDirectory(folder);
        using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
        foreach (string name in Assets)
        {
            bool font = name.EndsWith(".sf2", StringComparison.Ordinal);
            string target = Path.Combine(folder, name);
            if (File.Exists(target))
            {
                if (font && !string.Equals(await HashAsync(target, token), Sha256, StringComparison.Ordinal))
                {
                    output.WriteLine($"{name} exists but does not match the pinned digest; leaving it untouched. Move it away and run again.");
                    return 1;
                }

                output.WriteLine($"{(font ? "verified" : "kept")}  {target}");
                continue;
            }

            string partial = target + ".part";
            try
            {
                string address = (baseUrl ?? BaseUrl) + name;
                progress?.Invoke(-1, $"Connecting to {new Uri(address).Host}");
                using HttpResponseMessage response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    output.WriteLine($"download failed ({(int)response.StatusCode}) for {name}");
                    return 1;
                }

                long cap = font ? MaxBytes : 16 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > cap)
                {
                    output.WriteLine($"{name} exceeds its size bound");
                    return 1;
                }

                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                long? expected = response.Content.Headers.ContentLength;
                int lastPercent = -1;
                await using (Stream source = await response.Content.ReadAsStreamAsync(token))
                await using (FileStream destination = new(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        total += read;
                        if (total > cap)
                        {
                            output.WriteLine($"{name} exceeds its size bound");
                            return 1;
                        }

                        hash.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), token);
                        if (progress is not null && font && expected is > 0)
                        {
                            int percent = (int)(total * 100 / expected.Value);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                progress(percent, $"Downloading {name}: {total / 1024 / 1024} of {expected.Value / 1024 / 1024} MB");
                            }
                        }
                    }
                }

                if (font) progress?.Invoke(-1, $"Checking the SHA-256 of {name}");
                if (font && !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), Sha256, StringComparison.Ordinal))
                {
                    output.WriteLine($"{name} does not match the pinned digest; not installed");
                    return 1;
                }

                File.Move(partial, target);
                output.WriteLine($"installed  {target} ({total / 1024 / 1024} MB)");
                if (font) progress?.Invoke(100, $"Installed {name}");
            }
            catch (HttpRequestException exception)
            {
                output.WriteLine($"download failed for {name}: {exception.Message}");
                return 1;
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        return 0;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
