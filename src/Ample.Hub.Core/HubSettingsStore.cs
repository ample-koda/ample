using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ample.Hub.Core;

/// <summary>
/// Writes <c>hub.json</c> the way every writer of that file has to: under the named mutex the setup (Tune-up) uses too, as a read-modify-write
/// that keeps fields this program does not know, and never over a file that is not valid JSON.
/// </summary>
public static class HubSettingsStore
{
    /// <summary>The name of the mutex the setup program takes while it writes the file.</summary>
    public const string MutexName = @"Local\AmpleHubSettings";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    /// <summary>Changes the settings: reads the file under the lock, applies the change, writes the result back.</summary>
    /// <param name="home">The AMPLE home.</param>
    /// <param name="change">The change, applied to what is in the file now.</param>
    /// <param name="mutexName">The mutex (the default is the one the setup uses).</param>
    /// <param name="wait">How long to wait for the mutex; after that the write goes ahead anyway.</param>
    /// <exception cref="InvalidDataException">The file is not a JSON object; it was left alone.</exception>
    public static void Update(string home, Func<HubSettings, HubSettings> change, string mutexName = MutexName, TimeSpan? wait = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        Directory.CreateDirectory(home);
        string path = HubSettings.PathIn(home);
        using Mutex gate = new(false, mutexName);
        bool owned;
        try { owned = gate.WaitOne(wait ?? DefaultWait); }
        catch (AbandonedMutexException) { owned = true; }
        try
        {
            JsonObject existing = ReadObject(path);
            HubSettings current = new();
            if (existing.Count > 0)
            {
                try { current = existing.Deserialize<HubSettings>(Json) ?? new HubSettings(); }
                catch (JsonException ex) { throw new InvalidDataException("hub.json has a value of the wrong kind, so it was left alone (" + ex.Message + ")", ex); }
                if (current.MainPort is < 0 or > 65535) current = new HubSettings();
            }

            HubSettings next = change(current);
            JsonObject fresh = JsonSerializer.SerializeToNode(next, Json)!.AsObject();
            foreach (KeyValuePair<string, JsonNode?> field in fresh.ToList())
            {
                string? key = existing.Select(e => e.Key).FirstOrDefault(k => string.Equals(k, field.Key, StringComparison.OrdinalIgnoreCase));
                if (key is not null) existing.Remove(key);
                existing[field.Key] = field.Value?.DeepClone();
            }

            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, existing.ToJsonString(Indented));
                File.Move(temporary, path, true);
            }
            catch
            {
                try { File.Delete(temporary); }
                catch (IOException) { /* the next write uses a new name */ }
                throw;
            }
        }
        finally
        {
            if (owned) gate.ReleaseMutex();
        }
    }

    private static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path)) return [];
        string text = File.ReadAllText(path);
        if (text.Trim().Length == 0) return [];
        try { return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("hub.json is not a JSON object, so it was left alone."); }
        catch (JsonException ex) { throw new InvalidDataException("hub.json is not valid JSON, so it was left alone (" + ex.Message + ")", ex); }
    }
}
