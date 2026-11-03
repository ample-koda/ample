namespace Ample.Language;

/// <summary>
/// The types of a control (a number over time that comes out of a sound or a curve and steers a parameter).
/// <c>control</c> is the general one: a level from 0 to 1 (written 0 to 1000 per mille) that is scaled to the parameter it steers.
/// <c>control&lt;Hz&gt;</c>, <c>control&lt;dB&gt;</c> and so on know their unit and only steer a parameter of that unit.
/// </summary>
public static class ControlTypes
{
    /// <summary>The general control.</summary>
    public const string General = "control";

    /// <summary>The units a typed control (and a number in a control expression) can have.</summary>
    public static IReadOnlySet<string> Units { get; } = new HashSet<string>(StringComparer.Ordinal) { "Hz", "dB", "ms", "s", "%", "st", "cents", "oct", "deg", "bars", "dBFS" };

    /// <summary>Gets whether <paramref name="type"/> is a control type (general or typed).</summary>
    public static bool Is(string type) => type == General || (type.StartsWith("control<", StringComparison.Ordinal) && type.EndsWith('>') && type.Length > 9);

    /// <summary>The unit of a typed control type, or null for the general one.</summary>
    public static string? UnitOf(string type) => type.StartsWith("control<", StringComparison.Ordinal) && type.EndsWith('>') ? type[8..^1] : null;

    /// <summary>The control type with this unit (the general one when there is no unit).</summary>
    public static string Typed(string? unit) => string.IsNullOrEmpty(unit) ? General : $"control<{unit}>";

    /// <summary>
    /// Whether a control of type <paramref name="source"/> can be connected to an input of type <paramref name="input"/>: a general input takes every control, a general control is scaled to any input,
    /// and a typed control only goes to an input of the same unit.
    /// </summary>
    public static bool Compatible(string source, string input)
    {
        if (!Is(source) || !Is(input)) return false;
        if (input == General || source == General) return true;
        return string.Equals(source, input, StringComparison.Ordinal);
    }

    /// <summary>The unit a block says its control output has (the <c>unit</c> parameter of follow, curve and map), or null for a general control.</summary>
    public static string? UnitOfBlock(BlockNode block)
    {
        string? text = block.Parameters.FirstOrDefault(p => p.Name == "unit")?.Value.Trim().Trim('"');
        return text is { Length: > 0 } && Units.Contains(text) ? text : null;
    }

    /// <summary>The type an output of <paramref name="block"/> really has: a control output of the template is typed by the unit of the block.</summary>
    public static string ResolveOutput(BlockNode block, string portType) => portType == General ? Typed(UnitOfBlock(block)) : portType;
}
