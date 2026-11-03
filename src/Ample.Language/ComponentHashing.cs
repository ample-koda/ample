using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>What a seal says about a component.</summary>
public enum SealState
{
    /// <summary>The component has no seal: it is a development object; it builds, but nothing about it is trusted or reused.</summary>
    Unsealed,

    /// <summary>The seal matches the component exactly.</summary>
    Valid,

    /// <summary>There is a seal, but the component no longer matches it (it was changed after it was sealed): it counts as unsealed.</summary>
    Invalid,
}

/// <summary>The result of checking a seal.</summary>
/// <param name="State">The state.</param>
/// <param name="Reason">Why the seal is invalid (empty otherwise).</param>
/// <param name="Semantic">The semantic hash of the component as it is now.</param>
/// <param name="Payload">The payload hash of the component as it is now.</param>
public sealed record SealCheck(SealState State, string Reason, string Semantic, string Payload);

/// <summary>
/// The two hashes of a component (plan A9, requirement R-B). The <b>semantic hash</b> covers what changes what the component makes: its canonical id, version, contract (ports, property types, defaults and the attributes that
/// constrain a value), the process, everything the process calls, the capabilities and the migrations. The <b>payload hash</b> covers everything the seal protects: the semantic content and the documentation, the presentation
/// attributes, the declarative UI, the deprecation and the KODA facts. A seal protects the payload; the semantic hash is what a song pins and what decides whether anything that depends on the component must be built again.
/// </summary>
public static partial class ComponentHashing
{
    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Spaces();

    private static string Collapse(string text) => Spaces().Replace(NotePipeline.StripComments(text), " ").Trim();

    private static string Sha(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The canonical text of the semantic content of a component.</summary>
    /// <param name="d">The component.</param>
    /// <param name="dependencyHashes">The hashes of the helpers and constants that the process uses (with what they use), from the helper table of the file the component lives in.</param>
    /// <returns>The text that is hashed.</returns>
    public static string SemanticText(ComponentDefinition d, IReadOnlyList<string> dependencyHashes)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(dependencyHashes);
        StringBuilder sb = new();
        sb.Append("ample-component/1\n");
        sb.Append("id=").Append(d.Id).Append('\n');
        sb.Append("version=").Append(d.Version).Append('\n');
        sb.Append("requires=").Append(string.Join(',', d.Requires.Order(StringComparer.Ordinal))).Append('\n');
        foreach (ComponentPort p in d.Inputs) sb.Append("input=").Append(p.Name).Append(':').Append(p.Type.TrimEnd('?')).Append(':').Append(p.Optional ? "optional" : "required").Append(':').Append(p.Fallback ?? string.Empty).Append('\n');
        foreach (ComponentPort p in d.Outputs) sb.Append("output=").Append(p.Name).Append(':').Append(p.Type.TrimEnd('?')).Append('\n');
        foreach (ComponentProperty p in d.Properties)
        {
            sb.Append("property=").Append(p.Name).Append(':').Append(p.Type.TrimEnd('?')).Append(':').Append(Collapse(p.Default ?? string.Empty));
            foreach (ComponentAnnotation a in p.Attributes.Where(a => !ComponentAttributes.Presentation.Contains(a.Name)).OrderBy(a => a.Name, StringComparer.Ordinal)) sb.Append(':').Append(a.Name).Append('(').Append(string.Join(',', a.Args)).Append(')');
            sb.Append('\n');
        }

        sb.Append("process=").Append(Collapse(string.Join('\n', d.ProcessBody))).Append('\n');
        foreach (string hash in dependencyHashes.Order(StringComparer.Ordinal)) sb.Append("uses=").Append(hash).Append('\n');
        foreach (ComponentMigration m in d.Migrations.OrderBy(m => m.To, StringComparer.Ordinal).ThenBy(m => m.From, StringComparer.Ordinal))
            sb.Append("migrate=").Append(m.From ?? string.Empty).Append("->").Append(m.To).Append(':').Append(m.Impact).Append(':').Append(Collapse(string.Join('\n', m.Body.Select(b => b.Text)))).Append('\n');
        return sb.ToString();
    }

    /// <summary>The semantic hash.</summary>
    /// <param name="d">The component.</param>
    /// <param name="dependencyHashes">The hashes of what the process uses.</param>
    /// <returns>The hash, <c>sha256:</c> and 64 hex digits.</returns>
    public static string Semantic(ComponentDefinition d, IReadOnlyList<string> dependencyHashes) => Sha(SemanticText(d, dependencyHashes));

    /// <summary>The payload hash: the semantic content and everything a seal protects besides.</summary>
    /// <param name="d">The component.</param>
    /// <param name="dependencyHashes">The hashes of what the process uses.</param>
    /// <returns>The hash.</returns>
    public static string Payload(ComponentDefinition d, IReadOnlyList<string> dependencyHashes)
    {
        ArgumentNullException.ThrowIfNull(d);
        StringBuilder sb = new(SemanticText(d, dependencyHashes));
        sb.Append("--- presentation\n");
        sb.Append("current=").Append(d.Current ? "yes" : "no").Append('\n');
        sb.Append("doc=").Append(Collapse(d.Doc is null ? string.Empty : d.Doc.Summary + " " + string.Join(' ', d.Doc.Params.Select(p => p.Name + " " + p.Text)))).Append('\n');
        foreach (ComponentAnnotation a in d.Attributes.OrderBy(a => a.Name, StringComparer.Ordinal)) sb.Append("attribute=").Append(a.Name).Append('(').Append(string.Join(',', a.Args)).Append(")\n");
        foreach (ComponentPort p in d.Inputs.Concat(d.Outputs)) foreach (ComponentAnnotation a in p.Attributes.OrderBy(a => a.Name, StringComparer.Ordinal)) sb.Append("attribute=").Append(p.Name).Append('.').Append(a.Name).Append('(').Append(string.Join(',', a.Args)).Append(")\n");
        foreach (ComponentProperty p in d.Properties)
        {
            foreach (ComponentAnnotation a in p.Attributes.Where(a => ComponentAttributes.Presentation.Contains(a.Name)).OrderBy(a => a.Name, StringComparer.Ordinal)) sb.Append("attribute=").Append(p.Name).Append('.').Append(a.Name).Append('(').Append(string.Join(',', a.Args)).Append(")\n");
            sb.Append("editor=").Append(p.Name).Append(':').Append(p.Editor ?? string.Empty).Append('\n');
            sb.Append("doc=").Append(p.Name).Append(':').Append(Collapse(p.Doc?.Summary ?? string.Empty)).Append('\n');
        }

        sb.Append("ui=").Append(Collapse(d.Ui ?? string.Empty)).Append('\n');
        if (d.Deprecation is { } dep) sb.Append("deprecated=").Append(dep.Message).Append('|').Append(dep.Replacement ?? string.Empty).Append('\n');
        foreach (ComponentMigration m in d.Migrations) sb.Append("migration-message=").Append(m.To).Append(':').Append(m.Message).Append('\n');
        return Sha(sb.ToString());
    }

    /// <summary>Checks the seal of a component against the component as it is.</summary>
    /// <param name="d">The component.</param>
    /// <param name="dependencyHashes">The hashes of what the process uses.</param>
    /// <returns>The state of the seal, and the hashes as they are now.</returns>
    public static SealCheck Check(ComponentDefinition d, IReadOnlyList<string> dependencyHashes)
    {
        ArgumentNullException.ThrowIfNull(d);
        string semantic = Semantic(d, dependencyHashes), payload = Payload(d, dependencyHashes);
        if (d.Seal is null) return new SealCheck(SealState.Unsealed, string.Empty, semantic, payload);
        if (d.Seal.Semantic != semantic) return new SealCheck(SealState.Invalid, $"the component changed after it was sealed (semantic hash {Abbreviate(d.Seal.Semantic)} is now {Abbreviate(semantic)}); a sealed version is immutable: make a new version.", semantic, payload);
        if (d.Seal.Payload != payload) return new SealCheck(SealState.Invalid, $"the documentation, presentation or UI changed after it was sealed (payload hash {Abbreviate(d.Seal.Payload)} is now {Abbreviate(payload)}).", semantic, payload);
        return new SealCheck(SealState.Valid, string.Empty, semantic, payload);
    }

    /// <summary>The hashes a semantic hash must contain besides the text of a component: for a Notes component those of the helpers its process calls, for an Audio component the processors it uses with the version of their blocks. One function, so that sealing, checking, the catalog and the build all agree.</summary>
    /// <param name="d">The component.</param>
    /// <param name="spec">The language data (the block templates are the processors).</param>
    /// <param name="helpers">The hashes of the helper closure (read only for a Notes component).</param>
    /// <param name="visible">The components an Audio component can call as a processor (their code is part of its hash), or null.</param>
    /// <returns>The dependency hashes.</returns>
    public static IReadOnlyList<string> DependenciesOf(ComponentDefinition d, SpecData spec, Func<IReadOnlyList<string>> helpers, IReadOnlyList<ComponentDefinition>? visible = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(helpers);
        return d.IsAudio ? AudioGraphBuilder.Build(d, spec, new Dictionary<string, string>(StringComparer.Ordinal), 120, visible).Dependencies : helpers();
    }

    /// <summary>Gives the first digits of a hash, for messages.</summary>
    /// <param name="hash">The hash.</param>
    /// <returns>For example <c>3e91c0a2</c>.</returns>
    public static string Abbreviate(string hash) => hash.StartsWith("sha256:", StringComparison.Ordinal) && hash.Length > 15 ? hash[7..15] : hash;

    /// <summary>The seal block as the engine writes it (the text of the lines, with the indent of a body inside a component).</summary>
    /// <param name="payload">The payload hash.</param>
    /// <param name="semantic">The semantic hash.</param>
    /// <param name="signedBy">Who signed, or null.</param>
    /// <param name="key">The public key id, or null.</param>
    /// <param name="signature">The signature, or null.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> SealLines(string payload, string semantic, string? signedBy = null, string? key = null, string? signature = null)
    {
        List<string> lines = ["  seal {", "    payload " + payload, "    semantic " + semantic];
        if (signedBy is not null) lines.Add("    signed-by \"" + signedBy.Replace("\"", "'", StringComparison.Ordinal) + "\"");
        if (key is not null) lines.Add("    key " + key);
        if (signature is not null) lines.Add("    signature " + signature);
        lines.Add("  }");
        return lines;
    }
}
