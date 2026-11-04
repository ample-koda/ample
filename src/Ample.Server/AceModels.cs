using System.Text.Json;
using Ample.Storage.Projects;
using Ample.Plugins;

namespace Ample.Server;

/// <summary>
/// Which ACE-Step model files exist where: on the local ACE server (what its <c>/props</c> lists, with sizes when the models folder is known)
/// and on the Faer server (its installed inventory). A file counts as the same on both when the name and the size match.
/// </summary>
internal static class AceModels
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>Lists the model files of both places and which are on which, for the settings editor and the build checks.</summary>
    /// <param name="session">The open project.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    internal static async Task<object> ListAsync(ProjectSession session, CancellationToken cancellationToken)
    {
        string stored = session.PluginSettingsJson("acestep");
        JsonDocument? settings = null;
        string? settingsNote = null;
        try
        {
            settings = JsonDocument.Parse(string.IsNullOrWhiteSpace(stored) ? "{}" : stored);
        }
        catch (JsonException)
        {
            settingsNote = "The stored ACE-Step settings could not be read; the defaults are used.";
        }

        using JsonDocument? owned = settings;
        JsonElement root = settings?.RootElement ?? default;
        string endpoint = Text(root, "endpoint", "http://127.0.0.1:8085");
        bool allowRemote = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("allowRemote", out JsonElement a) && a.ValueKind == JsonValueKind.True;
        string folder = Text(root, "modelsFolder", string.Empty);

        Dictionary<string, long?> local = new(StringComparer.Ordinal);
        bool reachable = false;
        string? localNote = null;
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && (uri.IsLoopback || allowRemote))
        {
            try
            {
                using JsonDocument props = JsonDocument.Parse(await Http.GetStringAsync(new Uri(uri, "/props"), cancellationToken).ConfigureAwait(false));
                reachable = true;
                if (props.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty kind in models.EnumerateObject().Where(k => k.Value.ValueKind == JsonValueKind.Array))
                    {
                        foreach (string name in kind.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!))
                        {
                            string path = folder.Length > 0 ? Path.Combine(folder, name) : string.Empty;
                            local[name] = path.Length > 0 && File.Exists(path) ? new FileInfo(path).Length : null;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                reachable = false;
                localNote = "The local ACE-Step server did not answer.";
            }
        }
        else
        {
            localNote = "The endpoint is not on this machine (set allowRemote to use it).";
        }

        Dictionary<string, long?> faer = new(StringComparer.Ordinal);
        bool configured = false, connected = false;
        string? faerNote = null;
        FaerProfile? profile = null;
        try
        {
            profile = FaerProfile.LoadLocal();
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            faerNote = "The Faer profile could not be read.";
        }

        if (profile is not null)
        {
            configured = true;
            try
            {
                using FaerClient client = profile.CreateClient();
                JsonElement status = await client.StatusAsync(cancellationToken).ConfigureAwait(false);
                connected = true;
                CollectGguf(status, faer);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Security.Cryptography.CryptographicException or TaskCanceledException or JsonException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                connected = false;
                faerNote = "Faer did not answer.";
            }
        }

        FaerRoutes routes = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(session.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), cancellationToken).ConfigureAwait(false)).Settings.Faer;
        object[] merged =
        [
            .. local.Keys.Union(faer.Keys, StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).Select(file =>
            {
                bool inLocal = local.TryGetValue(file, out long? localBytes), inFaer = faer.TryGetValue(file, out long? faerBytes);
                bool? same = inLocal && inFaer && localBytes is not null && faerBytes is not null ? localBytes == faerBytes : null;
                return (object)new { file, role = RoleOf(file), local = reachable ? inLocal : (bool?)null, faer = connected ? inFaer : (bool?)null, localBytes, faerBytes, same };
            }),
        ];
        // known: the inventory had at least one recognised model, so "not on this server" can be trusted; otherwise the lists are only a hint.
        return new { routes = new { song = routes.Song, voice = routes.Voice }, local = new { endpoint, reachable, known = reachable && local.Count > 0, note = localNote ?? settingsNote, sizesKnown = folder.Length > 0 }, faer = new { configured, connected, known = connected && faer.Count > 0, note = faerNote }, models = merged };
    }

    private static string Text(JsonElement root, string name, string fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : fallback;

    private static string RoleOf(string file) =>
        file.Contains("5Hz-lm", StringComparison.OrdinalIgnoreCase) ? "lm"
        : file.StartsWith("acestep-v15-", StringComparison.OrdinalIgnoreCase) ? "dit"
        : file.StartsWith("vae", StringComparison.OrdinalIgnoreCase) ? "vae"
        : file.Contains("Embedding", StringComparison.OrdinalIgnoreCase) ? "embedding" : "other";

    // The shape of Faer's status is not fixed in one place here, so every .gguf name in it counts, with a size when the same object has one.
    private static void CollectGguf(JsonElement node, Dictionary<string, long?> into)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                long? size = null;
                foreach (JsonProperty p in node.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && (p.Name is "size" or "bytes" or "sizeBytes") && p.Value.TryGetInt64(out long n))
                    {
                        size = n;
                    }
                }

                foreach (JsonProperty p in node.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } text && text.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                    {
                        into[Path.GetFileName(text)] = size;
                    }
                    else
                    {
                        CollectGguf(p.Value, into);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in node.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text && text.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                    {
                        into.TryAdd(Path.GetFileName(text), null);
                    }
                    else
                    {
                        CollectGguf(item, into);
                    }
                }

                break;
        }
    }
}
