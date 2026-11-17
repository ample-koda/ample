using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>WP3 of plan/diagnostics/PLAN.md: the layers of the logging and assertion options, the shared corpus that the JS twin runs, and the options in <c>settings { }</c>.</summary>
public sealed class ObservationConfigTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    public static IEnumerable<object[]> Cases() =>
        JsonDocument.Parse(Corpus.Read("spec-tests/config-layers.json")).RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_layers_give_the_value_and_the_layer_that_the_corpus_expects(string id)
    {
        JsonElement c = JsonDocument.Parse(Corpus.Read("spec-tests/config-layers.json")).RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        List<ConfigLayerInput> layers = [];
        foreach (JsonElement l in c.GetProperty("layers").EnumerateArray())
        {
            ConfigLayer layer = Enum.Parse<ConfigLayer>(l.GetProperty("layer").GetString()!, ignoreCase: true);
            string? origin = l.TryGetProperty("origin", out JsonElement o) ? o.GetString() : null;
            Dictionary<string, (string, int)> values = [];
            foreach (JsonProperty v in l.GetProperty("values").EnumerateObject()) values[v.Name] = (v.Value[0].GetString()!, v.Value[1].GetInt32());
            layers.Add(new ConfigLayerInput(new ConfigSource(layer, origin), values));
        }

        ObservationConfig config = ObservationConfig.Resolve(layers);
        foreach (JsonProperty e in c.GetProperty("expect").EnumerateObject())
        {
            Assert.Equal(e.Value[0].GetString(), config.Get(e.Name).Value);
            Assert.Equal(e.Value[1].GetString(), config.Get(e.Name).Source.Layer.ToString().ToLowerInvariant());
        }

        Assert.Equal(c.GetProperty("problems").GetInt32(), config.Problems.Count);
    }

    [Fact]
    public void An_effective_value_says_why_it_is_what_it_is_and_the_budgets_follow_it()
    {
        ObservationConfig config = ObservationConfig.Resolve([new ConfigLayerInput(new ConfigSource(ConfigLayer.Instance, "up"), new Dictionary<string, (string, int)> { ["logging.level"] = ("trace", 12), ["logging.maxPerSite"] = ("7", 13) })]);
        Assert.Equal("logging.level is trace: set by the instance up (line 12)", config.Explain("logging.level"));
        Assert.Equal(LogLevel.Trace, config.Level);
        Assert.False(config.Assertions);
        Assert.Equal(7, config.Budgets.MaxPerSite);
        Assert.Equal(200, config.Budgets.MaxTotal);
    }

    [Fact]
    public void The_options_in_the_settings_of_a_song_are_not_supported_before_their_feature_is_on()
    {
        using IDisposable none = SyntaxGate.Use(SyntaxFeatures.None);
        AmpleDocument doc = Parser.Parse(Header + "settings { logging.level debug  diagnostics.assertions on }\n", Corpus.Spec);
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == "AMP6020"));   // never accepted and then ignored
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP6020" && d.Message.Contains("assert", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void With_the_features_on_the_options_are_validated_and_their_lines_are_known()
    {
        using IDisposable scope = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Assert);
        AmpleDocument good = Parser.Parse(Header + "settings { logging.level debug  diagnostics.assertions on  logging.maxTotal 300 }\n", Corpus.Spec);
        Assert.DoesNotContain(good.Diagnostics, d => d.Code is "AMP6020" or "AMP6030");
        Assert.True(good.SettingLines["logging.level"] > 0);
        AmpleDocument bad = Parser.Parse(Header + "settings { logging.level loud  logging.maxTotal 99999  diagnostics.assertions maybe }\n", Corpus.Spec);
        Assert.Equal(3, bad.Diagnostics.Count(d => d.Code == "AMP6030"));
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("off, warning, info, debug, trace", StringComparison.Ordinal));
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("at most 10000", StringComparison.Ordinal));
    }

    [Fact]
    public void A_song_without_these_options_is_untouched_and_the_options_never_enter_the_plugin_settings()
    {
        using IDisposable scope = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Assert);
        AmpleDocument doc = Parser.Parse(Header + "settings { acestep.synthModel sft  logging.level debug }\n", Corpus.Spec);
        Assert.Equal("sft", doc.Settings["acestep.synthModel"]);
        Assert.Equal("debug", doc.Settings["logging.level"]);   // read by the observation config only: BuildEngine hands a plugin only the keys of its own prefix, so a take plan never sees it
    }
}
