namespace Ample.Language.Tests;

/// <summary>Capabilities (docs/language/16): the policy chain (server ceiling and default, project, the settings of the song in order) only narrows; an explicit off is permanent; gated features fail with the reason; dynamic helpers are declared; names follow the rules.</summary>
[Collection("Static host capability layer")]
public sealed class NoteCapabilitiesTests
{
    private const string Head = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n";

    private const string Clip = "\nmidi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip lead at 2 for auto {\n  notes theme.notes |> transpose(secret())\n}\n";

    private const string Secret = "dynamic helper secret() -> Int {\n  return Int(environment(\"AMPLE_KEY\"))\n}\n";

    private static AmpleDocument Parse(string song, ServerCapabilities? server = null) => Parser.Parse(song, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true, ServerCapabilities = server, Environment = name => name == "AMPLE_KEY" ? "7" : null });

    private static CapabilityState State(AmpleDocument doc, string name) => doc.Capabilities.Single(c => c.Name == name);

    private static ServerCapabilities Forbid(params string[] names) => new(new HashSet<string>(names), new HashSet<string>(["clock", "os-random"]));

    [Fact]
    public void The_defaults_leave_the_harmless_capabilities_on_and_the_sensitive_ones_off_with_the_reason()
    {
        AmpleDocument doc = Parse(Head);
        Assert.True(State(doc, "clock").Effective);
        CapabilityState environment = State(doc, "environment");
        Assert.False(environment.Effective);
        Assert.Contains("off by default", environment.Reason, StringComparison.Ordinal);
        Assert.Contains("capabilities.environment on", environment.Reason, StringComparison.Ordinal);
        Assert.Equal(7, doc.Capabilities.Count);
    }

    [Fact]
    public void A_song_can_switch_on_what_the_ceiling_allows_and_off_is_a_one_way_door()
    {
        Assert.True(State(Parse(Head + "settings { capabilities.environment on }\n"), "environment").Effective);
        AmpleDocument narrowed = Parse(Head + "settings { capabilities.environment on }\nsettings { capabilities.environment off }\n");
        Assert.False(State(narrowed, "environment").Effective);
        Assert.DoesNotContain(narrowed.Diagnostics, d => d.Code == "AMP2076");
        AmpleDocument reopened = Parse(Head + "settings { capabilities.environment off }\nsettings { capabilities.environment on }\n");
        Assert.False(State(reopened, "environment").Effective);   // the capability stays off
        Diagnostic fatal = Assert.Single(reopened.Diagnostics, d => d.Code == "AMP2076");
        Assert.Equal(Severity.Error, fatal.Severity);
        Assert.Contains("cannot be turned on again", fatal.Message, StringComparison.Ordinal);
        Assert.Contains("permanently disabled by this song", State(reopened, "environment").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_below_the_server_can_open_what_the_server_forbids()
    {
        AmpleDocument doc = Parse(Head + "settings { capabilities.environment on }\n", Forbid("environment"));
        Assert.False(State(doc, "environment").Effective);
        Assert.Equal("server", State(doc, "environment").Scope);
        Assert.Equal("disabled by server policy", State(doc, "environment").Reason);
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2076" && d.Message.Contains("server policy", StringComparison.Ordinal));
        AmpleDocument fromProject = Parser.Parse(Head + "settings { capabilities.environment on }\n", Corpus.Spec, new ParseOptions { ProjectCapabilities = new Dictionary<string, string> { ["environment"] = "off" } });
        Assert.False(State(fromProject, "environment").Effective);
        Assert.Equal("disabled by project policy", State(fromProject, "environment").Reason);
        Assert.Contains(fromProject.Diagnostics, d => d.Code == "AMP2076" && d.Message.Contains("project", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_capability_gets_a_suggestion()
    {
        Diagnostic unknown = Assert.Single(Parse(Head + "settings { capabilities.enviroment on }\n").Diagnostics, d => d.Code == "AMP2076");
        Assert.NotNull(unknown.Fix);
        Assert.Equal("environment", unknown.Fix!.Text);
    }

    [Fact]
    public void A_denied_capability_is_an_error_that_says_why_in_the_helper_and_where_it_runs()
    {
        AmpleDocument off = Parse(Head + Secret + Clip);
        Diagnostic denied = Assert.Single(off.Diagnostics, d => d.Code == "AMP2077" && d.Message.Contains("needs the capability", StringComparison.Ordinal));
        Assert.Contains("off by default", denied.Message, StringComparison.Ordinal);
        Assert.Contains(off.Diagnostics, d => d.Code == "AMP2077" && d.Message.StartsWith("Capability", StringComparison.Ordinal));   // and again where the clip runs it
        AmpleDocument on = Parse(Head + "settings { capabilities.environment on }\n" + Secret + Clip);
        Assert.DoesNotContain(on.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("dynamic", on.Stability);
        string notes = on.Blocks.Single(b => b.Name == "lead").Parameters.Single(p => p.Name == "notes").Value;
        Assert.StartsWith("1:0:1:67:100", notes, StringComparison.Ordinal);   // the environment gave 7
        Assert.DoesNotContain(on.StabilityNotes, n => n.Contains("= 7", StringComparison.Ordinal));   // the value is never written down, only a fingerprint
        Assert.Contains(on.StabilityNotes, n => n.Contains("environment(\"AMPLE_KEY\") = sha256:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_server_that_forbids_the_clock_refuses_dynamic_time()
    {
        string song = Head + "dynamic helper now() -> Int { currentTime().minute }\n" + Clip.Replace("secret()", "now()", StringComparison.Ordinal);
        Assert.Contains(Parse(song, Forbid("clock")).Diagnostics, d => d.Code == "AMP2077");
        Assert.DoesNotContain(Parse(song).Diagnostics, d => d.Code == "AMP2077");
    }

    [Fact]
    public void A_dynamic_helper_must_say_so_and_helper_names_are_lower_camel_case()
    {
        string unmarked = Head + "helper nudge() -> Int { currentTime().minute }\n" + Clip.Replace("secret()", "nudge()", StringComparison.Ordinal);
        Diagnostic effect = Assert.Single(Parse(unmarked).Diagnostics, d => d.Code == "AMP2060");
        Assert.Equal(Severity.Error, effect.Severity);
        Assert.NotNull(effect.Fix);
        Diagnostic naming = Assert.Single(Parse(Head + "helper canon-voice(Int a) -> Int { a }\n").Diagnostics, d => d.Code == "AMP2078");
        Assert.Contains("canonVoice", naming.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Random_seed_is_spelled_with_camel_case_and_the_old_spelling_still_works_with_a_warning()
    {
        Assert.Empty(NotePipeline.CheckTypes("1:0:1:60:100 |> thin(0.5, seed=randomSeed())"));
        PipeIssue old = Assert.Single(NotePipeline.CheckTypes("1:0:1:60:100 |> thin(0.5, seed=random-seed())"));
        Assert.Equal("deprecated-name", old.Id);
        Assert.Equal("randomSeed", old.Suggestion);
        PipeResult unresolved = NotePipeline.Evaluate("1:0:1:60:100 |> thin(0.5, seed=randomSeed())", new NotePipeline.Context());
        Assert.Contains(unresolved.Errors, e => e.Id == "unresolved-random-seed");
    }

    [Fact]
    public void The_policy_guard_refuses_anything_that_widens_capabilities_or_limits_and_accepts_narrowing()
    {
        AmpleDocument Doc(string settings) => Parse(Head + settings);
        AmpleDocument plain = Doc(string.Empty);
        Assert.Contains(PolicyGuard.Widening(plain, Doc("settings { capabilities.environment on }\n")), r => r.Contains("turns on the capability environment", StringComparison.Ordinal));
        AmpleDocument locked = Doc("settings { capabilities.environment off }\n");
        Assert.Contains(PolicyGuard.Widening(locked, plain), r => r.Contains("removes the line that turns environment off", StringComparison.Ordinal));
        Assert.Contains(PolicyGuard.Widening(Doc("settings { limits.fuel 1000 }\n"), Doc("settings { limits.fuel 5000 }\n")), r => r.Contains("raises limits.fuel from 1000 to 5000", StringComparison.Ordinal));
        Assert.Empty(PolicyGuard.Widening(plain, locked));   // turning something off is always fine
        Assert.Empty(PolicyGuard.Widening(Doc("settings { limits.fuel 5000 }\n"), Doc("settings { limits.fuel 1000 }\n")));
        Assert.Empty(PolicyGuard.Widening(plain, plain));
    }

    [Fact]
    public void Environment_allow_lists_only_narrow_through_server_project_and_song()
    {
        List<CapabilityIssue> issues = [];
        ServerCapabilities server = ServerCapabilities.Local with { EnvironmentAllow = ["TZ", "LANG*", "AMPLE_*"] };
        CapabilityPolicy policy = CapabilityPolicy.Resolve(server, new Dictionary<string, string> { ["environment"] = "on", ["environment.allow"] = "TZ, AMPLE_*" }, [("environment.allow", "AMPLE_KEY", 3)], issues);
        Assert.Empty(issues);
        Assert.True(policy.Allowed("environment"));
        Assert.Equal(3, policy.AllowLists("environment").Count);
        Assert.True(policy.NameAllowed("environment", "AMPLE_KEY"));
        Assert.False(policy.NameAllowed("environment", "AMPLE_OTHER"));   // the song narrowed it to one name
        Assert.False(policy.NameAllowed("environment", "TZ"));            // the song's list does not have it, so no list can widen it again
        Assert.False(policy.NameAllowed("environment", "HOME"));

        List<CapabilityIssue> bad = [];
        _ = CapabilityPolicy.Resolve(ServerCapabilities.Local, null, [("clock.allow", "x", 1), ("environment.allow", " ", 2)], bad);
        Assert.Equal(2, bad.Count);
    }

    [Fact]
    public void The_project_layer_of_the_host_is_used_by_every_parse()
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nmidi-clip a at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n";
        try
        {
            ServerCapabilities.Project = new Dictionary<string, string> { ["clock"] = "off" };
            AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
            Assert.False(doc.CapabilityPolicy.Allowed("clock"));
            Assert.Contains("project", doc.Capabilities.First(c => c.Name == "clock").Reason, StringComparison.Ordinal);
        }
        finally
        {
            ServerCapabilities.Project = null;
        }
    }
}

/// <summary>One test sets the static project layer of the host (ServerCapabilities.Project): it must not run beside tests that parse songs that use the clock.</summary>
[CollectionDefinition("Static host capability layer", DisableParallelization = true)]
public sealed class StaticHostCapabilityLayer;
