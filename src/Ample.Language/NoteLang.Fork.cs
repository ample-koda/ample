using System.Diagnostics;

namespace Ample.Language;

public static partial class NotePipeline
{
    private enum ForkMode { Observe, Assert }

    internal sealed class AssertionBudget
    {
        internal const int StatementFuel = 50_000;
        internal const int SessionFuel = 1_000_000;
        internal const int Depth = 16;
        internal const int MemoryMb = 16;

        internal long Remaining { get; private set; } = SessionFuel;
        internal long Available => Math.Min(StatementFuel, Remaining);

        internal void Charge(long consumed) => Remaining -= Math.Min(Available, consumed);
    }

    internal enum AssertionCondition { Passed, False, Raised, Unavailable }

    // Entry for hosts that evaluate a standalone condition. The language statement remains gated until WP5b.
    internal static AssertionCondition EvaluateAssertionCondition(string expression, Context context)
    {
        string clean = StripComments(expression);
        Node condition = Typed(new ExprParser(clean, Tokenize(clean)).Parse());
        Rat tempo = ParseDecimal(context.Tempo) ?? Rat.Of(120);
        Interp interpreter = new(context, tempo, [], [], clean);
        return interpreter.CheckAssertionCondition(condition);
    }

    private sealed partial class Interp
    {
        // Only expression reads can cross the boundary. Assignment walks Env parents, never ObserverEnv.
        private sealed class ObserverEnv(Env original, Func<string, Val?>? lazy = null)
        {
            public bool TryGet(string name, out Val value)
            {
                if (original.TryGet(name, out value)) return true;
                value = lazy?.Invoke(name)!;
                return value is not null;
            }
        }

        private readonly ForkMode? forkMode;
        private long observerStarted;
        private TimeSpan observerDeadline;
        private bool observerTimedOut;

        private Interp CreateFork(ForkMode mode, long fuelAvailable)
        {
            Context fork = new()
            {
                Tempo = context.Tempo,
                BeatsPerBar = context.BeatsPerBar,
                Limit = context.Limit,
                MasterSeed = context.MasterSeed,
                Streams = context.Streams,
                Helpers = table,
                Clock = context.Clock,
                Capabilities = context.Capabilities,
                Cancellation = context.Cancellation,
                TimeSource = context.TimeSource,
                PeekSource = context.PeekSource,
                ObservationOptions = context.ObservationOptions,
                Budget = TimeSpan.MaxValue,
                Limits = limits with
                {
                    StackDepth = mode == ForkMode.Assert ? AssertionBudget.Depth : 16,
                    Fuel = fuelAvailable,
                    MemoryMb = mode == ForkMode.Assert ? AssertionBudget.MemoryMb : 4,
                },
            };
            return new Interp(fork, tempo, [], [], sourceText, mode);
        }

        private void RunFork(
            ForkMode mode, Env environment, long fuelAvailable, TimeSpan timeLimit, SourceSite site,
            string helper, Action<Interp, Env> evaluate)
        {
            Legacy.PauseClock();
            try
            {
                Interp fork = CreateFork(mode, fuelAvailable);
                fork.observerStarted = context.TimeSource();
                fork.observerDeadline = timeLimit;
                try
                {
                    evaluate(fork, fork.ForkEnvironment(environment, helper));
                    context.Cancellation.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (mode == ForkMode.Assert) context.Assertions.Charge(fork.Fuel);
                    else if (context.Observations is { } sink)
                    {
                        TimeSpan elapsed = Stopwatch.GetElapsedTime(fork.observerStarted, context.TimeSource());
                        sink.ChargeObservation(Math.Min(fuelAvailable, fork.Fuel), elapsed, fork.observerTimedOut, site, context.ObservationOptions);
                    }
                }
            }
            finally
            {
                Legacy.ResumeClock();
            }
        }

        internal AssertionCondition CheckAssertionCondition(Node condition) => CheckAssertionCondition(condition, new Env(null));

        private AssertionCondition CheckAssertionCondition(Node condition, Env environment)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (forkMode is not null) return AssertionCondition.Passed;
            long available = context.Assertions.Available;
            if (available == 0) return AssertionCondition.Unavailable;
            AssertionCondition result = AssertionCondition.Raised;
            SourceSite site = SiteResolver.At("song", sourceText, 0, condition.Start, condition.End);
            RunFork(ForkMode.Assert, environment, available, TimeSpan.MaxValue, site, string.Empty, (fork, scope) =>
            {
                try
                {
                    result = Truthy(fork.Eval(condition, scope)) ? AssertionCondition.Passed : AssertionCondition.False;
                }
                catch (LangFailure error)
                {
                    string id = error.Id == "reported" ? fork.errors.LastOrDefault()?.Id ?? error.Id : error.Id;
                    result = id is "fuel" or "memory" or "stack-depth" or "loop-limit" or "while-limit" or "cap-events" or "unknown-reference"
                        ? AssertionCondition.Unavailable
                        : AssertionCondition.Raised;
                }
            });
            return result;
        }

        private Env ForkEnvironment(Env original, string helper)
        {
            IReadOnlyDictionary<string, ComponentProperty>? properties = table.ObservationProperties(helper);
            if (forkMode != ForkMode.Observe || properties is not { Count: > 0 })
                return new Env(null, new ObserverEnv(original));
            Dictionary<string, Val> loaded = new(StringComparer.Ordinal);
            HashSet<string> loading = new(StringComparer.Ordinal);
            Env? defaults = null;
            defaults = new Env(null, new ObserverEnv(original.Root, Resolve));
            return new Env(null, new ObserverEnv(original, Resolve));

            Val? Resolve(string name)
            {
                if (loaded.TryGetValue(name, out Val? existing)) return existing;
                if (!properties.TryGetValue(name, out ComponentProperty? property) || property.Default is not { } expression) return null;
                if (!loading.Add(name)) throw new LangFailure("cycle", "An observer property depends on itself", null);
                string previous = sourceText;
                try
                {
                    sourceText = StripComments(expression);
                    Node value = Typed(new ExprParser(sourceText, Tokenize(sourceText)).Parse());
                    Val result = Coerce(Eval(value, defaults!), property.Type.TrimEnd('?'), value, name);
                    loaded.Add(name, result);
                    return result;
                }
                catch (SyntaxError error)
                {
                    throw new LangFailure(error.Id, "An observer property has an invalid default", null);
                }
                finally
                {
                    sourceText = previous;
                    loading.Remove(name);
                }
            }
        }

        private void Observe(ObserveStmt statement, Env environment, string helper)
        {
            if (forkMode is not null || context.Observations is not { } sink || statement.Kind == "assert") return;
            EventLevel level = statement.Kind switch
            {
                "warn" => EventLevel.Warning,
                "trace" => EventLevel.Trace,
                "debug" => EventLevel.Debug,
                _ => EventLevel.Info,
            };
            ObservationConfig? config = context.ObservationOptions;
            if (!ObservationSink.Allows(level, config?.Level ?? sink.Level)) return;
            SourceSite? original = context.ObservationHelperSites?.GetValueOrDefault(helper);
            int firstLine = original is null ? table.LineOf(helper) : original.Line - 1;
            SourceSite site = SiteResolver.At(original?.Origin ?? "song", sourceText, firstLine, statement.Start, statement.End);
            (long fuelAvailable, TimeSpan timeAvailable) = sink.RemainingObservationBudget(config);
            bool mayEvaluate = fuelAvailable > 0 && timeAvailable > TimeSpan.Zero;
            string siteKey = ObservationSink.SiteKeyOf(site, context.ObservationRoot, level);
            EventDecision decision = sink.Begin(level, siteKey, mayEvaluate, config);
            if (!decision.Evaluate) return;
            List<StackFrameInfo> frames = EventStack(level);   // the stack belongs to the main evaluation: the fork has none
            TimeSpan timeLimit = TimeSpan.FromMilliseconds(Math.Min(250, timeAvailable.TotalMilliseconds));
            RunFork(ForkMode.Observe, environment, fuelAvailable, timeLimit, site, helper, (fork, scope) =>
            {
                LogEvent entry;
                try
                {
                    Val value = fork.Eval(statement.Positional[0], scope);
                    Dictionary<string, Val> named = new(StringComparer.Ordinal);
                    foreach (NamedNode arg in statement.Named) named.Add(arg.Name, fork.Eval(arg.Value, scope));
                    fork.CheckObserverTime(statement);
                    entry = fork.MakeObservation(decision, level, site, value, named) with { Stack = frames };
                }
                catch (LangFailure error)
                {
                    string message = error.Id == "unknown-reference"
                        ? "The block value has not been evaluated at this point"
                        : "The observation expression could not be evaluated";
                    entry = new LogEvent(0, level, decision.SiteKey, site, error.Id, message, null, null, [], fork.Dynamic);
                }
                context.Cancellation.ThrowIfCancellationRequested();
                sink.Add(decision, entry);
            });
        }

        /// <summary>The stack an event carries: <c>logging.stack</c> says for which levels (errors: none of these, warnings: warnings, all: every event).</summary>
        private List<StackFrameInfo> EventStack(EventLevel level)
        {
            string mode = context.ObservationOptions?.StackMode ?? "errors";
            bool wanted = mode == "all" || (mode == "warnings" && level == EventLevel.Warning);
            return wanted ? StackSnapshot() : [];
        }

        private LogEvent MakeObservation(EventDecision decision, EventLevel level, SourceSite site, Val value, Dictionary<string, Val> named)
        {
            string? code = named.TryGetValue("code", out Val? codeValue) && codeValue is StrV codeText
                && (codeValue.Marks & ValueMarks.Sensitive) == 0 && IsEventCode(codeText.V) ? codeText.V : null;
            string message = ObservationText(value);
            string? hint = named.TryGetValue("hint", out Val? hintValue) ? ObservationText(hintValue) : null;
            string? json = named.TryGetValue("context", out Val? record) ? ObservationJson(record) : null;
            return new LogEvent(0, level, decision.SiteKey, site, code, message, hint, json, [], Dynamic);
        }

        private static bool IsEventCode(string code) =>
            code.Length is > 0 and <= 64 && char.IsAsciiLetterUpper(code[0])
            && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            && !(code.Length == 7 && code.StartsWith("AMP", StringComparison.Ordinal) && code[3..].All(char.IsAsciiDigit));

        /// <summary>The text of an observed value in a message or hint, the same in a helper and in an audio process (plan/diagnostics WP9): a text as it is,
        /// a single value as it is written (40%, 3, true), a list, a record or notes as compact JSON (a secret field redacted there, the caps applied),
        /// and a secret value as the redaction mark. The structured outputs carry this text as one JSON string; only the context is JSON itself.</summary>
        private string ObservationText(Val value) => value switch
        {
            RecordV or ListV or NotesV => ObsRedactor.Cut(ObservationJson(value)),
            _ when IsSensitive(value) => ObsRedactor.Redacted,
            StrV text => ObsRedactor.Cut(text.V),
            UnitV => "nothing",
            _ => ObsRedactor.Cut(ScalarText(value) ?? ValText(value)),   // the same text as String(value) and %s
        };

        private string ObservationJson(Val value)
        {
            int remaining = 256;
            ObsLimits limits = new(MaxBytes: context.ObservationOptions?.ContextBytes ?? 4096);
            return ObsRedactor.ToJson(Summarize(value, 1, ref remaining), limits);
        }

        private ObsValue Summarize(Val value, int depth, ref int remaining)
        {
            if ((value.Marks & ValueMarks.Sensitive) != 0) return new ObsText(string.Empty, Sensitive: true);
            if (depth > 8 || remaining-- <= 0) return new ObsText("…(cut)");
            if (value is RecordV record)
            {
                List<(string Key, ObsValue Value)> fields = [];
                foreach ((string key, Val item) in record.Fields.Take(65))
                {
                    fields.Add((key, Summarize(item, depth + 1, ref remaining)));
                    if (remaining <= 0) break;
                }
                return new ObsRecord(fields);
            }
            if (value is ListV list)
            {
                List<ObsValue> items = [];
                foreach (Val item in list.Items.Take(65))
                {
                    items.Add(Summarize(item, depth + 1, ref remaining));
                    if (remaining <= 0) break;
                }
                return new ObsList(items);
            }
            return value switch
            {
                StrV text => new ObsText(text.V),
                BoolV flag => new ObsBool(flag.V),
                IntV or FloatV or QuantV => new ObsNumber(ValText(value)),
                NotesV notes => SummarizeNotes(notes.V),
                UnitV => new ObsNull(),
                _ => new ObsText(SafeText(value)),
            };
        }

        private static ObsRecord SummarizeNotes(PipeValue value) => new(
        [
            ("events", new ObsNumber(value.Events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ("spanTicks", new ObsNumber(value.Span.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        ]);

        private void CheckObserverTime(Node node)
        {
            if (forkMode == ForkMode.Observe && Stopwatch.GetElapsedTime(observerStarted, context.TimeSource()) > observerDeadline)
            {
                observerTimedOut = true;
                throw new LangFailure("cap-time", "The observation reached its time budget", node);
            }
        }

        private NotesV Peek(RefNode reference)
        {
            if (reference.Port == "notes" && context.PeekSource?.Invoke(reference.Block) is { Value: { } value } existing)
                return new NotesV(value, existing.Recipe ?? Sha("ref"))
                {
                    Marks = existing.Sensitive ? ValueMarks.Sensitive : ValueMarks.None,
                };

            throw new LangFailure("unknown-reference", "The block value has not been evaluated at this point", reference);
        }
    }
}
