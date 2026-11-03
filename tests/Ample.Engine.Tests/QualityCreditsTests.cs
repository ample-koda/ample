using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Engine.Credits;
using Ample.Storage.Quality;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Core.Tests;

[Trait("Category", "Quality")]
public sealed class QualityTests
{
    private const int Rate = 44100;

    private static float[] Tone(double amplitude, double seconds, double hz = 1000, double dc = 0) => [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)((amplitude * Math.Sin(2 * Math.PI * hz * i / Rate)) + dc))];

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static byte[] Wave(IEnumerable<float> samples) => Wav.Encode([.. samples.Select(s => (short)Math.Clamp(Math.Round(s * 32767), -32768, 32767))], Rate);

    [Fact]
    public void A_sine_has_the_loudness_the_standard_gives_it()
    {
        AudioMetrics m = AudioAnalysis.Measure(Tone(0.1, 3), Rate);
        Assert.NotNull(m.Lufs);
        Assert.InRange(m.Lufs!.Value, -23.4, -22.6);
        Assert.InRange(m.PeakDb, -20.1, -19.9);
        Assert.Equal(0, m.ClippedRuns);
        Assert.InRange(m.TruePeakDb, -20.1, -19.7);
    }

    [Fact]
    public void Clipping_dc_offset_and_silence_inside_the_song_are_measured_and_lead_in_and_tail_are_not_counted()
    {
        float[] clipped = [.. Tone(2.0, 1).Select(s => Math.Clamp(s, -1f, 1f))];
        Assert.True(AudioAnalysis.Measure(clipped, Rate).ClippedRuns > 10);

        Assert.InRange(AudioAnalysis.Measure(Tone(0.2, 2, dc: 0.1), Rate).DcOffset, 0.09, 0.11);

        float[] song = [.. Silence(1.5), .. Tone(0.3, 1), .. Silence(3), .. Tone(0.3, 1), .. Silence(2)];
        Assert.InRange(AudioAnalysis.Measure(song, Rate).LongestSilenceSeconds, 2.9, 3.1);
        Assert.Equal(0, AudioAnalysis.Measure(Silence(2), Rate).LongestSilenceSeconds);
        Assert.Null(AudioAnalysis.Measure(Silence(2), Rate).Lufs);
        Assert.Null(AudioAnalysis.Measure(Tone(0.1, 0.1), Rate).Lufs);
    }

    [Fact]
    public void A_true_peak_between_samples_is_higher_than_the_sample_peak()
    {
        // A tone at a quarter of the sample rate with a phase that puts the samples off the crest.
        float[] x = [.. Enumerable.Range(0, 4410).Select(i => (float)Math.Sin((Math.PI / 2 * i) + (Math.PI / 4)))];
        AudioMetrics m = AudioAnalysis.Measure(x, Rate);
        Assert.True(m.TruePeakDb > m.PeakDb + 1.0, $"{m.TruePeakDb} vs {m.PeakDb}");
    }

    [Fact]
    public void Other_formats_are_refused_with_a_plain_message()
    {
        Assert.Throws<InvalidDataException>(() => AudioAnalysis.ReadWav("not a wave file at all, long enough to look like a header"u8));
        (float[] stereoMixed, int rate) = AudioAnalysis.ReadWav(Wave(Tone(0.1, 0.1)));
        Assert.Equal(Rate, rate);
        Assert.Equal(4410, stereoMixed.Length);
    }

    private static async Task<string> TakeAsync(Takebank bank, string block, byte[] wav, string intent = "bake")
    {
        PlanRecord plan = new(Guid.NewGuid(), "uuid-" + block, block, "src-" + Guid.NewGuid().ToString("N"), [], [], 1, "builtin", "1.0", "{}", "{}", intent, null);
        TakeTicket ticket = await bank.BeginAsync(plan);
        TakeRecord take = await ticket.CommitAsync([("audio", new MemoryStream(wav))]);
        return take.TakeId;
    }

    [Fact]
    public async Task The_service_measures_takes_applies_the_rules_and_keeps_the_result()
    {
        using HistoryRig rig = await HistoryRig.CreateAsync(("song.amplet", "one"));
        string loud = await TakeAsync(rig.Bank, "master", Wave(Tone(2.0, 3).Select(s => Math.Clamp(s, -1f, 1f))));
        QualityService service = new(rig.Bank, rig.Clock, rig.Root);
        QualityResult r = service.Check(loud);
        Assert.False(r.Passed);
        Assert.Equal("failed", r.Findings.Single(f => f.RuleId == "Q001").Status);
        Assert.Equal("failed", r.Findings.Single(f => f.RuleId == "Q002").Status);
        Assert.Equal("failed", r.Findings.Single(f => f.RuleId == "Q003").Status);
        Assert.Equal("not-run", r.Findings.Single(f => f.RuleId == "Q007").Status);
        Assert.Equal(loud, service.Result(loud)!.TakeId);
        Assert.Equal(r.Findings.Count, service.Result(loud)!.Findings.Count);

        string good = await TakeAsync(rig.Bank, "master", Wave(Tone(0.28, 4)));
        QualityResult g = service.Check(good);
        Assert.True(g.Passed, string.Join(" | ", g.Findings.Where(f => f.Status == "failed").Select(f => f.Message)));

        string preview = await TakeAsync(rig.Bank, "drums", Wave(Tone(0.1, 1)), "preview");
        Assert.DoesNotContain(service.Check(preview).Findings, f => f.RuleId is "Q003" or "Q004" or "Q006");

        string bad = await TakeAsync(rig.Bank, "weird", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44]);
        QualityResult b = service.Check(bad);
        Assert.StartsWith("AMP5331", b.Error, StringComparison.Ordinal);
        Assert.False(b.Passed);
    }

    [Fact]
    public async Task The_policy_can_make_a_rule_required_for_tag_and_promote_but_nothing_blocks_without_one()
    {
        using HistoryRig rig = await HistoryRig.CreateAsync(("song.amplet", "one"));
        await rig.History.CheckpointAsync("first");
        string clipped = await TakeAsync(rig.Bank, "master", Wave(Tone(2.0, 3).Select(s => Math.Clamp(s, -1f, 1f))));
        await rig.History.TagAsync("v0", "studio", "no policy yet");

        Directory.CreateDirectory(Path.Combine(rig.Root, ".ample"));
        File.WriteAllText(QualityPolicy.FileOf(rig.Root), "{\"require\":{\"tag\":[\"Q001\"],\"promote\":[\"Q001\"]}}");
        AmpleException stop = await Assert.ThrowsAsync<AmpleException>(async () => await rig.History.TagAsync("v1", "studio", "with policy"));
        Assert.Equal("AMP5333", stop.DiagnosticCode);
        Assert.Contains("Q001", stop.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.History.Tags(), t => t.Name == "v1");
        Assert.Equal("AMP5333", (await Assert.ThrowsAsync<AmpleException>(async () => await rig.History.PromoteAsync())).DiagnosticCode);

        rig.Bank.Tombstone(clipped, "replaced by a better mix");
        await TakeAsync(rig.Bank, "master", Wave(Tone(0.28, 4)));
        await rig.History.TagAsync("v2", "studio", "now it passes");
        Assert.Contains(rig.History.Tags(), t => t.Name == "v2");

        File.WriteAllText(QualityPolicy.FileOf(rig.Root), "{\"require\":{\"tag\":[\"Q001\"]},\"masterBlock\":\"nothing-here\"}");
        Assert.Equal("AMP5333", (await Assert.ThrowsAsync<AmpleException>(async () => await rig.History.TagAsync("v3", "studio", "no such block"))).DiagnosticCode);
    }

    [Fact]
    public void A_policy_can_change_thresholds_and_switch_rules_off()
    {
        string root = Directory.CreateTempSubdirectory("ample-qp-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".ample"));
            File.WriteAllText(QualityPolicy.FileOf(root), "{\"targetLufs\":-16,\"toleranceLu\":3,\"thresholds\":{\"Q002\":-3},\"disabled\":[\"Q004\"],\"require\":{\"tag\":[\"Q001\"]}}");
            QualityPolicy p = QualityPolicy.Read(root)!;
            Assert.Equal(-16, p.TargetLufs);
            Assert.Equal(3, p.ToleranceLu);
            Assert.Equal(-3, p.Thresholds["Q002"]);
            Assert.Contains("Q004", p.Disabled);
            File.WriteAllText(QualityPolicy.FileOf(root), "{ not json");
            Assert.Equal("AMP5331", Assert.Throws<AmpleException>(() => QualityPolicy.Read(root)).DiagnosticCode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_rules_file_is_built_in_and_every_rule_has_what_the_service_needs()
    {
        IReadOnlyList<QualityRule> rules = QualityService.BuiltInRules();
        Assert.Equal(["Q001", "Q002", "Q003", "Q004", "Q005", "Q006", "Q007"], rules.Select(r => r.Id));
        Assert.All(rules, r => Assert.NotEmpty(r.Explanation));
    }
}

[Trait("Category", "Credits")]
public sealed class CreditsTests
{
    [Theory]
    [InlineData("", "unknown")]
    [InlineData("CC-BY-4.0", "open")]
    [InlineData("CC BY-NC 4.0", "non-commercial")]
    [InlineData("Non-commercial use only", "non-commercial")]
    [InlineData("CC-BY-ND-4.0", "no-derivatives")]
    [InlineData("CC0", "open")]
    public void Licences_are_sorted_into_classes(string license, string expected) => Assert.Equal(expected, CreditsService.Classify(license));

    [Fact]
    public async Task The_sheet_comes_from_the_plans_the_library_and_the_modules_and_flags_licence_conflicts_in_a_release()
    {
        using HistoryRig rig = await HistoryRig.CreateAsync(("song.amplet", "one"));
        string home = Path.Combine(Directory.CreateTempSubdirectory("ample-credits-").FullName, "home");
        string kits = Path.Combine(Path.GetDirectoryName(home)!, "kits");
        Directory.CreateDirectory(kits);
        File.WriteAllBytes(Path.Combine(kits, "kick.wav"), Wav.Encode(new short[2000], 44100));
        File.WriteAllBytes(Path.Combine(kits, "snare.wav"), Wav.Encode(new short[3000], 44100));
        File.WriteAllBytes(Path.Combine(kits, "hat.wav"), Wav.Encode(new short[1000], 44100));
        using Ample.Storage.Library.Library lib = Ample.Storage.Library.Library.Open(home);
        Ample.Storage.Library.Mount mount = lib.LinkMount("Kits", kits);
        lib.Scan(mount.Id);
        lib.Describe(lib.Assets().Single(a => a.Name == "kick").Uuid, license: "CC BY-NC 4.0", credit: "Studio A");
        lib.Describe(lib.Assets().Single(a => a.Name == "snare").Uuid, license: "CC-BY-4.0", credit: "Studio B");

        PlanRecord plan = new(Guid.NewGuid(), "u1", "drums", "h", [], [], 1, "acestep", "0.3", "{\"model\":\"m1\"}", "{\"voice\":\"anna\"}", "bake", null);
        TakeTicket ticket = await rig.Bank.BeginAsync(plan);
        await ticket.CommitAsync([("audio", new MemoryStream(Wav.Encode(new short[100], 44100)))]);

        string song = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\ndrums drums {\n  kit kick\n  pattern snare\n}\ndrums more {\n  kit hat\n}\n";
        AmpleDocument doc = Parser.Parse(song, EngineRig.Spec);
        ModuleUsage module = new("groove", "1.2", "sha256:abc", "project", 10);

        CreditSheet before = CreditsService.Build(rig.Root, "demo", rig.Bank, lib, [doc], [module], rig.Clock);
        Assert.False(before.ReleaseContext);
        Assert.DoesNotContain(before.Warnings, w => w.Severity == "warning");
        Assert.Contains(before.Warnings, w => w.Message.Contains("'hat'", StringComparison.Ordinal) && w.Message.Contains("no licence", StringComparison.Ordinal));
        Assert.Equal("acestep", before.Blocks.Single().Plugin);
        Assert.Equal("anna", before.Blocks.Single().Voice);
        Assert.Equal(["hat", "kick", "snare"], before.Assets.Select(a => a.Name));
        Assert.Equal(["drums.kit"], before.Assets.Single(a => a.Name == "kick").UsedBy);

        await rig.History.CheckpointAsync("release candidate");
        await rig.History.TagAsync("v1", "studio", "release");
        CreditSheet release = CreditsService.Build(rig.Root, "demo", rig.Bank, lib, [doc], [module], rig.Clock);
        Assert.True(release.ReleaseContext);
        Assert.Contains(release.Warnings, w => w.Severity == "warning" && w.Code == "AMP5332" && w.Message.Contains("'kick'", StringComparison.Ordinal));
        Assert.DoesNotContain(release.Warnings, w => w.Severity == "warning" && w.Message.Contains("'snare'", StringComparison.Ordinal));

        Directory.CreateDirectory(Path.Combine(rig.Root, ".ample"));
        File.WriteAllText(CreditsService.PeopleFile(rig.Root), "{\"usage\":\"personal\",\"people\":[{\"name\":\"Koda\",\"role\":\"words\",\"note\":\"all of them\"}]}");
        CreditSheet withPeople = CreditsService.Build(rig.Root, "demo", rig.Bank, lib, [doc], [module], rig.Clock);
        string md = CreditsService.ToMarkdown(withPeople);
        Assert.Contains("# Credits: demo", md, StringComparison.Ordinal);
        Assert.Contains("- Koda (words): all of them", md, StringComparison.Ordinal);
        Assert.Contains("- **drums**: acestep 0.3, voice anna", md, StringComparison.Ordinal);
        Assert.Contains("- groove 1.2", md, StringComparison.Ordinal);
        Assert.Contains("not legal advice", md, StringComparison.Ordinal);
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(CreditsService.ToJson(withPeople));
        Assert.Equal(3, json.RootElement.GetProperty("assets").GetArrayLength());
        Assert.Equal("Koda", json.RootElement.GetProperty("people")[0].GetProperty("name").GetString());

        CreditsService.WriteSheet(rig.Root, withPeople);
        Assert.True(File.Exists(Path.Combine(rig.Root, ".ample", "credits", "CREDITS.md")));
        Assert.Contains(Ample.Storage.Backup.BackupStore.ProjectFiles(rig.Root, true), f => f.Rel == ".ample/credits/credits.json");
    }
}
