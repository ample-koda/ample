using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>One state report a game sent: a change of music state with the numbers behind it.</summary>
/// <param name="Seq">The running number of the report.</param>
/// <param name="At">When the server got it.</param>
/// <param name="Session">The name of the game session.</param>
/// <param name="State">The music state the game moved to.</param>
/// <param name="Previous">The state it left, or null.</param>
/// <param name="Bar">The bar of the score the change was scheduled on (as the game counted it), or -1.</param>
/// <param name="Intensity">The smoothed intensity from 0 to 1.</param>
/// <param name="Advantage">The advantage from -1 (losing) to 1 (winning).</param>
/// <param name="Revision">The revision of the score that was playing, or an empty text.</param>
/// <param name="Signals">The raw signals behind the decision.</param>
internal sealed record GameStateEntry(long Seq, DateTimeOffset At, string Session, string State, string? Previous, double Bar, double Intensity, double Advantage, string Revision, IReadOnlyDictionary<string, double> Signals);

/// <summary>
/// The reports a game sends about its music state. They are only remembered in memory (the last few hundred) and shown to whoever watches: a report never changes the project.
/// </summary>
internal sealed class GameStateLog(int capacity = 500)
{
    private const int MaxSignals = 32;
    private readonly object gate = new();
    private readonly Queue<GameStateEntry> entries = new();
    private long seq;

    /// <summary>Checks a report, remembers it and returns it.</summary>
    /// <param name="body">The JSON body the game sent.</param>
    /// <param name="now">The time of the report.</param>
    /// <exception cref="AmpleException">AMP5285 when the report is not valid.</exception>
    public GameStateEntry Add(JsonElement body, DateTimeOffset now)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new AmpleException("AMP5285", "A state report is a JSON object.");
        }

        string state = body.TryGetProperty("state", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : string.Empty;
        if (!GameScoreDefinition.IsId(state))
        {
            throw new AmpleException("AMP5285", "A state report needs \"state\": 1 to 40 lowercase letters, digits and -.");
        }

        string? previous = body.TryGetProperty("previous", out JsonElement p) && p.ValueKind == JsonValueKind.String && GameScoreDefinition.IsId(p.GetString()) ? p.GetString() : null;
        string session = Short(body, "session", "game", 64);
        string revision = Short(body, "revision", string.Empty, 64);
        Dictionary<string, double> signals = new(StringComparer.Ordinal);
        if (body.TryGetProperty("signals", out JsonElement sig) && sig.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in sig.EnumerateObject())
            {
                if (signals.Count >= MaxSignals)
                {
                    break;
                }

                if (property.Name.Length is > 0 and <= 40 && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double value) && double.IsFinite(value))
                {
                    signals[property.Name] = value;
                }
            }
        }

        lock (gate)
        {
            GameStateEntry entry = new(++seq, now, session, state, previous, Number(body, "bar", -1, -1, 1_000_000), Number(body, "intensity", 0, 0, 1), Number(body, "advantage", 0, -1, 1), revision, signals);
            entries.Enqueue(entry);
            while (entries.Count > capacity)
            {
                entries.Dequeue();
            }

            return entry;
        }
    }

    /// <summary>Gets the most recent reports, newest last.</summary>
    /// <param name="count">How many at most.</param>
    public IReadOnlyList<GameStateEntry> Recent(int count)
    {
        lock (gate)
        {
            return [.. entries.TakeLast(Math.Clamp(count, 1, capacity))];
        }
    }

    /// <summary>Gets the latest report, or null when the game has not sent any.</summary>
    public GameStateEntry? Latest()
    {
        lock (gate)
        {
            return entries.Count == 0 ? null : entries.Last();
        }
    }

    private static string Short(JsonElement body, string key, string fallback, int max)
    {
        if (!body.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString()))
        {
            return fallback;
        }

        string text = e.GetString()!.Trim();
        return text.Length <= max && text.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':') ? text : fallback;
    }

    private static double Number(JsonElement body, string key, double fallback, double min, double max)
    {
        if (body.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double value) && double.IsFinite(value))
        {
            return Math.Clamp(value, min, max);
        }

        return fallback;
    }
}
