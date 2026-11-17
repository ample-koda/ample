using System.Text.Json;

namespace Ample.Language;

/// <summary>
/// How a failure ends an evaluation (plan D7, D24). The class decides whether a <c>catch</c> may handle it and whether a <c>finally</c> runs while it unwinds. The classes are data (<c>spec/failure-classes.json</c>),
/// so no failure is classified by accident and the C# and JS evaluators read the same table.
/// </summary>
public enum FailureClass
{
    /// <summary>T1: an operation error or a user <c>throw</c>/<c>require</c>: a <c>catch</c> may handle it; <c>finally</c> runs.</summary>
    Catchable = 1,

    /// <summary>T2: a controlled fatal (<c>fail</c>, a failed <c>assert</c>, a programming error): no <c>catch</c>; <c>finally</c> runs under the unwind reserve.</summary>
    Fatal = 2,

    /// <summary>T3: a safety stop (fuel, memory, depth, limits, time, cancellation): no user code runs after it.</summary>
    Safety = 3,

    /// <summary>S: a deliberate early stop that is not an error (a bake stage stop): no user code runs after it.</summary>
    Stop = 4,
}

/// <summary>What a catch may see of a failure that was suppressed or displaced: no place, no stack (plan D26).</summary>
/// <param name="Type">The error type.</param>
/// <param name="Message">The message.</param>
/// <param name="Code">The code, or null.</param>
public sealed record FailureSummary(string Type, string Message, string? Code);

/// <summary>
/// A failure as the engine knows it: the diagnostic metadata that tooling reads (place, stack, time stamp) next to the data a program may read. The metadata is private: no AMPLE expression can reach it (plan D26, I16, I25).
/// </summary>
/// <param name="Id">The failure id (<c>divide-by-zero</c>, <c>fuel</c>, <c>fail</c>).</param>
/// <param name="Class">How it ends the evaluation.</param>
/// <param name="Type">The error type name.</param>
/// <param name="Message">The message (never holds a position).</param>
/// <param name="Code">The stable code, or null.</param>
/// <param name="Site">Where it was made.</param>
/// <param name="Frames">The call stack at that moment.</param>
/// <param name="ObservedAtTicks">The wall-clock time stamp of the throw (UTC ticks): diagnostic data, never deterministic, never readable by code.</param>
public sealed record Failure(string Id, FailureClass Class, string Type, string Message, string? Code, SourceSite? Site, IReadOnlyList<StackFrameInfo> Frames, long ObservedAtTicks)
{
    /// <summary>Gets the severity used to decide which of two failures is the primary one: the safety stops and early stops are the highest.</summary>
    public int Rank => Class switch { FailureClass.Catchable => 1, FailureClass.Fatal => 2, _ => 3 };

    /// <summary>Gets the failures that happened while this one unwound and did not replace it (at most four are kept; <see cref="SuppressedCount"/> counts all).</summary>
    public IReadOnlyList<FailureSummary> Suppressed { get; init; } = [];

    /// <summary>Gets how many failures were suppressed.</summary>
    public int SuppressedCount { get; init; }

    /// <summary>Gets the failures that this one replaced because it was strictly more severe (kept whole, with their stacks).</summary>
    public IReadOnlyList<Failure> Displaced { get; init; } = [];

    /// <summary>Gets whether the failure was made under a tainted decision or carries tainted data (provenance).</summary>
    public bool Tainted { get; init; }
}

/// <summary>One row of <c>spec/failure-classes.json</c>.</summary>
/// <param name="Id">The failure id.</param>
/// <param name="Class">The class.</param>
/// <param name="Code">The stable code a program may compare, or null for an id that is never catchable.</param>
/// <param name="Runtimes">Which evaluators raise it (<c>csharp</c>, <c>js</c>).</param>
/// <param name="Refine">Whether the class is a default that the raise sites refine (type-mismatch and bad-argument are raised both for programming errors and for data problems).</param>
public sealed record FailureClassRow(string Id, FailureClass Class, string? Code, IReadOnlyList<string> Runtimes, bool Refine)
{
    /// <summary>Gets whether a <c>catch</c> may handle it.</summary>
    public bool Catchable => Class == FailureClass.Catchable;

    /// <summary>Gets whether <c>finally</c> blocks run while it unwinds.</summary>
    public bool RunsFinally => Class is FailureClass.Catchable or FailureClass.Fatal;
}

/// <summary>The table of failure classes, read from <c>spec/failure-classes.json</c>: the single source that both runtimes use.</summary>
public sealed class FailureClassTable
{
    private readonly Dictionary<string, FailureClassRow> rows;

    private FailureClassTable(Dictionary<string, FailureClassRow> rows) => this.rows = rows;

    /// <summary>Gets every row.</summary>
    public IReadOnlyCollection<FailureClassRow> Rows => rows.Values;

    /// <summary>Reads the table.</summary>
    /// <param name="json">The text of <c>failure-classes.json</c>.</param>
    /// <returns>The table.</returns>
    public static FailureClassTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument doc = JsonDocument.Parse(json);
        Dictionary<string, FailureClassRow> rows = new(StringComparer.Ordinal);
        foreach (JsonElement e in doc.RootElement.GetProperty("failures").EnumerateArray())
        {
            string id = e.GetProperty("id").GetString()!;
            FailureClass cls = e.GetProperty("class").GetString() switch { "T1" => FailureClass.Catchable, "T2" => FailureClass.Fatal, "T3" => FailureClass.Safety, "S" => FailureClass.Stop, var other => throw new FormatException($"Unknown failure class {other} for {id}") };
            string? code = e.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            string[] runtimes = [.. e.GetProperty("runtimes").EnumerateArray().Select(r => r.GetString()!)];
            bool refine = e.TryGetProperty("refine", out JsonElement r2) && r2.GetBoolean();
            if (!rows.TryAdd(id, new FailureClassRow(id, cls, code, runtimes, refine))) throw new FormatException($"Failure id {id} is listed twice");
        }

        return new FailureClassTable(rows);
    }

    /// <summary>Looks a failure id up.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The row, or null when the id is not in the table.</returns>
    public FailureClassRow? Find(string id) => rows.GetValueOrDefault(id);
}
