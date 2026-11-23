namespace Ample.Player;

/// <summary>
/// What the player remembers, in %LOCALAPPDATA%\AMPLE\player.ini (one "key=value" per line): the last output device and the volume for
/// everything, and the loop (on or off, and the region) per song.
/// </summary>
internal sealed class PlayerSettings
{
    private readonly string _path = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AMPLE", "player.ini");
    private readonly System.Collections.Generic.Dictionary<string, string> _values = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the file when there is one.</summary>
    public PlayerSettings()
    {
        try
        {
            if (!System.IO.File.Exists(_path)) return;
            foreach (string line in System.IO.File.ReadAllLines(_path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) _values[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            _values.Clear();
        }
    }

    /// <summary>A stored text, or null.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out string? value) ? value : null;

    /// <summary>A stored number (culture independent), or the fallback.</summary>
    public double GetNumber(string key, double fallback) =>
        double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;

    /// <summary>Stores a text (null removes the key) and writes the file.</summary>
    public void Set(string key, string? value)
    {
        if (value is null) _values.Remove(key); else _values[key] = value.Replace("\r", " ").Replace("\n", " ");
        Save();
    }

    /// <summary>Stores a number (culture independent) and writes the file.</summary>
    public void SetNumber(string key, double value) =>
        Set(key, value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            System.IO.File.WriteAllLines(_path, System.Linq.Enumerable.Select(_values, p => p.Key + "=" + p.Value));
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("Echo: settings not saved: " + ex.Message);
        }
    }
}
