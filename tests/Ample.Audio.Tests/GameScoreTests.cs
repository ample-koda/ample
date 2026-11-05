using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

/// <summary>Game score: the definition file, the cut into loopable stems and the manifest the game reads.</summary>
public sealed class GameScoreTests
{
    private const string Good = """
        {
          "version": 1,
          "name": "demo-score",
          "song": "songs/score.amplet",
          "loopFromBar": 1,
          "loopBars": 2,
          "initialState": "calm",
          "layers": [
            { "id": "pad", "block": "pad" },
            { "id": "bass", "block": "bass" },
            { "id": "drums", "block": "drums" }
          ],
          "stingers": [ { "id": "win-hit", "block": "hits", "fromBar": 1, "bars": 1 } ],
          "states": [
            { "id": "calm", "levels": { "pad": 1 } },
            { "id": "tension", "levels": { "pad": 1, "bass": 0.8, "drums": 0.5 }, "fallback": "calm" },
            { "id": "victory", "levels": { "pad": 1, "bass": 0.5 }, "stinger": "win-hit" }
          ],
          "transitions": {
            "default": { "quantize": "bar", "fadeBars": 1 },
            "rules": [ { "from": "*", "to": "victory", "quantize": "beat", "fadeBars": 0.5 } ]
          },
          "variants": [ { "id": "soviet", "song": "songs/score-soviet.amplet" } ]
        }
        """;

    private static GameScoreDefinition Parse(string json)
    {
        Assert.True(GameScoreDefinition.TryParse(json, out GameScoreDefinition? definition, out IReadOnlyList<string> problems), string.Join(" | ", problems));
        return definition!;
    }

    private static IReadOnlyList<string> Problems(string json)
    {
        Assert.False(GameScoreDefinition.TryParse(json, out GameScoreDefinition? definition, out IReadOnlyList<string> problems));
        Assert.Null(definition);
        Assert.NotEmpty(problems);
        return problems;
    }

    private static StereoBuffer Constant(int frames, short value) => new(Enumerable.Repeat(value, frames).ToArray(), Enumerable.Repeat(value, frames).ToArray());

    private static GameScoreProvenance Provenance() => new("0.1.0", "1", "4242", "songs/score.amplet", "aa", "bb", [new GameScoreTake("pad", "t1", "p1")]);

    private static Dictionary<string, StereoBuffer> Audio(int frames) => new(StringComparer.Ordinal)
    {
        ["pad"] = Constant(frames, 1000),
        ["bass"] = Constant(frames, 2000),
        ["drums"] = Constant(frames, 3000),
        ["hits"] = Constant(frames, 4000),
    };

    [Fact]
    public void A_good_definition_is_read_with_its_layers_states_stinger_and_rules()
    {
        GameScoreDefinition d = Parse(Good);
        Assert.Equal("demo-score", d.Name);
        Assert.Equal(["pad", "bass", "drums"], d.Layers.Select(l => l.Id));
        Assert.Equal("calm", d.InitialState);
        Assert.Equal(0.8, d.States.Single(s => s.Id == "tension").Levels["bass"]);
        Assert.Equal("win-hit", d.States.Single(s => s.Id == "victory").Stinger);
        Assert.Equal("soviet", Assert.Single(d.Variants).Id);
    }

    [Fact]
    public void The_first_rule_that_matches_decides_the_transition_and_the_default_covers_the_rest()
    {
        GameScoreDefinition d = Parse(Good);
        Assert.Equal("beat", d.TransitionFor("tension", "victory").Quantize);
        Assert.Equal(0.5, d.TransitionFor("calm", "victory").FadeBars);
        Assert.Equal("bar", d.TransitionFor("calm", "tension").Quantize);
    }

    [Fact]
    public void The_initial_state_defaults_to_the_first_state()
    {
        GameScoreDefinition d = Parse(Good.Replace("\"initialState\": \"calm\",", string.Empty, StringComparison.Ordinal));
        Assert.Equal("calm", d.InitialState);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Garbage_is_refused_with_problems(string json)
    {
        Assert.NotEmpty(Problems(json));
    }

    [Fact]
    public void A_state_may_only_name_layers_that_exist_with_levels_from_0_to_1()
    {
        IReadOnlyList<string> unknown = Problems(Good.Replace("\"bass\": 0.8", "\"strings\": 0.8", StringComparison.Ordinal));
        Assert.Contains(unknown, p => p.Contains("strings", StringComparison.Ordinal));
        IReadOnlyList<string> loud = Problems(Good.Replace("\"bass\": 0.8", "\"bass\": 1.5", StringComparison.Ordinal));
        Assert.Contains(loud, p => p.Contains("0 to 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_names_unknown_stingers_and_bad_fallbacks_are_found()
    {
        Assert.Contains(Problems(Good.Replace("\"id\": \"bass\"", "\"id\": \"pad\"", StringComparison.Ordinal)), p => p.Contains("more than one", StringComparison.Ordinal));
        Assert.Contains(Problems(Good.Replace("\"stinger\": \"win-hit\"", "\"stinger\": \"nope\"", StringComparison.Ordinal)), p => p.Contains("nope", StringComparison.Ordinal));
        Assert.Contains(Problems(Good.Replace("\"fallback\": \"calm\"", "\"fallback\": \"tension\"", StringComparison.Ordinal)), p => p.Contains("fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void The_loop_the_initial_state_and_the_transitions_are_checked()
    {
        Assert.Contains(Problems(Good.Replace("\"loopBars\": 2", "\"loopBars\": 0", StringComparison.Ordinal)), p => p.Contains("loopBars", StringComparison.Ordinal));
        Assert.Contains(Problems(Good.Replace("\"initialState\": \"calm\"", "\"initialState\": \"boom\"", StringComparison.Ordinal)), p => p.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(Problems(Good.Replace("\"quantize\": \"beat\"", "\"quantize\": \"sometime\"", StringComparison.Ordinal)), p => p.Contains("quantize", StringComparison.Ordinal));
        Assert.Contains(Problems(Good.Replace("\"to\": \"victory\"", "\"to\": \"gone\"", StringComparison.Ordinal)), p => p.Contains("gone", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("demo-score", true)]
    [InlineData("a", true)]
    [InlineData("Red", false)]
    [InlineData("-red", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void Ids_are_short_lowercase_names(string id, bool valid)
    {
        Assert.Equal(valid, GameScoreDefinition.IsId(id));
    }

    [Fact]
    public void A_build_cuts_equal_length_loop_stems_on_whole_bars_and_names_the_files()
    {
        GameScoreDefinition d = Parse(Good);
        const int rate = 44100;
        const double tempo = 120;
        int bar = (int)(rate * 60.0 / tempo * 4);
        GameScoreBuild build = GameScoreBuilder.Build(d, null, Audio(bar * 4), rate, tempo, 4, 4, Provenance());

        Assert.Equal("demo-score.manifest.json", build.ManifestFile);
        Assert.Equal(["demo-score.pad.wav", "demo-score.bass.wav", "demo-score.drums.wav", "demo-score.win-hit.wav"], build.Files.Select(f => f.Name));
        Assert.All(build.Files.Where(f => f.Role == "layer"), f => Assert.Equal(bar * 2, f.Frames));
        Assert.Equal(bar, build.Files.Single(f => f.Role == "stinger").Frames);
        Assert.Empty(build.Warnings);
        (StereoBuffer back, int backRate) = StereoWav.Decode(build.Files[0].Data);
        Assert.Equal(rate, backRate);
        Assert.Equal(bar * 2, back.Length);
        Assert.Equal(1000, back.Left[10]);
    }

    [Fact]
    public void The_manifest_carries_the_contract_the_game_reads()
    {
        GameScoreDefinition d = Parse(Good);
        const int rate = 44100;
        int bar = (int)(rate * 60.0 / 120 * 4);
        GameScoreBuild build = GameScoreBuilder.Build(d, null, Audio(bar * 3), rate, 120, 4, 4, Provenance());

        using JsonDocument doc = JsonDocument.Parse(build.ManifestJson);
        JsonElement root = doc.RootElement;
        Assert.Equal("ample-game-score", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(120, root.GetProperty("tempo").GetDouble());
        Assert.Equal(rate, root.GetProperty("sampleRate").GetInt32());
        Assert.Equal(bar * 2, root.GetProperty("loop").GetProperty("frames").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("variant").ValueKind);
        Assert.Equal(3, root.GetProperty("layers").GetArrayLength());
        JsonElement tension = root.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "tension");
        Assert.Equal(0.8, tension.GetProperty("levels").GetProperty("bass").GetDouble());
        Assert.Equal(0.0, root.GetProperty("states").EnumerateArray().First().GetProperty("levels").GetProperty("drums").GetDouble());
        Assert.Equal("t1", root.GetProperty("provenance").GetProperty("takes")[0].GetProperty("takeId").GetString());
        Assert.Equal("4242", root.GetProperty("provenance").GetProperty("masterSeed").GetString());
        Assert.Equal(build.Revision, root.GetProperty("revision").GetString());
        Assert.StartsWith("/api/game/stems/", root.GetProperty("layers")[0].GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_input_gives_exactly_the_same_files_and_manifest_and_a_variant_changes_the_names()
    {
        GameScoreDefinition d = Parse(Good);
        int bar = (int)(44100 * 60.0 / 120 * 4);
        GameScoreBuild a = GameScoreBuilder.Build(d, null, Audio(bar * 3), 44100, 120, 4, 4, Provenance());
        GameScoreBuild b = GameScoreBuilder.Build(d, null, Audio(bar * 3), 44100, 120, 4, 4, Provenance());
        Assert.Equal(a.ManifestJson, b.ManifestJson);
        Assert.Equal(a.Revision, b.Revision);

        GameScoreBuild v = GameScoreBuilder.Build(d, "soviet", Audio(bar * 3), 44100, 120, 4, 4, Provenance());
        Assert.Equal("demo-score.soviet.manifest.json", v.ManifestFile);
        Assert.Equal("demo-score.soviet.pad.wav", v.Files[0].Name);
        using JsonDocument doc = JsonDocument.Parse(v.ManifestJson);
        Assert.Equal("soviet", doc.RootElement.GetProperty("variant").GetString());
    }

    [Fact]
    public void A_song_shorter_than_the_loop_a_missing_block_and_a_waltz_are_refused()
    {
        GameScoreDefinition d = Parse(Good);
        int bar = (int)(44100 * 60.0 / 120 * 4);
        AmpleException tooShort = Assert.Throws<AmpleException>(() => GameScoreBuilder.Build(d, null, Audio(bar), 44100, 120, 4, 4, Provenance()));
        Assert.Contains("loop", tooShort.Message, StringComparison.Ordinal);

        Dictionary<string, StereoBuffer> missing = Audio(bar * 3);
        missing.Remove("bass");
        Assert.Throws<AmpleException>(() => GameScoreBuilder.Build(d, null, missing, 44100, 120, 4, 4, Provenance()));
        Assert.Throws<AmpleException>(() => GameScoreBuilder.Build(d, null, Audio(bar * 3), 44100, 120, 3, 4, Provenance()));
    }

    [Fact]
    public void A_silent_stem_is_a_warning_not_a_failure()
    {
        GameScoreDefinition d = Parse(Good);
        int bar = (int)(44100 * 60.0 / 120 * 4);
        Dictionary<string, StereoBuffer> audio = Audio(bar * 3);
        audio["drums"] = Constant(bar * 3, 0);
        GameScoreBuild build = GameScoreBuilder.Build(d, null, audio, 44100, 120, 4, 4, Provenance());
        Assert.Contains(build.Warnings, w => w.Contains("drums", StringComparison.Ordinal));
    }

    [Fact]
    public void Mono_block_audio_is_widened_to_stereo()
    {
        byte[] mono = Wav.Encode(new short[] { 100, 200, 300 }, 44100);
        (StereoBuffer buffer, int rate) = GameScoreBuilder.Decode(mono);
        Assert.Equal(44100, rate);
        Assert.Equal(new short[] { 100, 200, 300 }, buffer.Right);
        Assert.Throws<AmpleException>(() => GameScoreBuilder.Decode([1, 2, 3]));
    }

    [Fact]
    public void The_blocks_a_score_needs_are_listed_once()
    {
        Assert.Equal(["pad", "bass", "drums", "hits"], GameScoreBuilder.Blocks(Parse(Good)));
    }
}
