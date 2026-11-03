using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>The result of an edit of the text of a component: the new text, or why it was not made.</summary>
/// <param name="Ok">Whether the edit was made.</param>
/// <param name="Text">The new text (the old text when it was not made).</param>
/// <param name="Message">One plain sentence: what was done, or why not.</param>
public sealed record ComponentEditResult(bool Ok, string Text, string Message);

/// <summary>What a signer returns for a payload hash.</summary>
/// <param name="SignedBy">Who signs (a name), or null.</param>
/// <param name="PublicKey">The public key (base64), so that anyone can verify without a key store.</param>
/// <param name="Signature">The signature (base64).</param>
public sealed record ComponentSignature(string? SignedBy, string PublicKey, string Signature);

/// <summary>Edits of the text of components: seal, unseal, make a new version. They change text only (the host writes it through the command path, so that it is journaled and can be undone).</summary>
public static partial class ComponentEdits
{
    [GeneratedRegex(@"\bversion\s+""?[0-9][0-9A-Za-z.+-]*""?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionWord();

    [GeneratedRegex(@"\s+current\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CurrentWord();

    private static ComponentEditResult No(string text, string message) => new(false, text, message);

    private static (ComponentDefinition? Definition, AmpleDocument Document, string? Problem) Find(string text, string name, string? version, SpecData spec)
    {
        AmpleDocument doc = Parser.Parse(text, spec, new ParseOptions { RequireProjectLine = false });
        List<ComponentDefinition> found = [.. doc.Components.Where(c => c.DeclaredName == name)];
        if (found.Count == 0) return (null, doc, $"There is no component \"{name}\" in this text.");
        if (version is not null)
        {
            found = [.. found.Where(c => c.Version.SameAs(SemVer.TryParse(version, out SemVer v) ? v : new SemVer(-1, 0, 0, [])))];
            return found.Count == 0 ? (null, doc, $"\"{name}\" has no version {version}.") : (found[0], doc, null);
        }

        return found.Count > 1 ? (null, doc, $"\"{name}\" has several versions ({string.Join(", ", found.Select(f => f.Version))}): say which one.") : (found[0], doc, null);
    }

    private static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    /// <summary>Seals a version: writes the seal block (the payload and semantic hash, and a signature when a signer is given) as the last thing in the component. An existing seal is replaced.</summary>
    /// <param name="text">The text of the song or of the <c>.amplec</c> file.</param>
    /// <param name="name">The component (its name as declared).</param>
    /// <param name="version">The version, or null when the component has one.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="sign">Signs the payload hash, or null for a seal without a signature.</param>
    /// <returns>The edit.</returns>
    public static ComponentEditResult Seal(string text, string name, string? version, SpecData spec, Func<string, ComponentSignature>? sign = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spec);
        (ComponentDefinition? d, AmpleDocument doc, string? problem) = Find(text, name, version, spec);
        if (d is null) return No(text, problem!);
        if (!d.IdWritten) return No(text, $"Write an id first (id \"com.you.{d.DeclaredName.ToLowerInvariant().Replace('_', '-')}\"): a sealed component is identified by it, and it must not change.");
        IReadOnlyList<string> deps = ComponentHashing.DependenciesOf(d, doc.Spec ?? spec, () => doc.HelperTable.ClosureHashes(string.Join('\n', d.ProcessBody)), [.. doc.Components, .. doc.ImportedComponents]);
        SealCheck check = ComponentHashing.Check(d, deps);
        ComponentSignature? signature = sign?.Invoke(check.Payload);
        List<string> lines = Lines(text);
        int close = d.EndLine;
        if (d.Seal is { } old)
        {
            lines.RemoveRange(old.StartLine, old.EndLine - old.StartLine + 1);
            close -= old.EndLine - old.StartLine + 1;
        }

        string closeLine = lines[close];
        int brace = closeLine.LastIndexOf('}');
        if (brace < 0 || closeLine[..brace].Trim().Length > 0) return No(text, "The closing brace of the component must stand on a line of its own.");
        IReadOnlyList<string> seal = ComponentHashing.SealLines(check.Payload, check.Semantic, signature?.SignedBy, signature?.PublicKey, signature?.Signature);
        lines.InsertRange(close, seal);
        return new ComponentEditResult(true, string.Join('\n', lines), $"Sealed {d.DeclaredName} {d.Version}: payload {ComponentHashing.Abbreviate(check.Payload)}, semantic {ComponentHashing.Abbreviate(check.Semantic)}" + (signature is null ? " (not signed)." : " (signed)."));
    }

    /// <summary>Removes the seal of a version, so that it can be changed again (it is a development version from then on). Anything that pinned it sees the change (AMP2203) when the code changes.</summary>
    /// <param name="text">The text.</param>
    /// <param name="name">The component.</param>
    /// <param name="version">The version, or null when the component has one.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The edit.</returns>
    public static ComponentEditResult Unseal(string text, string name, string? version, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spec);
        (ComponentDefinition? d, _, string? problem) = Find(text, name, version, spec);
        if (d is null) return No(text, problem!);
        if (d.Seal is not { } seal) return No(text, $"{d.DeclaredName} {d.Version} has no seal.");
        List<string> lines = Lines(text);
        lines.RemoveRange(seal.StartLine, seal.EndLine - seal.StartLine + 1);
        return new ComponentEditResult(true, string.Join('\n', lines), $"Removed the seal of {d.DeclaredName} {d.Version}. It is a development version again.");
    }

    /// <summary>Makes a working copy of a version with a new version number: the whole declaration without its seal and without <c>current</c>, written right after the original. The original stays as it is (a sealed version is never changed).</summary>
    /// <param name="text">The text.</param>
    /// <param name="name">The component.</param>
    /// <param name="fromVersion">The version to copy, or null when the component has one.</param>
    /// <param name="newVersion">The new version.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The edit.</returns>
    public static ComponentEditResult NewVersion(string text, string name, string? fromVersion, string newVersion, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spec);
        if (!SemVer.TryParse(newVersion, out SemVer target)) return No(text, $"\"{newVersion}\" is not a version.");
        (ComponentDefinition? d, AmpleDocument doc, string? problem) = Find(text, name, fromVersion, spec);
        if (d is null) return No(text, problem!);
        if (doc.Components.Any(c => c.DeclaredName == name && c.Version.SameAs(target))) return No(text, $"{name} {target} exists already.");
        List<string> lines = Lines(text);
        List<string> copy = [.. lines.Skip(d.StartLine).Take(d.EndLine - d.StartLine + 1)];
        if (d.Seal is { } seal) copy.RemoveRange(seal.StartLine - d.StartLine, seal.EndLine - seal.StartLine + 1);
        int header = d.HeaderLine - d.StartLine;
        bool changed = false;
        for (int i = header; i < copy.Count && !changed; i++)
        {
            Match m = VersionWord().Match(copy[i]);
            if (!m.Success) continue;
            copy[i] = VersionWord().Replace(copy[i], $"version {target}", 1);
            changed = true;
        }

        if (!changed) return No(text, "The version of the component is not written as 'version x.y.z' in its header, so it cannot be changed here.");
        for (int i = header; i < copy.Count; i++)
        {
            if (!copy[i].Contains('{', StringComparison.Ordinal)) copy[i] = CurrentWord().Replace(copy[i], string.Empty);
            else { copy[i] = CurrentWord().Replace(copy[i], string.Empty); break; }
        }

        lines.InsertRange(d.EndLine + 1, ["", .. copy]);
        return new ComponentEditResult(true, string.Join('\n', lines), $"Made {name} {target} as a copy of {d.Version}, not sealed. The original is untouched.");
    }

}
