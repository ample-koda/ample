using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>An attribute written above a declaration of a component: <c>#[MinValue(1), MaxValue(16)]</c> gives two of them.</summary>
/// <param name="Name">The attribute name.</param>
/// <param name="Args">The arguments as written (strings keep their quotes, a named argument keeps its name).</param>
/// <param name="Line">The 0-based line of the original text.</param>
/// <param name="Col">The 0-based column where the attribute starts.</param>
public sealed record ComponentAnnotation(string Name, IReadOnlyList<string> Args, int Line, int Col);

/// <summary>An input or an output of a component.</summary>
/// <param name="Name">The port name.</param>
/// <param name="Type">The type as written (<c>Notes</c>).</param>
/// <param name="Optional">Whether an input may stay unconnected.</param>
/// <param name="Fallback">The property that stands in for an unconnected input (the local notes), or null.</param>
/// <param name="Line">The 0-based line.</param>
/// <param name="Attributes">The attributes above it.</param>
/// <param name="Doc">The doc comment above it, or null.</param>
public sealed record ComponentPort(string Name, string Type, bool Optional, string? Fallback, int Line, IReadOnlyList<ComponentAnnotation> Attributes, DocComment? Doc);

/// <summary>A property of a component: a typed setting of an instance.</summary>
/// <param name="Name">The property name.</param>
/// <param name="Type">The type as written.</param>
/// <param name="Default">The default as written, or null (a Notes property then starts empty).</param>
/// <param name="Editor">The editor that edits it (<c>jam</c> for notes), or null.</param>
/// <param name="Line">The 0-based line.</param>
/// <param name="Attributes">The attributes above it.</param>
/// <param name="Doc">The doc comment above it, or null.</param>
public sealed record ComponentProperty(string Name, string Type, string? Default, string? Editor, int Line, IReadOnlyList<ComponentAnnotation> Attributes, DocComment? Doc);

/// <summary>The seal of a component: what the engine wrote when the version was sealed.</summary>
/// <param name="Payload">The payload hash (<c>sha256:...</c>).</param>
/// <param name="Semantic">The semantic hash.</param>
/// <param name="SignedBy">Who signed it, or null.</param>
/// <param name="Key">The public key (or its id) that signed it, or null.</param>
/// <param name="Signature">The signature, or null.</param>
/// <param name="StartLine">The 0-based line where the seal block starts.</param>
/// <param name="EndLine">The 0-based line where it ends.</param>
public sealed record ComponentSeal(string Payload, string Semantic, string? SignedBy, string? Key, string? Signature, int StartLine, int EndLine);

/// <summary>That a version should not be used any more, and what to use instead.</summary>
/// <param name="Message">What the author says.</param>
/// <param name="Replacement">The replacement as written (<c>koda.fx.canonMaker@^4</c>), or null.</param>
/// <param name="Line">The 0-based line of the deprecated block.</param>
public sealed record ComponentDeprecation(string Message, string? Replacement, int Line);

/// <summary>An automatic migration to another version (or another component): the declaration; its body is read by the migration engine.</summary>
/// <param name="From">The version it migrates from, or null (it migrates from the version it is written in).</param>
/// <param name="To">The target: a version of this component (<c>4.0.0</c>) or a component with a version (<c>koda.fx.echo@4</c>).</param>
/// <param name="Impact">The declared impact: none, equivalent, musicalChange or destructive; empty when it is not declared.</param>
/// <param name="Message">What the author says about it, or empty.</param>
/// <param name="Body">The lines of its body with their 0-based line numbers.</param>
/// <param name="Line">The 0-based line of the declaration.</param>
public sealed record ComponentMigration(string? From, string To, string Impact, string Message, IReadOnlyList<(int Line, string Text)> Body, int Line);

/// <summary>A problem found in a component declaration.</summary>
/// <param name="Line">The 0-based line.</param>
/// <param name="Col">The 0-based start column.</param>
/// <param name="EndCol">The 0-based end column (exclusive).</param>
/// <param name="Code">The diagnostic code (AMP22xx).</param>
/// <param name="Severity">The severity.</param>
/// <param name="Message">One plain sentence.</param>
/// <param name="Hint">What to do, or empty.</param>
public sealed record ComponentIssue(int Line, int Col, int EndCol, string Code, Severity Severity, string Message, string Hint = "");

/// <summary>An <c>import "file.amplec" [as alias]</c> line.</summary>
/// <param name="Target">The file or module name as written.</param>
/// <param name="Alias">The alias, or null.</param>
/// <param name="Line">The 0-based line.</param>
public sealed record ComponentImport(string Target, string? Alias, int Line);

/// <summary>A <c>reexport source as target</c> line: a symbol (or a namespace with <c>.*</c>) of an imported module offered again under another path.</summary>
/// <param name="Source">The symbol or namespace as written (through the alias of an import, or a full path).</param>
/// <param name="Target">The path it is offered under.</param>
/// <param name="Line">The 0-based line.</param>
public sealed record ComponentReexport(string Source, string Target, int Line);

/// <summary>
/// A reusable, typed block that is made of Notes code (docs/language/19-components.md). One definition is shared by every instance that uses it.
/// </summary>
public sealed class ComponentDefinition
{
    private string? exportedAs;

    /// <summary>Gets the exported name: the path that source code writes (<c>koda.notes.tripleNotes</c>). An import with an alias or a reexport offers the same component under another path (see <see cref="Renamed"/>).</summary>
    public string Name { get => exportedAs ?? DeclaredName; init => DeclaredName = value; }

    /// <summary>Gets the name as the declaration writes it.</summary>
    public string DeclaredName { get; private set; } = string.Empty;

    /// <summary>Makes the same component, with the same canonical id, version and content, offered under another path.</summary>
    /// <param name="path">The path it is exported under.</param>
    /// <returns>The copy.</returns>
    public ComponentDefinition Renamed(string path)
    {
        ComponentDefinition copy = (ComponentDefinition)MemberwiseClone();
        copy.exportedAs = path;
        return copy;
    }

    /// <summary>Gets the canonical identity (<c>demo.koda.notes.triple-notes</c>); when none is written it is <c>local:</c> and the exported path.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets whether the identity is written in the source (it has to be before the component is sealed and exported).</summary>
    public bool IdWritten { get; init; }

    /// <summary>Gets the version.</summary>
    public SemVer Version { get; init; } = new(0, 0, 0, []);

    /// <summary>Gets whether this version is marked <c>current</c> (it is the default of its family, over a higher release).</summary>
    public bool Current { get; init; }

    /// <summary>Gets the inputs.</summary>
    public IReadOnlyList<ComponentPort> Inputs { get; init; } = [];

    /// <summary>Gets the outputs.</summary>
    public IReadOnlyList<ComponentPort> Outputs { get; init; } = [];

    /// <summary>Gets the properties.</summary>
    public IReadOnlyList<ComponentProperty> Properties { get; init; } = [];

    /// <summary>Gets the attributes of the component itself.</summary>
    public IReadOnlyList<ComponentAnnotation> Attributes { get; init; } = [];

    /// <summary>Gets the capabilities the component says it needs.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Gets the lines of the process body (the lines between its braces), or empty when there is none.</summary>
    public IReadOnlyList<string> ProcessBody { get => processLines; init => processLines = value; }

    private IReadOnlyList<string> processLines = [];

    /// <summary>Makes the same component with another process body (the calls of other components written with the names they have for a file).</summary>
    /// <param name="body">The lines of the process.</param>
    /// <returns>The copy.</returns>
    public ComponentDefinition WithProcess(IReadOnlyList<string> body)
    {
        ComponentDefinition copy = (ComponentDefinition)MemberwiseClone();
        copy.processLines = body;
        return copy;
    }

    /// <summary>Gets the 0-based line of the <c>process</c> keyword, or -1.</summary>
    public int ProcessLine { get; init; } = -1;

    /// <summary>Gets the text of the declarative <c>ui</c> block (without its braces), or null.</summary>
    public string? Ui { get; init; }

    /// <summary>Gets the layout read from the <c>ui</c> block (what is written; <see cref="ComponentUiReader.Effective"/> adds the one made from the attributes), or null when there is no block.</summary>
    public ComponentUi? UiTree { get; init; }

    /// <summary>Gets the 0-based line of the <c>ui</c> word, or -1.</summary>
    public int UiLine { get; init; } = -1;

    /// <summary>Gets the 0-based line of the closing brace of the <c>ui</c> block, or -1.</summary>
    public int UiEndLine { get; init; } = -1;

    /// <summary>Gets the deprecation, or null.</summary>
    public ComponentDeprecation? Deprecation { get; init; }

    /// <summary>Gets the migrations.</summary>
    public IReadOnlyList<ComponentMigration> Migrations { get; init; } = [];

    /// <summary>Gets the seal, or null for an unsealed component.</summary>
    public ComponentSeal? Seal { get; init; }

    /// <summary>Gets the doc comment of the component, or null.</summary>
    public DocComment? Doc { get; init; }

    /// <summary>Gets where the definition was found (a file name, <c>song</c> or <c>embedded:module</c>).</summary>
    public string Origin { get; init; } = string.Empty;

    /// <summary>Gets the 0-based line where the declaration starts (the first attribute line counts).</summary>
    public int StartLine { get; init; }

    /// <summary>Gets the 0-based line of the <c>component</c> keyword.</summary>
    public int HeaderLine { get; init; }

    /// <summary>Gets the 0-based line of the closing brace.</summary>
    public int EndLine { get; init; }

    /// <summary>Gets the text of the whole declaration (attributes, header, body, seal) as written.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the name of the helper that the process becomes: unique per canonical id and version, so the same component under two paths is one helper.</summary>
    public string HelperName => "__c_" + Regex.Replace(Id, "[^A-Za-z0-9]", "_") + "_" + Version.Major.ToString(CultureInfo.InvariantCulture) + "_" + Version.Minor.ToString(CultureInfo.InvariantCulture) + "_" + Version.Patch.ToString(CultureInfo.InvariantCulture) + (Version.IsPrerelease ? "_" + Regex.Replace(string.Join('_', Version.Pre), "[^A-Za-z0-9]", "_") : string.Empty);

    /// <summary>Gets the title shown in the palette: the <c>DisplayName</c> attribute, else the last part of the name.</summary>
    public string Title => Attributes.FirstOrDefault(a => a.Name == "DisplayName") is { Args.Count: > 0 } shown ? shown.Args[0].Trim('"') : Name[(Name.LastIndexOf('.') + 1)..];

    /// <summary>Gets the namespace: the exported name without its last part (empty for a name of one part).</summary>
    public string Namespace => Name.Contains('.', StringComparison.Ordinal) ? Name[..Name.LastIndexOf('.')] : string.Empty;

    /// <summary>Gets the first output, or null.</summary>
    public ComponentPort? Output => Outputs.Count > 0 ? Outputs[0] : null;

    /// <summary>Gets a value indicating whether the component has an Audio port: its <c>process</c> is then a graph of audio processors (see AudioGraphBuilder) and not Notes code.</summary>
    public bool IsAudio => Inputs.Concat(Outputs).Any(p => p.Type.TrimEnd('?') == "Audio");

    /// <summary>Gets a property by name.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The property, or null.</returns>
    public ComponentProperty? Property(string name) => Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>A module embedded in a song: <c>embedded module name { id "..." version "..." component ... }</c>. Its components are the same objects as the imported ones with the same id, version and payload.</summary>
/// <param name="Name">The name of the module (the name of the file it came from, without the extension).</param>
/// <param name="Id">The canonical id written in the module, or null.</param>
/// <param name="Version">The version written in the module, or null.</param>
/// <param name="Payload">The payload hash of the module seal (empty when it has none).</param>
/// <param name="Start">The 0-based first line.</param>
/// <param name="End">The 0-based last line (the closing brace).</param>
/// <param name="Components">The names of the components it holds.</param>
public sealed record EmbeddedModule(string Name, string? Id, string? Version, string Payload, int Start, int End, IReadOnlyList<string> Components);

/// <summary>What was found in a text: components, imports, reexports and problems.</summary>
/// <param name="Definitions">The components, in the order they are written.</param>
/// <param name="Imports">The import lines.</param>
/// <param name="Reexports">The reexport lines.</param>
/// <param name="Issues">The problems.</param>
/// <param name="Blank">The 0-based lines that hold declarations (the song parser must not read them).</param>
public sealed record ComponentFile(IReadOnlyList<ComponentDefinition> Definitions, IReadOnlyList<ComponentImport> Imports, IReadOnlyList<ComponentReexport> Reexports, IReadOnlyList<ComponentIssue> Issues, IReadOnlySet<int> Blank)
{
    /// <summary>Gets the embedded modules (a module boundary kept inside a song).</summary>
    public IReadOnlyList<EmbeddedModule> Modules { get; init; } = [];

    /// <summary>Gets an empty result.</summary>
    public static ComponentFile Empty { get; } = new([], [], [], [], new HashSet<int>());
}

/// <summary>Reads the declarations of components (<c>component</c>, <c>import</c>, <c>reexport</c>) out of the lines of a song or of a <c>.amplec</c> file.</summary>
public static partial class ComponentReader
{
    [GeneratedRegex(@"^component\s+([A-Za-z_][\w-]*(?:\.[A-Za-z_][\w-]*)*)(.*)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^embedded\s+module\s+([A-Za-z_][\w.-]*)\s*\{\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmbeddedPattern();

    [GeneratedRegex(@"^(id|version|seal)\b\s*(.*)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ModuleLinePattern();

    [GeneratedRegex(@"^import\s+(""[^""]+""|\S+)(?:\s+as\s+([A-Za-z_][\w.-]*))?\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ImportPattern();

    [GeneratedRegex(@"^reexport\s+(\S+)\s+as\s+(\S+)\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReexportPattern();

    [GeneratedRegex(@"^(input|output)\s+([A-Za-z_][\w-]*)\s*:\s*([^{]+?)\s*(\{.*)?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"^property\s+([A-Za-z_][\w-]*)\s*:\s*([^={]+?)\s*(?:=\s*([^{]*?))?\s*(\{.*)?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PropertyPattern();

    private static bool IsAttributeLine(string trimmed) => trimmed.StartsWith("#[", StringComparison.Ordinal) && trimmed.EndsWith(']');

    /// <summary>Gets whether a line is an attribute line (<c>#[Name(args)]</c>): a comment to every tool that does not know attributes.</summary>
    /// <param name="line">The line.</param>
    /// <returns>True for an attribute line.</returns>
    public static bool IsAttribute(string line) => IsAttributeLine(line.Trim());

    /// <summary>The net change of the brace depth over a line (strings and <c>#</c> comments are skipped; <c>#[</c> is a comment too).</summary>
    private static int Depth(string line, int depth, out int firstOpen)
    {
        firstOpen = -1;
        bool inString = false;
        for (int k = 0; k < line.Length; k++)
        {
            char c = line[k];
            if (c == '"' && (k == 0 || line[k - 1] != '\\')) inString = !inString;
            else if (!inString && c == '#' && !(k > 0 && char.IsLetterOrDigit(line[k - 1]))) break;
            else if (!inString && c == '{') { if (firstOpen < 0) firstOpen = k; depth++; }
            else if (!inString && c == '}') depth = Math.Max(0, depth - 1);
        }

        return depth;
    }

    private static string StripComment(string line)
    {
        bool inString = false;
        for (int k = 0; k < line.Length; k++)
        {
            if (line[k] == '"' && (k == 0 || line[k - 1] != '\\')) inString = !inString;
            else if (!inString && line[k] == '#' && !(k > 0 && char.IsLetterOrDigit(line[k - 1]))) return line[..k];
        }

        return line;
    }

    /// <summary>Reads the attributes of an attribute line: <c>#[MinValue(1), MaxValue(16)]</c>.</summary>
    /// <param name="line">The text of the line.</param>
    /// <param name="lineNo">The 0-based line number.</param>
    /// <param name="issues">Receives the problems.</param>
    /// <returns>The attributes.</returns>
    public static List<ComponentAnnotation> ReadAttributes(string line, int lineNo, List<ComponentIssue> issues)
    {
        List<ComponentAnnotation> found = [];
        int open = line.IndexOf("#[", StringComparison.Ordinal);
        string inner = line[(open + 2)..line.LastIndexOf(']')];
        int baseCol = open + 2;
        int offset = 0;
        foreach (string piece in SplitTop(inner, ','))
        {
            int at = inner.IndexOf(piece, offset, StringComparison.Ordinal);
            offset = at + piece.Length;
            string text = piece.Trim();
            Match m = Regex.Match(text, @"^([A-Za-z_][\w]*)\s*(?:\((.*)\))?$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (!m.Success)
            {
                issues.Add(new ComponentIssue(lineNo, baseCol + Math.Max(0, at), baseCol + Math.Max(0, at) + text.Length, "AMP2200", Severity.Error, $"An attribute is written Name or Name(arguments): \"{text}\" cannot be read.", "For example: #[MinValue(1), MaxValue(16)]"));
                continue;
            }

            found.Add(new ComponentAnnotation(m.Groups[1].Value, m.Groups[2].Success ? [.. SplitTop(m.Groups[2].Value, ',').Select(a => a.Trim())] : [], lineNo, baseCol + Math.Max(0, at)));
        }

        return found;
    }

    /// <summary>Splits a text at a separator that is not inside a string or brackets.</summary>
    /// <param name="text">The text.</param>
    /// <param name="separator">The separator.</param>
    /// <returns>The parts (empty parts are dropped).</returns>
    public static List<string> SplitTop(string text, char separator)
    {
        List<string> parts = [];
        StringBuilder current = new();
        int depth = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
            if (!inString)
            {
                if (c is '(' or '[' or '{' or '<') depth++;
                else if (c is ')' or ']' or '}' or '>') depth = Math.Max(0, depth - 1);
                else if (c == separator && depth == 0) { parts.Add(current.ToString()); current.Clear(); continue; }
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return [.. parts.Where(p => p.Trim().Length > 0)];
    }

    /// <summary>Reads the declarations of a text.</summary>
    /// <param name="lines">The lines with the block comments blanked (they stay the same number of lines).</param>
    /// <param name="docs">The doc comments by the line of the declaration under them.</param>
    /// <param name="origin">Where the text comes from (a file name or <c>song</c>).</param>
    /// <returns>The declarations and the problems.</returns>
    public static ComponentFile Read(IReadOnlyList<string> lines, IReadOnlyDictionary<int, DocComment> docs, string origin)
    {
        List<ComponentDefinition> definitions = [];
        List<ComponentImport> imports = [];
        List<ComponentReexport> reexports = [];
        List<ComponentIssue> issues = [];
        HashSet<int> blank = [];
        List<ComponentAnnotation> pending = [];
        List<EmbeddedModule> modules = [];
        int pendingStart = -1, moduleEnd = -1, moduleStart = -1, firstOfModule = 0;
        string moduleName = string.Empty;
        string? moduleId = null, moduleVersion = null;
        string modulePayload = string.Empty;
        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (moduleEnd >= 0 && i > moduleEnd)
            {
                modules.Add(new EmbeddedModule(moduleName, moduleId, moduleVersion, modulePayload, moduleStart, moduleEnd, [.. definitions.Skip(firstOfModule).Select(d => d.DeclaredName)]));
                moduleEnd = -1;
            }

            if (moduleEnd < 0 && raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && EmbeddedPattern().Match(StripComment(raw).TrimEnd()) is { Success: true } embedded)
            {
                int close = i;
                int level = Depth(lines[i], 0, out _);
                while (level > 0 && close + 1 < lines.Count) { close++; level = Depth(lines[close], level, out _); }
                if (level > 0) issues.Add(new ComponentIssue(i, 0, raw.Length, "AMP2200", Severity.Error, "The embedded module is not closed (missing \"}\").", "Every { needs its }."));
                moduleName = embedded.Groups[1].Value;
                (moduleId, moduleVersion, modulePayload) = (null, null, string.Empty);
                (moduleStart, moduleEnd, firstOfModule) = (i, close, definitions.Count);
                blank.Add(i);
                blank.Add(close);
                continue;
            }

            if (moduleEnd >= 0 && i < moduleEnd)
            {
                // inside an embedded module: the module's own lines (id, version, seal), or a component (which may be indented)
                string inner = StripComment(raw).Trim();
                Match own = ModuleLinePattern().Match(inner);
                if (own.Success && !HeaderPattern().IsMatch(inner))
                {
                    blank.Add(i);
                    string value = own.Groups[2].Value.Trim().Trim('"');
                    if (own.Groups[1].Value == "id") moduleId = value;
                    else if (own.Groups[1].Value == "version") moduleVersion = value;
                    else
                    {
                        // seal { payload sha256:... ... } on one line or over several
                        int sealEnd = i;
                        int sealLevel = Depth(lines[i], 0, out _);
                        while (sealLevel > 0 && sealEnd + 1 < moduleEnd) { sealEnd++; sealLevel = Depth(lines[sealEnd], sealLevel, out _); blank.Add(sealEnd); }
                        string sealText = string.Join('\n', lines.Skip(i).Take(sealEnd - i + 1));
                        if (Regex.Match(sealText, @"\bpayload\s+""?(sha256:[0-9a-f]+)""?", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } pm) modulePayload = pm.Groups[1].Value;
                        i = sealEnd;
                    }

                    continue;
                }
            }

            bool inModule = moduleEnd >= 0 && i < moduleEnd;
            if (IsAttributeLine(trimmed))
            {
                if (pending.Count == 0) pendingStart = i;
                pending.AddRange(ReadAttributes(raw, i, issues));
                continue;
            }

            if (trimmed[0] == '#') continue;
            string code = StripComment(raw).TrimEnd();
            bool atStart = raw.Length > 0 && (!char.IsWhiteSpace(raw[0]) || inModule);
            Match head = atStart ? HeaderPattern().Match(inModule ? code.TrimStart() : code) : Match.Empty;
            if (head.Success)
            {
                (ComponentDefinition? definition, int end) = ReadComponent(lines, docs, i, pendingStart >= 0 ? pendingStart : i, [.. pending], origin, issues);
                if (definition is not null) definitions.Add(definition);
                for (int b = pendingStart >= 0 ? pendingStart : i; b <= end; b++) blank.Add(b);
                pending.Clear();
                pendingStart = -1;
                i = end;
                continue;
            }

            if (pending.Count > 0)
            {
                issues.Add(new ComponentIssue(pending[0].Line, pending[0].Col, pending[0].Col + 1, "AMP2225", Severity.Warning, "Attributes are only read above a component declaration (here nothing follows them).", "Put them directly above the component, or write a # comment."));
                pending.Clear();
                pendingStart = -1;
            }

            if (atStart && ImportPattern().Match(code) is { Success: true } import)
            {
                imports.Add(new ComponentImport(import.Groups[1].Value.Trim('"'), import.Groups[2].Success ? import.Groups[2].Value : null, i));
                blank.Add(i);
                continue;
            }

            if (atStart && ReexportPattern().Match(code) is { Success: true } reexport)
            {
                reexports.Add(new ComponentReexport(reexport.Groups[1].Value, reexport.Groups[2].Value, i));
                blank.Add(i);
            }
        }

        if (moduleEnd >= 0) modules.Add(new EmbeddedModule(moduleName, moduleId, moduleVersion, modulePayload, moduleStart, moduleEnd, [.. definitions.Skip(firstOfModule).Select(d => d.DeclaredName)]));
        return new ComponentFile(definitions, imports, reexports, issues, blank) { Modules = modules };
    }

    private static (ComponentDefinition? Definition, int End) ReadComponent(IReadOnlyList<string> lines, IReadOnlyDictionary<int, DocComment> docs, int header, int start, List<ComponentAnnotation> attributes, string origin, List<ComponentIssue> issues)
    {
        // the header runs up to the first brace (it may go over several lines: the name, then "version ...", then the brace)
        int openLine = -1, openCol = -1, depth = 0;
        for (int i = header; i < lines.Count && openLine < 0; i++)
        {
            depth = Depth(lines[i], 0, out int first);
            if (first >= 0) { openLine = i; openCol = first; }
        }

        if (openLine < 0)
        {
            issues.Add(new ComponentIssue(header, 0, lines[header].Length, "AMP2200", Severity.Error, "This component declaration has no body: expected component <name> version <x.y.z> { ... }.", "Open the body with a brace."));
            return (null, header);
        }

        int end = openLine;
        depth = Depth(lines[openLine][openCol..], 0, out _);
        while (depth > 0 && end + 1 < lines.Count) { end++; depth = Depth(lines[end], depth, out _); }
        if (depth > 0)
        {
            issues.Add(new ComponentIssue(header, 0, lines[header].Length, "AMP2200", Severity.Error, "The body of this component is not closed (missing \"}\").", "Every { needs its }."));
        }

        StringBuilder headText = new();
        for (int i = header; i <= openLine; i++) headText.Append(i == openLine ? StripComment(lines[i])[..Math.Min(StripComment(lines[i]).Length, openCol)] : StripComment(lines[i])).Append(' ');
        string headline = headText.ToString().Trim();
        Match hm = HeaderPattern().Match(headline);
        string name = hm.Groups[1].Value;
        string rest = hm.Groups[2].Value.Trim();
        string? versionText = null;
        bool current = false;
        string? id = null;
        string[] words = [.. Regex.Matches(rest, @"""[^""]*""|\S+", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Value)];
        for (int w = 0; w < words.Length; w++)
        {
            if (words[w] == "version" && w + 1 < words.Length) versionText = words[++w].Trim('"');
            else if (words[w] == "current") current = true;
            else if (words[w] == "id" && w + 1 < words.Length) id = words[++w].Trim('"');
            else issues.Add(new ComponentIssue(header, 0, lines[header].Length, "AMP2200", Severity.Error, $"Unexpected \"{words[w]}\" in the header of the component (expected version <x.y.z>, current or id \"...\").", string.Empty));
        }

        List<ComponentPort> inputs = [], outputs = [];
        List<ComponentProperty> properties = [];
        List<string> requires = [];
        List<string> processBody = [];
        int processLine = -1;
        string? ui = null;
        ComponentUi? uiTree = null;
        int uiLine = -1, uiEnd = -1;
        ComponentDeprecation? deprecation = null;
        List<ComponentMigration> migrations = [];
        ComponentSeal? seal = null;
        List<ComponentAnnotation> pendingAttrs = [];
        int bodyDepth = 1;
        for (int i = openLine; i <= end; i++)
        {
            string raw = i == openLine ? lines[i][(openCol + 1)..] : lines[i];
            int baseCol = i == openLine ? openCol + 1 : 0;
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (i == end && trimmed == "}") break;
            if (IsAttributeLine(trimmed))
            {
                pendingAttrs.AddRange(ReadAttributes(lines[i], i, issues));
                continue;
            }

            if (trimmed[0] == '#') continue;
            string code = StripComment(raw).Trim();
            if (code.Length == 0) continue;
            // a statement: one line, or a line that opens a brace and runs to the matching one
            int stmtEnd = i;
            int d = Depth(raw, 0, out _);
            while (d > 0 && stmtEnd + 1 <= end) { stmtEnd++; d = Depth(lines[stmtEnd], d, out _); }
            if (i == end) code = code.TrimEnd('}').Trim();
            string word = Regex.Match(code, @"^[A-Za-z_][\w-]*", RegexOptions.None, TimeSpan.FromSeconds(1)).Value;
            DocComment? doc = docs.GetValueOrDefault(i);
            switch (word)
            {
                case "id" when Regex.Match(code, @"^id\s+""([^""]+)""\s*$", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } idm:
                    id = idm.Groups[1].Value;
                    break;
                case "version":
                    versionText = code[7..].Trim().Trim('"');
                    break;
                case "current":
                    current = true;
                    break;
                case "requires":
                    requires.AddRange(code[8..].Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "input" or "output":
                    {
                        Match pm = PortPattern().Match(JoinLines(lines, i, stmtEnd, raw));
                        if (!pm.Success)
                        {
                            issues.Add(new ComponentIssue(i, baseCol, lines[i].Length, "AMP2200", Severity.Error, $"Expected: {word} <name>: <Type> (for example {word} source: Notes).", string.Empty));
                            break;
                        }

                        string options = pm.Groups[4].Success ? pm.Groups[4].Value : string.Empty;
                        bool optional = Regex.IsMatch(options, @"\boptional(?!\s+false)\b", RegexOptions.None, TimeSpan.FromSeconds(1));
                        string? fallback = Regex.Match(options, @"\bfallback\s+([A-Za-z_][\w-]*)", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } fm ? fm.Groups[1].Value : null;
                        ComponentPort port = new(pm.Groups[2].Value, pm.Groups[3].Value.Trim(), optional, fallback, i, [.. pendingAttrs], doc);
                        (word == "input" ? inputs : outputs).Add(port);
                        break;
                    }

                case "property":
                    {
                        Match pm = PropertyPattern().Match(JoinLines(lines, i, stmtEnd, raw));
                        if (!pm.Success)
                        {
                            issues.Add(new ComponentIssue(i, baseCol, lines[i].Length, "AMP2200", Severity.Error, "Expected: property <name>: <Type> [= <default>] (for example property copies: Int = 3).", string.Empty));
                            break;
                        }

                        string options = pm.Groups[4].Success ? pm.Groups[4].Value : string.Empty;
                        string? editor = Regex.Match(options, @"\beditor\s+([A-Za-z_][\w-]*)", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } em ? em.Groups[1].Value : null;
                        string? def = pm.Groups[3].Success && pm.Groups[3].Value.Trim().Length > 0 ? pm.Groups[3].Value.Trim() : null;
                        properties.Add(new ComponentProperty(pm.Groups[1].Value, pm.Groups[2].Value.Trim(), def, editor, i, [.. pendingAttrs], doc));
                        break;
                    }

                case "process":
                    {
                        int open = code.IndexOf('{', StringComparison.Ordinal);
                        if (open < 0) { issues.Add(new ComponentIssue(i, baseCol, lines[i].Length, "AMP2200", Severity.Error, "Expected: process { ... }.", string.Empty)); break; }
                        processLine = i;
                        processBody = BodyLines(lines, i, stmtEnd);
                        break;
                    }

                case "ui":
                    ui = string.Join('\n', BodyLines(lines, i, stmtEnd));
                    uiTree = ComponentUiReader.Read(lines, i, stmtEnd, issues);
                    uiLine = i;
                    uiEnd = stmtEnd;
                    break;
                case "deprecated":
                    {
                        string body = string.Join('\n', BodyLines(lines, i, stmtEnd));
                        string message = Regex.Match(body, @"\bmessage\s+""((?:[^""\\]|\\.)*)""", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } mm ? mm.Groups[1].Value : string.Empty;
                        string? replacement = Regex.Match(body, @"\breplacement\s+(\S+)", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } rm ? rm.Groups[1].Value : null;
                        deprecation = new ComponentDeprecation(message, replacement, i);
                        break;
                    }

                case "migrate":
                    {
                        Match mm = Regex.Match(code, @"^migrate\s+(?:(\S+)\s*->\s*|to\s+)(\S+?)\s*\{?$", RegexOptions.None, TimeSpan.FromSeconds(1));
                        if (!mm.Success) { issues.Add(new ComponentIssue(i, baseCol, lines[i].Length, "AMP2221", Severity.Error, "Expected: migrate <from> -> <to> { ... } (or migrate to <to> { ... }).", "For example: migrate 3.2.0 -> 4.0.0 {")); break; }
                        List<(int Line, string Text)> bodyLines = [];
                        for (int b = i + 1; b < stmtEnd; b++) bodyLines.Add((b, lines[b]));
                        string bodyText = string.Join('\n', bodyLines.Select(l => l.Text));
                        string impact = Regex.Match(bodyText, @"^\s*impact\s+(\w+)", RegexOptions.Multiline, TimeSpan.FromSeconds(1)) is { Success: true } im ? im.Groups[1].Value : string.Empty;
                        string message = Regex.Match(bodyText, @"^\s*message\s+""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline, TimeSpan.FromSeconds(1)) is { Success: true } msg ? msg.Groups[1].Value : string.Empty;
                        migrations.Add(new ComponentMigration(mm.Groups[1].Success ? mm.Groups[1].Value : null, mm.Groups[2].Value, impact, message, bodyLines, i));
                        break;
                    }

                case "seal":
                    {
                        string body = string.Join('\n', BodyLines(lines, i, stmtEnd));
                        string? Field(string key) => Regex.Match(body, @"\b" + key + @"\s+""?([^\s"";]+)""?", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } fm ? fm.Groups[1].Value : null;
                        string? signedBy = Regex.Match(body, @"\bsigned-by\s+""([^""]*)""", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } sm ? sm.Groups[1].Value : Field("signed-by");
                        seal = new ComponentSeal(Field("payload") ?? string.Empty, Field("semantic") ?? string.Empty, signedBy, Field("key") ?? Field("public-key"), Field("signature"), i, stmtEnd);
                        break;
                    }

                default:
                    issues.Add(new ComponentIssue(i, baseCol + (raw.Length - raw.TrimStart().Length), baseCol + raw.TrimEnd().Length, "AMP2200", Severity.Error, $"Unexpected \"{code}\" in a component (expected input, output, property, process, ui, requires, deprecated, migrate or seal).", string.Empty));
                    break;
            }

            if (word is "input" or "output" or "property" or "process" or "ui" or "deprecated" or "migrate" or "seal") pendingAttrs.Clear();
            i = stmtEnd;
            _ = bodyDepth;
        }

        if (versionText is null)
        {
            issues.Add(new ComponentIssue(header, 0, lines[header].Length, "AMP2200", Severity.Error, "A component needs a version: component <name> version <x.y.z>.", "For example: component koda.notes.tripleNotes version 1.0.0 {"));
            versionText = "0.0.0";
        }

        if (!SemVer.TryParse(versionText, out SemVer version))
        {
            issues.Add(new ComponentIssue(header, 0, lines[header].Length, "AMP2223", Severity.Error, $"\"{versionText}\" is not a version (1.2.3, 1.2 or 4.0.0-beta.1).", string.Empty));
            version = new SemVer(0, 0, 0, []);
        }

        StringBuilder text = new();
        for (int i = start; i <= end; i++) text.Append(lines[i]).Append('\n');
        ComponentDefinition definition = new()
        {
            Name = name,
            Id = id ?? "local:" + name,
            IdWritten = id is not null,
            Version = version,
            Current = current,
            Inputs = inputs,
            Outputs = outputs,
            Properties = properties,
            Attributes = attributes,
            Requires = requires,
            ProcessBody = processBody,
            ProcessLine = processLine,
            Ui = ui,
            UiTree = uiTree,
            UiLine = uiLine,
            UiEndLine = uiEnd,
            Deprecation = deprecation,
            Migrations = migrations,
            Seal = seal,
            Doc = docs.GetValueOrDefault(header) ?? docs.GetValueOrDefault(start),
            Origin = origin,
            StartLine = start,
            HeaderLine = header,
            EndLine = end,
            Text = text.ToString(),
        };
        return (definition, end);
    }

    private static string JoinLines(IReadOnlyList<string> lines, int from, int to, string first)
    {
        if (to == from) return StripComment(first).Trim();
        StringBuilder sb = new(StripComment(first).Trim());
        for (int i = from + 1; i <= to; i++) sb.Append(' ').Append(StripComment(lines[i]).Trim());
        return sb.ToString().Trim();
    }

    /// <summary>The lines between the braces of a statement (the lines after the opening line up to the line before the closing one), or the text between the braces of a one-line statement.</summary>
    private static List<string> BodyLines(IReadOnlyList<string> lines, int from, int to)
    {
        if (to == from)
        {
            string one = lines[from];
            int open = one.IndexOf('{', StringComparison.Ordinal), close = one.LastIndexOf('}');
            return open >= 0 && close > open ? [one[(open + 1)..close]] : [];
        }

        List<string> body = [];
        string first = lines[from];
        int firstOpen = first.IndexOf('{', StringComparison.Ordinal);
        if (firstOpen >= 0 && first[(firstOpen + 1)..].Trim().Length > 0) body.Add(first[(firstOpen + 1)..]);
        for (int i = from + 1; i < to; i++) body.Add(lines[i]);
        string last = lines[to];
        int lastClose = last.LastIndexOf('}');
        if (lastClose > 0 && last[..lastClose].Trim().Length > 0) body.Add(last[..lastClose]);
        return body;
    }
}
