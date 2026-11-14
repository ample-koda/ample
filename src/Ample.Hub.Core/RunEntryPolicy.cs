using System.Security;

namespace Ample.Hub.Core;

/// <summary>Reads and writes the "start at sign-in" entry (the HKCU Run value of the hub).</summary>
public interface IRunEntryStore
{
    /// <summary>Reads the value; null when there is none (or no Run key).</summary>
    /// <returns>The value or null.</returns>
    string? Read();

    /// <summary>Writes the value.</summary>
    /// <param name="value">The command line.</param>
    void Write(string value);

    /// <summary>Removes the value (no error when it is not there).</summary>
    void Delete();
}

/// <summary>The decisions about the Run entry, apart from the registry.</summary>
public static class RunEntryPolicy
{
    /// <summary>Gets the Run value for a program path.</summary>
    /// <param name="exePath">The path of the hub.</param>
    /// <returns>The quoted path.</returns>
    public static string ValueFor(string exePath) => "\"" + exePath + "\"";

    /// <summary>Gets the preference when there is no hub.json yet: the installer's choice (an entry is there or not).</summary>
    /// <param name="store">The Run entry.</param>
    /// <param name="fallback">The answer when the entry cannot be read.</param>
    /// <returns>True when the hub should start at sign-in.</returns>
    public static bool InitialPreference(IRunEntryStore store, bool fallback)
    {
        ArgumentNullException.ThrowIfNull(store);
        try { return store.Read() is not null; }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { return fallback; }
    }

    /// <summary>Makes the entry match the preference, and writes nothing when it already does.</summary>
    /// <param name="store">The Run entry.</param>
    /// <param name="on">Whether the hub starts at sign-in.</param>
    /// <param name="exePath">The path of the running hub.</param>
    /// <returns>Null when done, else why the entry was not changed.</returns>
    public static string? Apply(IRunEntryStore store, bool on, string? exePath)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            if (!on)
            {
                if (store.Read() is not null) store.Delete();
                return null;
            }

            if (string.IsNullOrEmpty(exePath)) return "The path of the hub is not known, so the start at sign-in was not set.";
            string wanted = ValueFor(exePath);
            if (string.Equals(store.Read(), wanted, StringComparison.OrdinalIgnoreCase)) return null;
            store.Write(wanted);
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return "The start at sign-in could not be changed: " + ex.Message;
        }
    }
}
