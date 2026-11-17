namespace Ample.Language;

public static partial class NotePipeline
{
    /// <summary>The failures that a <c>catch</c> can handle (class T1 of spec/failure-classes.json, checked by a test) with their stable codes.</summary>
    private static readonly Dictionary<string, string> CatchableCodes = new(StringComparer.Ordinal)
    {
        ["divide-by-zero"] = "DIV_ZERO",
        ["unknown-field"] = "UNKNOWN_FIELD",
        ["conversion"] = "CONVERSION",
        ["throw"] = "THROW",
        ["require"] = "REQUIRE",
    };

    /// <summary>The safety stops (class T3): no user code runs after them, so no <c>finally</c> either.</summary>
    private static readonly HashSet<string> SafetyStops = new(StringComparer.Ordinal)
    {
        "fuel", "loop-limit", "while-limit", "stack-depth", "memory", "cap-events", "cap-time", "cancelled",
    };

    /// <summary>The error value: what a <c>catch</c> binds and what <c>throw</c> raises. Only the whitelisted members can be read from the language; hint, stack and place stay with the tooling (plan D26).</summary>
    private sealed record ErrorV(string Type, string Message, string? Code, string? Hint, RecordV? Context, ErrorV? Cause, string Origin,
        IReadOnlyList<FailureSummary> Suppressed, int SuppressedCount) : Val
    {
        /// <summary>Gets the type and its parents, ending in Error.</summary>
        public IReadOnlyList<string> Chain { get; init; } = [Type, "Error"];

        /// <summary>Gets the values of the declared fields, parents first.</summary>
        public IReadOnlyList<(string Key, Val Value)> Fields { get; init; } = [];
    }

    private sealed partial class Interp
    {
        /// <summary>The errors that are being handled by a running <c>catch</c>, innermost last: a failure raised inside one gets it as its cause.</summary>
        private readonly List<ErrorV> handling = [];

        private long fuelLimit = -1;

        private int depthLimit = -1;

        private bool reserveExhausted;

        private bool inReserve;

        private const int UnwindFuel = 5_000;

        private const int UnwindDepth = 8;

        private long FuelLimit => fuelLimit >= 0 ? fuelLimit : limits.Fuel;

        private int DepthLimit => depthLimit >= 0 ? depthLimit : limits.StackDepth;

        private static bool IsCatchable(Exception ex) => ex is LangFailure f && CatchableCodes.ContainsKey(f.Id);

        private static bool IsSafetyStop(Exception ex) => ex is OperationCanceledException || (ex is LangFailure f && SafetyStops.Contains(f.Id));

        /// <summary>1 for a catchable error, 2 for a controlled fatal one (fail and every programming error), 3 for a safety stop; 0 for an early stop (<c>stop</c>), which ends the run without running anything else.</summary>
        private static int Severity(Exception ex) => ex switch
        {
            StopRun => 0,
            _ when IsSafetyStop(ex) => 3,
            _ when IsCatchable(ex) => 1,
            _ => 2,
        };

        private static ErrorV ErrorOf(LangFailure failure)
        {
            if (failure.Error is { } own)
            {
                ErrorV withSuppressed = failure.SuppressedTotal == 0 ? own : own with { Suppressed = [.. failure.Suppressed.Take(4)], SuppressedCount = failure.SuppressedTotal };
                return failure.Marks == ValueMarks.None ? withSuppressed : (ErrorV)WithMarks(withSuppressed, failure.Marks);
            }

            string type = failure.Id == "require" ? "RequirementError" : "Error";
            IReadOnlyList<string> chain = type == "Error" ? ["Error"] : ["RequirementError", "Error"];
            ErrorV made = new(type, failure.Message, CatchableCodes.GetValueOrDefault(failure.Id), failure.Suggestion, null, null, "error",
                [.. failure.Suppressed.Take(4)], failure.SuppressedTotal)
            { Marks = failure.Marks, Chain = chain };
            return made;
        }

        private static bool Matches(string? type, ErrorV error) => type is null || error.Chain.Contains(type, StringComparer.OrdinalIgnoreCase);

        private Flow Try(TryStmt statement, Env env, Frame frame)
        {
            NotesV? emittedBefore = frame.Emitted;
            Flow flow = Flow.Next;
            Exception? pending = null;
            try
            {
                flow = ExecBlock(statement.Body, new Env(env), frame);
            }
            catch (Exception ex) when (ex is LangFailure or OperationCanceledException or StopRun)
            {
                pending = ex;
            }

            if (pending is LangFailure caught && IsCatchable(caught))
            {
                ErrorV error = ErrorOf(caught);
                CatchClause? clause = statement.Catches.FirstOrDefault(c => Matches(c.Type, error));
                if (clause is not null)
                {
                    frame.Emitted = emittedBefore;   // what the try body emitted is dropped: the catch decides what happens next
                    pending = null;
                    Env scope = new(env);
                    scope.Define(clause.Name, error, false);
                    handling.Add(error);
                    try
                    {
                        flow = Selected(clause.Body, scope, frame, error.Marks & ValueMarks.Dynamic);
                    }
                    catch (Exception ex) when (ex is LangFailure or OperationCanceledException or StopRun)
                    {
                        pending = ex;
                    }
                    finally
                    {
                        handling.RemoveAt(handling.Count - 1);
                    }
                }
            }

            if (statement.Finally is not null && pending is not null && Severity(pending) is 0 or 3) return Rethrow(pending);
            if (statement.Finally is not null && !reserveExhausted) pending = Finally(statement.Finally, env, frame, pending);
            return pending is null ? flow : Rethrow(pending);
        }

        private static Flow Rethrow(Exception ex) => throw ex;

        /// <summary>Runs a finally block. While nothing fails or a catchable error propagates it runs on the main budget; while a controlled fatal failure propagates it runs on a small separate reserve (plan D20).</summary>
        private Exception? Finally(StmtBlock block, Env env, Frame frame, Exception? primary)
        {
            bool reserve = primary is not null && Severity(primary) == 2 && !inReserve;   // a finally inside a finally that already runs on the reserve shares it
            (long savedFuel, long savedFuelLimit, int savedDepthLimit) = (fuel, fuelLimit, depthLimit);
            if (reserve)
            {
                fuel = (primary as LangFailure)?.ReserveUsed ?? 0;   // what earlier finally blocks of this unwinding already spent
                fuelLimit = UnwindFuel;
                inReserve = true;
                depthLimit = depth + UnwindDepth;
            }

            try
            {
                ExecBlock(block, new Env(env), frame);
                return primary;
            }
            catch (Exception ex) when (ex is LangFailure or OperationCanceledException or StopRun)
            {
                if ((reserve || inReserve) && ex is LangFailure { Id: "fuel" or "stack-depth" })
                {
                    bool first = !reserveExhausted;
                    reserveExhausted = true;   // the reserve is spent: the rest of this unwinding runs no more finally blocks, and the primary does not change
                    if (first) Warn("unwind-reserve", "A finally block ran out of its small reserve while the evaluation was stopping: the remaining finally blocks were skipped", block);
                    return primary;
                }

                return Replace(primary, ex);
            }
            finally
            {
                if (reserve)
                {
                    if (primary is LangFailure spent) spent.ReserveUsed = fuel;
                    inReserve = false;
                    fuel = savedFuel;
                    fuelLimit = savedFuelLimit;
                    depthLimit = savedDepthLimit;
                }
            }
        }

        /// <summary>A failure arrived while another one was in flight: it becomes the primary only when it is strictly more severe, the other one is then kept as displaced; otherwise it is attached to the primary as suppressed (at most 4, as plain summaries).</summary>
        private static Exception Replace(Exception? primary, Exception arrived)
        {
            if (primary is null) return arrived;
            if (Severity(arrived) > Severity(primary))
            {
                if (arrived is LangFailure higher && primary is LangFailure old) higher.Displaced.Add(old);
                return arrived;
            }

            if (primary is LangFailure keep && arrived is LangFailure extra)
            {
                keep.SuppressedTotal++;
                if (keep.Suppressed.Count < 4) keep.Suppressed.Add(new FailureSummary(extra.Error?.Type ?? "Error", extra.Message, extra.Error?.Code ?? CatchableCodes.GetValueOrDefault(extra.Id)));
            }

            return primary;
        }

        /// <summary>assert (plan D9r, D27): off, neither the condition nor the message runs (the tick of the statement was charged either way);
        /// on, the condition runs in the assertion fork and a failure is fatal (T2), never catchable.</summary>
        private void Assert(ObserveStmt statement, Env env)
        {
            if (forkMode is not null || context.ObservationOptions is not { Assertions: true }) return;
            Node condition = statement.Positional[0];
            switch (CheckAssertionCondition(condition, env))
            {
                case AssertionCondition.Passed:
                    return;
                case AssertionCondition.False:
                    throw new LangFailure("assert", AssertionMessage(statement, env) ?? "Assertion failed: " + Collapse(SourceOf(condition)), statement);
                case AssertionCondition.Raised:
                    throw new LangFailure("assert", "Assertion failed: the condition raised an error (" + Collapse(SourceOf(condition)) + ")", statement);
                default:
                    throw new LangFailure("assert-unavailable", "The assertion could not be evaluated within its budget: " + Collapse(SourceOf(condition)), statement);
            }
        }

        /// <summary>The message of a failed assertion, evaluated in the assertion fork; null when there is none or it cannot be evaluated (the default text is used then).</summary>
        private string? AssertionMessage(ObserveStmt statement, Env env)
        {
            if (statement.Positional.Count < 2 || context.Assertions.Available == 0) return null;
            string? text = null;
            SourceSite site = SiteResolver.At("song", sourceText, 0, statement.Start, statement.End);
            RunFork(ForkMode.Assert, env, context.Assertions.Available, TimeSpan.MaxValue, site, string.Empty, (fork, scope) =>
            {
                try
                {
                    Val value = fork.Eval(statement.Positional[1], scope);
                    text = value is StrV message && !IsSensitive(value) ? message.V : fork.SafeText(value);
                }
                catch (LangFailure)
                {
                    text = null;
                }
            });
            return text;
        }

        /// <summary><c>throw value</c>: a text becomes an error that remembers the one being handled as its cause; an error value is thrown again as it is.</summary>
        private void Throw(ObserveStmt statement, Env env)
        {
            Val value = Eval(statement.Positional[0], env);
            ErrorV error;
            switch (value)
            {
                case ErrorV existing:
                    error = existing;
                    break;
                case StrV text:
                    error = new ErrorV("Error", text.V, null, null, null, handling.Count > 0 ? handling[^1] : null, "string", [], 0) { Marks = text.Marks };
                    break;
                default:
                    throw new LangFailure("type-mismatch", $"throw needs a message or an Error, not {Describe(value)}", statement.Positional[0]);
            }

            string message = (DeepMarks(error) & ValueMarks.Sensitive) != 0 ? ObsRedactor.Redacted : error.Message;
            throw new LangFailure("throw", message, statement, error.Hint) { Error = error, Marks = control | (DeepMarks(error) & ValueMarks.Dynamic) };
        }

        /// <summary>Makes an error: <c>Error(message, code=..., hint=..., context={...}, cause=err)</c>,
        /// the same for <c>RequirementError</c> and for a declared error with its fields as named arguments.</summary>
        private ErrorV ConstructError(CallNode call, string type, Env env)
        {
            if (!table.TryErrorSpec(type, out List<string> chain, out List<ErrorFieldDecl> declared)) throw new LangFailure("unknown-operator", $"Unknown error \"{type}\"", call);
            string name = chain[0];
            string? message = null, code = null, hint = null;
            RecordV? context = null;
            ErrorV? cause = null;
            Dictionary<string, Val> given = new(StringComparer.Ordinal);
            ValueMarks marks = ValueMarks.None;
            foreach (Node argument in call.Args)
            {
                string? argName = argument is NamedNode named ? named.Name : null;
                Val value = Eval(argument is NamedNode n ? n.Value : argument, env);
                marks |= DeepMarks(value);
                switch (argName)
                {
                    case null when message is null && value is StrV text: message = text.V; break;
                    case null: throw new LangFailure("type-mismatch", $"{name} takes one message (a String) first; everything else is named", argument);
                    case "code" when value is StrV text: code = text.V; break;
                    case "hint" when value is StrV text: hint = text.V; break;
                    case "context" when value is RecordV record: context = record; break;
                    case "cause" when value is ErrorV inner: cause = inner; break;
                    case { } field when declared.Any(f => f.Name == field):
                        if (!given.TryAdd(field, value)) throw new LangFailure("bad-argument", $"\"{field}\" is given twice", argument);
                        break;
                    default:
                        throw new LangFailure("bad-argument",
                        $"{name} has no argument \"{argName}\" of this kind (message, code=String, hint=String, context=Record, "
                        + $"cause=Error{string.Concat(declared.Select(f => ", " + f.Name + "=" + f.Type))})", argument);
                }
            }

            if (message is null) throw new LangFailure("bad-argument", $"{name} needs a message", call);
            List<(string Key, Val Value)> fields = [];
            foreach (ErrorFieldDecl field in declared)
            {
                if (!given.TryGetValue(field.Name, out Val? value)) throw new LangFailure("bad-argument", $"{name} needs the field \"{field.Name}\" ({field.Type})", call);
                fields.Add((field.Name, Coerce(value, field.Type, call, $"{name}.{field.Name}")));
            }

            return new ErrorV(name, message, code, hint, context, cause ?? (handling.Count > 0 ? handling[^1] : null), "error", [],
                0)
            { Marks = marks | (control & ValueMarks.Dynamic), Chain = chain, Fields = fields };
        }

        private static Val ErrorField(ErrorV error, string name, Node at)
        {
            Val result = name switch
            {
                "type" => new StrV(error.Type),
                "message" => new StrV(error.Message),
                "code" => new StrV(error.Code ?? string.Empty),
                "origin" => new StrV(error.Origin),
                "context" => error.Context ?? new RecordV([]),
                "cause" => error.Cause ?? throw new LangFailure("unknown-field", "This error has no cause", at),
                "suppressed" => new ListV([.. error.Suppressed.Select(s => (Val)new RecordV([("type", new StrV(s.Type)), ("message", new StrV(s.Message)), ("code", new StrV(s.Code ?? string.Empty))]))]),
                "suppressedCount" => new IntV(error.SuppressedCount),
                _ when error.Fields.Any(f => f.Key == name) => error.Fields.First(f => f.Key == name).Value,
                _ => throw new LangFailure("unknown-field", $"An error has no field \"{name}\" (it has type, message, code, origin, context, cause, suppressed, suppressedCount)", at),
            };
            return WithMarks(result, error.Marks);
        }
    }
}
