namespace Ample.Language.Tests;

/// <summary>WP5a of plan/diagnostics/PLAN.md (D6, D32): error declarations with typed fields, one parent, catch by the chain, and the explicit dynamic mark on a field.</summary>
public sealed class ErrorDeclarationTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100";

    private const string Preset = "error UnknownPresetError extends Error { String preset }";

    private static (PipeResult Result, List<HelperIssue> Issues) Run(string expr, params string[] decls)
    {
        List<PipeIssue> bad = [];
        List<HelperIssue> issues = [];
        PipeValue theme = new() { Events = NotePipeline.ReadNotes(Theme, bad), Span = 2 * NotePipeline.Beat };
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Helpers = NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), issues),
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(theme, "test:theme", null) : new NotePipeline.SourceInfo(null, null, "none"),
            Clock = () => new DateTime(2026, 11, 16, 10, 5, 0),
        };
        return (NotePipeline.Evaluate(expr, context), issues);
    }

    private static string Shift(string steps) => "theme.notes |> transpose(" + steps + ")";

    private static string Body(string statements) => "helper f() -> Notes {\n  " + statements + "\n  return theme.notes\n}";

    private static string Shifted(int steps) => Run("theme.notes |> transpose(" + steps + ")").Result.Notes!;

    private static void Gives(int steps, string expr, params string[] decls)
    {
        (PipeResult result, List<HelperIssue> issues) = Run(expr, decls);
        Assert.Empty(issues);
        Assert.Empty(result.Errors);
        Assert.Equal(Shifted(steps), result.Notes);
    }

    [Fact]
    public void A_declared_error_is_thrown_caught_by_its_type_and_its_fields_are_read()
    {
        const string check = "e.preset == \"x\" && e.message == \"no such preset\" && e.type == \"UnknownPresetError\"";
        Gives(3, "f()", Preset, Body("try { throw UnknownPresetError(\"no such preset\", preset=\"x\") } catch (UnknownPresetError e) { return " + Shift("(" + check + ") ? 3 : 9") + " }"));
    }

    [Fact]
    public void A_catch_of_a_parent_handles_the_children_and_the_chain_gives_is_and_inherited_fields()
    {
        string[] all = ["error BaseError extends Error { Int level }", "error ChildError extends BaseError { String name }"];
        const string is3 = "e is ChildError && e is BaseError && e is Error";
        Gives(4, "f()", all[0], all[1], Body("try { throw ChildError(\"boom\", level=4, name=\"c\") } catch (BaseError e) { return " + Shift("(" + is3 + ") ? e.level : 9") + " }"));
        string handlers = "catch (ChildError e) { return " + Shift("1") + " } catch (Error e) { return " + Shift("7") + " }";
        Gives(7, "f()", all[0], all[1], Body("try { throw BaseError(\"boom\", level=1) } " + handlers));
    }

    [Fact]
    public void A_declared_error_without_a_parent_extends_Error_and_fields_are_optional()
    {
        Gives(2, "f()", "error PlainError { }", Body("try { throw PlainError(\"x\") } catch (Error e) { return " + Shift("e.type == \"PlainError\" ? 2 : 9") + " }"));
    }

    [Fact]
    public void A_missing_unknown_or_mistyped_field_is_an_error_that_is_not_caught()
    {
        (PipeResult missing, _) = Run("f()", Preset, "helper f() -> Notes {\n  try { throw UnknownPresetError(\"x\") } catch (e) { return theme.notes }\n  return theme.notes\n}");
        Assert.Contains(missing.Errors, e => e.Id == "bad-argument" && e.Message.Contains("preset", StringComparison.Ordinal));
        (PipeResult unknown, _) = Run("f()", Preset, "helper f() -> Notes {\n  throw UnknownPresetError(\"x\", preset=\"a\", other=1)\n}");
        Assert.Contains(unknown.Errors, e => e.Id == "bad-argument");
        (PipeResult mistyped, _) = Run("f()", "error CountError { Int n }", "helper f() -> Notes {\n  throw CountError(\"x\", n=\"text\")\n}");
        Assert.NotEmpty(mistyped.Errors);
    }

    [Theory]
    [InlineData("error A extends Nope { Int n }")]
    [InlineData("error log { Int n }")]
    [InlineData("error Error { Int n }")]
    [InlineData("error B { Int message }")]
    [InlineData("error C { Int n, String n }")]
    [InlineData("error D { Missing n }")]
    public void A_wrong_declaration_is_reported_when_the_file_is_read(string declaration)
    {
        (_, List<HelperIssue> issues) = Run("1", declaration);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Errors_that_extend_each_other_in_a_circle_are_reported()
    {
        (_, List<HelperIssue> issues) = Run("1", "error A extends B { }", "error B extends A { }");
        Assert.Contains(issues, i => i.Issue.Id == "error-decl");
    }

    [Fact]
    public void Reading_a_dynamic_field_needs_a_dynamic_helper_and_the_result_is_dynamic()
    {
        const string declaration = "error VariationError extends Error { dynamic Int variation }";
        const string thrown = "try { throw VariationError(\"v\", variation=currentTime().minute % 8) } ";
        string maker = "dynamic helper make() -> Notes {\n  " + thrown + "catch (VariationError e) { return " + Shift("e.variation") + " }\n  return theme.notes\n}";
        (PipeResult good, List<HelperIssue> goodIssues) = Run("make()", declaration, maker);
        Assert.DoesNotContain(goodIssues, i => i.Issue.Id == "helper-effect");
        Assert.Empty(good.Errors);
        Assert.True(good.Dynamic);
        Assert.True(good.Tainted);
        (_, List<HelperIssue> badIssues) = Run("make()", declaration, maker.Replace("dynamic helper", "helper", StringComparison.Ordinal));
        Assert.Contains(badIssues, i => i.Issue.Id == "helper-effect");
        const string reader = "helper read(Error e) -> Int {\n  return e.variation\n}";
        (_, List<HelperIssue> readerIssues) = Run("1", declaration, reader);
        Assert.Contains(readerIssues, i => i.Issue.Id == "helper-effect");
    }

    [Fact]
    public void Constructing_a_value_with_a_dynamic_field_without_reading_it_needs_no_declaration()
    {
        const string declaration = "error VariationError extends Error { dynamic Int variation }";
        (_, List<HelperIssue> issues) = Run("1", declaration, "helper f(Int n) -> Int {\n  try { throw VariationError(\"v\", variation=n) } catch (e) { return 1 }\n  return 2\n}");
        Assert.DoesNotContain(issues, i => i.Issue.Id == "helper-effect");
    }

    [Fact]
    public void A_dynamic_mark_on_a_field_of_a_generic_type_is_read()
    {
        (_, List<HelperIssue> issues) = Run("1", "error ListError { dynamic List<Int> items }");
        Assert.Empty(issues);
    }
}
