namespace Ample.Language.Tests;

public sealed class SettingsCompletionTests
{
    private static readonly SettingHint[] Hints =
    [
        new("acestep.backend", "How the sound is made.", ["server", "cli"], "server"),
        new("acestep.synthModel", "Which model.", ["turbo", "sft"], "default"),
        new("acestep.endpoint", "The server address.", [], "http://127.0.0.1:8085"),
    ];

    private static string[] Labels(string line)
    {
        string text = "composition \"demo\" { ample 1  tempo 120 }\n" + line;
        return [.. Queries.CompletionAt(Parser.Parse(text, Corpus.Spec), Corpus.Spec, 2, line.Length + 1, Hints).Select(c => c.Label)];
    }

    [Fact]
    public void A_settings_line_proposes_the_names_that_are_not_stated_yet()
    {
        Assert.Equal(["acestep.backend", "acestep.synthModel", "acestep.endpoint"], Labels("settings { "));
        Assert.Equal(["acestep.synthModel", "acestep.endpoint"], Labels("settings { acestep.backend server "));
    }

    [Fact]
    public void After_a_name_the_values_of_that_setting_are_proposed()
    {
        Assert.Equal(["turbo", "sft"], Labels("settings { acestep.synthModel "));
        Assert.Equal(["server", "cli"], Labels("settings { acestep.backend "));
        Assert.Empty(Labels("settings { acestep.endpoint "));
        Assert.Empty(Labels("settings { nope.thing "));
    }

    [Fact]
    public void The_settings_word_is_proposed_at_the_top_and_a_brace_after_it()
    {
        Assert.Contains("settings", Labels(""));
        Assert.Equal(["{ "], Labels("settings "));
    }

    [Fact]
    public void A_quoted_value_with_a_space_keeps_the_name_and_value_pairs_in_step()
    {
        Assert.Equal(["acestep.synthModel", "acestep.endpoint"], Labels("settings { acestep.backend \"my file.gguf\" "));
        Assert.Equal(["turbo", "sft"], Labels("settings { acestep.endpoint \"a b\" acestep.synthModel "));
    }

    [Fact]
    public void Nothing_is_proposed_in_an_open_quote_or_after_the_closing_brace()
    {
        Assert.Empty(Labels("settings { acestep.endpoint \"a b"));
        Assert.Empty(Labels("settings { acestep.backend cli } "));
        Assert.Empty(Labels("settings { acestep.backend cli }"));
    }
}
