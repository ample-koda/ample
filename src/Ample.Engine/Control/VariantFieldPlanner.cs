using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Audio.Random;
using Ample.Language;

namespace Ample.Engine.Control;

/// <summary>A parameter that can be varied deterministically between takes.</summary>
public sealed record VariantField(string Name, string Label, string Kind, string Value, IReadOnlyList<string> Options, double? Min, double? Max, double? Step);

/// <summary>A source field excluded from automatic variation.</summary>
public sealed record UnsupportedVariantField(string Name, string Label, string Kind);

/// <summary>A proposed value for one field in one variant.</summary>
public sealed record VariantFieldChange(string Name, string Label, string From, string To);

/// <summary>Finds safely variable fields and derives reproducible per-take parameter changes.</summary>
public static class VariantFieldPlanner
{
    private static readonly HashSet<string> SupportedKinds = ["number", "seed", "renderer-seed"];

    /// <summary>Lists output-affecting scalar fields that can be proposed for a block.</summary>
    public static IReadOnlyList<VariantField> Fields(BlockNode block, TemplateSpec template) => [.. template.ParameterSpecs.Values
        .Where(p => p.AffectsOutput && (SupportedKinds.Contains(p.Kind) || p.Options?.Count > 1) && (KindOf(p) != "number" || p.Min is null || p.Max is null || p.Max > p.Min))
        .Select(p => new VariantField(p.Name, p.Label, KindOf(p), block.Parameters.FirstOrDefault(x => x.Name == p.Name)?.Value ?? p.Default, p.Options ?? [], p.Min, p.Max, p.Step))
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .Concat(template.Dynamic ? [new VariantField("$seed", "Renderer seed", "renderer-seed", "derived", [], null, null, null)] : [])];

    /// <summary>Lists output fields that are intentionally not auto-varied by the current scalar proposal engine.</summary>
    public static IReadOnlyList<UnsupportedVariantField> UnsupportedFields(TemplateSpec template) => [.. template.ParameterSpecs.Values
        .Where(p => p.AffectsOutput && !SupportedKinds.Contains(p.Kind) && (p.Options is null || p.Options.Count <= 1))
        .Select(p => new UnsupportedVariantField(p.Name, p.Label, p.Kind))
        .OrderBy(p => p.Name, StringComparer.Ordinal)];

    private static string KindOf(TemplateParameterSpec parameter) => parameter.Options?.Count > 1 ? "choice" : parameter.Kind;

    /// <summary>Creates the exact parameter differences for a repeatable variant request.</summary>
    public static IReadOnlyList<IReadOnlyList<VariantFieldChange>> Propose(ulong masterSeed, BlockNode block, TemplateSpec template, int count, int startIndex, string kind, IReadOnlyList<string> names)
    {
        if (count is < 1 or > 20 || startIndex < 0 || startIndex > 20 - count) throw new AmpleException("AMP5261", "Variant count and start index must fit within the 20-take limit.");
        IReadOnlyList<VariantField> available = Fields(block, template);
        VariantField[] selected = [.. names.Distinct(StringComparer.Ordinal).Select(name => available.FirstOrDefault(field => field.Name == name) ?? throw new AmpleException("AMP5261", $"'{name}' is not a supported variant field for '{block.Name}'.")).OrderBy(field => field.Name, StringComparer.Ordinal)];
        if (selected.Length == 0) throw new AmpleException("AMP5261", "Select at least one field to vary.");
        return [.. Enumerable.Range(startIndex, count).Select(index => (IReadOnlyList<VariantFieldChange>)[.. selected.Select(field => new VariantFieldChange(field.Name, field.Label, field.Value, Value(masterSeed, block.Name, kind, (ulong)index, field)))])];
    }

    private static string Value(ulong masterSeed, string block, string kind, ulong index, VariantField field)
    {
        ulong random = SeedDeriver.Derive(masterSeed, "block", block, kind, index, "field", field.Name);
        if (field.Kind == "renderer-seed") return SeedDeriver.Derive(masterSeed, "block", block, kind, index).ToString(CultureInfo.InvariantCulture);
        if (field.Kind == "seed") return SeedDeriver.Derive(masterSeed, "block", block, kind, index, "seed", field.Name).ToString(CultureInfo.InvariantCulture);
        if (field.Kind == "choice")
        {
            string[] choices = [.. field.Options.Where(option => option != field.Value)];
            return choices[(int)(random % (ulong)choices.Length)];
        }

        double min = field.Min ?? 0, max = field.Max ?? 1, step = field.Step is > 0 ? field.Step.Value : Math.Max((max - min) / 100, 0.000001);
        double baseline = double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : (min + max) / 2;
        long steps = Math.Max(1, (long)Math.Floor((max - min) / step));
        long distance = Math.Max(1, (long)Math.Floor(steps * (0.05 + ((random >> 16) % 1600) / 10000d)));
        double proposed = Math.Clamp(baseline + ((random & 1) == 0 ? distance : -distance) * step, min, max);
        if (Math.Abs(proposed - baseline) < step / 100) proposed = proposed + step <= max ? proposed + step : Math.Max(min, proposed - step);
        return proposed.ToString("0.################", CultureInfo.InvariantCulture);
    }
}
