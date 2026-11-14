using System;
using System.Collections.Generic;

namespace Ample.Setup
{
    /// <summary>The new text of the hub settings file (hub.json) when the wizard changes the sign-in option and nothing else.</summary>
    internal static class HubSettingsMerge
    {
        /// <summary>
        /// Every field of the existing file is kept (also the ones this program does not know), the fields that are missing get their defaults and <c>mainLogin</c> is set.
        /// An existing text that is not a JSON object throws <see cref="InvalidOperationException"/>: the file is then left alone.
        /// </summary>
        /// <param name="existingText">The text of the file; null or blank when there is none.</param>
        /// <param name="login">The new value of <c>mainLogin</c>.</param>
        /// <param name="parse">Reads JSON text into an object (null when it is not an object); it may throw on invalid JSON.</param>
        /// <param name="serialize">Writes the settings as JSON text.</param>
        internal static string Merge(string existingText, bool login, Func<string, Dictionary<string, object>> parse, Func<Dictionary<string, object>, string> serialize)
        {
            Dictionary<string, object> settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (existingText != null && existingText.Trim().Length > 0)
            {
                Dictionary<string, object> parsed;
                try { parsed = parse(existingText); }
                catch (Exception ex) { throw new InvalidOperationException("hub.json is not valid JSON, so it was left alone (" + ex.Message + ")"); }
                if (parsed == null) throw new InvalidOperationException("hub.json is not a JSON object, so it was left alone");
                foreach (KeyValuePair<string, object> pair in parsed) settings[pair.Key] = pair.Value;
            }

            if (!settings.ContainsKey("mainPort")) settings["mainPort"] = 0;
            if (!settings.ContainsKey("acceptOthers")) settings["acceptOthers"] = true;
            if (!settings.ContainsKey("startAtLogin")) settings["startAtLogin"] = true;
            settings["mainLogin"] = login;
            return serialize(settings) + "\n";
        }
    }
}
