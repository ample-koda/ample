namespace Ample.Language.Tests;

public sealed class ParameterAliasTests
{
    private static AmpleDocument Read(string block) => Parser.Parse("composition \"demo\" { ample 1  tempo 120 }\n" + block, Corpus.Spec, ParseOptions.Editor);

    [Fact]
    public void A_rate_in_hz_is_read_as_the_period_in_ms()
    {
        AmpleDocument r = Read("chorus wide {\n  rate 0.5\n}\n");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "AMP2003");
        Parameter p = Assert.Single(r.Blocks[0].Parameters);
        Assert.Equal("period", p.Name);
        Assert.Equal("2000 ms", p.Value);
    }

    [Fact]
    public void The_old_name_still_works_and_the_filter_and_arpeggiator_take_the_tempo_names()
    {
        Assert.DoesNotContain(Read("flanger f {\n  period 2000 ms\n}\n").Diagnostics, d => d.Code == "AMP2003");
        AmpleDocument filter = Read("filter wobble {\n  lfo_sync 1/8\n  lfo_depth 2 oct\n}\n");
        Assert.DoesNotContain(filter.Diagnostics, d => d.Code == "AMP2003");
        Assert.Contains(filter.Blocks[0].Parameters, p => p.Name == "lfo_rate" && p.Value == "1/8");
        AmpleDocument arp = Read("arpeggiator a {\n  sync 1/8\n}\n");
        Assert.DoesNotContain(arp.Diagnostics, d => d.Code == "AMP2003");
    }

    [Fact]
    public void An_unknown_name_is_still_an_error()
    {
        Assert.Contains(Read("chorus wide {\n  speed 1\n}\n").Diagnostics, d => d.Code == "AMP2003");
    }
}
