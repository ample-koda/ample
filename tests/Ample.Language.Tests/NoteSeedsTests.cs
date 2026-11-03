namespace Ample.Language.Tests;

/// <summary>The seed model in a document: renames and moves never change the music, the master-seed does (for stable seeds only), copies reuse / fork / go independent, randomSeed() has to be picked,
/// streams consume in order, and bakes know that stable seeds follow the master-seed.</summary>
public sealed class NoteSeedsTests
{
    private const string Source = "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100\"\n}\n\n";

    private static string Song(string clips, string master = "424242") => $"composition \"x\" {{ ample 1  tempo 120  master-seed {master} }}\n\n{Source}{clips}";

    private static string Clip(string name, string body, int at = 2) => $"midi-clip {name} at {at} for auto {{\n{body}\n}}\n";

    private static string NotesOf(string song, string block) => Parser.Parse(song, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true }).Blocks.Single(b => b.Name == block).Parameters.Single(p => p.Name == "notes").Value;

    private static IReadOnlyList<string> Codes(string song) => [.. Parser.Parse(song, Corpus.Spec).Diagnostics.Select(d => d.Code)];

    private const string Humanize = "  notes a.notes\n    |> humanize(timing=30ms, velocity=6, seed=stable(\"verse\"))";

    [Fact]
    public void Renaming_a_clip_or_moving_it_does_not_change_the_music()
    {
        string notes = NotesOf(Song(Clip("lead", Humanize)), "lead");
        Assert.Equal(notes, NotesOf(Song(Clip("renamed-lead", Humanize)), "renamed-lead"));
        Assert.Equal(notes, NotesOf(Song(Clip("lead", Humanize, at: 9)), "lead"));
    }

    [Fact]
    public void An_unrelated_random_call_elsewhere_does_not_shift_a_stable_result()
    {
        string alone = NotesOf(Song(Clip("lead", Humanize)), "lead");
        string noise = Clip("noise", "  notes a.notes\n    |> thin(0.5, seed=stable(\"other\"))\n    |> randomize(velocity=1..=127, seed=stable(\"more\"))", 5);
        Assert.Equal(alone, NotesOf(Song(noise + "\n" + Clip("lead", Humanize)), "lead"));
    }

    [Fact]
    public void A_new_master_seed_changes_stable_seeds_and_leaves_explicit_ones()
    {
        string stable = Clip("lead", Humanize), pinned = Clip("lead", Humanize.Replace("stable(\"verse\")", "7", StringComparison.Ordinal));
        Assert.NotEqual(NotesOf(Song(stable, "1"), "lead"), NotesOf(Song(stable, "2"), "lead"));
        Assert.Equal(NotesOf(Song(stable, "1"), "lead"), NotesOf(Song(stable, "1"), "lead"));
        Assert.Equal(NotesOf(Song(pinned, "1"), "lead"), NotesOf(Song(pinned, "2"), "lead"));
    }

    [Fact]
    public void A_bake_that_uses_a_stable_seed_goes_stale_when_the_master_seed_changes_but_not_for_an_explicit_seed()
    {
        string baked = NoteBake.Bake(Song(Clip("lead", Humanize + "\n    |> bake s")), Corpus.Spec, "lead", "s", null, null, 100_000).Text;
        Assert.DoesNotContain("AMP2030", Codes(baked));
        Assert.Contains("AMP2030", Codes(baked.Replace("master-seed 424242", "master-seed 5", StringComparison.Ordinal)));
        string pinned = NoteBake.Bake(Song(Clip("lead", Humanize.Replace("stable(\"verse\")", "7", StringComparison.Ordinal) + "\n    |> bake s")), Corpus.Spec, "lead", "s", null, null, 100_000).Text;
        Assert.DoesNotContain("AMP2030", Codes(pinned.Replace("master-seed 424242", "master-seed 5", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rebaking_gives_the_same_bake_for_a_stable_seed()
    {
        string once = NoteBake.Bake(Song(Clip("lead", Humanize + "\n    |> bake s")), Corpus.Spec, "lead", "s", null, null, 100_000).Text;
        Assert.Equal(once, NoteBake.Bake(once, Corpus.Spec, "lead", "s", null, null, 100_000).Text);
    }

    [Fact]
    public void A_random_seed_has_to_be_picked_and_after_that_the_result_is_reproducible()
    {
        string song = Song(Clip("lead", "  notes a.notes\n    |> humanize(timing=30ms, seed=randomSeed())   # pick me"));
        Assert.Contains("AMP2039", Codes(song));
        (string picked, int count) = NoteBake.PickSeeds(song, () => 123456789012L);
        Assert.Equal(1, count);
        Assert.Contains("seed=123456789012)   # pick me", picked, StringComparison.Ordinal);
        Assert.DoesNotContain("AMP2039", Codes(picked));
        Assert.Equal(NotesOf(picked, "lead"), NotesOf(picked, "lead"));
        Assert.Equal(0, NoteBake.PickSeeds("# randomSeed() in a comment\n", () => 1).Count);
    }

    [Fact]
    public void A_copy_can_reuse_fork_or_leave_the_namespaces_and_the_effect_on_the_music_is_what_it_says()
    {
        string song = Song(Clip("lead", Humanize));
        string reuse = NoteBake.DuplicateClip(song, Corpus.Spec, "lead", "lead-2", NoteBake.SeedCopyMode.Reuse);
        string fork = NoteBake.DuplicateClip(song, Corpus.Spec, "lead", "lead-2", NoteBake.SeedCopyMode.Fork);
        string independent = NoteBake.DuplicateClip(song, Corpus.Spec, "lead", "lead-2", NoteBake.SeedCopyMode.Independent);
        Assert.Equal(NotesOf(reuse, "lead"), NotesOf(reuse, "lead-2"));
        Assert.NotEqual(NotesOf(fork, "lead"), NotesOf(fork, "lead-2"));
        Assert.NotEqual(NotesOf(independent, "lead"), NotesOf(independent, "lead-2"));
        Assert.Equal(NotesOf(song, "lead"), NotesOf(fork, "lead"));   // the original is untouched
        Assert.Contains("stable(\"verse\").fork(\"copy-lead-2\")", fork, StringComparison.Ordinal);
        Assert.Contains("stable(\"verse_2\")", independent, StringComparison.Ordinal);
        Assert.Contains("midi-clip lead-2", reuse, StringComparison.Ordinal);
        Assert.DoesNotContain("AMP2002", Codes(fork));
    }

    [Fact]
    public void Independent_copies_get_the_next_free_names_with_one_mapping_per_copy()
    {
        string two = "  notes a.notes\n    |> humanize(timing=30ms, seed=stable(\"verse\"))\n    |> randomize(velocity=60..=90, seed=stable(\"verse\"))";
        string song = Song(Clip("lead", two));
        string first = NoteBake.DuplicateClip(song, Corpus.Spec, "lead", "c2", NoteBake.SeedCopyMode.Independent);
        string second = NoteBake.DuplicateClip(first, Corpus.Spec, "lead", "c3", NoteBake.SeedCopyMode.Independent);
        Assert.Equal(2, first.Split("stable(\"verse_2\")").Length - 1);   // both uses in the copy moved to the same new name
        Assert.Equal(2, second.Split("stable(\"verse_3\")").Length - 1);
    }

    [Fact]
    public void Explicit_seeds_and_streams_are_copied_sensibly()
    {
        string body = "  random-stream drift stable(\"verse\").fork(\"drift\")\n  notes a.notes\n    |> shift(drift.next(0ms..=20ms))\n    |> humanize(timing=10ms, seed=7)";
        string song = Song(Clip("lead", body));
        string independent = NoteBake.DuplicateClip(song, Corpus.Spec, "lead", "c2", NoteBake.SeedCopyMode.Independent);
        Assert.Contains("random-stream drift stable(\"verse_2\").fork(\"drift\")", independent, StringComparison.Ordinal);
        Assert.Contains("seed=7", independent, StringComparison.Ordinal);
        string fork = NoteBake.DuplicateClip(song, Corpus.Spec, "lead", "c2", NoteBake.SeedCopyMode.Fork);
        Assert.Contains("random-stream drift stable(\"verse\").fork(\"drift\").fork(\"copy-c2\")", fork, StringComparison.Ordinal);
        Assert.NotEqual(NotesOf(fork, "lead"), NotesOf(fork, "c2"));
    }

    [Fact]
    public void A_stream_consumes_in_order_and_the_provenance_says_how_much()
    {
        string body = "  random-stream drift stable(\"verse\").fork(\"drift\")\n  notes a.notes\n    |> shift(drift.next(0ms..=20ms))\n    |> transpose(drift.next(-2..=2))";
        (IReadOnlyList<SeedUseInfo> seeds, IReadOnlyList<StreamUseInfo> streams, IReadOnlyList<(string Name, int Count)> names) = NoteBake.Provenance(Song(Clip("lead", body)), Corpus.Spec, "lead", null, null);
        Assert.Single(streams);
        Assert.Equal(2, streams[0].Consumed);
        Assert.Equal("drift", streams[0].Name);
        Assert.Equal(16, streams[0].Key.Length);
        Assert.Contains(seeds, s => s.Operator == "stream:drift" && s.Draws == 2);
        Assert.Equal([("verse", 1)], names);
        // the same two values written as stateless draws give the same music
        string stateless = "  notes a.notes\n    |> shift(random(0ms..=20ms, seed=stable(\"verse\").fork(\"drift\"), round=0))\n    |> transpose(random(-2..=2, seed=stable(\"verse\").fork(\"drift\"), round=1))";
        Assert.Equal(NotesOf(Song(Clip("lead", body)), "lead"), NotesOf(Song(Clip("lead", stateless)), "lead"));
    }

    [Fact]
    public void The_provenance_lists_every_seed_with_its_key_and_draws()
    {
        (IReadOnlyList<SeedUseInfo> seeds, _, _) = NoteBake.Provenance(Song(Clip("lead", Humanize)), Corpus.Spec, "lead", null, null);
        SeedUseInfo use = Assert.Single(seeds);
        Assert.Equal("humanize", use.Operator);
        Assert.Equal("stable(\"verse\")", use.Seed);
        Assert.Equal(NoteRandom.Hex(NoteRandom.Stable(424242, "verse")), use.Key);
        Assert.Equal(8, use.Draws);   // a timing and a velocity draw for each of the 4 notes
    }

    [Fact]
    public void A_stable_name_that_looks_like_a_typo_is_warned_about_with_a_fix()
    {
        string song = Song(Clip("b", "  notes a.notes\n    |> humanize(timing=10ms, seed=stable(\"verse\"))", 2) + "\n" + Clip("c", "  notes a.notes\n    |> humanize(timing=10ms, seed=stable(\"verse\"))", 3) + "\n" + Clip("d", "  notes a.notes\n    |> humanize(timing=10ms, seed=stable(\"verce\"))", 4));
        Diagnostic typo = Parser.Parse(song, Corpus.Spec).Diagnostics.Single(d => d.Code == "AMP2041");
        Assert.Equal("\"verse\"", typo.Fix!.Text);
    }

    [Fact]
    public void A_fork_label_that_is_a_number_is_not_a_name_and_nesting_order_matters()
    {
        string Notes(string seed) => NotesOf(Song(Clip("lead", $"  notes a.notes\n    |> transpose(random(-12..=12, seed={seed}, round=0))")), "lead");
        Assert.NotEqual(Notes("stable(\"v\").fork(3)"), Notes("stable(\"v\").fork(\"3\")"));
        Assert.NotEqual(Notes("stable(\"v\").fork(\"a\").fork(\"b\")"), Notes("stable(\"v\").fork(\"b\").fork(\"a\")"));
        Assert.Equal(Notes("stable(\"v\").fork(\"a\")"), Notes("stable( \"v\" ) . fork( \"a\" )"));
    }
}
