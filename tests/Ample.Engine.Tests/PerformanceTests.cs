using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.Backup;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Xunit.Abstractions;

namespace Ample.Core.Tests;

/// <summary>
/// The performance gate: every measurement is compared with its budget in <c>docs/perf-budgets.json</c> (a generous multiple of the baseline measured
/// on the development machine, so that a slow CI machine passes and a real regression, often ten times slower, fails). Set <c>AMPLE_PERF_RECORD=1</c> to print the
/// measured numbers in a form that can be pasted into the budgets file.
/// </summary>
/// <summary>The performance tests run alone: while other test classes hammer the same disk the numbers are meaningless (they were twenty times slower in the first full run).</summary>
[CollectionDefinition("Performance", DisableParallelization = true)]
public sealed class PerformanceRunAlone;

[Trait("Category", "Performance")]
[Collection("Performance")]
public sealed class PerformanceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-perf-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static JsonElement Budgets()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "docs", "perf-budgets.json")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("docs/perf-budgets.json");
        }

        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "docs", "perf-budgets.json"))).RootElement.GetProperty("budgetsMilliseconds").Clone();
    }

    private void Gate(string name, Stopwatch sw)
    {
        double budget = Budgets().GetProperty(name).GetDouble();
        output.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms (budget {budget} ms)");
        Assert.True(sw.ElapsedMilliseconds <= budget, $"{name} took {sw.ElapsedMilliseconds} ms, the budget is {budget} ms.");
    }

    [Fact]
    public async Task Auto_denoise_of_sixty_seconds_of_stereo_audio_stays_within_budget()
    {
        const int rate = 44100, seconds = 60;
        short[] left = new short[rate * seconds], right = new short[rate * seconds];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / rate) + 700 * Math.Sin(2 * Math.PI * 1280 * i / rate));
            right[i] = (short)(5200 * Math.Sin(2 * Math.PI * 330 * i / rate) + 500 * Math.Sin(2 * Math.PI * 1750 * i / rate));
        }

        BlockNode block = new() { Type = "denoise", Name = "clean" };
        block.Parameters.Add(new Parameter("noise", "auto", 0));
        byte[] input = StereoWav.Encode(new StereoBuffer(left, right), rate);
        RenderRequest request = new(block, EngineRig.Spec.Templates["denoise"], rate, rate, 1, seconds, new HashSet<int> { 1 }, new Dictionary<string, byte[]> { ["in"] = input }, 1, folder);
        Stopwatch sw = Stopwatch.StartNew();
        IReadOnlyList<RenderOutput> outputs = await ExtraEffects.All.Single(r => r.BlockType == "denoise").RenderAsync(request, CancellationToken.None);
        sw.Stop();
        Assert.Equal(left.Length, StereoWav.Decode(outputs.Single(o => o.Role == "out").Data).Buffer.Left.Length);
        if (Environment.GetEnvironmentVariable("AMPLE_PERF_RECORD") == "1") output.WriteLine($"denoiseAutoStereo60Seconds44kStereo: {sw.ElapsedMilliseconds} ms (record only)");
        else Gate("denoiseAutoStereo60Seconds44kStereo", sw);
    }

    private static string BigSong(int blocks)
    {
        StringBuilder sb = new("composition \"big\" { ample 1  tempo 120  master-seed 1 }\nmidi-clip chords {\n  chords Am F C G\n}\n");
        for (int i = 0; i < blocks; i++)
        {
            sb.Append("bass bass").Append(i).Append(" at 1 for 4 {\n  from chords.notes -> notes\n}\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void Parsing_and_formatting_a_song_of_two_thousand_blocks_stays_within_budget()
    {
        string song = BigSong(2000);
        Stopwatch sw = Stopwatch.StartNew();
        AmpleDocument doc = Parser.Parse(song, EngineRig.Spec);
        sw.Stop();
        Assert.Equal(2001, doc.Blocks.Count);
        Gate("parse2000Blocks", sw);

        sw.Restart();
        string formatted = Formatter.Format(song, EngineRig.Spec);
        sw.Stop();
        Assert.NotEmpty(formatted);
        Gate("format2000Blocks", sw);
    }

    [Fact]
    public async Task Appending_to_the_black_box_and_reading_it_back_stays_within_budget()
    {
        string root = Path.Combine(folder, "bb");
        using OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), root, "bb");
        using Journal journal = await Journal.OpenAsync(new PhysicalFileSystem(), new SystemClock(), root, p.Manifest.Id.ToString("D"));
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            await journal.AppendAsync("user", "file.write", "ok", "c" + i, JsonSerializer.Serialize(new { i }));
        }

        sw.Stop();
        Gate("journalAppend1000", sw);

        sw.Restart();
        Assert.Equal(1000, (await journal.ReadAllAsync()).Count);
        Assert.Null((await journal.VerifyAsync()).BadSeq);
        sw.Stop();
        Gate("journalReadAndVerify1000", sw);
    }

    [Fact]
    public async Task A_big_graph_is_built_and_then_served_from_the_cache_within_budget()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        StringBuilder sb = new("composition \"graph\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 2 {\n  chords Am F\n}\n");
        for (int i = 0; i < 40; i++)
        {
            sb.Append("bass b").Append(i).Append(" at 1 for 2 {\n  from chords.notes -> notes\n}\n");
        }

        AmpleDocument doc = Parser.Parse(sb.ToString(), EngineRig.Spec);
        Stopwatch sw = Stopwatch.StartNew();
        BuildReport first = await rig.Engine.BuildAsync(doc, new BuildOptions(Intent: "preview"));
        sw.Stop();
        Assert.Equal(41, first.Blocks.Count(b => b.Status == "rendered"));
        Gate("buildFortyBlocksPreview", sw);

        sw.Restart();
        BuildReport second = await rig.Engine.BuildAsync(doc, new BuildOptions(Intent: "preview"));
        sw.Stop();
        Assert.Equal(41, second.Blocks.Count(b => b.Status == "cached"));
        Gate("cachedRebuildFortyBlocks", sw);
    }

    [Fact]
    public async Task Listing_thousands_of_takes_and_backing_up_a_project_stay_within_budget()
    {
        string root = Path.Combine(folder, "bank");
        PhysicalFileSystem fs = new();
        using OpenProject p = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "bank");
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        byte[] data = new byte[2048];
        for (int i = 0; i < 1500; i++)
        {
            data[0] = (byte)i;
            data[1] = (byte)(i >> 8);
            PlanRecord plan = new(Guid.NewGuid(), "u" + i, "block" + (i % 50), "h" + i, [], [new PlanSeed("seed", (ulong)i)], 1, "test", "1", "{}", "{}", "bake", null);
            await (await bank.BeginAsync(plan)).CommitAsync([("audio", new MemoryStream(data))]);
        }

        Stopwatch sw = Stopwatch.StartNew();
        Assert.Equal(1500, bank.ListTakes().Count);
        sw.Stop();
        Gate("listTakes1500", sw);

        string wide = Path.Combine(folder, "wide");
        using OpenProject w = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), wide, "wide");
        System.Random r = new(5);
        byte[] blob = new byte[1024 * 1024];
        for (int i = 0; i < 64; i++)
        {
            r.NextBytes(blob);
            Directory.CreateDirectory(Path.Combine(wide, "audio"));
            await File.WriteAllBytesAsync(Path.Combine(wide, "audio", $"f{i}.bin"), blob);
        }

        BackupStore store = new(Path.Combine(folder, "target"), new SystemClock());
        sw.Restart();
        BackupReport report = store.Create(wide, w.Manifest.Id, "wide");
        sw.Stop();
        Assert.True(report.TotalBytes >= 64L * 1024 * 1024);
        Gate("backup64MiB", sw);

        sw.Restart();
        Assert.True(store.Verify(report.Id).Ok);
        sw.Stop();
        Gate("verifyBackup64MiB", sw);

        sw.Restart();
        BackupReport again = store.Create(wide, w.Manifest.Id, "wide");
        sw.Stop();
        Assert.Equal(0, again.NewObjects);
        Gate("secondBackupUnchanged", sw);
    }
}
