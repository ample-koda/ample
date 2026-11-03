using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>
/// The written form of the controllers of a midi-clip (shared by the lint while writing and by the clip when it renders): the named controllers with their units,
/// which values each takes, and the entries of <c>cc</c>.
/// </summary>
public static partial class ClipControlSyntax
{
    /// <summary>The named controllers: parameter name, controller number, unit (percent, db, pan, onoff).</summary>
    public static IReadOnlyList<(string Name, int Number, string Unit)> Named { get; } =
    [
        ("modwheel", 1, "percent"), ("volume", 7, "db"), ("expression", 11, "db"), ("pan", 10, "pan"), ("sustain", 64, "onoff"), ("reverb_send", 91, "percent"), ("chorus_send", 93, "percent"),
    ];

    /// <summary>The controllers every instrument follows; the rest (and cc) only the gm block and midi-render play.</summary>
    public static IReadOnlySet<string> EveryInstrument { get; } = new HashSet<string>(StringComparer.Ordinal) { "volume", "expression", "pan" };

    /// <summary>The blocks that play every controller of a clip.</summary>
    public static IReadOnlySet<string> PlaysAll { get; } = new HashSet<string>(StringComparer.Ordinal) { "gm", "midi-render" };

    /// <summary>A value without its dB unit (-6dB, auto(1:0dB, 2:-12dB)).</summary>
    public static string Plain(string text) => text.Trim().Trim('"').Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a value is a curve (auto, lfo, env, a fade or a control binding: a word with its arguments in brackets).</summary>
    public static bool IsCurve(string text) => CurveForm().IsMatch(Plain(text));

    /// <summary>Whether a value can be played: a number in the range of its unit, on/off for sustain, or a curve.</summary>
    public static bool Valid(string text, string unit)
    {
        text = Plain(text);
        if (unit == "onoff" && text is "on" or "off") return true;
        if (IsCurve(text)) return true;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)) return false;
        return unit switch
        {
            "percent" or "onoff" => value is >= 0 and <= 100,
            "db" => value is >= -96 and <= 0,
            "pan" => value is >= -100 and <= 100,
            _ => value is >= 0 and <= 127,
        };
    }

    /// <summary>What a unit takes, for a message.</summary>
    public static string UnitText(string unit) => unit switch { "db" => "dB from 0 down to -96", "pan" => "-100 (left) to 100 (right)", "onoff" => "on or off", "raw" => "0 to 127", _ => "0 to 100 percent" };

    /// <summary>The entries of a <c>cc</c> parameter: controller number and value text, in order. Null when the text is not only <c>number=value</c> entries with numbers and values 0..127.</summary>
    public static IReadOnlyList<(int Number, string Value)>? ParseCc(string text)
    {
        text = text.Trim().Trim('"');
        List<(int Number, string Value)> entries = [];
        foreach (Match m in CcEntry().Matches(text))
        {
            int number = int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number > 127 || !Valid(m.Groups[2].Value, "raw")) return null;
            entries.Add((number, m.Groups[2].Value));
        }

        return CcEntry().Replace(text, string.Empty).Trim().Length == 0 ? entries : null;
    }

    [GeneratedRegex(@"(\d{1,3})\s*=\s*([a-z]+\([^)]*\)|-?\d+)")]
    private static partial Regex CcEntry();

    [GeneratedRegex(@"^[a-z][a-z-]*\(.*\)$")]
    private static partial Regex CurveForm();
}
