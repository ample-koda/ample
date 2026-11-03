using System.Diagnostics;
using Ample.Core.Abstractions;
using Ample.Engine.Library;
using Ample.Storage.BlackBox;
using Ample.Storage.Library;
using Ample.Language;

namespace Ample.Core.Tests;

internal sealed class LibraryRig : IDisposable
{
    public readonly string Base = Directory.CreateTempSubdirectory("ample-lib-").FullName;
    public readonly Ample.Storage.Library.Library Lib;

    public LibraryRig() => Lib = Ample.Storage.Library.Library.Open(Path.Combine(Base, "home"));

    public string Folder(string name)
    {
        string p = Path.Combine(Base, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string Put(string folder, string rel, string text)
    {
        string full = Path.Combine(folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        Lib.Dispose();
        try
        {
            Directory.Delete(Base, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the test run.
        }
    }
}

[Trait("Category", "Library")]
public sealed class LibraryTests
{
    private static AmpleDocument Song(string text) => Parser.Parse(text, Spec.Value);

    private static class Spec
    {
        public static readonly SpecData Value = Load();

        private static SpecData Load()
        {
            string root = AppContext.BaseDirectory;
            while (!Directory.Exists(Path.Combine(root, "spec")))
            {
                root = Path.GetDirectoryName(root)!;
            }

            string spec = Path.Combine(root, "spec");
            return SpecData.FromFiles(Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText));
        }
    }

    [Fact]
    public void Nothing_is_registered_until_a_person_names_a_folder_and_a_read_only_link_is_never_written_to()
    {
        using LibraryRig r = new();
        Assert.Empty(r.Lib.Mounts());
        string kits = r.Folder("kits");
        string before = LibraryRig.Put(kits, "tape/kick.wav", "RIFF-kick");
        Mount ro = r.Lib.LinkMount("Kits", kits);
        Assert.True(ro.ReadOnly);
        Assert.False(File.Exists(Path.Combine(kits, ".ample-catalog.json")));
        r.Lib.Scan(ro.Id);
        Assert.Equal(["tape/kick.wav"], Directory.GetFiles(kits, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(kits, f).Replace('\\', '/')).ToArray());
        Assert.Equal("RIFF-kick", File.ReadAllText(before));

        string work = r.Folder("work");
        Mount rw = r.Lib.LinkMount("Work", work, readOnly: false);
        Assert.True(File.Exists(Path.Combine(work, ".ample-catalog.json")));
        Assert.Contains(rw.Uuid, File.ReadAllText(Path.Combine(work, ".ample-catalog.json")), StringComparison.Ordinal);
        Assert.Equal("AMP5290", Assert.Throws<AmpleException>(() => r.Lib.LinkMount("Nope", Path.Combine(r.Base, "missing"))).DiagnosticCode);
    }

    [Fact]
    public void A_scan_finds_kinds_hashes_the_right_ones_and_never_opens_other_files()
    {
        using LibraryRig r = new();
        string dir = r.Folder("lib");
        LibraryRig.Put(dir, "drums/kick.wav", "kick");
        LibraryRig.Put(dir, "lyrics/chorus.lyr", "words");
        LibraryRig.Put(dir, "chords/main.chords", "Am F");
        LibraryRig.Put(dir, "data/weather.json", "{}");
        LibraryRig.Put(dir, "packs/felt/pack.json", "{\"name\":\"felt-piano\",\"version\":\"2.1\"}");
        LibraryRig.Put(dir, "notes/readme.txt", "do not read me");
        LibraryRig.Put(dir, ".hidden/secret.wav", "hidden");
        Mount m = r.Lib.LinkMount("Lib", dir);
        ScanReport s = r.Lib.Scan(m.Id);
        Assert.Equal(6, s.Added);
        Assert.Equal(5, s.Hashed);
        IReadOnlyList<AssetRecord> all = r.Lib.Assets(m.Id);
        Assert.Equal(AssetKind.Sample, all.Single(a => a.Name == "drums/kick").Kind);
        Assert.Equal(AssetKind.Lyrics, all.Single(a => a.Name == "lyrics/chorus").Kind);
        Assert.Equal(AssetKind.Chords, all.Single(a => a.Name == "chords/main").Kind);
        Assert.Equal(AssetKind.Data, all.Single(a => a.Name == "data/weather").Kind);
        Assert.Equal("felt-piano@2.1", all.Single(a => a.Kind == AssetKind.Pack).Name);
        AssetRecord other = all.Single(a => a.Kind == AssetKind.Other);
        Assert.Null(other.Hash);
        Assert.StartsWith("sha256:", all.Single(a => a.Name == "drums/kick").Hash, StringComparison.Ordinal);
        Assert.DoesNotContain(all, a => a.RelPath.Contains("secret", StringComparison.Ordinal));
        Assert.All(all, a => Assert.StartsWith("ast-", a.Uuid, StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_scan_reads_nothing_and_a_changed_file_gets_a_new_revision_with_the_same_uuid()
    {
        using LibraryRig r = new();
        string dir = r.Folder("lib");
        string kick = LibraryRig.Put(dir, "kick.wav", "one");
        LibraryRig.Put(dir, "snare.wav", "two");
        Mount m = r.Lib.LinkMount("Lib", dir);
        r.Lib.Scan(m.Id);
        ScanReport again = r.Lib.Scan(m.Id);
        Assert.Equal((0, 0, 0, 0, 2, 0), (again.Added, again.Changed, again.Moved, again.Missing, again.Unchanged, again.Hashed));

        string uuid = r.Lib.Assets(m.Id).Single(a => a.Name == "kick").Uuid;
        File.WriteAllText(kick, "one but longer");
        ScanReport changed = r.Lib.Scan(m.Id);
        Assert.Equal(1, changed.Changed);
        AssetRecord after = r.Lib.Assets(m.Id).Single(a => a.Name == "kick");
        Assert.Equal(uuid, after.Uuid);
        Assert.Equal(2, after.Revision);

        File.WriteAllText(kick, "one but longer");
        File.SetLastWriteTimeUtc(kick, DateTime.UtcNow.AddMinutes(5));
        ScanReport same = r.Lib.Scan(m.Id);
        Assert.Equal((0, 1, 1), (same.Changed, same.Unchanged - 1, same.Hashed));
        Assert.Equal(2, r.Lib.Assets(m.Id).Single(a => a.Name == "kick").Revision);
    }

    [Fact]
    public void A_renamed_file_is_the_same_asset_a_deleted_one_is_missing_and_duplicates_are_found_by_hash()
    {
        using LibraryRig r = new();
        string dir = r.Folder("lib");
        string kick = LibraryRig.Put(dir, "kick.wav", "kick content");
        LibraryRig.Put(dir, "gone.wav", "gone content");
        LibraryRig.Put(dir, "copy-a.wav", "same bytes");
        LibraryRig.Put(dir, "copy-b.wav", "same bytes");
        Mount m = r.Lib.LinkMount("Lib", dir);
        r.Lib.Scan(m.Id);
        string uuid = r.Lib.Assets(m.Id).Single(a => a.Name == "kick").Uuid;
        Assert.Single(r.Lib.Duplicates());

        Directory.CreateDirectory(Path.Combine(dir, "drums"));
        File.Move(kick, Path.Combine(dir, "drums", "bd.wav"));
        File.Delete(Path.Combine(dir, "gone.wav"));
        ScanReport s = r.Lib.Scan(m.Id);
        Assert.Equal((1, 1, 0), (s.Moved, s.Missing, s.Added));
        AssetRecord moved = r.Lib.Assets(m.Id).Single(a => a.Uuid == uuid);
        Assert.Equal("drums/bd", moved.Name);
        Assert.False(moved.Missing);
        Assert.True(r.Lib.Assets(m.Id).Single(a => a.Name == "gone").Missing);
    }

    [Fact]
    public void Relocate_repairs_a_moved_folder_by_marker_or_fingerprint_and_refuses_a_stranger()
    {
        using LibraryRig r = new();
        string dir = r.Folder("old");
        LibraryRig.Put(dir, "kick.wav", "kick");
        LibraryRig.Put(dir, "snare.wav", "snare");
        Mount rw = r.Lib.LinkMount("Writable", dir, readOnly: false);
        r.Lib.Scan(rw.Id);
        string[] uuids = [.. r.Lib.Assets(rw.Id).Select(a => a.Uuid)];

        string moved = Path.Combine(r.Base, "new-place");
        Directory.Move(dir, moved);
        Assert.Equal("offline", r.Lib.GetMount(rw.Id).State);
        Assert.Equal(moved, r.Lib.FindMoved(rw.Id, [r.Base]));
        Mount back = r.Lib.Relocate(rw.Id, moved);
        Assert.Equal("online", back.State);
        Assert.Equal(uuids.Order(), r.Lib.Assets(rw.Id).Select(a => a.Uuid).Order());
        Assert.All(r.Lib.Assets(rw.Id), a => Assert.False(a.Missing));

        string stranger = r.Folder("stranger");
        LibraryRig.Put(stranger, "other.wav", "other");
        Assert.Equal("AMP5292", Assert.Throws<AmpleException>(() => r.Lib.Relocate(rw.Id, stranger)).DiagnosticCode);
        Assert.Equal(stranger, r.Lib.Relocate(rw.Id, stranger, force: true).Path);

        string ro = r.Folder("readonly");
        LibraryRig.Put(ro, "a.wav", "aaa");
        Mount roMount = r.Lib.LinkMount("Read only", ro);
        string copy = Path.Combine(r.Base, "readonly-on-another-drive");
        Directory.Move(ro, copy);
        Assert.Equal(copy, r.Lib.Relocate(roMount.Id, copy).Path);
    }

    [Fact]
    public void Writes_into_a_read_only_mount_are_refused_by_code()
    {
        using LibraryRig r = new();
        Mount ro = r.Lib.LinkMount("Kits", r.Folder("kits"));
        Assert.Equal("AMP5293", Assert.Throws<AmpleException>(() => r.Lib.ResolveWritable(ro.Id, "new.wav")).DiagnosticCode);
        Mount rw = r.Lib.LinkMount("Mine", r.Folder("mine"), readOnly: false);
        Assert.StartsWith(rw.Path, r.Lib.ResolveWritable(rw.Id, "sub/new.wav"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("AMP5201", Assert.Throws<AmpleException>(() => r.Lib.ResolveWritable(rw.Id, "../outside.wav")).DiagnosticCode);
        r.Lib.SetReadOnly(rw.Id, true);
        Assert.Equal("AMP5293", Assert.Throws<AmpleException>(() => r.Lib.ResolveWritable(rw.Id, "x.wav")).DiagnosticCode);
        Directory.Delete(rw.Path, true);
        r.Lib.SetReadOnly(rw.Id, false);
        Assert.Equal("AMP5290", Assert.Throws<AmpleException>(() => r.Lib.ResolveWritable(rw.Id, "x.wav")).DiagnosticCode);
    }

    [Fact]
    public void Resolution_goes_project_then_project_links_then_user_links_then_packs()
    {
        using LibraryRig r = new();
        string project = r.Folder("project");
        string projectKits = r.Folder("project-kits");
        string userKits = r.Folder("user-kits");
        string packs = r.Folder("packs");
        LibraryRig.Put(project, "assets/kit/tape.wav", "from project");
        LibraryRig.Put(projectKits, "kit/tape.wav", "from project link");
        LibraryRig.Put(userKits, "kit/tape.wav", "from user library");
        LibraryRig.Put(userKits, "kit/only-user.wav", "only user");
        LibraryRig.Put(packs, "kit/pack.json", "{\"name\":\"kit\",\"version\":\"1.0\"}");
        Mount pl = r.Lib.LinkMount("Project kits", projectKits, "project", "proj-1");
        Mount ul = r.Lib.LinkMount("User kits", userKits);
        Mount pk = r.Lib.LinkMount("Packs", packs);
        foreach (Mount m in new[] { pl, ul, pk })
        {
            r.Lib.Scan(m.Id);
        }

        Resolution a = r.Lib.Resolve(new AssetRef("kit/tape"), project, "proj-1");
        Assert.Equal(("found", "project"), (a.Status, a.Where));
        File.Delete(Path.Combine(project, "assets/kit/tape.wav"));
        Resolution b = r.Lib.Resolve(new AssetRef("kit/tape"), project, "proj-1");
        Assert.Equal($"mount:{pl.Id}", b.Where);
        Resolution b2 = r.Lib.Resolve(new AssetRef("kit/tape"), project, "other-project");
        Assert.Equal($"mount:{ul.Id}", b2.Where);
        Assert.Equal($"mount:{ul.Id}", r.Lib.Resolve(new AssetRef("kit/only-user"), project, "proj-1").Where);
        Assert.Equal(("found", "pack"), (r.Lib.Resolve(new AssetRef("kit"), project).Status, r.Lib.Resolve(new AssetRef("kit"), project).Where));

        Resolution none = r.Lib.Resolve(new AssetRef("nowhere/at-all"), project);
        Assert.Equal(("missing", "AMP5291"), (none.Status, none.Code));
    }

    [Fact]
    public void A_changed_pinned_hash_an_offline_folder_and_a_vanished_file_each_say_what_is_wrong()
    {
        using LibraryRig r = new();
        string dir = r.Folder("lib");
        string kick = LibraryRig.Put(dir, "kick.wav", "kick");
        Mount m = r.Lib.LinkMount("Lib", dir);
        r.Lib.Scan(m.Id);
        string pinned = r.Lib.Assets(m.Id).Single().Hash!;
        Assert.Equal("found", r.Lib.Resolve(new AssetRef("kick", Hash: pinned)).Status);
        File.WriteAllText(kick, "kick changed");
        Resolution changed = r.Lib.Resolve(new AssetRef("kick", Hash: pinned));
        Assert.Equal(("changed", "AMP5294"), (changed.Status, changed.Code));
        File.Delete(kick);
        Assert.Equal("AMP5291", r.Lib.Resolve(new AssetRef("kick")).Code);
        string away = Path.Combine(r.Base, "away");
        Directory.Move(dir, away);
        Resolution offline = r.Lib.Resolve(new AssetRef("kick"));
        Assert.Equal(("offline", "AMP5290"), (offline.Status, offline.Code));
    }

    [Fact]
    public async Task The_lock_pins_what_a_song_uses_is_the_same_bytes_every_time_and_verify_notices_changes()
    {
        using LibraryRig r = new();
        string project = r.Folder("project");
        string dir = r.Folder("lib");
        string kit = LibraryRig.Put(dir, "kit/tape.wav", "tape kit");
        LibraryRig.Put(dir, "unused.wav", "unused");
        LibraryRig.Put(dir, "felt/pack.json", "{\"name\":\"felt-piano\",\"version\":\"2.1\"}");
        Mount m = r.Lib.LinkMount("Lib", dir);
        r.Lib.Scan(m.Id);
        AmpleDocument song = Song("composition \"d\" { ample 1  tempo 92  master-seed 1 }\ndrums d {\n  kit kit/tape\n}\npiano p pinned@2.0 {\n  set felt-piano/close\n}\n");
        string first = await LockFile.UpdateAsync(project, r.Lib, [song]);
        Assert.Equal(first, await LockFile.UpdateAsync(project, r.Lib, [song]));
        IReadOnlyList<LockEntry> entries = LockFile.Parse(first);
        Assert.Equal(["kit/tape", "felt-piano@2.1"], entries.Select(e => e.Name).ToArray());
        Assert.DoesNotContain(entries, e => e.Name == "unused");
        Assert.Equal(LockFile.Serialize(entries), first);
        Assert.All(LockFile.Verify(project, r.Lib), c => Assert.Equal("ok", c.Status));

        File.WriteAllText(kit, "tape kit, tampered");
        r.Lib.Scan(m.Id);
        LockCheck changed = LockFile.Verify(project, r.Lib).Single(c => c.Entry.Name == "kit/tape");
        Assert.Equal("changed", changed.Status);
        File.Delete(kit);
        r.Lib.Scan(m.Id);
        Assert.Equal("missing", LockFile.Verify(project, r.Lib).Single(c => c.Entry.Name == "kit/tape").Status);
        string away = Path.Combine(r.Base, "away");
        Directory.Move(dir, away);
        Assert.Equal("offline", LockFile.Verify(project, r.Lib).Single(c => c.Entry.Name == "felt-piano@2.1").Status);
    }

    [Fact]
    public void A_damaged_lock_file_is_reported_not_guessed_at()
    {
        Assert.Equal("AMP5296", Assert.Throws<AmpleException>(() => LockFile.Parse("version 1\n\nthings\n  x\n")).DiagnosticCode);
        Assert.Equal("AMP5296", Assert.Throws<AmpleException>(() => LockFile.Parse("version 1\n\nassets\n  ast-1 not-json mount - sha256:00 size 1\n")).DiagnosticCode);
        using LibraryRig r = new();
        Assert.Equal("AMP5296", Assert.Throws<AmpleException>(() => LockFile.Verify(r.Folder("empty-project"), r.Lib)).DiagnosticCode);
    }

    [Fact]
    public void Packs_are_validated_and_installed_by_copy_or_by_link()
    {
        using LibraryRig r = new();
        string good = r.Folder("analog");
        LibraryRig.Put(good, "pack.json", "{\"name\":\"analog-808\",\"version\":\"1.4\",\"license\":\"CC0\",\"format\":\"sfz\"}");
        LibraryRig.Put(good, "samples/kick.wav", "kick");
        Assert.Equal(new PackManifest("analog-808", "1.4", "CC0", "sfz"), Ample.Storage.Library.Library.ValidatePack(good));
        foreach ((string json, string why) in new[] { ("{", "json"), ("{\"version\":\"1\"}", "name"), ("{\"name\":\"a b\",\"version\":\"1\"}", "name chars") })
        {
            string bad = r.Folder("bad-" + why.Replace(' ', '-'));
            LibraryRig.Put(bad, "pack.json", json);
            Assert.Equal("AMP5295", Assert.Throws<AmpleException>(() => Ample.Storage.Library.Library.ValidatePack(bad)).DiagnosticCode);
        }

        Assert.Equal("AMP5295", Assert.Throws<AmpleException>(() => Ample.Storage.Library.Library.ValidatePack(r.Folder("none"))).DiagnosticCode);
        Mount copied = r.Lib.InstallPack(good, link: false);
        Assert.StartsWith(Path.Combine(r.Lib.Home, "packs"), copied.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(copied.Path, "samples", "kick.wav")));
        Assert.Equal("AMP5295", Assert.Throws<AmpleException>(() => r.Lib.InstallPack(good, link: false)).DiagnosticCode);
        Assert.Contains(r.Lib.Assets(copied.Id), a => a.Kind == AssetKind.Pack && a.Name == "analog-808@1.4");

        string second = r.Folder("felt");
        LibraryRig.Put(second, "pack.json", "{\"name\":\"felt\",\"version\":\"2\"}");
        Mount linked = r.Lib.InstallPack(second, link: true);
        Assert.Equal(second, linked.Path);
        Assert.True(linked.ReadOnly);
    }

    [Fact]
    public async Task Importing_copies_the_file_into_the_project_with_its_provenance_and_never_over_an_existing_one()
    {
        using LibraryRig r = new();
        string dir = r.Folder("lib");
        LibraryRig.Put(dir, "kit/tape.wav", "tape kit");
        Mount m = r.Lib.LinkMount("Lib", dir);
        r.Lib.Scan(m.Id);
        string project = r.Folder("project");
        string uuid = r.Lib.Assets(m.Id).Single().Uuid;
        string rel = await r.Lib.ImportAsync(uuid, project);
        Assert.Equal("assets/kit/tape.wav", rel);
        Assert.Equal("tape kit", File.ReadAllText(Path.Combine(project, rel)));
        string provenance = File.ReadAllText(Path.Combine(project, rel + ".provenance.json"));
        Assert.Contains(uuid, provenance, StringComparison.Ordinal);
        Assert.Contains(m.Uuid, provenance, StringComparison.Ordinal);
        Assert.Equal("AMP5201", (await Assert.ThrowsAsync<AmpleException>(async () => await r.Lib.ImportAsync(uuid, project))).DiagnosticCode);
        Assert.Equal("AMP5291", (await Assert.ThrowsAsync<AmpleException>(async () => await r.Lib.ImportAsync("ast-none", project))).DiagnosticCode);
        Assert.Equal("tape kit", File.ReadAllText(Path.Combine(dir, "kit/tape.wav")));
    }

    [Fact]
    public void The_usage_index_finds_the_blocks_and_parameters_that_name_an_asset()
    {
        using LibraryRig r = new();
        string dir = r.Folder("lib");
        LibraryRig.Put(dir, "kit/tape.wav", "tape");
        LibraryRig.Put(dir, "felt/pack.json", "{\"name\":\"felt-piano\",\"version\":\"2.1\"}");
        Mount m = r.Lib.LinkMount("Lib", dir);
        r.Lib.Scan(m.Id);
        AmpleDocument song = Song("composition \"d\" { ample 1  tempo 92  master-seed 1 }\ndrums d {\n  kit kit/tape\n}\ndrums e {\n  kit kit/tape\n}\npiano p pinned@2.0 {\n  set felt-piano/close\n}\n");
        IReadOnlyList<AssetUse> uses = r.Lib.Uses(song);
        Assert.Equal(["d.kit", "e.kit", "p.set"], uses.Select(u => $"{u.Block}.{u.Parameter}").Order());
    }

    [Fact]
    public async Task Unlinking_is_a_dangerous_act_that_touches_no_file()
    {
        using HistoryRig h = await HistoryRig.CreateAsync(("song.amplet", "x"));
        using LibraryRig r = new();
        string dir = r.Folder("lib");
        string file = LibraryRig.Put(dir, "kick.wav", "kick");
        Mount m = r.Lib.LinkMount("Kits", dir);
        r.Lib.Scan(m.Id);
        UnlinkMountAct act = new(r.Lib, m.Id);
        DryRunResult dry = await act.DryRunAsync(h.Context, CancellationToken.None);
        Assert.Contains("1 asset", dry.Impact[0], StringComparison.Ordinal);
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.ExecuteAsync(h.Journal, h.Context, "user", "wrong", "x"))).DiagnosticCode);
        Assert.Single(r.Lib.Mounts());
        await act.ExecuteAsync(h.Journal, h.Context, "user", act.Acceptance, "the drive is gone");
        Assert.Empty(r.Lib.Mounts());
        Assert.Empty(r.Lib.Assets());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_project_link_is_only_visible_to_its_project()
    {
        using LibraryRig r = new();
        r.Lib.LinkMount("Everyone", r.Folder("a"));
        r.Lib.LinkMount("Only one", r.Folder("b"), "project", "proj-1");
        Assert.Single(r.Lib.Mounts());
        Assert.Equal(2, r.Lib.Mounts("proj-1").Count);
        Assert.Single(r.Lib.Mounts("proj-2"));
    }

    [Fact]
    public async Task Scanning_many_files_is_incremental_and_the_numbers_are_recorded()
    {
        // 5,000 files by default; AMPLE_PERF=1 makes it the 50,000 of the plan (making them is slow on Windows: about 26 s).
        int count = Environment.GetEnvironmentVariable("AMPLE_PERF") == "1" ? 50_000 : 5_000;
        using LibraryRig r = new();
        string dir = r.Folder("big");
        Stopwatch make = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, ct) =>
        {
            string sub = Path.Combine(dir, (i % 100).ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(sub);
            await File.WriteAllTextAsync(Path.Combine(sub, $"s{i}.wav"), "sample " + i, ct);
        });
        make.Stop();
        Mount m = r.Lib.LinkMount("Big", dir);
        ScanReport first = r.Lib.Scan(m.Id);
        ScanReport second = r.Lib.Scan(m.Id);
        File.WriteAllText(Path.Combine(dir, "000", "s0.wav"), "changed sample");
        ScanReport third = r.Lib.Scan(m.Id);
        Console.WriteLine($"library scan of {count} files (files made in {make.ElapsedMilliseconds} ms): first {first.Milliseconds} ms ({first.Hashed} hashed), unchanged {second.Milliseconds} ms ({second.Hashed} hashed), one changed {third.Milliseconds} ms ({third.Hashed} hashed)");
        Assert.Equal(count, first.Added);
        Assert.Equal((0, count), (second.Hashed, second.Unchanged));
        Assert.Equal((1, 1), (third.Changed, third.Hashed));
        Assert.True(second.Milliseconds < first.Milliseconds, "the second scan must be cheaper");
    }
}
