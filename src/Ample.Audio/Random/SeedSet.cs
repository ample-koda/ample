using System.Globalization;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Audio.Random;

/// <summary>Where a seed value came from.</summary>
public enum SeedOrigin
{
    /// <summary>Derived from the master seed.</summary>
    Derived,

    /// <summary>Typed or chosen by the user.</summary>
    UserSet,

    /// <summary>Taken from an imported plan.</summary>
    Imported,
}

/// <summary>One change of a seed, kept for ever in the seed history.</summary>
/// <param name="OldValue">The value before.</param>
/// <param name="NewValue">The value after.</param>
/// <param name="Reason">Why it was changed.</param>
/// <param name="At">When (from the injected clock).</param>
public sealed record SeedChange(ulong OldValue, ulong NewValue, string Reason, DateTimeOffset At);

/// <summary>A named seed with its lock state and history.</summary>
public sealed class SeedEntry
{
    internal SeedEntry(string name, ulong value, bool locked, SeedOrigin origin)
    {
        Name = name;
        Value = value;
        Locked = locked;
        Origin = origin;
    }

    /// <summary>Gets the seed name (for example <c>seed</c> or <c>lm_seed</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the current value.</summary>
    public ulong Value { get; internal set; }

    /// <summary>Gets a value indicating whether the seed is locked (the default). Changing a locked seed is refused.</summary>
    public bool Locked { get; internal set; }

    /// <summary>Gets where the current value came from.</summary>
    public SeedOrigin Origin { get; internal set; }

    /// <summary>Gets the changes, oldest first.</summary>
    public List<SeedChange> History { get; } = [];
}

/// <summary>The seeds of one block. Seeds are locked by default; changing one needs an explicit unlock and leaves a history entry.</summary>
public sealed class SeedSet
{
    private readonly SortedDictionary<string, SeedEntry> entries = new(StringComparer.Ordinal);

    /// <summary>Gets the entries in name order.</summary>
    public IReadOnlyCollection<SeedEntry> Entries => entries.Values;

    /// <summary>Gets an entry, or null.</summary>
    public SeedEntry? Get(string name) => entries.GetValueOrDefault(name);

    /// <summary>Creates a seed set whose values are derived from the master seed, the block id and the seed name; all seeds start locked.</summary>
    public static SeedSet Derive(ulong masterSeed, string blockId, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        SeedSet set = new();
        foreach (string name in names)
        {
            set.entries[name] = new SeedEntry(name, SeedDeriver.Derive(masterSeed, "block", blockId, "seed", name), locked: true, SeedOrigin.Derived);
        }

        return set;
    }

    /// <summary>Unlocks a seed so it can be changed.</summary>
    public void Unlock(string name) => Require(name).Locked = false;

    /// <summary>Locks a seed.</summary>
    public void Lock(string name) => Require(name).Locked = true;

    /// <summary>Changes a seed. Refused while the seed is locked. The change is recorded in the history.</summary>
    /// <exception cref="InvalidOperationException">The seed is locked.</exception>
    public void Set(string name, ulong value, string reason, IClock clock, SeedOrigin origin = SeedOrigin.UserSet)
    {
        ArgumentNullException.ThrowIfNull(clock);
        SeedEntry entry = Require(name);
        if (entry.Locked)
        {
            throw new InvalidOperationException($"Seed '{name}' is locked. Unlock it first: changing it probably makes reproduction of earlier takes impossible.");
        }

        entry.History.Add(new SeedChange(entry.Value, value, reason, clock.UtcNow));
        entry.Value = value;
        entry.Origin = origin;
    }

    /// <summary>Serialises to canonical JSON: entries in name order, fixed property order, 64-bit values as decimal strings, times as UTC round-trip text.</summary>
    public string ToJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("rngVersion", RngVersion.Current);
            writer.WriteStartArray("seeds");
            foreach (SeedEntry e in entries.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("name", e.Name);
                writer.WriteString("value", e.Value.ToString(CultureInfo.InvariantCulture));
                writer.WriteBoolean("locked", e.Locked);
                writer.WriteString("origin", e.Origin.ToString());
                writer.WriteStartArray("history");
                foreach (SeedChange c in e.History)
                {
                    writer.WriteStartObject();
                    writer.WriteString("old", c.OldValue.ToString(CultureInfo.InvariantCulture));
                    writer.WriteString("new", c.NewValue.ToString(CultureInfo.InvariantCulture));
                    writer.WriteString("reason", c.Reason);
                    writer.WriteString("at", c.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads the JSON written by <see cref="ToJson"/>.</summary>
    public static SeedSet FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        SeedSet set = new();
        foreach (JsonElement e in doc.RootElement.GetProperty("seeds").EnumerateArray())
        {
            string name = e.GetProperty("name").GetString()!;
            SeedEntry entry = new(name, ulong.Parse(e.GetProperty("value").GetString()!, CultureInfo.InvariantCulture), e.GetProperty("locked").GetBoolean(), Enum.Parse<SeedOrigin>(e.GetProperty("origin").GetString()!));
            foreach (JsonElement c in e.GetProperty("history").EnumerateArray())
            {
                entry.History.Add(new SeedChange(ulong.Parse(c.GetProperty("old").GetString()!, CultureInfo.InvariantCulture), ulong.Parse(c.GetProperty("new").GetString()!, CultureInfo.InvariantCulture), c.GetProperty("reason").GetString()!, DateTimeOffset.Parse(c.GetProperty("at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }

            set.entries[name] = entry;
        }

        return set;
    }

    private SeedEntry Require(string name) => entries.TryGetValue(name, out SeedEntry? e) ? e : throw new KeyNotFoundException($"There is no seed named '{name}'.");
}
