namespace Ample.Language;

/// <summary>The budgets of one observation session (plan D4, D25). Every one of them is a count, never a time.</summary>
/// <param name="MaxPerSite">How many events of one statement are stored (log, debug, warnings, errors).</param>
/// <param name="MaxTotal">How many log and debug events are stored in all.</param>
/// <param name="TraceMaxPerSite">How many trace events of one statement are stored.</param>
/// <param name="TraceMaxTotal">How many trace events are stored in all.</param>
/// <param name="DiagnosticMaxTotal">How many warnings and errors are stored in all (the counters never stop).</param>
/// <param name="MaxSites">How many different statements are followed one by one; the rest share one bucket.</param>
/// <param name="StackMaxDepth">How many frames of a call stack an event keeps.</param>
public sealed record ObservationBudgets(int MaxPerSite = 3, int MaxTotal = 200, int TraceMaxPerSite = 100, int TraceMaxTotal = 5000, int DiagnosticMaxTotal = 200, int MaxSites = 1024, int StackMaxDepth = 24);

/// <summary>What the sink says about one event that is about to be made.</summary>
/// <param name="Registered">Whether the event counts at all (an ordinary log below the level does not: nothing is evaluated for it).</param>
/// <param name="Store">Whether it is stored in full: the message and the context must be evaluated and handed to <see cref="ObservationSink.Add"/>.</param>
/// <param name="Sample">Whether it is not stored but evaluated for the report of the site (<c>lastEvaluated</c>, at the ordinals that are powers of two).</param>
/// <param name="Seq">Its place in the order of registration.</param>
/// <param name="Ordinal">How many events this site has made, this one included.</param>
/// <param name="SiteKey">The key of the site (the shared bucket when there are too many sites).</param>
public readonly record struct EventDecision(bool Registered, bool Store, bool Sample, long Seq, long Ordinal, string SiteKey)
{
    /// <summary>Gets whether the caller has to evaluate the arguments of the statement.</summary>
    public bool Evaluate => Store || Sample;
}

/// <summary>What one site made, with what each number means (plan I9): <c>Count</c> is exact, <c>LastEvaluated</c> is the last event that was evaluated and says at which ordinal, <c>LastSeen</c> is only a position.</summary>
/// <param name="SiteKey">The site.</param>
/// <param name="Class">Its budget class.</param>
/// <param name="Count">All events, exact (saturating).</param>
/// <param name="Stored">How many are stored in full.</param>
/// <param name="Suppressed">How many were counted and not stored.</param>
/// <param name="LastSeenOrdinal">The ordinal of the most recent event.</param>
/// <param name="LastEvaluatedOrdinal">The ordinal of the most recent event that was evaluated (stored or sampled), or 0.</param>
/// <param name="LastEvaluatedJson">The context of that event, or null.</param>
public sealed record SiteCounter(string SiteKey, EventClass Class, long Count, long Stored, long Suppressed, long LastSeenOrdinal, long LastEvaluatedOrdinal, string? LastEvaluatedJson);

/// <summary>
/// The place where the events of one evaluation session are collected (plan D2, D4): bounded in what it stores and in what it follows, exact in what it counts, deterministic (the same calls give the same events, the same
/// order and the same numbers), and independent per class so that trace can never push a warning out. It holds no wall-clock state.
/// </summary>
public sealed class ObservationSink
{
    /// <summary>The key of the bucket that follows every site beyond <see cref="ObservationBudgets.MaxSites"/>.</summary>
    public const string OtherSites = "(other sites)";

    private readonly object gate = new();
    private readonly Dictionary<string, Counter> sites = new(StringComparer.Ordinal);
    private readonly Dictionary<EventClass, Counter> overflow = [];
    private readonly List<LogEvent> events = [];
    private readonly long[] classCount = new long[3];
    private readonly long[] classStored = new long[3];
    private long seq;
    private long observationFuel;
    private TimeSpan observationTime;
    private bool observationTimeExceeded;
    private bool observationLimitReported;

    /// <summary>Initializes a new instance of the <see cref="ObservationSink"/> class.</summary>
    /// <param name="budgets">The budgets, or null for the defaults.</param>
    /// <param name="level">The level of the log view (warnings and errors are registered whatever it is).</param>
    public ObservationSink(ObservationBudgets? budgets = null, LogLevel level = LogLevel.Warning)
    {
        Budgets = budgets ?? new ObservationBudgets();
        Level = level;
    }

    /// <summary>Gets the budgets.</summary>
    public ObservationBudgets Budgets { get; }

    /// <summary>Gets the level.</summary>
    public LogLevel Level { get; }

    /// <summary>The budget class of a level.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The class.</returns>
    public static EventClass ClassOf(EventLevel level) => level switch { EventLevel.Trace => EventClass.Trace, EventLevel.Debug or EventLevel.Info => EventClass.Log, _ => EventClass.Diagnostic };

    /// <summary>The key of a site: the statement (file, line, column), the component instance path and the level. Never the message text: a message that varies must not make a site per message.</summary>
    /// <param name="site">Where the statement stands.</param>
    /// <param name="instance">The component instance path, or null.</param>
    /// <param name="level">The level.</param>
    /// <returns>The key.</returns>
    public static string SiteKeyOf(SourceSite? site, string? instance, EventLevel level) => $"{level}|{site?.Origin}:{site?.Line}:{site?.Col}|{instance}";

    /// <summary>Whether an event of this level is registered at all: warnings and errors always, the rest when the level of the log view allows it. A statement checks this before it evaluates anything.</summary>
    /// <param name="level">The level of the event.</param>
    /// <returns>True when it is registered.</returns>
    public bool Allows(EventLevel level) => Allows(level, Level);

    internal static bool Allows(EventLevel level, LogLevel effective) => level switch
    {
        EventLevel.Warning or EventLevel.Error => true,
        EventLevel.Info => effective >= LogLevel.Info,
        EventLevel.Debug => effective >= LogLevel.Debug,
        _ => effective >= LogLevel.Trace,
    };

    /// <summary>Counts one event of a site and says whether it is stored, sampled or only counted.</summary>
    /// <param name="level">The level.</param>
    /// <param name="siteKey">The site.</param>
    /// <returns>The decision.</returns>
    public EventDecision Begin(EventLevel level, string siteKey) => Begin(level, siteKey, evaluate: true, null);

    internal EventDecision Begin(EventLevel level, string siteKey, bool evaluate, ObservationConfig? config)
    {
        ArgumentNullException.ThrowIfNull(siteKey);
        if (!Allows(level, config?.Level ?? Level)) return default;
        lock (gate)
        {
            EventClass cls = ClassOf(level);
            bool useOverflow = !sites.ContainsKey(siteKey) && sites.Count >= Budgets.MaxSites;
            string key = useOverflow ? OtherSites : siteKey;
            Counter c;
            if (useOverflow)
            {
                if (!overflow.TryGetValue(cls, out c!)) overflow[cls] = c = new Counter(cls);
            }
            else if (!sites.TryGetValue(key, out c!))
            {
                sites[key] = c = new Counter(cls);
            }
            c.Count = c.Count == long.MaxValue ? long.MaxValue : c.Count + 1;
            classCount[(int)cls] = classCount[(int)cls] == long.MaxValue ? long.MaxValue : classCount[(int)cls] + 1;
            c.LastSeen = c.Count;
            ObservationBudgets effective = config?.Budgets ?? Budgets;
            int perSite = cls == EventClass.Trace ? effective.TraceMaxPerSite : effective.MaxPerSite;
            int total = cls switch
            {
                EventClass.Trace => Math.Min(effective.TraceMaxTotal, Budgets.TraceMaxTotal),
                EventClass.Log => Math.Min(effective.MaxTotal, Budgets.MaxTotal),
                _ => Budgets.DiagnosticMaxTotal,
            };
            bool store = evaluate && c.Stored < perSite && classStored[(int)cls] < total;
            bool sampledOrdinal = c.Count > perSite && (c.Count & (c.Count - 1)) == 0;
            bool sample = evaluate && !store && (config?.ExactLast == true || sampledOrdinal);
            if (store) { c.Stored++; classStored[(int)cls]++; }
            else if (c.Suppressed < long.MaxValue) c.Suppressed++;
            return new EventDecision(true, store, sample, ++seq, c.Count, key);
        }
    }

    internal (long Fuel, TimeSpan Time) RemainingObservationBudget(ObservationConfig? config)
    {
        lock (gate)
        {
            int fuelLimit = config?.ObservationFuel ?? 200_000;
            TimeSpan timeLimit = config?.ObservationTime ?? TimeSpan.FromSeconds(2);
            return (Math.Min(20_000, fuelLimit - observationFuel),
                observationTimeExceeded ? TimeSpan.Zero : timeLimit - observationTime);
        }
    }

    internal void ChargeObservation(long fuel, TimeSpan time, bool timedOut, SourceSite site, ObservationConfig? config)
    {
        lock (gate)
        {
            observationFuel += fuel;
            observationTime += time;
            observationTimeExceeded |= timedOut;   // plan D28: the cap of one statement ends evaluation for the rest of the session
            if (observationLimitReported) return;
            bool timeLimit = observationTimeExceeded || observationTime >= (config?.ObservationTime ?? TimeSpan.FromSeconds(2));
            if (!timeLimit && observationFuel < (config?.ObservationFuel ?? 200_000)) return;
            observationLimitReported = true;
            string message = timeLimit ? "Observation stopped at its time budget" : "Observation stopped at its fuel budget";
            events.Add(new LogEvent(++seq, EventLevel.Info, SiteKeyOf(site, null, EventLevel.Info), site, "AMP6007", message, null, null, []));
        }
    }

    /// <summary>Hands in the event that <see cref="Begin(EventLevel, string)"/> asked for.</summary>
    /// <param name="decision">What registration decided.</param>
    /// <param name="event">The event (its <c>Seq</c> is replaced by the one of the decision).</param>
    public void Add(EventDecision decision, LogEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (!decision.Registered || !decision.Evaluate) return;
        lock (gate)
        {
            EventClass cls = ClassOf(@event.Level);
            Counter c = decision.SiteKey == OtherSites ? overflow[cls] : sites[decision.SiteKey];
            c.LastEvaluated = decision.Ordinal;
            c.LastEvaluatedJson = @event.ContextJson;
            if (decision.Store) events.Add(@event with { Seq = decision.Seq, SiteKey = decision.SiteKey });
        }
    }

    /// <summary>Gets the stored events in the order they were registered.</summary>
    public IReadOnlyList<LogEvent> Events { get { lock (gate) return [.. events]; } }

    /// <summary>Gets what every site made, in the order the sites first appeared.</summary>
    public IReadOnlyList<SiteCounter> Sites
    {
        get
        {
            lock (gate)
            {
                return [.. sites.Select(s => Snapshot(s.Key, s.Value)), .. overflow.Values.Select(c => Snapshot(OtherSites, c))];
            }
        }
    }

    private static SiteCounter Snapshot(string key, Counter value) => new(
        key, value.Class, value.Count, value.Stored, value.Suppressed, value.LastSeen, value.LastEvaluated, value.LastEvaluatedJson);

    /// <summary>How many events of a class were counted (exact).</summary>
    /// <param name="cls">The class.</param>
    /// <returns>The count.</returns>
    public long CountOf(EventClass cls) { lock (gate) return classCount[(int)cls]; }

    /// <summary>How many events of a class are stored in full.</summary>
    /// <param name="cls">The class.</param>
    /// <returns>The number.</returns>
    public long StoredOf(EventClass cls) { lock (gate) return classStored[(int)cls]; }

    private sealed class Counter(EventClass cls)
    {
        public EventClass Class { get; } = cls;

        public long Count { get; set; }

        public long Stored { get; set; }

        public long Suppressed { get; set; }

        public long LastSeen { get; set; }

        public long LastEvaluated { get; set; }

        public string? LastEvaluatedJson { get; set; }
    }
}
