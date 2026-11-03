namespace Ample.Language.Tests;

/// <summary>The control type, the control expression of a parameter and the rules that go with them (plan: control lanes, C01).</summary>
public sealed class ControlTests
{
    private const string Head = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static AmpleDocument Parse(string body) => Parser.Parse(Head + body, Corpus.Spec);

    [Fact]
    public void A_general_control_fits_every_control_input_and_a_typed_one_only_its_own_unit()
    {
        Assert.True(ControlTypes.Compatible("control", "control<Hz>"));
        Assert.True(ControlTypes.Compatible("control<Hz>", "control<Hz>"));
        Assert.False(ControlTypes.Compatible("control<dB>", "control<Hz>"));
        Assert.True(ControlTypes.Compatible("control<dB>", "control"));
        Assert.False(ControlTypes.Compatible("audio", "control"));
        Assert.Equal("control<Hz>", ControlTypes.Typed("Hz"));
        Assert.Equal("control", ControlTypes.Typed(null));
        Assert.Equal("Hz", ControlTypes.UnitOf("control<Hz>"));
        Assert.Null(ControlTypes.UnitOf("control"));
    }

    [Theory]
    [InlineData("600 + 3000 * traffic.level", true)]
    [InlineData("traffic.level", true)]
    [InlineData("-6 dB + 12 * a-b.level", true)]
    [InlineData("auto(1:100, 5:2000)", false)]
    [InlineData("lfo(4, 100, 900)", false)]
    [InlineData("1000 Hz", false)]
    [InlineData("0.5", false)]
    public void An_expression_is_recognised_by_the_block_and_output_it_names(string text, bool expected) =>
        Assert.Equal(expected, ControlExpression.LooksLike(text));

    [Fact]
    public void An_expression_is_integer_fixed_point_and_a_general_control_is_a_fraction()
    {
        ControlExpression? e = ControlExpression.TryParse("600 + 3000 * traffic.level", out string? error);
        Assert.NotNull(e);
        Assert.Null(error);
        ControlExpression.Reference r = Assert.Single(e!.References);
        Assert.Equal(("traffic", "level"), (r.Block, r.Output));
        Assert.Equal(600, ControlExpression.ToInt(e.Evaluate(_ => 0)));       // no car: 600
        Assert.Equal(2100, ControlExpression.ToInt(e.Evaluate(_ => 500)));    // half way: 600 + 1500
        Assert.Equal(3600, ControlExpression.ToInt(e.Evaluate(_ => 1000)));   // a car: 600 + 3000
        Assert.Equal(e.Evaluate(_ => 333), e.Evaluate(_ => 333));              // the same every time
    }

    [Fact]
    public void Units_decimals_negatives_and_brackets_are_read()
    {
        ControlExpression? e = ControlExpression.TryParse("-6 dB - (12 dB * a.level) + 0.5 * b.level", out _);
        Assert.NotNull(e);
        Assert.Equal(["dB", "dB"], e!.Units);
        Assert.Equal(2, e.References.Count);
        // a = 0.5 (500), b = 1 (1000): -6 - 6 + 0.5
        Assert.Equal(-12 * 1000 + 500, e.Evaluate(i => i == 0 ? 500 : 1000));
    }

    [Theory]
    [InlineData("600 + * traffic.level")]
    [InlineData("traffic")]
    [InlineData("(1 + traffic.level")]
    [InlineData("traffic.")]
    public void A_broken_expression_says_what_is_wrong(string text)
    {
        Assert.Null(ControlExpression.TryParse(text, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void The_automatable_parameters_are_marked_and_give_control_inputs()
    {
        TemplateSpec filter = Corpus.Spec.Templates["filter"];
        PortSpec cutoff = Assert.Single(filter.ControlInputs, c => c.Name == "cutoff");
        Assert.Equal("control<Hz>", cutoff.Type);
        Assert.Equal("control<%>", Assert.Single(filter.ControlInputs, c => c.Name == "resonance").Type);
        Assert.Equal("control<dB>", Assert.Single(Corpus.Spec.Templates["volume"].ControlInputs, c => c.Name == "gain").Type);
        Assert.Empty(Corpus.Spec.Templates["drums"].ControlInputs);
    }

    [Fact]
    public void A_parameter_that_reads_something_that_is_not_a_control_is_refused()
    {
        AmpleDocument doc = Parse("drums d at 1 for 4 {\n}\n\nfilter f {\n  from d.audio -> audio\n  cutoff 600 + 3000 * d.audio\n}\n");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2252");
    }

    [Fact]
    public void A_broken_control_expression_in_a_parameter_is_an_error()
    {
        AmpleDocument doc = Parse("filter f {\n  cutoff 600 + * x.level\n}\n");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2251" && d.Severity == Severity.Error);
    }

    [Fact]
    public void A_control_expression_in_a_parameter_that_cannot_be_automated_is_not_read_as_one()
    {
        // "type" is not automatable: the text stays what it is (the normal check of the value applies)
        AmpleDocument doc = Parse("filter f {\n  type x.level\n}\n");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code is "AMP2251" or "AMP2252");
    }

    [Fact]
    public void A_control_can_be_connected_to_a_parameter_that_can_be_automated_through_the_editor_operation()
    {
        AmpleDocument doc = Parse("curve sw {\n}\n\nvolume vol {\n}\n");
        EditResult ok = Edits.Connect(doc, Corpus.Spec, "sw", "level", "vol", "gain");
        Assert.True(ok.Ok, ok.Error);
        EditResult refused = Edits.Connect(doc, Corpus.Spec, "sw", "level", "vol", "left_gain");   // not a parameter that can be automated
        Assert.False(refused.Ok);
    }

    [Fact]
    public void A_control_that_only_a_parameter_reads_counts_as_used()
    {
        AmpleDocument doc = Parse("curve sw {\n  value auto(1:0, 4:1000)\n}\n\nvolume vol {\n  gain -20 dB * sw.level\n}\n");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "L001" && d.Message.Contains("\"sw\"", StringComparison.Ordinal));
    }
}
