namespace Ample.Language;

/// <summary>
/// The heads of a conditional in AMPLE code (<c>if</c>, <c>else if</c>, <c>} else if</c>, <c>} else</c>) read the same way everywhere: the condition
/// is written with the parentheses around it or without
/// (<c>if (a &gt; 5) {</c> or <c>if a &gt; 5 {</c>), and the parentheses of the whole condition are told from parentheses inside it by matching them,
/// one character at a time, outside strings: never by cutting the first
/// and the last one off the text. The formatter writes the parenthesized form. Pure and shared by the audio components, the formatter and the hash of a component.
/// </summary>
public static class ConditionSyntax
{
    /// <summary>What a head is.</summary>
    public enum HeadKind
    {
        /// <summary><c>if condition {</c></summary>
        If,

        /// <summary><c>else if condition {</c>, or <c>} else if condition {</c></summary>
        ElseIf,

        /// <summary><c>else {</c>, or <c>} else {</c></summary>
        Else,
    }

    /// <summary>A problem in the head of a conditional.</summary>
    /// <param name="Column">The 0-based column in the line.</param>
    /// <param name="Length">How many characters it covers.</param>
    /// <param name="Message">What is wrong.</param>
    /// <param name="Hint">What to write.</param>
    public sealed record Problem(int Column, int Length, string Message, string Hint);

    /// <summary>The head of a conditional, read from one line.</summary>
    /// <param name="Kind">If, else if or else.</param>
    /// <param name="Closes">Whether the line starts with the <c>}</c> that ends the branch before it.</param>
    /// <param name="Condition">The condition without the parentheses around the whole of it (empty for an else, or when it is missing).</param>
    /// <param name="Wrapped">Whether the condition was written with parentheses around the whole of it.</param>
    /// <param name="ConditionColumn">Where the condition (as written, with its parentheses) starts in the line.</param>
    /// <param name="ConditionLength">How long the condition is as written.</param>
    /// <param name="Rest">What stands after the <c>{</c> (a body on the same line), without the leading spaces; empty when the line ends with the <c>{</c>.</param>
    /// <param name="Problem">What is wrong with the head, or null.</param>
    public sealed record Head(HeadKind Kind, bool Closes, string Condition, bool Wrapped, int ConditionColumn, int ConditionLength, string Rest, Problem? Problem);

    /// <summary>The index of the character after a string that starts at <paramref name="i"/> (a <c>"</c>), with <c>\</c> escapes; the end of the text when it never closes.</summary>
    private static int StringEnd(string s, int i)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == '\\') i += 2;
            else if (s[i] == '"') return i + 1;
            else i++;
        }

        return -1;
    }

    private static int SkipString(string s, int i) => StringEnd(s, i) is var end and >= 0 ? end : s.Length;

    /// <summary>The index of the <c>)</c> that matches the <c>(</c> at <paramref name="open"/>, outside strings; -1 when it never closes.</summary>
    public static int MatchingParen(string s, int open)
    {
        ArgumentNullException.ThrowIfNull(s);
        int depth = 0;
        for (int i = open; i < s.Length;)
        {
            char c = s[i];
            if (c == '"') { i = SkipString(s, i); continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
            i++;
        }

        return -1;
    }

    /// <summary>
    /// Takes the parentheses off a condition that has them around the whole of it, as many layers as there are: <c>(a &gt; 5)</c> and <c>((a &gt;
    /// 5))</c> are <c>a &gt; 5</c>, but <c>(a &gt; 5) &amp;&amp; (b &lt; 3)</c> stays as it is,
    /// because the first <c>(</c> closes before the end of the text.
    /// </summary>
    /// <param name="condition">The condition as written (spaces around it do not matter).</param>
    /// <returns>The condition without the parentheses around all of it, trimmed.</returns>
    public static string Unwrap(string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        string text = condition.Trim();
        while (text.Length >= 2 && text[0] == '(' && MatchingParen(text, 0) == text.Length - 1) text = text[1..^1].Trim();
        return text;
    }

    /// <summary>Whether the whole of the condition is wrapped in one pair of parentheses.</summary>
    /// <param name="condition">The condition as written.</param>
    /// <returns>True for <c>(a &gt; 5)</c>, false for <c>a &gt; 5</c> and for <c>(a &gt; 5) &amp;&amp; (b &lt; 3)</c>.</returns>
    public static bool IsWrapped(string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        string text = condition.Trim();
        return text.Length >= 2 && text[0] == '(' && MatchingParen(text, 0) == text.Length - 1;
    }

    /// <summary>The condition in the written form of the formatter: <c>(condition)</c>, the parentheses of the whole condition once.</summary>
    /// <param name="condition">The condition as written.</param>
    /// <returns>The condition with exactly one pair of parentheses around all of it.</returns>
    public static string Wrap(string condition) => "(" + Unwrap(condition) + ")";

    /// <summary>The first problem with the parentheses of a text outside strings, or null: a <c>)</c> without a <c>(</c>, a <c>(</c> that never closes, a string that never closes.</summary>
    /// <param name="s">The text.</param>
    /// <returns>Where and what, or null when they match.</returns>
    public static Problem? ParenProblem(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        Stack<int> open = new();
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (c == '"')
            {
                int end = StringEnd(s, i);
                if (end < 0) return new Problem(i, s.Length - i, "A string in this condition is never closed.", "Close it with a \".");
                i = end;
                continue;
            }

            if (c == '(') open.Push(i);
            else if (c == ')')
            {
                if (open.Count == 0) return new Problem(i, 1, "This ) closes a ( that is not there.", "Remove it, or add the ( that goes with it.");
                open.Pop();
            }

            i++;
        }

        return open.Count > 0 ? new Problem(open.Peek(), 1, "This ( is never closed.", "Add the ) that goes with it before the {.") : null;
    }

    /// <summary>The index of the first <c>{</c> outside strings and parentheses, or -1.</summary>
    private static int BlockOpen(string s, int from)
    {
        int depth = 0;
        for (int i = from; i < s.Length;)
        {
            char c = s[i];
            if (c == '"') { i = SkipString(s, i); continue; }
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == '{' && depth == 0) return i;
            i++;
        }

        return -1;
    }

    private static bool WordAt(string s, int i, string word) =>
        string.CompareOrdinal(s, i, word, 0, word.Length) == 0 && (i + word.Length == s.Length || !(char.IsLetterOrDigit(s[i + word.Length]) || s[i + word.Length] is '_' or '-'));

    /// <summary>
    /// Reads the head of a conditional from a line of code (comments already taken off). Returns null when the line is not a head at all (it does not
    /// start with <c>if</c>, <c>else if</c>, <c>else</c>, <c>} else if</c> or <c>} else</c>
    /// followed by a condition or a <c>{</c>); a head with a mistake comes back with its <see cref="Head.Problem"/> set, so the caller can say what is wrong and where.
    /// </summary>
    /// <param name="line">The line, with or without its indentation.</param>
    /// <returns>The head, or null.</returns>
    public static Head? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        bool closes = false;
        if (i < line.Length && line[i] == '}')
        {
            closes = true;
            i++;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        }

        HeadKind kind;
        if (WordAt(line, i, "if") && !closes) { kind = HeadKind.If; i += 2; }
        else if (WordAt(line, i, "else"))
        {
            i += 4;
            int afterElse = i;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (WordAt(line, i, "if")) { kind = HeadKind.ElseIf; i += 2; }
            else { kind = HeadKind.Else; i = afterElse; }
        }
        else return null;

        // an "if" that is the start of a name or a call (ifx, if(x) as a function) is not a head: a head has a { at the end of its condition
        int open = BlockOpen(line, i);
        if (kind == HeadKind.Else)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length || line[i] != '{') return null;
            return new Head(kind, closes, string.Empty, false, i, 0, line[(i + 1)..].Trim(), null);
        }

        if (open < 0)
        {
            // if/else if without a { : still a head when it starts the way a head starts, so that the mistake can be named
            string tail = line[i..].Trim();
            int column = i + (line[i..].Length - line[i..].TrimStart().Length);
            Problem missing = new(column, Math.Max(1, tail.Length),
                kind == HeadKind.If ? "This if has no { after its condition." : "This else if has no { after its condition.", "Write the block of the branch between { and }.");
            return new Head(kind, closes, Unwrap(tail), IsWrapped(tail), column, tail.Length, string.Empty, ParenProblem(tail) ?? missing);
        }

        string written = line[i..open];
        int start = i + (written.Length - written.TrimStart().Length);
        string trimmed = written.Trim();
        Problem? problem = ParenProblem(trimmed) is { } p ? p with { Column = p.Column + start } : null;
        if (problem is null && trimmed.Length == 0) problem = new Problem(open, 1,
            kind == HeadKind.If ? "This if has no condition." : "This else if has no condition.", "Write the condition between if and {, for example if (value > 5) {.");
        if (problem is null && Unwrap(trimmed).Length == 0) problem = new Problem(start, trimmed.Length,
            "The parentheses of this condition are empty.", "Write the condition between the parentheses.");
        return new Head(kind, closes, Unwrap(trimmed), IsWrapped(trimmed), start, trimmed.Length, line[(open + 1)..].Trim(), problem);
    }

    /// <summary>The head of a conditional in the written form of the formatter: <c>if (condition) {</c>, <c>} else if (condition) {</c>, <c>} else
    /// {</c>, with the rest of the line after it.</summary>
    /// <param name="head">The head (without a problem).</param>
    /// <returns>The text of the head without indentation.</returns>
    public static string Format(Head head)
    {
        ArgumentNullException.ThrowIfNull(head);
        string lead = head.Closes ? "} " : string.Empty;
        string text = head.Kind switch
        {
            HeadKind.If => $"{lead}if {Wrap(head.Condition)} {{",
            HeadKind.ElseIf => $"{lead}else if {Wrap(head.Condition)} {{",
            _ => $"{lead}else {{",
        };
        return head.Rest.Length == 0 ? text : text + " " + head.Rest;
    }
}
