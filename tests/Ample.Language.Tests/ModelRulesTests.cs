namespace Ample.Language.Tests;

public sealed class ModelRulesTests
{
    private const string Song = "composition \"demo\" { ample 1  tempo 120 }\nsettings { acestep.backend server  acestep.synthModelFile acestep-v15-turbo-Q5_K_M.gguf }\nlyrics words {\n  lang en\n  file lyrics/a.lyr\n}\nai-voice voice at 1 for 4 {\n  voice clear lead\n  guidance 7\n}\n";

    private static Diagnostic[] Check(string text) => Parser.Parse(text, Corpus.Spec, ParseOptions.Editor).Diagnostics.Where(d => d.Code == "AMP2014").ToArray();

    // Applies a replace fix the way the editors do (1-based line and columns; ASCII text here).
    private static string Apply(string text, Fix fix)
    {
        string[] lines = text.Split('\n');
        string line = lines[fix.Line - 1];
        lines[fix.Line - 1] = line[..(fix.Col - 1)] + fix.Text + line[(fix.EndCol - 1)..];
        return string.Join('\n', lines);
    }

    [Fact]
    public void A_turbo_model_with_another_guidance_is_an_error_with_a_fix()
    {
        Diagnostic d = Assert.Single(Check(Song));
        Assert.Equal(9, d.Line);
        Assert.Equal("1", d.Fix!.Text);
        Assert.Empty(Check(Apply(Song, d.Fix)));
    }

    [Fact]
    public void Guidance_one_or_another_model_is_fine()
    {
        Assert.Empty(Check(Song.Replace("guidance 7", "guidance 1", StringComparison.Ordinal)));
        Assert.Empty(Check(Song.Replace("turbo-Q5_K_M", "sft-Q5_K_M", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_model_a_block_names_in_its_engine_counts_instead_of_the_song()
    {
        // the song says turbo (a file), the block asks for the sft family: guidance 7 is fine there
        Assert.Empty(Check(Song.Replace("  voice clear lead\n", "  voice clear lead\n  engine \"faer:xl-sft\"\n", StringComparison.Ordinal)));
        // the other way round: the song is sft, the block asks for turbo with guidance 7
        string sftSong = Song.Replace("turbo-Q5_K_M", "sft-Q5_K_M", StringComparison.Ordinal);
        Assert.Empty(Check(sftSong));
        Diagnostic d = Assert.Single(Check(sftSong.Replace("  voice clear lead\n", "  voice clear lead\n  engine \"local:turbo\"\n", StringComparison.Ordinal)));
        Assert.Equal("1", d.Fix!.Text);
        // a route alone changes nothing
        Assert.Single(Check(Song.Replace("  voice clear lead\n", "  voice clear lead\n  engine \"faer\"\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void Plain_parsing_leaves_it_to_the_build()
    {
        Assert.DoesNotContain(Parser.Parse(Song, Corpus.Spec).Diagnostics, d => d.Code == "AMP2014");
    }

    [Fact]
    public void A_number_outside_its_range_has_a_fix_that_writes_the_nearest_limit()
    {
        const string Text = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\nvolume v {\n  gain 999\n}\n";
        Diagnostic d = Assert.Single(Parser.Parse(Text, Corpus.Spec).Diagnostics, x => x.Code == "AMP2021");
        Assert.NotNull(d.Fix);
        Assert.DoesNotContain(Parser.Parse(Apply(Text, d.Fix), Corpus.Spec).Diagnostics, x => x.Code == "AMP2021");
    }

    // Same table as web/lib/test/ace-generation.test.mjs: true = turbo (AMP2014 fires), false = free.
    [Theory]
    [InlineData("acestep.synthModelFile acestep-v15-turbo.gguf", true)]
    [InlineData("acestep.synthModelFile acestep-v15-xl-turbo-Q8_0.gguf", true)]
    [InlineData("acestep.synthModelFile turbo.v2.gguf", true)]
    [InlineData("acestep.synthModelFile acestep-v15-sft.gguf", false)]
    [InlineData("acestep.synthModelFile acestep-v15-base.gguf", false)]
    [InlineData("acestep.synthModelFile mystery.gguf  acestep.synthModel turbo", false)]
    [InlineData("acestep.synthModelFile sftx.gguf", false)]
    [InlineData("acestep.synthModel xl-turbo", true)]
    [InlineData("acestep.synthModel default", false)]
    [InlineData("acestep.backend server", false)]
    public void Turbo_family_table_matches_the_browser(string setting, bool turbo)
    {
        string text = Song.Replace("acestep.synthModelFile acestep-v15-turbo-Q5_K_M.gguf", setting, StringComparison.Ordinal);
        Assert.Equal(turbo, Check(text).Length == 1);
    }

    [Fact]
    public void The_guidance_fix_replaces_only_the_value()
    {
        string text = Song.Replace("guidance 7", "guidance 7", StringComparison.Ordinal);
        Diagnostic d = Assert.Single(Check(text));
        Assert.Contains("  guidance 1", Apply(text, d.Fix!), StringComparison.Ordinal);
    }

    [Fact]
    public void The_range_fix_replaces_only_the_value_and_never_writes_an_exponent()
    {
        const string Text = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\nvolume v {\n  gain 999\n}\n";
        Diagnostic d = Assert.Single(Parser.Parse(Text, Corpus.Spec).Diagnostics, x => x.Code == "AMP2021");
        string fixedText = Apply(Text, d.Fix!);
        Assert.Contains("  gain 48", fixedText, StringComparison.Ordinal);
        Assert.DoesNotContain("E-", fixedText, StringComparison.Ordinal);
        Assert.DoesNotContain("E+", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fix_for_a_missing_backend_setting_makes_the_warning_go_away()
    {
        string text = Song.Replace("settings { acestep.backend server  acestep.synthModelFile acestep-v15-turbo-Q5_K_M.gguf }\n", string.Empty, StringComparison.Ordinal).Replace("guidance 7", "guidance 1", StringComparison.Ordinal);
        Diagnostic d = Assert.Single(Parser.Parse(text, Corpus.Spec, ParseOptions.Editor).Diagnostics, x => x.Code == "AMP2013");
        Assert.DoesNotContain(Parser.Parse(Apply(text, d.Fix!), Corpus.Spec, ParseOptions.Editor).Diagnostics, x => x.Code == "AMP2013");
    }

    [Fact]
    public void A_part_past_the_last_bar_has_a_fix_that_shortens_it()
    {
        string text = Song.Replace("at 1 for 4", "at 1022 for 4", StringComparison.Ordinal);
        Diagnostic d = Assert.Single(Parser.Parse(text, Corpus.Spec, ParseOptions.Editor).Diagnostics, x => x.Code == "AMP5002");
        Assert.Equal("3", d.Fix!.Text);
        Assert.DoesNotContain(Parser.Parse(Apply(text, d.Fix), Corpus.Spec, ParseOptions.Editor).Diagnostics, x => x.Code == "AMP5002");
    }

    [Fact]
    public void A_part_that_starts_past_the_last_bar_has_no_shorten_fix()
    {
        string text = Song.Replace("at 1 for 4", "at 1030 for 4", StringComparison.Ordinal);
        Assert.Null(Assert.Single(Parser.Parse(text, Corpus.Spec, ParseOptions.Editor).Diagnostics, x => x.Code == "AMP5002").Fix);
    }

    [Fact]
    public void The_fix_for_an_internal_port_name_writes_the_stream_name_on_the_from_line()
    {
        const string Text = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\nlint { L010 warning }\ndrums d at 1 for 4 {\n}\nvolume v {\n  from d.audio -> in\n}\nvolume w {\n  from v.mixed -> in\n}\n";
        Diagnostic[] all = [.. Parser.Parse(Text, Corpus.Spec).Diagnostics.Where(x => x.Code == "L010")];
        Assert.Equal(2, all.Length);
        Assert.Equal(6, all[0].Fix!.Line);
        // one name per fix: a connection that needs two is fixed by running the fix again
        string fixedText = Text;
        for (int round = 0; round < 6 && Parser.Parse(fixedText, Corpus.Spec).Diagnostics.FirstOrDefault(x => x.Code == "L010") is { } next; round++)
        {
            fixedText = Apply(fixedText, next.Fix!);
        }

        Assert.DoesNotContain(Parser.Parse(fixedText, Corpus.Spec).Diagnostics, x => x.Code == "L010");
    }

    [Fact]
    public void An_effect_without_an_input_gets_a_risky_fix_that_connects_the_nearest_earlier_block()
    {
        const string Text = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\ndrums d at 1 for 4 {\n}\nvolume v {\n}\n";
        Diagnostic d = Assert.Single(Parser.Parse(Text, Corpus.Spec).Diagnostics, x => x.Code == "L002");
        Assert.True(d.Fix!.Risky);
        string fixedText = Apply(Text, d.Fix);
        Assert.Contains("  from d.", fixedText, StringComparison.Ordinal);
        Assert.DoesNotContain(Parser.Parse(fixedText, Corpus.Spec).Diagnostics, x => x.Code == "L002");
    }
}
