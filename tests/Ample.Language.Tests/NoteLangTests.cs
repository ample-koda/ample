namespace Ample.Language.Tests;

/// <summary>The language layer (docs/language/14): helpers in any order, typed parameters, let/var, if / else if / else, bounded for / repeat / while, recursion with a stack-depth guard, fuel, dynamic helpers and the clock, value bakes, and the diagnostics of the declarations.</summary>
public sealed class NoteLangTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100";

    private static readonly DateTime Noon = new(2026, 10, 26, 12, 7, 30);

    private static NotePipeline.HelperTable Table(string[] decls, List<HelperIssue>? issues = null) => NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), issues ?? []);

    private static PipeResult Run(string expr, string[]? decls = null, DateTime? now = null, LangLimits? limits = null, List<HelperIssue>? issues = null)
    {
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Helpers = Table(decls ?? [], issues),
            Clock = () => now ?? Noon,
            Limits = limits ?? new LangLimits(),
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(Value(Theme), "test:theme", null) : new NotePipeline.SourceInfo(null, null, $"There is no block \"{name}\""),
        };
        return NotePipeline.Evaluate(expr, context);
    }

    private static PipeValue Value(string notes)
    {
        List<PipeIssue> bad = [];
        List<PipeNote> events = NotePipeline.ReadNotes(notes, bad);
        return new PipeValue { Events = events, Span = 4 * NotePipeline.Beat * 1 };
    }

    private static string Notes(PipeResult r)
    {
        Assert.Empty(r.Errors);
        return r.Notes!;
    }

    private static string Plain(string expr) => Notes(Run(expr));

    [Fact]
    public void A_helper_gives_the_same_notes_as_the_operators_written_out()
    {
        string[] decls = ["helper canonVoice(Notes source, Interval by, Duration delay) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}"];
        Assert.Equal(Plain($"{Theme} |> transpose(+7) |> shift(2bars)"), Notes(Run($"canonVoice({Theme}, +7st, 2bars)", decls)));
        Assert.Equal(Plain("theme.notes |> transpose(+7) |> shift(2bars)"), Notes(Run("theme.notes |> canonVoice(+7, 2bars)", decls)));
    }

    [Fact]
    public void The_order_of_the_declarations_does_not_matter_and_helpers_call_helpers()
    {
        string[] decls =
        [
            "helper buildCanon(Notes theme, Int voices = 3, Interval by = +7st, Duration delay = 2bars) -> Notes {\n  for i in 0..voices {\n    emit canonVoice(theme, by * i, delay * i)\n  }\n}",
            "helper canonVoice(Notes source, Interval by, Duration delay) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}",
        ];
        string manual = Plain($"{Theme} |> merge({Theme} |> transpose(+7) |> shift(2bars)) |> merge({Theme} |> transpose(+14) |> shift(4bars))");
        PipeResult canon = Run("buildCanon(theme.notes, voices=3)", decls);
        Assert.Empty(canon.Errors);
        Assert.Equal(12, canon.Notes!.Split(' ').Length);
        Assert.Equal(manual, canon.Notes);   // three shifted voices, written out by hand
        Assert.Equal(Plain(Theme), Notes(Run("buildCanon(theme.notes, voices=1)", decls)));   // one voice is the theme itself
    }

    [Fact]
    public void If_else_if_else_chooses_the_branch()
    {
        string[] decls = ["helper pick(Int minute) -> Int {\n  if minute < 20 { return 3 }\n  else if minute < 40 { return 4 }\n  else { return 5 }\n}"];
        string Shifted(int minute) => Notes(Run($"{Theme} |> transpose(pick({minute}))", decls));
        Assert.Equal(Plain($"{Theme} |> transpose(3)"), Shifted(5));
        Assert.Equal(Plain($"{Theme} |> transpose(4)"), Shifted(25));
        Assert.Equal(Plain($"{Theme} |> transpose(5)"), Shifted(55));
    }

    [Fact]
    public void Arithmetic_comparison_logic_and_conversions_follow_the_rules()
    {
        string[] decls = ["helper t(Int a) -> Int {\n  let b = a * 2 + 1\n  var c = b\n  c = c - 1\n  if c == a * 2 && !(c < 0) || false { return c % 3 }\n  return 0 - 1\n}"];
        Assert.Equal(Plain($"{Theme} |> transpose(1)"), Notes(Run($"{Theme} |> transpose(t(5))", decls)));   // b=11, c=10, 10 % 3 = 1
        Assert.Equal(Plain($"{Theme} |> transpose(4)"), Notes(Run($"{Theme} |> transpose(Round(7 / 2))", decls)));   // 3.5 rounds half to even: 4
        Assert.Equal(Plain($"{Theme} |> transpose(2)"), Notes(Run($"{Theme} |> transpose(Round(5 / 2))", decls)));   // 2.5 -> 2
        Assert.Equal(Plain($"{Theme} |> transpose(3)"), Notes(Run($"{Theme} |> transpose(Floor(7 / 2))", decls)));
        Assert.Equal(Plain($"{Theme} |> transpose(4)"), Notes(Run($"{Theme} |> transpose(Ceil(7 / 2))", decls)));
        Assert.Equal(Plain($"{Theme} |> shift(1.5beat)"), Notes(Run($"{Theme} |> shift(1beat + 0.5beat)", decls)));
        Assert.Equal(Plain($"{Theme} |> shift(3beats)"), Notes(Run($"{Theme} |> shift(1beat * 3)", decls)));
    }

    [Fact]
    public void A_union_alias_is_narrowed_with_is_and_a_lossy_float_to_int_is_an_error_with_a_fix()
    {
        string[] decls = ["type Steps = Int | Float", "helper delayBy(Notes n, Steps s) -> Notes {\n  if s is Int { return n |> shift(s * 1beat) }\n  return n |> shift(Round(s) * 1beat)\n}", "helper twice(Int n) -> Int { n * 2 }"];
        Assert.Equal(Plain($"{Theme} |> shift(2beat)"), Notes(Run($"delayBy({Theme}, 2)", decls)));
        Assert.Equal(Plain($"{Theme} |> shift(2beat)"), Notes(Run($"delayBy({Theme}, 2.4)", decls)));
        PipeResult lossy = Run($"{Theme} |> transpose(twice(2.5))", decls);
        PipeIssue error = Assert.Single(lossy.Errors, e => e.Id == "lossy-conversion");   // no musical choice is made without intent
        Assert.Equal("Round(2.5)", error.Suggestion);
        Assert.Null(lossy.Notes);
        Assert.Equal(Plain($"{Theme} |> transpose(4)"), Notes(Run($"{Theme} |> transpose(twice(Round(2.5)))", decls)));   // 2.5 rounds half to even to 2; twice is 4
    }

    [Fact]
    public void A_bounded_while_stops_at_its_max_as_an_error_unless_stop_is_written()
    {
        string grow = "helper grow(Notes n, Int target) -> Notes {\n  var result = n\n  while count(result) < target max 3 {\n    result = result |> merge(n |> shift(span(result)))\n  }\n  return result\n}";
        string stop = grow.Replace("max 3 {", "max 3 stop {", StringComparison.Ordinal);
        Assert.Contains(Run($"grow({Theme}, 100)", [grow]).Errors, e => e.Id == "while-limit");
        PipeResult stopped = Run($"grow({Theme}, 100)", [stop]);
        Assert.Empty(stopped.Errors);
        Assert.Equal(16, stopped.Notes!.Split(' ').Length);   // 4 notes, doubled 3 times: 4 -> 8 -> 16 -> 32? the guard stops after 3 rounds
    }

    [Fact]
    public void Recursion_is_guarded_by_the_stack_depth_and_loops_by_their_limits_and_the_fuel()
    {
        string echo = "helper echo(Notes n, Int times) -> Notes {\n  if times == 0 { return n }\n  return n |> merge(echo(n |> shift(1beat) |> transpose(-12), times - 1))\n}";
        Assert.Equal(16, Run($"echo({Theme}, 3)", [echo]).Notes!.Split(' ').Length);
        Assert.Contains(Run($"echo({Theme}, 100)", [echo], limits: new LangLimits(StackDepth: 8)).Errors, e => e.Id == "stack-depth");
        string many = "helper many(Notes n) -> Notes {\n  repeat 100000000 { }\n  return n\n}";
        Assert.Contains(Run($"many({Theme})", [many]).Errors, e => e.Id == "loop-limit");
        string busy = "helper busy(Notes n) -> Notes {\n  var k = 0\n  for i in 0..5000 { k = k + 1 }\n  return n\n}";
        Assert.Contains(Run($"busy({Theme})", [busy], limits: new LangLimits(Fuel: 1000)).Errors, e => e.Id == "fuel");
        Assert.Empty(Run($"busy({Theme})", [busy]).Errors);
    }

    [Fact]
    public void A_dynamic_helper_reads_the_clock_and_the_result_says_so()
    {
        string[] decls = ["dynamic helper variation() -> Int { return currentTime().minute % 8 }"];
        PipeResult a = Run($"{Theme} |> transpose(variation())", decls, new DateTime(2026, 10, 26, 12, 3, 0));
        PipeResult b = Run($"{Theme} |> transpose(variation())", decls, new DateTime(2026, 10, 26, 12, 6, 0));
        Assert.True(a.Dynamic && b.Dynamic);
        Assert.Equal(Plain($"{Theme} |> transpose(3)"), a.Notes);
        Assert.Equal(Plain($"{Theme} |> transpose(6)"), b.Notes);
        Assert.NotEqual(a.Recipe, b.Recipe);
        Assert.Contains(a.DynamicInputs, i => i.StartsWith("currentTime()", StringComparison.Ordinal));
        Assert.False(Run($"{Theme} |> transpose(3)", decls).Dynamic);
    }

    [Fact]
    public void A_baked_dynamic_helper_and_a_value_bake_do_not_run_the_clock()
    {
        string[] baked = ["dynamic helper variation() -> Int bake as 5 {\n  return currentTime().minute % 8\n}"];
        PipeResult a = Run($"{Theme} |> transpose(variation())", baked, new DateTime(2026, 10, 26, 12, 3, 0));
        PipeResult b = Run($"{Theme} |> transpose(variation())", baked, new DateTime(2026, 10, 26, 12, 6, 0));
        Assert.False(a.Dynamic);
        Assert.Equal(Plain($"{Theme} |> transpose(5)"), a.Notes);
        Assert.Equal(a.Notes, b.Notes);
        Assert.Equal(a.Recipe, b.Recipe);
        string[] plain = ["dynamic helper variation() -> Int { return currentTime().minute % 8 }"];
        PipeResult site = Run($"{Theme} |> transpose(bake variation() as 2)", plain, new DateTime(2026, 10, 26, 12, 3, 0));
        Assert.False(site.Dynamic);
        Assert.Equal(Plain($"{Theme} |> transpose(2)"), site.Notes);
    }

    [Fact]
    public void A_value_bake_of_a_pure_expression_is_signed_and_noticed_when_it_goes_stale_or_is_changed_by_hand()
    {
        string[] decls = ["helper gainFor(Notes n) -> Int { count(n) * 2 }"];
        PipeResult fresh = Run($"{Theme} |> transpose(bake gainFor(theme.notes) as 8)", decls);
        Assert.Empty(fresh.Errors);
        Assert.DoesNotContain(fresh.Warnings, w => w.Id.EndsWith("value-bake", StringComparison.Ordinal));
        PipeIssue dirty = Assert.Single(Run($"{Theme} |> transpose(bake gainFor(theme.notes) as 9 @00000000)", decls).Warnings, w => w.Id == "stale-value-bake");
        Assert.StartsWith("as 8 @", dirty.Suggestion, StringComparison.Ordinal);
        string hash = dirty.Suggestion![6..];
        Assert.Contains(Run($"{Theme} |> transpose(bake gainFor(theme.notes) as 9 @{hash})", decls).Warnings, w => w.Id == "dirty-value-bake");
        Assert.Contains(Run($"{Theme} |> transpose(bake gainFor(theme.notes) as 9 @{hash} override)", decls).Warnings, w => w.Id == "value-bake-override");
        Assert.Equal(Plain($"{Theme} |> transpose(9)"), Notes(Run($"{Theme} |> transpose(bake gainFor(theme.notes) as 9 @{hash} override)", decls)));
    }

    [Fact]
    public void The_declarations_are_checked_without_running_anything()
    {
        List<HelperIssue> issues = [];
        Table(["helper nudge(Int x) -> Int { return x + currentTime().minute }", "helper Nudge(Int x) -> Int { return x }", "helper both(Notes n) -> Notes {\n  emit n\n  return n\n}", "helper loop(Int x) { return loop(x - 1) }", "helper typo(Fnt x) -> Int { return 1 }", "helper rand() -> Notes { return euclid(3, 8) |> humanize(timing=10ms, seed=stable(\"hat\")) }", "helper caller() -> Notes { return trnspose(1) }", "helper fine() -> Notes { return euclid(3, 8) }"], issues);
        string[] ids = [.. issues.Select(i => i.Issue.Id)];
        Assert.Contains("helper-effect", ids);
        Assert.Contains("duplicate-helper", ids);
        Assert.Contains("emit-return", ids);
        Assert.Contains("recursion-needs-type", ids);
        Assert.Contains("unknown-type", ids);
        Assert.Contains("shared-stable", ids);
        Assert.Contains("unknown-helper", ids);
        Assert.Equal("dynamic helper", issues.First(i => i.Issue.Id == "helper-effect").Issue.Suggestion);
        Assert.Equal("transpose", issues.First(i => i.Issue.Id == "unknown-helper").Issue.Suggestion);
    }

    [Fact]
    public void A_helper_name_with_other_casing_works_with_a_warning_and_a_fix()
    {
        string[] decls = ["helper CanonVoice(Notes source, Interval by) -> Notes { source |> transpose(by) }"];
        PipeResult r = Run($"canonvoice({Theme}, +7)", decls);
        Assert.Empty(r.Errors);
        PipeIssue warning = Assert.Single(r.Warnings, w => w.Id == "helper-casing");
        Assert.Equal("CanonVoice", warning.Suggestion);
        Assert.Equal(Plain($"{Theme} |> transpose(7)"), r.Notes);
    }

    [Fact]
    public void Constants_are_file_wide_and_unknown_operators_get_a_suggestion()
    {
        Assert.Equal(Plain($"{Theme} |> shift(2bars)"), Notes(Run($"{Theme} |> shift(pause)", ["let pause = 2bars"])));
        PipeIssue unknown = Assert.Single(Run($"{Theme} |> trnspose(+7)").Errors);
        Assert.Equal("unknown-operator", unknown.Id);
        Assert.Equal("transpose", unknown.Suggestion);
    }

    [Fact]
    public void A_song_with_helpers_evaluates_its_clips_and_reports_a_dynamic_helper_that_is_not_marked()
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nhelper nudge(Notes n) -> Notes {\n  n |> transpose(currentTime().minute % 5)\n}\n\nmidi-clip theme at 1 for auto {\n  notes \"" + Theme + "\"\n}\n\nmidi-clip lead at 2 for auto {\n  notes nudge(theme.notes)\n}\n";
        var result = Parser.Parse(song, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true, Clock = () => new DateTime(2026, 10, 26, 12, 7, 0) });
        Diagnostic effect = Assert.Single(result.Diagnostics, d => d.Code == "AMP2060");
        Assert.NotNull(effect.Fix);
        Assert.Equal("nudge", Assert.Single(result.Helpers).Name);
        Assert.Equal("dynamic", result.Helpers[0].Effect);
        string notes = result.Blocks.Single(b => b.Name == "lead").Parameters.Single(p => p.Name == "notes").Value;
        Assert.Equal(Plain($"{Theme} |> transpose(2)").Replace("1:", "1:", StringComparison.Ordinal), notes.Replace("1:", "1:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_made_as_one_musical_type_is_not_accepted_as_another()
    {
        // the review of the demo: pickRegister gives an Octave, a program wants a General MIDI program: both are whole numbers, but the intent differs
        string[] decls = ["helper pickRegister(Int minute) -> Octave {\n  if minute < 20 { return 3 }\n  return 4\n}", "helper programFor(Program p) -> Int { p }", "helper twice(Int n) -> Int { n * 2 }"];
        PipeResult wrong = Run($"{Theme} |> transpose(programFor(pickRegister(5)))", decls);
        Assert.Contains(wrong.Errors, e => e.Id == "type-mismatch" && e.Message.Contains("Octave", StringComparison.Ordinal) && e.Message.Contains("Program", StringComparison.Ordinal));
        Assert.Empty(Run($"{Theme} |> transpose(programFor(3))", decls).Errors);   // a plain number takes the type of the place it is written in
        Assert.Empty(Run($"{Theme} |> transpose(twice(pickRegister(5)))", decls).Errors);   // an Octave is a whole number where a whole number is expected
        Assert.Empty(Run($"{Theme} |> transpose(programFor(Int(pickRegister(5)) + 1))", decls).Errors);   // Int(...) and computing make it a plain number again
    }

    [Fact]
    public void Emit_is_for_notes_only_code_after_a_return_is_flagged_and_limits_above_the_cap_are_reported()
    {
        List<HelperIssue> issues = [];
        Table(["helper foo() -> Int {\n  emit 1\n  emit 2\n}", "helper late(Notes n) -> Notes {\n  return n\n  n |> reverse()\n}"], issues);
        Assert.Contains(issues, i => i.Issue.Id == "emit-return");
        Assert.Contains(issues, i => i.Issue.Id == "unreachable-code");
        Assert.Contains(Run("1:0:1:60:100 |> merge(foo2())", ["helper foo2() { emit 1 }"]).Errors, e => e.Id is "type-mismatch" or "bad-argument");
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\nsettings { limits.fuel 999999999999 }\n";
        Assert.Contains(Parser.Parse(song, Corpus.Spec).Diagnostics, d => d.Code == "AMP2071");
        Assert.Equal(LangLimits.Caps.Fuel, LangLimits.From(new Dictionary<string, string> { ["limits.fuel"] = "999999999999" }).Fuel);
    }

    [Fact]
    public void Helpers_report_what_they_depend_on_and_seeded_randomness_is_not_an_effect()
    {
        NotePipeline.HelperTable deps = Table(["helper a(Notes n) -> Notes { n |> thin(0.5, seed=stable(\"x\")) }", "helper b(Notes n) -> Notes { n |> thin(0.5, seed=7) }", "helper c(Notes n) -> Notes { n |> shift(250ms) }", "helper d(Notes n) -> Notes { b(c(n)) }", "dynamic helper f() -> Int { currentTime().minute }"]);
        string[] Of(string name) => [.. deps.Infos.Single(i => i.Name == name).Dependencies];
        Assert.Equal(["master-seed", "random"], Of("a"));
        Assert.Equal(["random"], Of("b"));   // an explicit seed draws random numbers but does not follow the master-seed
        Assert.Equal(["tempo"], Of("c"));
        Assert.Equal(["random", "tempo"], Of("d"));
        Assert.Equal(["clock"], Of("f"));
        Assert.Equal("pure", deps.Infos.Single(i => i.Name == "a").Effect);   // reproducible: a dependency, not an effect
    }

    [Fact]
    public void A_bare_return_ends_an_emitting_helper_and_a_hyphenated_name_that_is_a_subtraction_gets_a_hint()
    {
        Assert.Equal(Plain(Theme), Notes(Run($"early({Theme}, true)", ["helper early(Notes n, Bool stop) -> Notes {\n  emit n\n  if stop { return }\n  emit n |> shift(1bar)\n}"])));
        PipeIssue hint = Assert.Single(Run($"{Theme} |> transpose(f(5, 2))", ["helper f(Int a, Int b) -> Int { a-b }"]).Errors);
        Assert.Equal("unknown-name", hint.Id);
        Assert.Equal("a - b", hint.Suggestion);
    }

    [Fact]
    public void Two_statements_on_one_line_need_a_semicolon()
    {
        List<HelperIssue> bad = [];
        _ = NotePipeline.HelperTable.Build([("helper f(Int p) -> Int {\n  if p > 1 { return 5 }\n  else { re turn 3 }\n}", 0)], bad);
        HelperIssue issue = Assert.Single(bad);
        Assert.Equal("syntax", issue.Issue.Id);
        Assert.Contains("separated by", issue.Issue.Message, StringComparison.Ordinal);
        List<HelperIssue> good = [];
        _ = NotePipeline.HelperTable.Build([("helper g(Int p) -> Int {\n  if p > 1 { return 5 } return 3\n}", 0), ("helper h(Int p) -> Int {\n  let a = 1; let b = 2; return a + b\n}", 20), ("helper k(Int p) -> Int {\n  let a = 1\n  let b = 2\n  return a + b\n}", 40)], good);
        Assert.Empty(good);
        // and the song says so: an error on a line of the helper
        AmpleDocument doc = Parser.Parse("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nhelper f(Int p) -> Int {\n  if p > 1 { return 5 }\n  else { re turn 3 }\n}\n", Corpus.Spec);
        Assert.Contains(doc.Diagnostics, d => d.Severity == Severity.Error && d.Line is >= 3 and <= 6);
    }

    [Fact]
    public void An_ample_ignore_comment_silences_the_warnings_and_notes_of_that_code_on_that_line_but_never_an_error()
    {
        const string head = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\ndynamic helper phase() -> Int {\n  return currentTime().second % 4\n}\n\n";
        const string clip = "midi-clip a at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n\n";
        const string player = "gm p at 1 for auto {\n  file pack:musescore-general\n  program phase()\n  from a.notes -> notes\n}\n";
        // a note on a line of a block: a comment at the end of that line
        string plain = head + clip + player;
        Assert.Contains(Parser.Parse(plain, Corpus.Spec).Diagnostics, d => d.Code == "AMP2073");
        AmpleDocument silenced = Parser.Parse(plain.Replace("program phase()", "program phase()   # ample-ignore AMP2073", StringComparison.Ordinal), Corpus.Spec);
        Assert.DoesNotContain(silenced.Diagnostics, d => d.Code == "AMP2073");
        // another code on that line is not silenced
        Assert.Contains(Parser.Parse(plain.Replace("program phase()", "program phase()   # ample-ignore AMP9999", StringComparison.Ordinal), Corpus.Spec).Diagnostics, d => d.Code == "AMP2073");
        // a block header cannot end with a comment: the comment on the line above covers it (for lint rules, and now for any warning or note)
        Assert.Contains(Parser.Parse(head + clip + player, Corpus.Spec).Diagnostics, d => d.Code == "L001");
        Assert.DoesNotContain(Parser.Parse(head + clip + "# ample-ignore L001\n" + player, Corpus.Spec).Diagnostics, d => d.Code == "L001");
        // an error stays, whatever the comment says
        AmpleDocument bad = Parser.Parse(plain.Replace("notes \"1:0:1:60:100\"", "notes \"1:0:1:60:100 9:9:9\"   # ample-ignore AMP2012", StringComparison.Ordinal), Corpus.Spec);
        Assert.Contains(bad.Diagnostics, d => d.Code == "AMP2012" && d.Severity == Severity.Error);
    }
    private static AmpleDocument Song(string helpers, string expr = "theme.notes |> transpose(f(0))")
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n" + helpers + "\n\nmidi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip use at 2 for auto {\n  notes " + expr + "\n}\n";
        return Parser.Parse(song, Corpus.Spec);
    }

    [Fact]
    public void A_helper_body_is_checked_in_every_branch_also_in_the_ones_that_never_run()
    {
        // a name that is nothing, in the else that is never taken
        Assert.Contains(Song("helper f(Int p) -> Int {\n  if p == 0 { return 1 }\n  else { return nothingHere }\n}").Diagnostics, d => d.Code == "AMP2082" && d.Severity == Severity.Warning);
        // in a calculation it is an error, also for a word like b that is a pitch class elsewhere
        Assert.Contains(Song("helper f(Int p) -> Int {\n  let a = b + 1\n  let b = 2\n  return a\n}").Diagnostics, d => d.Code == "AMP2082" && d.Severity == Severity.Error);
        // a literal that cannot be what the helper returns
        Assert.Contains(Song("helper f(Int p) -> Int {\n  if p == 0 { return 1 }\n  else { return \"text\" }\n}").Diagnostics, d => d.Code == "AMP2044" && d.Severity == Severity.Error);
        // a path that ends without a value
        Assert.Contains(Song("helper f(Int p) -> Int {\n  if p == 0 { return 1 }\n}").Diagnostics, d => d.Code == "AMP2083");
        // a statement that does nothing, a variable that is never read, a helper nobody calls
        Assert.Contains(Song("helper f(Int p) -> Int {\n  p\n  return 1\n}").Diagnostics, d => d.Code == "AMP2085" && d.Severity == Severity.Info);
        Assert.Contains(Song("helper f(Int p) -> Int {\n  let unused = 5\n  return 1\n}").Diagnostics, d => d.Code == "AMP2084" && d.Severity == Severity.Info);
        Assert.Contains(Song("helper nobody(Int p) -> Int { return p }", "theme.notes").Diagnostics, d => d.Code == "AMP2086" && d.Severity == Severity.Info);
    }

    [Fact]
    public void A_call_of_a_helper_with_a_syntax_error_gives_one_error_not_two()
    {
        AmpleDocument doc = Song("helper f(Int p) -> Int {\n  if p == 0 return 1\n  return 2\n}");
        Assert.Single(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2087" && d.Severity == Severity.Info);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "AMP2023");
    }

    [Fact]
    public void Good_helper_code_gets_none_of_the_new_diagnostics()
    {
        const string helpers = @"let pause = 2bars
type Steps = Int | Float

helper canonVoice(Notes source, Interval by, Duration delay) -> Notes {
  source |> transpose(by) |> shift(delay)
}

helper buildCanon(Notes theme, Int voices = 3, Interval by = +7st, Duration delay = 2bars) -> Notes {
  for i in 0..voices {
    emit canonVoice(theme, by * i, delay * i)
  }
}

dynamic helper phase() -> Int {
  return currentTime().second % 4
}

helper modeFor(Int p) -> ScaleMode {
  if p == 0 { return major }
  else if p == 1 { return minor }
  else if p == 2 { return dorian }
  else { return blues }
}

helper delayBy(Notes n, Steps s) -> Notes {
  if s is Int { return n |> shift(s * 1beat) }
  return n |> shift(Round(s) * 1beat)
}

helper counted(Notes n) -> Int {
  var total = 0
  for note in n { total = total + 1 }
  return total
}";
        AmpleDocument doc = Song(helpers.Replace("\r\n", "\n", StringComparison.Ordinal), "buildCanon(theme.notes, voices=2 + phase(), delay=pause) |> delayBy(2) |> transpose(counted(theme.notes))");
        string[] mine = ["AMP2082", "AMP2083", "AMP2084", "AMP2085", "AMP2087"];
        Assert.DoesNotContain(doc.Diagnostics, d => mine.Contains(d.Code));
    }

    [Fact]
    public void A_clip_that_only_a_note_pipeline_reads_is_not_reported_as_unused()
    {
        const string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nmidi-clip a at 1 for 2 {\n  notes 1:0:4:60:100\n  bars 2\n}\n\nmidi-clip b at 1 for 4 {\n  notes a.notes |> shift(2bars)\n}\n\nsynth s at 1 for 4 {\n  wave sine\n  from b.notes -> notes\n}\n\nmaster out {\n  from s.audio -> audio\n}\n";
        Assert.DoesNotContain(Parser.Parse(song, Corpus.Spec).Diagnostics, d => d.Code == "L001");
        // a clip that nothing reads is still reported
        Assert.Contains(Parser.Parse(song.Replace("a.notes |> shift(2bars)", "1:0:4:60:100", StringComparison.Ordinal), Corpus.Spec).Diagnostics, d => d.Code == "L001");
    }
}
