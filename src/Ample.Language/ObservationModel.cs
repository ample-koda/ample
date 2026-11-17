using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>The levels of logging, from nothing to everything (plan/diagnostics/PLAN.md, D3). A higher level shows the lower ones too; errors and warnings never depend on it.</summary>
public enum LogLevel
{
    /// <summary>Ordinary logs are hidden (warnings and errors stay available as diagnostics).</summary>
    Off = 0,

    /// <summary>Warnings.</summary>
    Warning = 1,

    /// <summary>log.</summary>
    Info = 2,

    /// <summary>debug.</summary>
    Debug = 3,

    /// <summary>trace.</summary>
    Trace = 4,
}

/// <summary>The level of one event.</summary>
public enum EventLevel
{
    /// <summary>trace.</summary>
    Trace,

    /// <summary>debug.</summary>
    Debug,

    /// <summary>log.</summary>
    Info,

    /// <summary>warn.</summary>
    Warning,

    /// <summary>A failure (fail, an uncaught throw, a failed assert or require).</summary>
    Error,
}

/// <summary>The budget class of an event: ordinary logs and trace can never evict warnings and errors (plan R1.2).</summary>
public enum EventClass
{
    /// <summary>Warnings and errors.</summary>
    Diagnostic,

    /// <summary>log and debug.</summary>
    Log,

    /// <summary>trace.</summary>
    Trace,
}

/// <summary>A place in a source file.</summary>
/// <param name="Origin">The file (or <c>song</c>, <c>embedded:name</c>, <c>component:name</c>).</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Col">The 1-based column where it starts.</param>
/// <param name="EndCol">The 1-based column where it ends (on the same line).</param>
public sealed record SourceSite(string Origin, int Line, int Col, int EndCol)
{
    /// <summary>Gets the text <c>file:line:col</c>.</summary>
    public override string ToString() => $"{Origin}:{Line}:{Col}";
}

/// <summary>One frame of the AMPLE call stack: a helper (or the clip or property expression) and the place it was called from. Never holds argument values.</summary>
/// <param name="Name">The helper, or the expression it stands for.</param>
/// <param name="Site">Where the call stands, or null.</param>
/// <param name="Instance">The component instance path, or null.</param>
public sealed record StackFrameInfo(string Name, SourceSite? Site, string? Instance);

/// <summary>What one event says, with the value tree already bounded and redacted (see <see cref="ObsRedactor"/>).</summary>
/// <param name="Seq">The order of registration in the session (deterministic).</param>
/// <param name="Level">The level.</param>
/// <param name="SiteKey">Which statement made it (<see cref="ObservationSink.SiteKeyOf"/>).</param>
/// <param name="Site">Where it stands.</param>
/// <param name="Code">The event code the author chose, or null (never an AMPxxxx code).</param>
/// <param name="Message">The message, redacted and cut.</param>
/// <param name="Hint">The hint, or null.</param>
/// <param name="ContextJson">The context record as canonical JSON (bounded, redacted), or null.</param>
/// <param name="Stack">The call stack (bounded), or empty.</param>
/// <param name="Volatile">Whether the content came from a clock read in the observer: such an event is not deterministic.</param>
/// <param name="Child">Whether it is a secondary failure that belongs to another event (shown under it, not as its own diagnostic).</param>
public sealed record LogEvent(
    long Seq, EventLevel Level, string SiteKey, SourceSite? Site, string? Code, string Message, string? Hint, string? ContextJson, IReadOnlyList<StackFrameInfo> Stack, bool Volatile = false, bool Child = false)
{
    /// <summary>Gets the budget class.</summary>
    public EventClass Class => ObservationSink.ClassOf(Level);
}

/// <summary>A value as the observation channel sees it: a small tree that can be bounded, redacted and written as JSON, whatever the interpreter value it came from.</summary>
public abstract record ObsValue;

/// <summary>Nothing.</summary>
public sealed record ObsNull : ObsValue;

/// <summary>A flag.</summary>
/// <param name="Value">The flag.</param>
public sealed record ObsBool(bool Value) : ObsValue;

/// <summary>A number or a quantity, kept as its exact text (<c>3</c>, <c>0.25</c>, <c>1bar</c>).</summary>
/// <param name="Text">The text.</param>
public sealed record ObsNumber(string Text) : ObsValue;

/// <summary>A text.</summary>
/// <param name="Value">The text.</param>
/// <param name="Sensitive">Whether the value is sensitive (it came from environment(), or from a value that is): it is never written.</param>
public sealed record ObsText(string Value, bool Sensitive = false) : ObsValue;

/// <summary>A list.</summary>
/// <param name="Items">The items.</param>
public sealed record ObsList(IReadOnlyList<ObsValue> Items) : ObsValue;

/// <summary>A record: fields in the order the author wrote them.</summary>
/// <param name="Fields">The fields.</param>
public sealed record ObsRecord(IReadOnlyList<(string Key, ObsValue Value)> Fields) : ObsValue;

/// <summary>The caps of one value written to an event (plan D5, S5).</summary>
/// <param name="MaxDepth">How deep records and lists may nest.</param>
/// <param name="MaxItems">How many fields or items one record or list shows.</param>
/// <param name="MaxText">How long one text is.</param>
/// <param name="MaxBytes">How long the whole JSON is.</param>
public sealed record ObsLimits(int MaxDepth = 8, int MaxItems = 64, int MaxText = 256, int MaxBytes = 4096);

/// <summary>
/// Redaction and bounding of the values of an event, applied while the event is made and never later (plan S1-S5): a field whose name says it holds a secret is replaced, a sensitive value is replaced, and what is
/// too deep, too long or too big is cut with a visible mark. The same value always gives the same text.
/// </summary>
public static partial class ObsRedactor
{
    /// <summary>The text that stands for a value that must not be shown.</summary>
    public const string Redacted = "[redacted]";

    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "token", "secret", "password", "passwd", "credential", "credentials", "apikey", "authorization", "cookie", "session", "private", "signature",
    };

    [GeneratedRegex("[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WordPart();

    /// <summary>Whether a field name says that its value is a secret: one of the words <c>token secret password passwd credential apikey authorization cookie session private signature</c>, or <c>api</c> next to <c>key</c>. A plain <c>key</c> is a musical key and is not a secret.</summary>
    /// <param name="name">The field name (camelCase, kebab-case or snake_case).</param>
    /// <returns>True when the value must be redacted.</returns>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string[] words = [.. WordPart().Matches(name).Select(m => m.Value.ToLowerInvariant())];
        for (int i = 0; i < words.Length; i++)
        {
            if (SecretWords.Contains(words[i])) return true;
            if (words[i] == "api" && i + 1 < words.Length && words[i + 1] == "key") return true;
        }

        return false;
    }

    /// <summary>The context of an event as JSON for a structured output (it is written by <see cref="ToJson"/>, so it is always valid), or null.</summary>
    /// <param name="json">The context JSON, or null.</param>
    /// <returns>The JSON element.</returns>
    public static System.Text.Json.JsonElement? ParseContext(string? json)
    {
        if (json is null) return null;
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Writes a value as canonical JSON with the caps applied and secrets replaced.</summary>
    /// <param name="value">The value.</param>
    /// <param name="limits">The caps.</param>
    /// <returns>The JSON text (never longer than <see cref="ObsLimits.MaxBytes"/> plus a mark).</returns>
    public static string ToJson(ObsValue value, ObsLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ObsLimits l = limits ?? new ObsLimits();
        StringBuilder sb = new();
        Write(sb, value, l, 1);
        if (Encoding.UTF8.GetByteCount(sb.ToString()) <= l.MaxBytes) return sb.ToString();
        // too big: what fits (as the text it then is, so every quote in it counts twice) and a mark, always valid JSON
        string cut = sb.ToString();
        string text = JsonText(cut + "…(cut)");
        while (cut.Length > 0 && Encoding.UTF8.GetByteCount(text) > l.MaxBytes)
        {
            cut = cut[..Math.Max(0, cut.Length - Math.Max(1, (Encoding.UTF8.GetByteCount(text) - l.MaxBytes) / 2))];
            text = JsonText(cut + "…(cut)");
        }

        return text;
    }

    /// <summary>Cuts a text to the cap, with a mark when something was left out.</summary>
    /// <param name="text">The text.</param>
    /// <param name="max">The cap.</param>
    /// <returns>The text, or its start and a mark.</returns>
    public static string Cut(string text, int max = 256)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string JsonText(string text)
    {
        StringBuilder sb = new("\"");
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c < ' ' ? $"\\u{(int)c:x4}" : c.ToString()); break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static void Write(StringBuilder sb, ObsValue value, ObsLimits limits, int depth)
    {
        switch (value)
        {
            case ObsNull: sb.Append("null"); break;
            case ObsBool b: sb.Append(b.Value ? "true" : "false"); break;
            case ObsNumber n: sb.Append(n.Text.All(c => char.IsDigit(c) || c is '-' or '.') && n.Text.Length > 0 ? n.Text : JsonText(n.Text)); break;
            case ObsText t: sb.Append(JsonText(t.Sensitive ? Redacted : Cut(t.Value, limits.MaxText))); break;
            case ObsList list:
                if (depth > limits.MaxDepth) { sb.Append(JsonText("…(too deep)")); break; }
                sb.Append('[');
                for (int i = 0; i < list.Items.Count && i < limits.MaxItems; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, list.Items[i], limits, depth + 1);
                }

                if (list.Items.Count > limits.MaxItems) sb.Append(',').Append(JsonText($"…(+{list.Items.Count - limits.MaxItems} more)"));
                sb.Append(']');
                break;
            case ObsRecord record:
                if (depth > limits.MaxDepth) { sb.Append(JsonText("…(too deep)")); break; }
                sb.Append('{');
                for (int i = 0; i < record.Fields.Count && i < limits.MaxItems; i++)
                {
                    if (i > 0) sb.Append(',');
                    (string key, ObsValue field) = record.Fields[i];
                    sb.Append(JsonText(Cut(key, limits.MaxText))).Append(':');
                    if (IsSecretName(key)) sb.Append(JsonText(Redacted));
                    else Write(sb, field, limits, depth + 1);
                }

                if (record.Fields.Count > limits.MaxItems) sb.Append(',').Append(JsonText("…")).Append(':').Append(JsonText($"+{record.Fields.Count - limits.MaxItems} more"));
                sb.Append('}');
                break;
            default: sb.Append("null"); break;
        }
    }
}

/// <summary>
/// Turns an offset in the text of an expression or of a helper declaration into a place in a file. A node offset is relative to the text that was parsed (the plan's finding F6): the text of the expression of a clip, or the
/// declaration of a helper, which starts on some line of some file; and an expanded component song moves lines (<c>ComponentExpansion.LineOrigin</c>).
/// </summary>
public static class SiteResolver
{
    /// <summary>The place of a range of the text of a declaration or an expression.</summary>
    /// <param name="origin">The file.</param>
    /// <param name="text">The text the offsets belong to.</param>
    /// <param name="firstLine">The 0-based line of the file where that text starts.</param>
    /// <param name="start">The offset where the range starts.</param>
    /// <param name="end">The offset where it ends.</param>
    /// <returns>The site (1-based line and column).</returns>
    public static SourceSite At(string origin, string text, int firstLine, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(text);
        int s = Math.Clamp(start, 0, text.Length);
        int e = Math.Clamp(end, s, text.Length);
        int lines = 0, lineStart = 0;
        for (int i = 0; i < s; i++)
        {
            if (text[i] == '\n') { lines++; lineStart = i + 1; }
        }

        int lineEnd = text.IndexOf('\n', s);
        if (lineEnd < 0) lineEnd = text.Length;
        int col = s - lineStart + 1;
        int endCol = Math.Min(e, lineEnd) - lineStart + 1;
        return new SourceSite(origin, firstLine + lines + 1, col, Math.Max(col, endCol));
    }

    /// <summary>Maps a place in an expanded song to the place in the song as written.</summary>
    /// <param name="expanded">The place in the expanded text.</param>
    /// <param name="lineOrigin">For every 0-based line of the expanded text the 0-based line of the original.</param>
    /// <returns>The place in the original, or the same place when the line is not in the map.</returns>
    public static SourceSite Original(SourceSite expanded, IReadOnlyList<int> lineOrigin)
    {
        ArgumentNullException.ThrowIfNull(expanded);
        ArgumentNullException.ThrowIfNull(lineOrigin);
        int index = expanded.Line - 1;
        return index >= 0 && index < lineOrigin.Count ? expanded with { Line = lineOrigin[index] + 1 } : expanded;
    }
}
