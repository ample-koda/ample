using System.Globalization;

namespace Ample.Language;

/// <summary>WP13 of plan/diagnostics/PLAN.md: the values the process of an audio component computes with the shared evaluator (the messages of observations, records, fail and require). The process is built once, when the instance is expanded, so each of these runs over the values of that instance: its properties and the value lets before it, and nothing else.</summary>
public static partial class NotePipeline
{
    /// <summary>One observation statement of an audio process, evaluated: the kind (log, debug, trace, warn), the message as it is shown (redacted and cut like every other event message) and the event code, or null when there is none or it is not a valid code.</summary>
    internal sealed record AudioObservation(string Kind, string Message, string? Code);

    /// <summary>A record value of an audio process: the record as it is written (a literal the evaluator reads back), every field that is not a record by its path (<c>inner.x</c>) as written, and whether a part of it comes from a secret.</summary>
    internal sealed record AudioRecord(string Literal, IReadOnlyList<(string Path, string Text)> Fields, bool Sensitive);

    /// <summary>The problem of an observation that reads the machine (AMP6012), as in a helper.</summary>
    internal const string VolatileRead = "This volatile operation is not available in this diagnostic expression";

    private const string AudioHead = "helper __p() {\n";

    /// <summary>Evaluates one <c>log</c>, <c>debug</c>, <c>trace</c> or <c>warn</c> of the process of an audio component with the shared evaluator: the message is any expression over the properties and value lets (<c>warn "room " + room</c>), a value that comes from a secret-named property is redacted wherever it is used, and nothing outside them is visible.</summary>
    /// <param name="statement">The statement as written (it may span lines).</param>
    /// <param name="scope">The values of the properties and of the value lets of the process, by name, as written.</param>
    /// <param name="sensitive">The names in the scope whose value comes from a secret (besides the secret names themselves).</param>
    /// <param name="tempo">The tempo of the song.</param>
    /// <param name="problem">Why the statement cannot be evaluated, or null.</param>
    /// <returns>The evaluated statement, or null.</returns>
    internal static AudioObservation? EvaluateAudioObservation(string statement, IReadOnlyDictionary<string, string> scope, IReadOnlySet<string> sensitive, double tempo, out string? problem)
    {
        if (ReadAudioStatement(statement, s => s is { Semantic: false, Kind: not "assert" }, out string wrapped, out problem) is not { } observation) return null;
        return AudioInterp(tempo, wrapped).AudioObservation(observation, scope, sensitive, out problem);
    }

    /// <summary>Evaluates a <c>fail</c> or <c>require</c> of an audio process: the message when the statement fails the instance, or null when a require holds (or the statement cannot be evaluated: then the problem says why).</summary>
    internal static string? EvaluateAudioCheck(string statement, IReadOnlyDictionary<string, string> scope, IReadOnlySet<string> sensitive, double tempo, out string? problem)
    {
        if (ReadAudioStatement(statement, s => s.Kind is "fail" or "require", out string wrapped, out problem) is not { } check) return null;
        return AudioInterp(tempo, wrapped).AudioCheck(check, scope, sensitive, out problem);
    }

    /// <summary>Evaluates the value of a let of an audio process that is a record (<c>{wet: 40%, size: room}</c>).</summary>
    internal static AudioRecord? EvaluateAudioRecord(string expression, IReadOnlyDictionary<string, string> scope, IReadOnlySet<string> sensitive, double tempo, out string? problem)
    {
        string clean = StripComments(expression);
        Node node;
        try
        {
            node = Typed(new ExprParser(clean, Tokenize(clean)) { AudioProcess = true }.Parse());
        }
        catch (SyntaxError e)
        {
            problem = e.Message;
            return null;
        }

        if (ReadsTheMachine(node)) { problem = "A value of an audio process cannot read the machine (environment, osRandom)."; return null; }
        return AudioInterp(tempo, clean).AudioRecord(node, scope, sensitive, out problem);
    }

    private static bool ReadsTheMachine(Node node) => Descendants(node).Prepend(node).OfType<CallNode>().Any(c => c.Name.ToLowerInvariant() is "osrandom" or "environment");

    private static ObserveStmt? ReadAudioStatement(string statement, Func<ObserveStmt, bool> kind, out string wrapped, out string? problem)
    {
        wrapped = AudioHead + StripComments(statement) + "\n}";
        try
        {
            if (new ExprParser(wrapped, Tokenize(wrapped)) { AudioProcess = true }.ParseDeclaration() is not HelperDeclNode { Body.Statements: [ObserveStmt one] } || !kind(one))
            {
                problem = "This is not one statement of this kind.";
                return null;
            }

            // the same rule as in a helper (AMP6012): what is observed or decided must not depend on the machine
            if (one.Args.Any(ReadsTheMachine))
            {
                problem = VolatileRead;
                return null;
            }

            problem = null;
            return one;
        }
        catch (SyntaxError e)
        {
            problem = e.Message;
            return null;
        }
    }

    private static Interp AudioInterp(double tempo, string text)
    {
        Context context = new() { Tempo = (tempo > 0 ? tempo : 120).ToString(CultureInfo.InvariantCulture) };
        return new Interp(context, ParseDecimal(context.Tempo) ?? Rat.Of(120), [], [], text);
    }

    private sealed partial class Interp
    {
        /// <summary>Evaluates an observation statement of an audio process over the given values (see <see cref="EvaluateAudioObservation"/>).</summary>
        public AudioObservation? AudioObservation(ObserveStmt statement, IReadOnlyDictionary<string, string> scope, IReadOnlySet<string> sensitive, out string? problem)
        {
            Env env = AudioEnv(scope, sensitive);
            try
            {
                if (statement.Positional.Count == 0) { problem = "The statement has no message."; return null; }
                Val message = Eval(statement.Positional[0], env);
                string? code = null;
                foreach (NamedNode named in statement.Named.Where(n => n.Name == "code"))
                {
                    if (Eval(named.Value, env) is StrV text && (text.Marks & ValueMarks.Sensitive) == 0 && IsEventCode(text.V)) code = text.V;   // an event code is a name, never free text
                }

                if (errors.Count > 0) { problem = errors[0].Message; return null; }
                problem = null;
                return new AudioObservation(statement.Kind, ObservationText(message), code);   // the same text as every other event
            }
            catch (LangFailure e)
            {
                problem = e.Message;
                return null;
            }
        }

        /// <summary>Evaluates a fail or require of an audio process (see <see cref="EvaluateAudioCheck"/>).</summary>
        public string? AudioCheck(ObserveStmt statement, IReadOnlyDictionary<string, string> scope, IReadOnlySet<string> sensitive, out string? problem)
        {
            Env env = AudioEnv(scope, sensitive);
            try
            {
                problem = null;
                if (statement.Positional.Count == 0) { problem = "The statement has no message."; return null; }
                if (statement.Kind == "fail") return MessageText(Eval(statement.Positional[0], env));
                Node condition = statement.Positional[0];
                if (Truthy(Eval(condition, env))) return null;
                return statement.Positional.Count > 1 ? MessageText(Eval(statement.Positional[1], env)) : "Requirement failed: " + Collapse(SourceOf(condition));
            }
            catch (LangFailure e)
            {
                problem = e.Message;
                return null;
            }
        }

        /// <summary>Evaluates the record value of a let of an audio process (see <see cref="EvaluateAudioRecord"/>).</summary>
        public AudioRecord? AudioRecord(Node node, IReadOnlyDictionary<string, string> scope, IReadOnlySet<string> sensitive, out string? problem)
        {
            try
            {
                if (Eval(node, AudioEnv(scope, sensitive)) is not RecordV record) { problem = "The value is not a record."; return null; }
                List<(string Path, string Text)> fields = [];
                void Flatten(string path, Val value)
                {
                    if (value is RecordV inner) foreach ((string key, Val item) in inner.Fields) Flatten(path.Length == 0 ? key : path + "." + key, item);
                    else fields.Add((path, Literal(value)));
                }

                Flatten(string.Empty, record);
                problem = null;
                return new AudioRecord(Literal(record), fields, IsSensitive(record));
            }
            catch (LangFailure e)
            {
                problem = e.Message;
                return null;
            }
        }

        /// <summary>A value as it is written, so the evaluator (and the calculations of the process) read it back: a text in quotes (a quote inside becomes an apostrophe), a record as a record literal.</summary>
        private string Literal(Val value) => value switch
        {
            RecordV r => "{" + string.Join(", ", r.Fields.Select(f => f.Key + ": " + Literal(f.Value))) + "}",
            ListV l => "[" + string.Join(", ", l.Items.Select(Literal)) + "]",
            StrV s => "\"" + s.V.Replace('"', '\'') + "\"",
            BoolV b => b.V ? "true" : "false",
            _ => ValText(value),
        };

        /// <summary>The environment of an audio process: its values by name, each read as written; a secret-named value, and a value that comes from a secret, is marked sensitive.</summary>
        private Env AudioEnv(IReadOnlyDictionary<string, string> scope, IReadOnlySet<string> sensitive)
        {
            Env env = new(null);
            foreach ((string name, string raw) in scope)
            {
                if (name.Contains('.', StringComparison.Ordinal)) continue;   // a field of a record let: the record itself is in the scope
                Val value = PropertyValue(raw);
                env.Define(name, ObsRedactor.IsSecretName(name) || sensitive.Contains(name) ? WithMarks(value, ValueMarks.Sensitive) : value, false);
            }

            return env;
        }

        /// <summary>The value of a property as written (40%, -3dB, true, "quiet", a record literal); a text that is not a value is that text.</summary>
        private Val PropertyValue(string raw)
        {
            string text = raw.Trim();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && text.IndexOf('"', 1) == text.Length - 1) return new StrV(text[1..^1]);
            try
            {
                Interp reader = new(context, tempo, [], [], text);
                return reader.Eval(Typed(new ExprParser(text, Tokenize(text)).Parse()), new Env(null));
            }
            catch (SyntaxError)
            {
                return new StrV(text);
            }
            catch (LangFailure)
            {
                return new StrV(text);
            }
        }
    }
}
