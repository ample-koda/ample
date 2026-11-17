namespace Ample.Language;

public static partial class NotePipeline
{
    /// <summary>
    /// The formatter rule for one observation statement (plan R1.6), derived from the shape of the statement and not from the way it was written: the statement form when it has one or two positional arguments
    /// and no named ones, the first does not start with a parenthesis and it fits; otherwise the call form, on one line when it fits and has no record with more than one field or a nested record, and on
    /// several lines (one argument per line) when not. Writing the result again changes nothing.
    /// </summary>
    /// <param name="code">The statement (one logical line, without comment).</param>
    /// <param name="indent">The columns before it.</param>
    /// <param name="width">The width it must fit in.</param>
    /// <returns>The lines without their indentation, or null when this is not one observation statement (the line stays as it is).</returns>
    internal static IReadOnlyList<string>? FormatObservation(string code, int indent, int width = 100)
    {
        string text = code.Trim();
        if (text.Length == 0 || !char.IsLetter(text[0])) return null;
        ObserveStmt? s;
        try
        {
            s = new ExprParser(text, Tokenize(text)).TryObservation();
        }
        catch (SyntaxError)
        {
            return null;
        }

        if (s is null) return null;
        // the argument texts come from the tokens (a node range leaves out the parentheses around an expression)
        List<Tok> tokens = Tokenize(text);
        int first = s.CallForm ? 2 : 1, stop = s.CallForm ? tokens.Count - 1 : tokens.Count;
        List<string> args = [];
        int depth = 0, begin = first;
        for (int i = first; i <= stop; i++)
        {
            if (i < stop)
            {
                if (tokens[i].Kind is T.LParen or T.LBracket or T.LBrace) depth++;
                else if (tokens[i].Kind is T.RParen or T.RBracket or T.RBrace) depth--;
            }

            if (i == stop || (tokens[i].Kind == T.Comma && depth == 0))
            {
                if (i > begin) args.Add(text[tokens[begin].Start..tokens[i - 1].End]);
                begin = i + 1;
            }
        }

        if (args.Count == 0) return null;
        string joined = string.Join(", ", args);
        if (s.Named.Count == 0 && !args[0].StartsWith('(') && indent + s.Kind.Length + 1 + joined.Length <= width) return [s.Kind + " " + joined];
        bool records = Descendants(s).OfType<RecordNode>().Any(r => r.Fields.Count > 1 || Descendants(r).OfType<RecordNode>().Any());
        string oneLine = s.Kind + "(" + joined + ")";
        if (indent + oneLine.Length <= width && !records) return [oneLine];
        List<string> lines = [s.Kind + "("];
        for (int i = 0; i < args.Count; i++) lines.Add(args[i] + (i < args.Count - 1 ? "," : string.Empty));
        lines.Add(")");
        return lines;
    }
}
