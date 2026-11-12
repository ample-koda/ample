using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Engine.Library;
using Ample.Language;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

// Plan bundles BU01 to BU03: saving blocks as a bundle, placing it under new names and handing the saved audio to the build.
[Trait("Category", "Bundles")]
public sealed class BundleTests
{
    private const string Seq = "sequencer 1\nchannel kick drum sample=samples/kick.wav\npattern p bars=1\n  kick x...x...x...x...\nplaylist\n  clip p track=1 at=1 for=2\n";
    private const string SongText = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\nsequencer groove at 1 for 2 {\n  file sequences/a.seq\n}\n";

    private static async Task<EngineRig> RigAsync()
    {
        EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        short[] hit = new short[300];
        hit[0] = 30000;
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(hit, 44100));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Seq);
        return rig;
    }

    private static List<(string Role, byte[] Data)>? TakeOf(EngineRig rig, BuildReport report, string block)
    {
        string? id = report.Blocks.FirstOrDefault(b => b.Block == block)?.TakeId;
        if (id is null) return null;
        List<(string Role, byte[] Data)> outputs = [];
        foreach (TakeDataRef data in rig.Bank.GetTake(id).Data)
        {
            using Stream stream = rig.Bank.OpenData(data.DataId);
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            outputs.Add((data.Role, copy.ToArray()));
        }

        return outputs;
    }

    private static string Home() => Directory.CreateTempSubdirectory("ample-bundles-").FullName;

    private static async Task<(EngineRig Rig, string Home, BundleInfo Info, byte[] Audio)> SavedAsync()
    {
        EngineRig rig = await RigAsync();
        AmpleDocument doc = EngineRig.Parse(SongText);
        BuildReport report = await rig.Engine.BuildAsync(doc);
        string home = Home();
        BundleInfo info = Bundles.Save(home, rig.ProjectRoot, null, doc, EngineRig.Spec, ["groove"], "Groove loop", "a kick", _ => 7UL, b => TakeOf(rig, report, b), "2026-10-08T10:00:00Z");
        return (rig, home, info, rig.Master(report));
    }

    [Fact]
    public async Task A_saved_bundle_is_a_folder_with_the_text_the_files_and_the_audio()
    {
        (EngineRig rig, string home, BundleInfo info, _) = await SavedAsync();
        using (rig)
        {
            Assert.Equal("groove-loop", info.Id);
            Assert.Equal(["groove"], info.Blocks);
            Assert.Equal(["samples/kick.wav", "sequences/a.seq"], info.Files.Select(f => f.Path));
            Assert.Contains(info.Slots, s => s.Block == "groove" && s.Param == "file" && s.Value == "sequences/a.seq");
            Assert.Contains(info.Takes, t => t.Role == "mix");
            Assert.True(File.Exists(Path.Combine(home, "bundles", "groove-loop", "files", "samples", "kick.wav")));
            Assert.Contains("sequencer groove", File.ReadAllText(Path.Combine(home, "bundles", "groove-loop", "blocks.amplet")), StringComparison.Ordinal);
            Assert.Equal(["groove-loop"], Bundles.List(home).Select(b => b.Id));
            BundleInfo second = Bundles.Save(home, rig.ProjectRoot, null, EngineRig.Parse(SongText), EngineRig.Spec, ["groove"], "Groove loop", string.Empty, _ => 7UL, _ => null, "2026-10-08T10:01:00Z");
            Assert.Equal("groove-loop-2", second.Id);
            Assert.Empty(second.Takes);   // a block without a take is saved without audio
            Bundles.Delete(home, "groove-loop-2");
            Assert.Single(Bundles.List(home));
        }
    }

    [Fact]
    public async Task A_block_that_is_not_seed_free_keeps_the_seed_it_had()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        AmpleDocument doc = EngineRig.Parse(EngineRig.Hello);
        string home = Home();
        BundleInfo info = Bundles.Save(home, rig.ProjectRoot, null, doc, EngineRig.Spec, ["drums", "chords"], "Beat", string.Empty, _ => 42UL, _ => null, "2026-10-08T10:00:00Z");
        Assert.Equal(42UL, info.Seeds["drums"]);   // the seed is kept next to the text: most blocks take no seed line
        Assert.DoesNotContain("seed", File.ReadAllText(Path.Combine(home, "bundles", info.Id, "blocks.amplet")), StringComparison.Ordinal);
        Assert.Equal(42UL, Bundles.Place(home, info.Id, rig.ProjectRoot, EngineRig.Spec, [], "z-", null, null, false).Seeds["z-drums"]);
        Assert.Equal(["chords", "drums"], info.Blocks.Order());
    }

    [Fact]
    public async Task Placing_under_new_names_copies_the_files_and_the_build_gives_the_block_its_saved_audio()
    {
        (EngineRig source, string home, BundleInfo info, byte[] saved) = await SavedAsync();
        using (source)
        using (EngineRig target = await EngineRig.CreateAsync())
        {
            BundlePlacement placement = Bundles.Place(home, info.Id, target.ProjectRoot, EngineRig.Spec, [], "intro-", null, null, useExisting: false);
            Assert.Equal("intro-groove", placement.Names["groove"]);
            Assert.Contains("sequencer intro-groove", placement.Text, StringComparison.Ordinal);
            Assert.Equal(["samples/kick.wav", "sequences/a.seq"], placement.Files.Select(f => f.Path));
            Assert.Equal(["intro-groove"], placement.Adoptable);
            foreach (BundleFileWrite file in placement.Files)
            {
                string path = Path.Combine(target.ProjectRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Data);
            }

            AmpleDocument doc = EngineRig.Parse("composition \"t\" { ample 1  tempo 120  master-seed 9 }\n" + placement.Text);
            Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
            IReadOnlyDictionary<string, IReadOnlyList<RenderOutput>> audio = Bundles.LoadTakes(home, info.Id, placement.Names);
            BuildReport report = await target.Engine.BuildAsync(doc, new BuildOptions { Adopt = block => audio.GetValueOrDefault(block) });
            Assert.Equal("adopted", report.Blocks.Single(b => b.Block == "intro-groove").Status);
            Assert.Equal(saved.Length, target.Master(report).Length);
            // a build again finds the take in the cache
            Assert.Equal("cached", (await target.Engine.BuildAsync(doc)).Blocks.Single(b => b.Block == "intro-groove").Status);
        }
    }

    [Fact]
    public async Task A_name_the_song_already_has_is_refused_and_the_names_can_be_chosen_one_by_one()
    {
        (EngineRig rig, string home, BundleInfo info, _) = await SavedAsync();
        using (rig)
        {
            Assert.Equal("AMP5392", Assert.Throws<AmpleException>(() => Bundles.Place(home, info.Id, rig.ProjectRoot, EngineRig.Spec, ["groove"], string.Empty, null, null, false)).DiagnosticCode);
            BundlePlacement named = Bundles.Place(home, info.Id, rig.ProjectRoot, EngineRig.Spec, ["groove"], string.Empty, new Dictionary<string, string> { ["groove"] = "drop" }, null, true);
            Assert.Equal("drop", named.Names["groove"]);
            Assert.Equal("AMP5391", Assert.Throws<AmpleException>(() => Bundles.Place(home, "nope", rig.ProjectRoot, EngineRig.Spec, [], string.Empty, null, null, false)).DiagnosticCode);
        }
    }

    [Fact]
    public async Task A_project_file_with_other_content_is_a_conflict_that_the_user_may_accept_but_then_the_audio_is_not_handed_over()
    {
        (EngineRig source, string home, BundleInfo info, _) = await SavedAsync();
        using (source)
        using (EngineRig target = await EngineRig.CreateAsync())
        {
            Directory.CreateDirectory(Path.Combine(target.ProjectRoot, "samples"));
            File.WriteAllBytes(Path.Combine(target.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[100], 44100));
            Assert.Equal("AMP5393", Assert.Throws<AmpleException>(() => Bundles.Place(home, info.Id, target.ProjectRoot, EngineRig.Spec, [], "x-", null, null, false)).DiagnosticCode);
            BundlePlacement kept = Bundles.Place(home, info.Id, target.ProjectRoot, EngineRig.Spec, [], "x-", null, null, true);
            Assert.Equal(["samples/kick.wav"], kept.Conflicts);
            Assert.DoesNotContain(kept.Files, f => f.Path == "samples/kick.wav");   // the project's file stays
            Assert.Contains(kept.Files, f => f.Path == "sequences/a.seq");
        }
    }

    [Fact]
    public async Task A_changed_slot_is_written_into_the_text_and_that_block_renders_instead_of_being_handed_the_audio()
    {
        (EngineRig source, string home, BundleInfo info, _) = await SavedAsync();
        using (source)
        using (EngineRig target = await EngineRig.CreateAsync())
        {
            BundlePlacement placement = Bundles.Place(home, info.Id, target.ProjectRoot, EngineRig.Spec, [], "b-", null, new Dictionary<string, string> { ["groove.file"] = "sequences/other.seq" }, false);
            Assert.Contains("file sequences/other.seq", placement.Text, StringComparison.Ordinal);
            Assert.Empty(placement.Adoptable);
        }
    }

    [Fact]
    public async Task A_block_called_like_its_type_keeps_its_type_when_it_is_renamed_and_inner_connections_follow()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string home = Home();
        BundleInfo info = Bundles.Save(home, rig.ProjectRoot, null, EngineRig.Parse(EngineRig.Hello), EngineRig.Spec, ["drums", "mix"], "Beat", string.Empty, _ => 5UL, _ => null, "2026-10-08T10:00:00Z");
        BundlePlacement placement = Bundles.Place(home, info.Id, rig.ProjectRoot, EngineRig.Spec, [], "x-", null, null, false);
        Assert.Contains("drums x-drums at ", placement.Text, StringComparison.Ordinal);
        Assert.Contains("from x-drums.audio -> audio1", placement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(EngineRig.Parse("composition \"t\" { ample 1  tempo 120  master-seed 9 }\n" + placement.Text).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Connections_from_outside_the_bundle_are_left_out_and_listed()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        AmpleDocument doc = EngineRig.Parse(EngineRig.Hello);
        string home = Home();
        BundleInfo info = Bundles.Save(home, rig.ProjectRoot, null, doc, EngineRig.Spec, ["bass"], "Bass", string.Empty, _ => 1UL, _ => null, "2026-10-08T10:00:00Z");
        Assert.Equal(["bass.notes"], info.OpenInputs);
        Assert.DoesNotContain("from chords", File.ReadAllText(Path.Combine(home, "bundles", info.Id, "blocks.amplet")), StringComparison.Ordinal);
    }
}
