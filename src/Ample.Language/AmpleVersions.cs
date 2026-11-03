namespace Ample.Language;

/// <summary>
/// The version of the language and of the built-in blocks ("AMPLE 1"): a whole number that only goes up when a change could alter how an existing song sounds or reads.
/// A file states the one it was written for (<c>ample N</c> in the composition line, a pin on a single block); a file that says nothing is read as the current one.
/// A song is never upgraded behind its author's back: older versions are carried to the current one in memory by <see cref="UpgradeStep"/>s, and the text only changes when its author migrates it.
/// </summary>
public static class AmpleVersions
{
    /// <summary>Gets the version this AMPLE writes and builds.</summary>
    public const int Current = 1;

    /// <summary>Gets the steps from one version to the next (none yet: version 1 is the first). Each step must keep the sound of the song: where a default or a name changed, it writes the old one explicitly.</summary>
    public static IReadOnlyList<UpgradeStep> Steps { get; } = [];

    /// <summary>The oldest version this AMPLE still reads.</summary>
    public const int Oldest = 1;

    /// <summary>Whether <paramref name="version"/> is one this AMPLE knows.</summary>
    public static bool IsKnown(int version) => version >= Oldest && version <= Current;

    /// <summary>
    /// The block types that changed after a song written for <paramref name="from"/>: the definition says so (<see cref="TemplateSpec.StableSince"/>), a step may add more. Blocks of those types are shown read-only (with their migrated values) until the song is migrated.
    /// </summary>
    /// <param name="spec">The language data (the definitions of the built-in blocks).</param>
    /// <param name="from">The version the song was written for.</param>
    /// <param name="steps">The steps to look at (the registered ones when null).</param>
    public static IReadOnlySet<string> StaleTypes(SpecData spec, int from, IReadOnlyList<UpgradeStep>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        HashSet<string> types = new(TouchedTypes(from, steps), StringComparer.Ordinal);
        types.UnionWith(spec.Templates.Values.Where(t => t.StableSince > from).Select(t => t.Id));
        return types;
    }

    /// <summary>
    /// The block types an upgrade from <paramref name="from"/> to the current version touches.
    /// </summary>
    public static IReadOnlySet<string> TouchedTypes(int from, IReadOnlyList<UpgradeStep>? steps = null)
    {
        HashSet<string> types = new(StringComparer.Ordinal);
        foreach (UpgradeStep step in steps ?? Steps)
        {
            if (step.From >= from) types.UnionWith(step.TouchedTypes);
        }

        return types;
    }

    /// <summary>
    /// Carries song text written for <paramref name="from"/> to the current version, step by step, and says which steps ran. The input is never changed; the caller decides whether the result is only built or also written (a migration).
    /// </summary>
    public static (string Text, IReadOnlyList<UpgradeStep> Applied) Upgrade(string text, int from, IReadOnlyList<UpgradeStep>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<UpgradeStep> applied = [];
        foreach (UpgradeStep step in (steps ?? Steps).Where(s => s.From >= from).OrderBy(s => s.From))
        {
            text = step.Rewrite(text);
            applied.Add(step);
        }

        return (text, applied);
    }
}

/// <summary>One step from version <see cref="From"/> to <see cref="From"/> + 1.</summary>
/// <param name="From">The version the step starts at.</param>
/// <param name="Title">What the step changes, in a sentence (shown before a migration is done).</param>
/// <param name="TouchedTypes">The block types the step changes.</param>
/// <param name="Rewrite">The text transformation: pure, and the same sound before and after.</param>
public sealed record UpgradeStep(int From, string Title, IReadOnlySet<string> TouchedTypes, Func<string, string> Rewrite);
