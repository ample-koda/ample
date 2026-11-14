using System.Text.Json;

namespace Ample.Hub.Core;

/// <summary>The settings of the tray hub (<c>hub.json</c> in the AMPLE home).</summary>
/// <param name="MainPort">The port of the main instance (0: a free one each time).</param>
/// <param name="MainLogin">Whether the main instance accepts the user name and password set by <c>ample auth set-password</c>.</param>
/// <param name="AcceptOthers">Whether other served instances are shown and managed (an instance started with <c>--no-hub</c> stays out in any case).</param>
/// <param name="StartAtLogin">Whether the hub starts when the person signs in to Windows.</param>
public sealed record HubSettings(int MainPort = 0, bool MainLogin = false, bool AcceptOthers = true, bool StartAtLogin = true)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Gets the path of the settings file.</summary>
    public static string PathIn(string home) => Path.Combine(home, "hub.json");

    /// <summary>Reads the settings; a missing or damaged file gives the defaults.</summary>
    public static HubSettings Load(string home)
    {
        try
        {
            string path = PathIn(home);
            if (!File.Exists(path)) return new HubSettings();
            HubSettings? read = JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(path), Json);
            return read is null || read.MainPort is < 0 or > 65535 ? new HubSettings() : read;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HubSettings();
        }
    }

    /// <summary>Writes the settings under the hub.json mutex, keeping fields this program does not know (see <see cref="HubSettingsStore"/>).</summary>
    public void Save(string home)
    {
        HubSettingsStore.Update(home, _ => this);
    }
}
