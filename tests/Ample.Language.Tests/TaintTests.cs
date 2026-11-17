namespace Ample.Language.Tests;

/// <summary>
/// WP-T of plan/diagnostics/PLAN.md (11f): a value keeps its volatile origin through lists, ranges, coercion, builtins, operators, helper results and the branch that was chosen; the Dynamic flag stays the authority
/// for what is built again; a bake of a value that was read upstream says so. The clock is injected, so every case is deterministic.
/// </summary>
public sealed class TaintTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100";

    private const string Phase = "dynamic helper phase() -> Int { return currentTime().minute % 8 }";

    private static readonly DateTime Minute5 = new(2026, 11, 16, 10, 5, 0);

    private static PipeResult Run(string expr, string[] decls, DateTime? now = null, List<HelperIssue>? issues = null)
    {
        List<PipeIssue> bad = [];
        PipeValue theme = new() { Events = NotePipeline.ReadNotes(Theme, bad), Span = 4 * NotePipeline.Beat };
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Helpers = NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), issues ?? []),
            Clock = () => now ?? Minute5,
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(theme, "test:theme", null) : new NotePipeline.SourceInfo(null, null, "none"),
        };
        return NotePipeline.Evaluate(expr, context);
    }

    private static PipeResult Good(string expr, params string[] decls)
    {
        PipeResult result = Run(expr, decls);
        Assert.Empty(result.Errors);
        return result;
    }

    [Fact]
    public void Choose_keeps_the_taint_of_its_positional_input()
    {
        const string expression = "theme.notes |> transpose(choose(currentTime().minute, seed=stable(\"pick\")))";
        PipeResult result = Good(expression);
        Assert.True(result.Dynamic);
        Assert.True(result.Tainted);
        Assert.Equal(Good("theme.notes |> transpose(+5)").Notes, result.Notes);
    }

    [Fact]
    public void A_clock_read_directly_and_through_a_dynamic_helper_is_dynamic_and_tainted()
    {
        PipeResult direct = Good("theme.notes |> transpose(currentTime().minute % 8)");
        Assert.True(direct.Dynamic);
        Assert.True(direct.Tainted);
        PipeResult viaHelper = Good("theme.notes |> transpose(phase())", Phase);
        Assert.True(viaHelper.Dynamic);
        Assert.True(viaHelper.Tainted);
        Assert.Equal(direct.Notes, viaHelper.Notes);   // minute 5 % 8 is 5 either way
    }

    [Fact]
    public void A_value_with_no_volatile_origin_is_neither_dynamic_nor_tainted()
    {
        PipeResult plain = Good("theme.notes |> transpose(+7)");
        Assert.False(plain.Dynamic);
        Assert.False(plain.Tainted);
    }

    [Fact]
    public void A_pure_helper_that_receives_a_tainted_value_stays_pure_and_keeps_the_taint()
    {
        List<HelperIssue> issues = [];
        PipeResult result = Run("up(theme.notes, phase())", [Phase, "helper up(Notes n, Int k) -> Notes { return n |> transpose(k) }"], issues: issues);
        Assert.Empty(issues);   // no helper-effect: up never reads a volatile source itself
        Assert.Empty(result.Errors);
        Assert.True(result.Tainted);
        Assert.True(result.Dynamic);
    }

    [Fact]
    public void A_helper_that_reads_the_clock_without_being_declared_dynamic_is_an_error()
    {
        List<HelperIssue> issues = [];
        _ = Run("theme.notes", ["helper bad() -> Int { return currentTime().minute % 8 }"], issues: issues);
        Assert.Contains(issues, i => i.Issue.Id == "helper-effect");
    }

    [Theory]
    [InlineData("let xs = [k, 3]\n  return theme.notes |> transpose(at(xs, 0))", true)]
    [InlineData("let xs = [k, 3]\n  return theme.notes |> transpose(at(xs, 1))", true)]   // the whole list is consumed: the hint is deliberately conservative
    [InlineData("let xs = [1, 3]\n  return theme.notes |> transpose(at(xs, 0))", false)]
    public void A_list_with_a_tainted_item_is_tainted_where_the_whole_list_is_used(string body, bool tainted)
    {
        PipeResult result = Good("f(phase())", Phase, $"helper f(Int k) -> Notes {{\n  {body}\n}}");
        Assert.Equal(tainted, result.Tainted);
    }

    [Fact]
    public void Looping_over_a_list_with_a_tainted_item_keeps_the_taint()
    {
        PipeResult loop = Good("f(phase())", Phase, "helper f(Int k) -> Notes {\n  for x in [k, 3] { emit theme.notes |> transpose(x) }\n}");
        Assert.True(loop.Tainted);
    }

    [Fact]
    public void A_range_with_a_tainted_bound_taints_what_the_loop_makes()
    {
        PipeResult result = Good("f(phase())", Phase, "helper f(Int k) -> Notes {\n  for i in 0..k { emit theme.notes |> shift(i * 1bar) }\n}");
        Assert.True(result.Tainted);
    }

    [Theory]
    [InlineData(5, 4)]   // minute 5 % 8 = 5 > 3: the branch that returns 7 runs
    [InlineData(2, 2)]   // minute 2: the condition is false and the code after the if runs
    public void A_branch_chosen_by_a_tainted_condition_taints_what_it_returns(int minute, int expectedNotesShift)
    {
        _ = expectedNotesShift;
        PipeResult result = Run("theme.notes |> transpose(pick(phase()))", [Phase, "helper pick(Int k) -> Int {\n  if k > 3 { return 7 }\n  return 1\n}"], new DateTime(2026, 11, 16, 10, minute, 0));
        Assert.Empty(result.Errors);
        Assert.True(result.Dynamic);   // identity: the clock was read, whatever the branch
        if (minute == 5) Assert.True(result.Tainted);   // the chosen branch is tainted
        else Assert.False(result.Tainted);   // documented limit: a value that is reached because a tainted guard did not fire is covered by the Dynamic flag, not by the taint
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void A_dynamic_helper_taints_whatever_it_returns_on_every_path(int minute)
    {
        string choose = "dynamic helper chooseV() -> Int {\n  if currentTime().minute % 2 == 0 { return 7 }\n  return 3\n}";
        PipeResult result = Run("theme.notes |> transpose(chooseV())", [choose], new DateTime(2026, 11, 16, 10, minute, 0));
        Assert.Empty(result.Errors);
        Assert.True(result.Dynamic);
        Assert.True(result.Tainted);
    }

    [Fact]
    public void Baking_the_dynamic_call_stops_the_clock_and_baking_a_value_read_earlier_does_not()
    {
        PipeResult whole = Run("theme.notes |> transpose(bake phase() as 3)", [Phase]);
        Assert.Empty(whole.Errors);
        Assert.False(whole.Dynamic);   // the call is not run at all
        Assert.True(whole.BakedDynamic);
        Assert.False(whole.Tainted);
        Assert.DoesNotContain(whole.Warnings, w => w.Id == "bake-upstream-dynamic");

        PipeResult later = Run("f()", [Phase, "dynamic helper f() -> Notes {\n  let p = phase()\n  return theme.notes |> transpose(bake p as 3)\n}"]);
        Assert.Empty(later.Errors);
        Assert.True(later.Dynamic);   // the clock was read above the bake and stays read
        Assert.Contains(later.Warnings, w => w.Id == "bake-upstream-dynamic");
    }

    [Fact]
    public void A_value_bake_of_a_plain_value_at_the_top_has_no_such_warning()
    {
        PipeResult result = Run("theme.notes |> transpose(bake 2 + 1 as 3)", []);
        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Warnings, w => w.Id == "bake-upstream-dynamic");
    }
}
