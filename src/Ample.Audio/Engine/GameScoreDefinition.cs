using System.Globalization;
using System.Text.Json;

namespace Ample.Audio.Engine;

/// <summary>One layer of a game score: a block of the song whose output is cut into a loopable stem.</summary>
/// <param name="Id">The layer name used in states and file names (lowercase letters, digits and -).</param>
/// <param name="Block">The block of the song whose take is the stem.</param>
public sealed record GameScoreLayer(string Id, string Block);

/// <summary>A one-shot sound (a stinger) cut out of a block of the song.</summary>
/// <param name="Id">The stinger name.</param>
/// <param name="Block">The block whose take holds the stinger.</param>
/// <param name="FromBar">The first bar of the stinger in the song (1 is the start).</param>
/// <param name="Bars">How many bars are kept.</param>
public sealed record GameScoreStinger(string Id, string Block, int FromBar, int Bars);

/// <summary>A music state: how loud each layer is (0 to 1) while the game is in it.</summary>
/// <param name="Id">The state name, such as <c>calm</c>, <c>tension</c> or <c>victory</c>.</param>
/// <param name="Levels">The level per layer id; a layer that is not named is silent.</param>
/// <param name="Stinger">A stinger that plays once when this state is entered, or null.</param>
/// <param name="Fallback">The state the game uses when it asks for a state the score does not have a better answer for, or null.</param>
public sealed record GameScoreState(string Id, IReadOnlyDictionary<string, double> Levels, string? Stinger, string? Fallback);

/// <summary>How a change from one state to another is made.</summary>
/// <param name="From">The state it leaves, or <c>*</c> for any.</param>
/// <param name="To">The state it enters, or <c>*</c> for any.</param>
/// <param name="Quantize"><c>immediate</c>, <c>beat</c> or <c>bar</c>: where the change starts.</param>
/// <param name="FadeBars">How long the levels take to move, in bars.</param>
public sealed record GameScoreTransition(string From, string To, string Quantize, double FadeBars);

/// <summary>An alternative song for the same layers and states (another faction, another key).</summary>
/// <param name="Id">The variant name.</param>
/// <param name="Song">The song file of the variant (project relative).</param>
public sealed record GameScoreVariant(string Id, string Song);

/// <summary>
/// The definition of a game score, kept as a JSON file in the project (by default <c>game/score.json</c>). It names a song, the layers (blocks of that song),
/// the music states with a level per layer, the loop region and the transition rules. See <c>docs/game-score.md</c>.
/// </summary>
/// <param name="Name">The score name (also the start of the file names it makes).</param>
/// <param name="Song">The song file (project relative).</param>
/// <param name="LoopFromBar">The first bar of the loop.</param>
/// <param name="LoopBars">The length of the loop in bars.</param>
/// <param name="InitialState">The state the game starts in.</param>
/// <param name="Layers">The layers.</param>
/// <param name="Stingers">The stingers.</param>
/// <param name="States">The states.</param>
/// <param name="Default">The transition used when no rule matches.</param>
/// <param name="Rules">The transition rules, first match wins.</param>
/// <param name="Variants">The alternative songs.</param>
public sealed record GameScoreDefinition(
    string Name,
    string Song,
    int LoopFromBar,
    int LoopBars,
    string InitialState,
    IReadOnlyList<GameScoreLayer> Layers,
    IReadOnlyList<GameScoreStinger> Stingers,
    IReadOnlyList<GameScoreState> States,
    GameScoreTransition Default,
    IReadOnlyList<GameScoreTransition> Rules,
    IReadOnlyList<GameScoreVariant> Variants)
{
    private const int MaxLayers = 32;
    private const int MaxStates = 32;
    private const int MaxVariants = 8;

    /// <summary>Gets the quantize values a transition may use.</summary>
    public static IReadOnlyList<string> QuantizeValues { get; } = ["immediate", "beat", "bar"];

    /// <summary>Gets whether a text is a valid score, layer, state, stinger or variant name: 1 to 40 lowercase letters, digits and -.</summary>
    public static bool IsId(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 40 || text[0] == '-')
        {
            return false;
        }

        foreach (char ch in text)
        {
            if (!(ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' || ch == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads and checks a score definition.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="definition">The definition when there are no problems, otherwise null.</param>
    /// <param name="problems">What is wrong, one sentence each (empty when the definition is good).</param>
    /// <returns>True when the definition is usable.</returns>
    public static bool TryParse(string json, out GameScoreDefinition? definition, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(json);
        List<string> found = [];
        definition = null;
        problems = found;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            found.Add("The score is not valid JSON: " + ex.Message);
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                found.Add("The score must be a JSON object.");
                return false;
            }

            if (!(root.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int v) && v == 1))
            {
                found.Add("The score needs \"version\": 1.");
            }

            string name = Text(root, "name", found, true);
            if (name.Length > 0 && !IsId(name))
            {
                found.Add("The name of the score is 1 to 40 lowercase letters, digits and - (for example \"demo-score\").");
            }

            string song = Text(root, "song", found, true);
            if (song.Length > 0 && !song.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase))
            {
                found.Add("\"song\" must be a .amplet file of the project.");
            }

            int loopFrom = Int(root, "loopFromBar", 1, found);
            int loopBars = Int(root, "loopBars", -1, found);
            if (loopFrom < 1 || loopFrom > 10_000)
            {
                found.Add("\"loopFromBar\" must be 1 or more.");
            }

            if (loopBars < 1 || loopBars > 256)
            {
                found.Add("\"loopBars\" is required and must be from 1 to 256.");
            }

            List<GameScoreLayer> layers = ReadLayers(root, found);
            List<GameScoreStinger> stingers = ReadStingers(root, found);
            HashSet<string> sounds = new(StringComparer.Ordinal);
            foreach (string id in layers.Select(l => l.Id).Concat(stingers.Select(s => s.Id)))
            {
                if (!sounds.Add(id))
                {
                    found.Add($"The name '{id}' is used by more than one layer or stinger.");
                }
            }

            List<GameScoreState> states = ReadStates(root, layers, stingers, found);
            string initial = root.TryGetProperty("initialState", out JsonElement init) && init.ValueKind == JsonValueKind.String ? init.GetString()! : states.Count > 0 ? states[0].Id : string.Empty;
            if (states.Count > 0 && !states.Any(s => s.Id == initial))
            {
                found.Add($"The initial state '{initial}' is not one of the states.");
            }

            GameScoreTransition defaultTransition = new("*", "*", "bar", 1.0);
            if (root.TryGetProperty("transitions", out JsonElement transitions) && transitions.ValueKind == JsonValueKind.Object)
            {
                if (transitions.TryGetProperty("default", out JsonElement d) && d.ValueKind == JsonValueKind.Object)
                {
                    defaultTransition = ReadTransition(d, "*", "*", found);
                }
            }

            List<GameScoreTransition> rules = [];
            if (root.TryGetProperty("transitions", out JsonElement t2) && t2.ValueKind == JsonValueKind.Object && t2.TryGetProperty("rules", out JsonElement ruleArray) && ruleArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement rule in ruleArray.EnumerateArray())
                {
                    string from = rule.ValueKind == JsonValueKind.Object ? Text(rule, "from", found, false) : string.Empty;
                    string to = rule.ValueKind == JsonValueKind.Object ? Text(rule, "to", found, false) : string.Empty;
                    from = from.Length == 0 ? "*" : from;
                    to = to.Length == 0 ? "*" : to;
                    foreach (string end in new[] { from, to })
                    {
                        if (end != "*" && !states.Any(s => s.Id == end))
                        {
                            found.Add($"A transition rule names the state '{end}', which does not exist.");
                        }
                    }

                    rules.Add(ReadTransition(rule, from, to, found));
                }
            }

            List<GameScoreVariant> variants = ReadVariants(root, found);
            if (found.Count > 0)
            {
                return false;
            }

            definition = new GameScoreDefinition(name, song, loopFrom, loopBars, initial, layers, stingers, states, defaultTransition, rules, variants);
            return true;
        }
    }

    /// <summary>Finds the transition for a change of state: the first rule that matches, otherwise the default.</summary>
    /// <param name="from">The state that is left.</param>
    /// <param name="to">The state that is entered.</param>
    public GameScoreTransition TransitionFor(string from, string to)
    {
        foreach (GameScoreTransition rule in Rules)
        {
            if ((rule.From == "*" || rule.From == from) && (rule.To == "*" || rule.To == to))
            {
                return rule;
            }
        }

        return Default;
    }

    private static string Text(JsonElement owner, string key, List<string> problems, bool required)
    {
        if (owner.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
        {
            return e.GetString()!.Trim();
        }

        if (required)
        {
            problems.Add($"\"{key}\" is required (text).");
        }

        return string.Empty;
    }

    private static int Int(JsonElement owner, string key, int fallback, List<string> problems)
    {
        if (!owner.TryGetProperty(key, out JsonElement e))
        {
            return fallback;
        }

        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int value))
        {
            return value;
        }

        problems.Add($"\"{key}\" must be a whole number.");
        return fallback;
    }

    private static double Number(JsonElement owner, string key, double fallback, double min, double max, List<string> problems)
    {
        if (!owner.TryGetProperty(key, out JsonElement e))
        {
            return fallback;
        }

        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double value) && value >= min && value <= max)
        {
            return value;
        }

        problems.Add($"\"{key}\" must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    private static List<GameScoreLayer> ReadLayers(JsonElement root, List<string> problems)
    {
        List<GameScoreLayer> layers = [];
        if (!root.TryGetProperty("layers", out JsonElement array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            problems.Add("\"layers\" is required: a list of { \"id\": ..., \"block\": ... }.");
            return layers;
        }

        if (array.GetArrayLength() > MaxLayers)
        {
            problems.Add($"A score has at most {MaxLayers} layers.");
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            string id = item.ValueKind == JsonValueKind.Object ? Text(item, "id", problems, true) : string.Empty;
            string block = item.ValueKind == JsonValueKind.Object ? Text(item, "block", problems, true) : string.Empty;
            if (id.Length > 0 && !IsId(id))
            {
                problems.Add($"The layer name '{id}' is 1 to 40 lowercase letters, digits and -.");
            }

            layers.Add(new GameScoreLayer(id, block));
        }

        return layers;
    }

    private static List<GameScoreStinger> ReadStingers(JsonElement root, List<string> problems)
    {
        List<GameScoreStinger> stingers = [];
        if (!root.TryGetProperty("stingers", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return stingers;
        }

        if (array.GetArrayLength() > MaxLayers)
        {
            problems.Add($"A score has at most {MaxLayers} stingers.");
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            string id = item.ValueKind == JsonValueKind.Object ? Text(item, "id", problems, true) : string.Empty;
            string block = item.ValueKind == JsonValueKind.Object ? Text(item, "block", problems, true) : string.Empty;
            int from = item.ValueKind == JsonValueKind.Object ? Int(item, "fromBar", 1, problems) : 1;
            int bars = item.ValueKind == JsonValueKind.Object ? Int(item, "bars", 2, problems) : 2;
            if (id.Length > 0 && !IsId(id))
            {
                problems.Add($"The stinger name '{id}' is 1 to 40 lowercase letters, digits and -.");
            }

            if (from < 1 || bars < 1 || bars > 16)
            {
                problems.Add($"The stinger '{id}' needs fromBar of 1 or more and bars from 1 to 16.");
            }

            stingers.Add(new GameScoreStinger(id, block, from, bars));
        }

        return stingers;
    }

    private static List<GameScoreState> ReadStates(JsonElement root, List<GameScoreLayer> layers, List<GameScoreStinger> stingers, List<string> problems)
    {
        List<GameScoreState> states = [];
        if (!root.TryGetProperty("states", out JsonElement array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            problems.Add("\"states\" is required: a list of { \"id\": ..., \"levels\": { layer: 0..1 } }.");
            return states;
        }

        if (array.GetArrayLength() > MaxStates)
        {
            problems.Add($"A score has at most {MaxStates} states.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<(string State, string Fallback)> fallbacks = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                problems.Add("Each state must be an object.");
                continue;
            }

            string id = Text(item, "id", problems, true);
            if (id.Length > 0 && !IsId(id))
            {
                problems.Add($"The state name '{id}' is 1 to 40 lowercase letters, digits and -.");
            }

            if (id.Length > 0 && !seen.Add(id))
            {
                problems.Add($"The state '{id}' is defined twice.");
            }

            Dictionary<string, double> levels = new(StringComparer.Ordinal);
            if (item.TryGetProperty("levels", out JsonElement levelObject) && levelObject.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty level in levelObject.EnumerateObject())
                {
                    if (!layers.Any(l => l.Id == level.Name))
                    {
                        problems.Add($"The state '{id}' sets the level of '{level.Name}', which is not a layer.");
                    }
                    else if (level.Value.ValueKind != JsonValueKind.Number || !level.Value.TryGetDouble(out double amount) || amount < 0 || amount > 1)
                    {
                        problems.Add($"The level of '{level.Name}' in state '{id}' must be a number from 0 to 1.");
                    }
                    else
                    {
                        levels[level.Name] = amount;
                    }
                }
            }
            else
            {
                problems.Add($"The state '{id}' needs \"levels\".");
            }

            string? stinger = item.TryGetProperty("stinger", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (stinger is not null && !stingers.Any(x => x.Id == stinger))
            {
                problems.Add($"The state '{id}' plays the stinger '{stinger}', which does not exist.");
            }

            string? fallback = item.TryGetProperty("fallback", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (fallback is not null)
            {
                fallbacks.Add((id, fallback));
            }

            states.Add(new GameScoreState(id, levels, stinger, fallback));
        }

        foreach ((string state, string fallback) in fallbacks)
        {
            if (state == fallback || !states.Any(s => s.Id == fallback))
            {
                problems.Add($"The fallback of state '{state}' must be another state of the score.");
            }
        }

        return states;
    }

    private static GameScoreTransition ReadTransition(JsonElement owner, string from, string to, List<string> problems)
    {
        string quantize = owner.TryGetProperty("quantize", out JsonElement q) && q.ValueKind == JsonValueKind.String ? q.GetString()! : "bar";
        if (!QuantizeValues.Contains(quantize))
        {
            problems.Add($"\"quantize\" must be immediate, beat or bar (not '{quantize}').");
            quantize = "bar";
        }

        double fade = Number(owner, "fadeBars", 1.0, 0, 16, problems);
        return new GameScoreTransition(from, to, quantize, fade);
    }

    private static List<GameScoreVariant> ReadVariants(JsonElement root, List<string> problems)
    {
        List<GameScoreVariant> variants = [];
        if (!root.TryGetProperty("variants", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return variants;
        }

        if (array.GetArrayLength() > MaxVariants)
        {
            problems.Add($"A score has at most {MaxVariants} variants.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement item in array.EnumerateArray())
        {
            string id = item.ValueKind == JsonValueKind.Object ? Text(item, "id", problems, true) : string.Empty;
            string song = item.ValueKind == JsonValueKind.Object ? Text(item, "song", problems, true) : string.Empty;
            if (id.Length > 0 && (!IsId(id) || id == "default"))
            {
                problems.Add($"The variant name '{id}' is 1 to 40 lowercase letters, digits and - (and not 'default').");
            }

            if (id.Length > 0 && !seen.Add(id))
            {
                problems.Add($"The variant '{id}' is defined twice.");
            }

            if (song.Length > 0 && !song.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"The song of variant '{id}' must be a .amplet file.");
            }

            variants.Add(new GameScoreVariant(id, song));
        }

        return variants;
    }
}
