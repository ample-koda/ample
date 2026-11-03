namespace Ample.Language;

/// <summary>How serious a diagnostic is.</summary>
public enum Severity
{
    /// <summary>Blocks apply.</summary>
    Error,

    /// <summary>Does not block.</summary>
    Warning,

    /// <summary>Informational.</summary>
    Info,
}

/// <summary>A suggested text change tied to a diagnostic.</summary>
/// <param name="Label">What the fix does.</param>
/// <param name="Kind"><c>replace</c>, <c>append</c> or <c>insert-top</c>.</param>
/// <param name="Text">Replacement or inserted text.</param>
/// <param name="Line">1-based line of a replacement.</param>
/// <param name="Col">1-based start column of a replacement.</param>
/// <param name="EndCol">1-based exclusive end column of a replacement.</param>
/// <param name="Risky">True when the fix changes what the song sounds like.</param>
/// <param name="EndLine">1-based line where the replacement ends (<c>EndCol</c> counts on that line); 0 means the same line as <c>Line</c>.</param>
public sealed record Fix(string Label, string Kind, string Text, int Line, int Col, int EndCol, bool Risky, int EndLine = 0);

/// <summary>Another place that matters for a diagnostic.</summary>
/// <param name="Line">1-based line.</param>
/// <param name="Message">Why it matters.</param>
public sealed record Related(int Line, string Message);

/// <summary>One diagnostic: stable code, exact range, message, optional hint, related place and quick fix.</summary>
/// <param name="Code">Stable code such as <c>AMP2003</c> or <c>L001</c>.</param>
/// <param name="Severity">Severity.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Col">1-based start column (code points).</param>
/// <param name="EndCol">1-based exclusive end column (code points).</param>
/// <param name="Message">One plain sentence.</param>
/// <param name="Hint">What to do, may be empty.</param>
/// <param name="Fix">Quick fix, if any.</param>
/// <param name="Related">Related place, if any.</param>
public sealed record Diagnostic(string Code, Severity Severity, int Line, int Col, int EndCol, string Message, string Hint, Fix? Fix, Related? Related);

/// <summary>A placed part of a block on the timeline.</summary>
/// <param name="At">First bar (1-based).</param>
/// <param name="Length">Length in bars.</param>
/// <param name="Name">Optional part name.</param>
public sealed record Part(int At, int Length, string Name)
{
    /// <summary>Gets whether the length was written as <c>auto</c> (the length of the notes of the clip, in whole bars).</summary>
    public bool Auto { get; init; }
}

/// <summary>A <c>name value</c> line inside a block.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Value">Raw value text.</param>
/// <param name="Line">0-based line index.</param>
public sealed record Parameter(string Name, string Value, int Line)
{
    /// <summary>Gets the last source line of a parameter that spans several lines.</summary>
    public int EndLine { get; init; } = Line;

    /// <summary>Gets the column (0-based) where the value starts on its first line; only set for a note expression, whose text can span several lines.</summary>
    public int ValueColumn { get; init; }

    /// <summary>Gets the value as a number when it is one.</summary>
    public double? Number => double.TryParse(Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && NumberLike(Value) ? d : null;

    private static bool NumberLike(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "^-?\\d+(\\.\\d+)?$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
}

/// <summary>A <c>from block.output -&gt; input</c> line.</summary>
/// <param name="Block">Source block name (unquoted).</param>
/// <param name="Output">Source output name.</param>
/// <param name="Input">Own input name.</param>
/// <param name="Line">0-based line index.</param>
/// <param name="RawBlock">Source block as written.</param>
/// <param name="RawOutput">Output as written.</param>
/// <param name="RawInput">Input as written.</param>
public sealed record FromLine(string Block, string Output, string Input, int Line, string RawBlock, string RawOutput, string RawInput)
{
    /// <summary>Gets whether the connection is retained but temporarily inactive.</summary>
    public bool Disabled { get; init; }
}

/// <summary>A block: header, parameters and connections.</summary>
public sealed class BlockNode
{
    /// <summary>Gets or sets the block type (first header word).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the unquoted block name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the block name as written.</summary>
    public string RawName { get; set; } = string.Empty;

    /// <summary>Gets or sets the class (<c>stable</c>, <c>pinned</c>, <c>live</c>) or empty when the header was malformed.</summary>
    public string Class { get; set; } = string.Empty;

    /// <summary>Gets or sets the version text.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets the placed parts.</summary>
    public List<Part> Parts { get; } = [];

    /// <summary>Gets the flags (<c>bake</c>, <c>lock</c>, <c>refresh</c>).</summary>
    public List<string> Flags { get; } = [];

    /// <summary>Gets or sets the freeze level (<c>seeds</c> or <c>plan</c>) or null.</summary>
    public string? Freeze { get; set; }

    /// <summary>Gets the parameters in source order.</summary>
    public List<Parameter> Parameters { get; } = [];

    /// <summary>Gets the connections in source order.</summary>
    public List<FromLine> Froms { get; } = [];

    /// <summary>Gets the controls the parameters of this block read in a control expression (<c>cutoff 600 + 3000 * traffic.level</c>): dependencies like connections.</summary>
    public List<ControlRef> ControlRefs { get; } = [];

    /// <summary>The connections of the block and, as connections too, the controls its parameters read (the input is then <c>~block.output</c>): everything this block needs before it can be built.</summary>
    public IEnumerable<FromLine> Dependencies() => Froms.Concat(ControlRefs.Select(c => new FromLine(c.Block, c.Output, "~" + c.Block + "." + c.Output, c.Line, c.Block, c.Output, c.Output)));

    /// <summary>Gets or sets the 0-based header line.</summary>
    public int Line { get; set; }

    /// <summary>Gets or sets the 0-based line of the closing brace, or -1 when the block is not closed.</summary>
    public int CloseLine { get; set; } = -1;

    /// <summary>Gets or sets a value indicating whether the header was too broken to keep the block in semantic checks.</summary>
    public bool Skipped { get; set; }
}

/// <summary>A <c>use &lt;module&gt;[@version] as &lt;name&gt; { ... }</c> statement: an instance of a reusable module. Its body gives the module's parameters (<c>name value</c>) and binds its inputs (<c>from block.output -&gt; input</c>).</summary>
public sealed class UseNode
{
    /// <summary>Gets or sets the unquoted module name.</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the pinned version (the text after <c>@</c>), or empty.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the unquoted instance name.</summary>
    public string Instance { get; set; } = string.Empty;

    /// <summary>Gets or sets the instance name as written.</summary>
    public string RawInstance { get; set; } = string.Empty;

    /// <summary>Gets the parameter values in source order.</summary>
    public List<Parameter> Parameters { get; } = [];

    /// <summary>Gets the input bindings in source order (<c>Input</c> is the module's input name).</summary>
    public List<FromLine> Froms { get; } = [];

    /// <summary>Gets or sets the 0-based header line.</summary>
    public int Line { get; set; }

    /// <summary>Gets or sets the 0-based line of the closing brace, or -1.</summary>
    public int CloseLine { get; set; } = -1;
}

/// <summary>A block property whose value is an expression (a helper call, a bake, a property of another block): what it resolves to and in which state it is.</summary>
/// <param name="Block">The block.</param>
/// <param name="Name">The property.</param>
/// <param name="Expression">The expression as written.</param>
/// <param name="Value">What it resolves to as the block takes it (a number in the unit of the template), or null on errors.</param>
/// <param name="State">expression, baked, override, stale or dirty.</param>
/// <param name="Dynamic">Whether a volatile value took part.</param>
/// <param name="BakedText">The baked value as written (baked, override, stale, dirty), or null.</param>
/// <param name="SourceText">What the expression resolves to now (baked states), or null.</param>
/// <param name="Line">The 0-based line of the property.</param>
public sealed record PropertyBinding(string Block, string Name, string Expression, string? Value, string State, bool Dynamic, string? BakedText, string? SourceText, int Line)
{
    /// <summary>Gets the recipe hash of the expression, the signature of a value bake.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Gets the expression without the bake around it.</summary>
    public string ExpressionText { get; init; } = string.Empty;
}

/// <summary>The project line.</summary>
/// <param name="Name">Project name.</param>
/// <param name="Tempo">Tempo.</param>
/// <param name="MasterSeed">Master seed.</param>
/// <param name="Line">0-based line.</param>
/// <param name="Ample">The AMPLE version the song says it was written for (<c>ample N</c>), or null when it says nothing (read as the current one).</param>
/// <param name="MeterNumerator">The top number of the meter (beats per bar), 1 to 16; 4 without a meter.</param>
/// <param name="MeterDenominator">The bottom number of the meter (the note that gets a beat): 2, 4, 8 or 16; 4 without a meter.</param>
public sealed record ProjectInfo(string Name, double Tempo, ulong MasterSeed, int Line, int? Ample = null, int MeterNumerator = 4, int MeterDenominator = 4)
{
    /// <summary>Gets the length of a bar in quarter notes: 4 for 4/4, 3 for 3/4 or 6/8, 5 for 5/4, 3.5 for 7/8.</summary>
    public double BeatsPerBar => 4.0 * MeterNumerator / MeterDenominator;
}

/// <summary>A parsed and analysed song file. The text is kept verbatim, so the tree is lossless.</summary>
public sealed class AmpleDocument
{
    /// <summary>Gets the original text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the lines of the text without line endings.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>Gets the project line, or null.</summary>
    public ProjectInfo? Project { get; init; }

    /// <summary>Gets the module switches from the features line.</summary>
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    /// <summary>Gets the assist level, or null.</summary>
    public string? Assist { get; init; }

    /// <summary>Gets the lint level overrides from the lint line.</summary>
    public IReadOnlyDictionary<string, string> Lint { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the blocks that were recognised (including blocks with semantic errors).</summary>
    public IReadOnlyList<BlockNode> Blocks { get; init; } = [];

    /// <summary>Gets the module instances (<c>use</c> statements).</summary>
    public IReadOnlyList<UseNode> Uses { get; init; } = [];

    /// <summary>Gets the layout positions by block name.</summary>
    public IReadOnlyDictionary<string, (int X, int Y)> Layout { get; init; } = new Dictionary<string, (int, int)>();

    /// <summary>Gets the plugin settings of this song from the settings line, by <c>plugin.name</c> (the text as written).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the helpers of the song as the evaluator uses them (for tools that evaluate pipelines: bake, provenance).</summary>
    public NotePipeline.HelperTable HelperTable { get; init; } = NotePipeline.HelperTable.Empty;

    /// <summary>Gets the instant currentTime() read for this parse (one instant for the whole song).</summary>
    public DateTime ParsedAt { get; init; } = DateTime.Now;

    /// <summary>Gets the effective capability policy of the song.</summary>
    public CapabilityPolicy CapabilityPolicy { get; init; } = CapabilityPolicy.AllowAll;

    /// <summary>Gets the capability settings of the song in source order: name, value, 0-based line.</summary>
    public IReadOnlyList<(string Name, string Value, int Line)> CapabilitySettings { get; init; } = [];

    /// <summary>Gets the effective state of every capability and the scope that decided it (docs/language/16).</summary>
    public IReadOnlyList<CapabilityState> Capabilities { get; init; } = [];

    /// <summary>Gets the effective limits of helper code and where each comes from (default, song, server-cap).</summary>
    public IReadOnlyList<(string Key, long Effective, string Source, long Cap)> Limits { get; init; } = [];

    /// <summary>Gets the block properties that are expressions, with what they resolve to.</summary>
    public IReadOnlyList<PropertyBinding> Properties { get; init; } = [];

    /// <summary>Gets whether the song is reproducible: stable, dynamic (an unbaked volatile value takes part), baked-dynamic (every volatile value is baked and fresh) or stale (a bake no longer matches).</summary>
    public string Stability { get; init; } = "stable";

    /// <summary>Gets why the song is not stable: the volatile values that were read, one line each.</summary>
    public IReadOnlyList<string> StabilityNotes { get; init; } = [];

    /// <summary>Gets the helpers, type aliases and constants declared in the file (any order).</summary>
    public IReadOnlyList<HelperInfo> Helpers { get; init; } = [];

    /// <summary>Gets the components declared in this text (not the imported ones).</summary>
    public IReadOnlyList<ComponentDefinition> Components { get; init; } = [];

    /// <summary>Gets the <c>import</c> lines of this text.</summary>
    public IReadOnlyList<ComponentImport> ComponentImports { get; init; } = [];

    /// <summary>Gets the <c>reexport</c> lines of this text.</summary>
    public IReadOnlyList<ComponentReexport> ComponentReexports { get; init; } = [];

    /// <summary>Gets the components that came from imported files (the editors know them as block types); their <c>Origin</c> is the file.</summary>
    public IReadOnlyList<ComponentDefinition> ImportedComponents { get; init; } = [];

    /// <summary>Gets the modules embedded in the song (<c>embedded module name { ... }</c>).</summary>
    public IReadOnlyList<EmbeddedModule> EmbeddedModules { get; init; } = [];

    /// <summary>Gets the language data this text was read with: the spec plus the templates of the components it uses (null: the plain spec).</summary>
    public SpecData? Spec { get; init; }

    /// <summary>Gets the doc comments (<c>/** */</c> or <c>##</c> lines) by the 0-based line of the declaration or block header under them.</summary>
    public IReadOnlyDictionary<int, DocComment> Docs { get; init; } = new Dictionary<int, DocComment>();

    /// <summary>Gets the 0-based line of the settings line, or -1 when the song has none.</summary>
    public int SettingsLine { get; init; } = -1;

    /// <summary>Gets all diagnostics sorted by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>Returns the text of the tree; always equal to <see cref="Text"/> (the tree is lossless).</summary>
    public string ToText() => Text;
}
