using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Speech;

// This file is AMPLE's own; it is not translated from Flite. It sets features on the tokens that the duration model and us_f0_model of Flite already read (see FliteEngine.Markup.cs).

/// <summary>
/// The inline markup in the text of a <c>speech</c> block that speaks with the <c>flite</c> engine: <c>&lt;pitch 220&gt;word&lt;/pitch&gt;</c> (a pitch in Hz, or <c>+3st</c> semitones
/// above the pitch of the block), <c>&lt;rate 1.3&gt;…&lt;/rate&gt;</c> (a speed, 1 is the speed of the block), <c>&lt;range 25&gt;…&lt;/range&gt;</c> (how far the pitch moves, in Hz),
/// <c>&lt;emph&gt;…&lt;/emph&gt;</c> (a word that is stressed: higher and slower) and <c>&lt;break 300ms/&gt;</c> (a pause of exactly that length). The tags change the words
/// between them and never what is said; <c>&amp;lt;</c>, <c>&amp;gt;</c> and <c>&amp;amp;</c> write the characters themselves. A <c>&lt;</c> that is not followed by a letter
/// or a slash is an ordinary character (<c>5 &lt; 6</c>). Immutable.
/// </summary>
public sealed class FliteMarkup
{
    /// <summary>The most breaks one text may have.</summary>
    public const int MaxBreaks = 64;

    /// <summary>The longest break in milliseconds.</summary>
    public const int MaxBreakMilliseconds = 5000;

    /// <summary>The most tags that may be open at the same time.</summary>
    public const int MaxDepth = 16;

    /// <summary>The semitones that <c>&lt;emph&gt;</c> raises a word.</summary>
    public const double EmphasisSemitones = 3;

    /// <summary>The factor that <c>&lt;emph&gt;</c> makes a word longer.</summary>
    public const double EmphasisLength = 1.15;

    private static readonly Regex Tag = new(@"</?(pitch|rate|range|emph|break)(?![A-Za-z0-9])", RegexOptions.CultureInvariant);

    private readonly List<Span> spans;
    private readonly int[] byteOffsets;
    private readonly int[] sourceFrom;
    private readonly int[] sourceTo;

    private FliteMarkup(string source, string plain, List<Span> spans, List<MarkupBreak> breaks, int[] sourceFrom, int[] sourceTo)
    {
        Source = source;
        Plain = plain;
        this.spans = spans;
        Breaks = breaks;
        this.sourceFrom = sourceFrom;
        this.sourceTo = sourceTo;
        byteOffsets = new int[plain.Length + 1];
        for (int i = 0; i < plain.Length; i++) byteOffsets[i + 1] = byteOffsets[i] + Utf8Bytes(plain, i);
    }

    /// <summary>Gets the text as it was written, with the tags.</summary>
    public string Source { get; }

    /// <summary>Gets the text that is spoken: the source without its tags, with the entities replaced.</summary>
    public string Plain { get; }

    /// <summary>Gets a value indicating whether the source has any tag or entity (otherwise <see cref="Plain"/> is the source).</summary>
    public bool HasMarkup => !string.Equals(Source, Plain, StringComparison.Ordinal) || spans.Count > 0 || Breaks.Count > 0;

    /// <summary>Gets the breaks in the order of the text.</summary>
    public IReadOnlyList<MarkupBreak> Breaks { get; }

    /// <summary>Tells whether a text has a tag of the markup in it (so that another engine can refuse a text that was written for this one).</summary>
    /// <param name="text">The text of a block.</param>
    /// <returns>True when the text has the start of a tag of this markup.</returns>
    public static bool LooksLikeMarkup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('<', StringComparison.Ordinal) && Tag.IsMatch(text);
    }

    /// <summary>Reads a text with tags.</summary>
    /// <param name="text">The text of a block.</param>
    /// <param name="pitchHz">The pitch of the block in Hz: a semitone shift and an absolute pitch are made into a factor of it.</param>
    /// <returns>The plain text with the tags apart.</returns>
    /// <exception cref="ArgumentException">A tag is unknown, not closed, wrongly nested or has a wrong value; the message names the tag and says what is allowed.</exception>
    public static FliteMarkup Parse(string text, double pitchHz)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('<', StringComparison.Ordinal) && !text.Contains('&', StringComparison.Ordinal))
        {
            return new(text, text, [], [], Identity(text.Length, 0), Identity(text.Length, 1));
        }

        StringBuilder plain = new(text.Length);
        List<int> from = [], to = [];
        List<Span> spans = [];
        List<MarkupBreak> breaks = [];
        Stack<(string Name, int SpanIndex)> open = new();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '<' && i + 1 < text.Length && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] == '/'))
            {
                int end = text.IndexOf('>', i + 1);
                if (end < 0) throw new ArgumentException($"The tag at position {i + 1} of the text is not closed with '>'.");
                ReadTag(text[(i + 1)..end].Trim(), i, plain.Length, pitchHz, spans, breaks, open);
                i = end + 1;
                continue;
            }

            if (c == '&' && Entity(text, i, out char decoded, out int length))
            {
                from.Add(i);
                to.Add(i + length);
                plain.Append(decoded);
                i += length;
                continue;
            }

            from.Add(i);
            to.Add(i + 1);
            plain.Append(c);
            i++;
        }

        if (open.Count > 0) throw new ArgumentException($"The tag <{open.Peek().Name}> is not closed: write </{open.Peek().Name}> after the words it covers.");
        return new(text, plain.ToString(), spans, breaks, [.. from], [.. to]);
    }

    /// <summary>Gives the position in the source of a position in the plain text: the first character of the plain character, or the end of the source for the end of the text.</summary>
    /// <param name="plainIndex">An offset in <see cref="Plain"/>, from 0 to its length.</param>
    /// <returns>The offset in <see cref="Source"/>.</returns>
    public int SourceStart(int plainIndex) => plainIndex < sourceFrom.Length ? sourceFrom[Math.Max(0, plainIndex)] : sourceTo.Length == 0 ? 0 : sourceTo[^1];

    /// <summary>Gives the position in the source where the plain text ends at <paramref name="plainEnd"/>: the end of the last plain character before it (an entity counts whole).</summary>
    /// <param name="plainEnd">An offset in <see cref="Plain"/> that ends a word, from 0 to its length.</param>
    /// <returns>The offset in <see cref="Source"/> (exclusive).</returns>
    public int SourceEnd(int plainEnd) => plainEnd <= 0 ? 0 : sourceTo[Math.Min(plainEnd, sourceTo.Length) - 1];

    /// <summary>The features that the tags give a token: the factor of the duration (1 over the rate, as <c>local_duration_stretch</c>), the factor of the pitch (<c>local_f0_shift</c>) and the
    /// pitch range in Hz (<c>local_f0_range</c>). A value of 0 means that the tags do not touch it.</summary>
    /// <param name="byteOffset">Where the token starts in the UTF-8 bytes of <see cref="Plain"/>.</param>
    /// <param name="stretch">The factor of the duration, or 0.</param>
    /// <param name="shift">The factor of the pitch, or 0.</param>
    /// <param name="range">The pitch range in Hz, or 0.</param>
    /// <returns>True when a tag covers the token.</returns>
    public bool TokenFeatures(int byteOffset, out float stretch, out float shift, out float range)
    {
        stretch = shift = range = 0;
        if (spans.Count == 0) return false;
        int at = Array.BinarySearch(byteOffsets, byteOffset);
        if (at < 0) at = ~at - 1;
        at = Math.Clamp(at, 0, Math.Max(0, Plain.Length - 1));
        double lengthFactor = 1, pitchFactor = 0, rangeHz = 0, emphasis = 1;
        bool covered = false;
        foreach (Span span in spans)
        {
            if (at < span.From || at >= span.To) continue;
            covered = true;
            switch (span.Kind)
            {
                case Kind.Pitch: pitchFactor = span.Value; break;   // the innermost tag wins
                case Kind.Rate: lengthFactor *= 1 / span.Value; break;   // speeds multiply
                case Kind.Range: rangeHz = span.Value; break;
                case Kind.Emph: emphasis *= Math.Pow(2, EmphasisSemitones / 12); lengthFactor *= EmphasisLength; break;
            }
        }

        if (!covered) return false;
        stretch = lengthFactor == 1 ? 0 : (float)lengthFactor;
        double factor = (pitchFactor == 0 ? 1 : pitchFactor) * emphasis;
        shift = factor == 1 ? 0 : (float)factor;
        range = (float)rangeHz;
        return stretch != 0 || shift != 0 || range != 0;
    }

    /// <summary>The UTF-8 bytes that the UTF-16 unit at <paramref name="index"/> adds: a pair of surrogates is four bytes, counted on the first.</summary>
    private static int Utf8Bytes(string text, int index)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) return 4;
        if (char.IsLowSurrogate(c) && index > 0 && char.IsHighSurrogate(text[index - 1])) return 0;
        return Encoding.UTF8.GetByteCount(text.AsSpan(index, 1));
    }

    private static int[] Identity(int length, int add)
    {
        int[] map = new int[length];
        for (int i = 0; i < length; i++) map[i] = i + add;
        return map;
    }

    private static bool Entity(string text, int at, out char decoded, out int length)
    {
        foreach ((string name, char value) in new[] { ("&lt;", '<'), ("&gt;", '>'), ("&amp;", '&') })
        {
            if (string.CompareOrdinal(text, at, name, 0, name.Length) == 0)
            {
                decoded = value;
                length = name.Length;
                return true;
            }
        }

        decoded = '\0';
        length = 0;
        return false;
    }

    private static void ReadTag(string inner, int at, int plainAt, double pitchHz, List<Span> spans, List<MarkupBreak> breaks, Stack<(string Name, int SpanIndex)> open)
    {
        string position = $"at position {at + 1}";
        bool closing = inner.StartsWith('/');
        bool selfClosing = inner.EndsWith('/');
        string body = closing ? inner[1..].Trim() : selfClosing ? inner[..^1].Trim() : inner;
        string[] parts = body.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException($"The tag {position} has no name. The tags are <pitch>, <rate>, <range>, <emph> and <break/>.");
        string name = parts[0].ToLowerInvariant();
        if (name is not ("pitch" or "rate" or "range" or "emph" or "break"))
            throw new ArgumentException($"Unknown tag <{parts[0]}> {position}. The tags are <pitch>, <rate>, <range>, <emph> and <break/>; write &lt; for a '<' that is not a tag.");
        if (closing)
        {
            if (parts.Length > 1) throw new ArgumentException($"The closing tag </{name}> {position} takes no value.");
            if (open.Count == 0 || open.Peek().Name != name)
            {
                string message = open.Count == 0
                    ? $"The closing tag </{name}> {position} has no opening tag."
                    : $"The closing tag </{name}> {position} does not match <{open.Peek().Name}>, which is still open.";
                throw new ArgumentException(message);
            }

            (_, int index) = open.Pop();
            spans[index] = spans[index] with { To = plainAt };
            return;
        }

        if (name == "break")
        {
            if (!selfClosing) throw new ArgumentException($"<break> {position} is written <break 300ms/>: it has no closing tag.");
            if (parts.Length != 2) throw new ArgumentException($"<break> {position} needs a length: <break 300ms/> or <break 0.5s/>.");
            if (breaks.Count >= MaxBreaks) throw new ArgumentException($"A text has at most {MaxBreaks} breaks.");
            breaks.Add(new MarkupBreak(plainAt, ReadMilliseconds(parts[1], position)));
            return;
        }

        if (selfClosing) throw new ArgumentException($"<{name}> {position} covers words: write <{name} …>words</{name}>.");
        Kind kind = name switch { "pitch" => Kind.Pitch, "rate" => Kind.Rate, "range" => Kind.Range, _ => Kind.Emph };
        double value = 0;
        if (kind == Kind.Emph)
        {
            if (parts.Length != 1) throw new ArgumentException($"<emph> {position} takes no value.");
        }
        else
        {
            if (parts.Length != 2) throw new ArgumentException($"<{name}> {position} needs one value: {Example(kind)}.");
            value = kind switch
            {
                Kind.Pitch => ReadPitch(parts[1], pitchHz, position),
                Kind.Rate => ReadRate(parts[1], position),
                _ => ReadRange(parts[1], position),
            };
        }

        if (open.Count >= MaxDepth) throw new ArgumentException($"At most {MaxDepth} tags may be open at the same time.");
        spans.Add(new Span(kind, value, plainAt, int.MaxValue));
        open.Push((name, spans.Count - 1));
    }

    private static string Example(Kind kind) => kind switch
    {
        Kind.Pitch => "<pitch 220> (Hz, 50 to 480) or <pitch +3st> (semitones, -24 to 24)",
        Kind.Rate => "<rate 1.3> (0.25 to 4, 1 is the speed of the block)",
        _ => "<range 25> (Hz, 0 to 200)",
    };

    private static double Number(string text, string what, string position)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new ArgumentException($"'{text}' {position} is not a number for {what}.");
        return value;
    }

    private static double ReadPitch(string text, double pitchHz, string position)
    {
        if (text.EndsWith("st", StringComparison.OrdinalIgnoreCase))
        {
            double semitones = Number(text[..^2], "the pitch", position);
            if (semitones is < -24 or > 24) throw new ArgumentException($"The pitch '{text}' {position} must be from -24st to +24st.");
            return (float)Math.Pow(2, semitones / 12);
        }

        double hz = Number(text.EndsWith("hz", StringComparison.OrdinalIgnoreCase) ? text[..^2] : text, "the pitch", position);
        if (hz is < 50 or > 480) throw new ArgumentException($"The pitch '{text}' {position} must be from 50 to 480 Hz (or written in semitones, like +3st): the voice follows no pitch outside that.");
        return (float)(hz / pitchHz);
    }

    private static double ReadRate(string text, string position)
    {
        double rate = Number(text, "the rate", position);
        if (rate is < 0.25 or > 4) throw new ArgumentException($"The rate '{text}' {position} must be from 0.25 to 4 (1 is the speed of the block).");
        return rate;
    }

    private static double ReadRange(string text, string position)
    {
        double hz = Number(text.EndsWith("hz", StringComparison.OrdinalIgnoreCase) ? text[..^2] : text, "the range", position);
        if (hz is < 0 or > 200) throw new ArgumentException($"The range '{text}' {position} must be from 0 to 200 Hz.");
        return hz == 0 ? 0.001 : hz;   // 0 is "not set" for the voice: a range that is almost nothing is a flat voice
    }

    private static int ReadMilliseconds(string text, string position)
    {
        string lower = text.ToLowerInvariant();
        double milliseconds = lower.EndsWith("ms", StringComparison.Ordinal) ? Number(lower[..^2], "the break", position)
            : lower.EndsWith('s') ? Number(lower[..^1], "the break", position) * 1000
            : throw new ArgumentException($"The break '{text}' {position} needs a unit: 300ms or 0.3s.");
        if (milliseconds is <= 0 or > MaxBreakMilliseconds) throw new ArgumentException($"The break '{text}' {position} must be longer than 0 and at most {MaxBreakMilliseconds} ms.");
        return (int)Math.Round(milliseconds, MidpointRounding.ToEven);
    }

    private enum Kind
    {
        Pitch,
        Rate,
        Range,
        Emph,
    }

    private readonly record struct Span(Kind Kind, double Value, int From, int To);
}

/// <summary>A pause that the markup asks for: where it stands in the plain text and how long it lasts.</summary>
/// <param name="PlainIndex">The offset in the plain text where the pause is: it is spoken before the word that starts there.</param>
/// <param name="Milliseconds">The length of the pause.</param>
public readonly record struct MarkupBreak(int PlainIndex, int Milliseconds);
