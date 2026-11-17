namespace Ample.Language.Tests;

/// <summary>Review of #719 (plan D32): a dynamic-field declaration affects reads of that declared field, not unrelated fields that share its name.</summary>
public sealed class DynamicFieldReceiverTests
{
    private const string Variation = "error VariationError extends Error { dynamic Int variation }";

    private static bool NeedsDynamic(string helper, params string[] declarations)
    {
        List<HelperIssue> issues = [];
        string[] parts = helper.Split("\n\n");
        _ = NotePipeline.HelperTable.Build([.. declarations.Concat(parts).Select((d, i) => (d, i * 20))], issues);
        return issues.Any(i => i.Issue.Id == "helper-effect");
    }

    [Theory]
    [InlineData("helper read(Record settings) -> Int {\n  return settings.variation\n}")]
    [InlineData("helper read(Record settings) -> Int {\n  let s = settings\n  return s.variation\n}")]
    [InlineData("helper read(Record settings) -> Int {\n  let s: Record = {variation: 3}\n  return s.variation\n}")]
    [InlineData("helper read() -> Int {\n  let s = {variation: 3}\n  return s.variation\n}")]
    [InlineData("helper read(PlainOne e) -> Int {\n  return e.variation\n}")]
    [InlineData("helper read() -> Int {\n  try { throw PlainOne(\"x\", variation=1) } catch (PlainOne e) { return e.variation }\n  return 0\n}")]
    public void A_field_of_another_type_with_the_same_name_does_not_need_dynamic(string helper)
    {
        Assert.False(NeedsDynamic(helper, Variation, "error PlainOne { Int variation }"));
    }

    [Theory]
    [InlineData("helper read(VariationError e) -> Int {\n  return e.variation\n}")]
    [InlineData("helper read(SubVariation e) -> Int {\n  return e.variation\n}")]
    [InlineData("helper read(Error e) -> Int {\n  return e.variation\n}")]
    [InlineData("helper read(VariationError e) -> Int {\n  let alias = e\n  return alias.variation\n}")]
    [InlineData("helper read() -> Int {\n  try { throw VariationError(\"x\", variation=1) } catch (VariationError e) { return e.variation }\n  return 0\n}")]
    [InlineData("helper read() -> Int {\n  try { throw VariationError(\"x\", variation=1) } catch (e) { return e.variation }\n  return 0\n}")]
    [InlineData("helper make() -> VariationError {\n  return VariationError(\"x\", variation=1)\n}\n\nhelper read() -> Int {\n  return make().variation\n}")]
    public void A_read_of_the_declared_dynamic_field_still_needs_dynamic(string helper)
    {
        Assert.True(NeedsDynamic(helper, Variation, "error SubVariation extends VariationError { }", "error PlainOne { Int variation }"));
    }

    [Fact]
    public void An_error_passed_between_helpers_keeps_its_taint_whatever_the_declarations_say()
    {
        const string maker = "dynamic helper make() -> Notes {\n  try { throw VariationError(\"v\", variation=currentTime().minute % 8) } catch (Error e) { return theme.notes |> transpose(Int(consume(e))) }\n  return theme.notes\n}";
        const string consumer = "dynamic helper consume(Error e) -> Int {\n  return e.variation\n}";
        List<PipeIssue> bad = [];
        PipeValue theme = new() { Events = NotePipeline.ReadNotes("1:0:1:60:100", bad), Span = NotePipeline.Beat };
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            Helpers = NotePipeline.HelperTable.Build([(Variation, 0), (consumer, 20), (maker, 40)], []),
            Source = _ => new NotePipeline.SourceInfo(theme, "t", null),
            Clock = () => new DateTime(2026, 11, 16, 10, 5, 0),
        };
        PipeResult result = NotePipeline.Evaluate("make()", context);
        Assert.Empty(result.Errors);
        Assert.True(result.Dynamic);
        Assert.True(result.Tainted);
    }
}
