using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The assistant can write notes as a formula and prove that nothing changed: the comparison of two clips or channels and the finder of the rhythm that repeats.</summary>
public sealed class AiNotesTests : IAsyncLifetime
{
    private string folder = string.Empty;
    private string library = string.Empty;
    private ProjectSession session = null!;

    private static string Bass(int bars)
    {
        int[] pitches = [38, 43, 45, 38];
        List<string> entries = [];
        for (int bar = 1; bar <= bars; bar++)
        {
            foreach ((string beat, string length, int add) in new[] { ("0", "0.5", 0), ("0.75", "0.25", 12), ("1.5", "0.5", 0), ("3", "0.5", 12) })
                entries.Add(string.Create(CultureInfo.InvariantCulture, $"{bar}:{beat}:{length}:{pitches[(bar - 1) % 4] + add}:{(bar % 3 == 0 ? 90 : 100)}"));
        }

        return string.Join(' ', entries);
    }

    public async Task InitializeAsync()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root);
        folder = Path.Combine(Path.GetTempPath(), "ample-ai-notes-" + Guid.NewGuid().ToString("N"));
        library = folder + "-library";
        using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "notes", null, CancellationToken.None)) { }
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project notes\n");
        session = await ProjectSession.OpenAsync(new ServerOptions(folder, root ?? throw new InvalidOperationException("AMPLE root not found."), LibraryHome: library), new EventBus(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await session.DisposeAsync();
        foreach (string path in new[] { folder, library })
        {
            try { Directory.Delete(path, true); } catch (IOException) { }
        }
    }

    private AiTask NewTask(string song)
    {
        AiTask task = new() { Request = new AiTaskRequest("custom", "project", "x", "song.amplet", "song.amplet", null, null, null, null), Mode = "manual", Workspace = Path.Combine(folder, ".ample", "ai", "work", "t" + Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(task.ProjectDir);
        File.WriteAllText(Path.Combine(task.ProjectDir, "song.amplet"), song);
        FieldInfo tokens = typeof(AiService).GetField("ContextTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((ConcurrentDictionary<string, AiTask>)tokens.GetValue(null)!)[task.ContextToken] = task;
        return task;
    }

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void A_formula_that_says_the_same_as_a_clip_is_found_the_same_and_one_that_does_not_is_not()
    {
        string song = $"composition \"x\" {{ ample 1  tempo 100  master-seed 1 }}\n\nmidi-clip bass at 1 for 16 {{\n  bars 16\n  notes {Bass(16)}\n}}\n";
        AiTask task = NewTask(song);
        string rhythm = "1:0:0.5:38:100 1:0.75:0.25:50:100 1:1.5:0.5:38:100 1:3:0.5:50:100";
        JsonObject bad = session.Ai.ResolveNotes(task.ContextToken, Body(new { block = "bass", compare_expression = rhythm + " |> bars(1..=1) |> repeat(16)" }));
        Assert.False(bad["compare"]!["same"]!.GetValue<bool>());
        Assert.True(bad["compare"]!["changed"]!.GetValue<int>() + bad["compare"]!["missingInB"]!.GetValue<int>() > 0);
        Assert.Equal(64, bad["a"]!["notes"]!.GetValue<int>());
    }

    [Fact]
    public void The_finder_gives_a_verified_shorter_formula_for_a_clip_and_that_formula_compares_the_same()
    {
        string song = $"composition \"x\" {{ ample 1  tempo 100  master-seed 1 }}\n\nmidi-clip bass at 1 for 32 {{\n  bars 32\n  notes {Bass(32)}\n}}\n";
        AiTask task = NewTask(song);
        JsonObject found = session.Ai.FindPatterns(task.ContextToken, Body(new { block = "bass" }));
        JsonObject best = found["candidates"]!.AsArray()[0]!.AsObject();
        Assert.True(best["verified"]!.GetValue<bool>());
        Assert.True(best["savedPercent"]!.GetValue<int>() >= 50, best.ToJsonString());
        JsonObject proof = session.Ai.ResolveNotes(task.ContextToken, Body(new { block = "bass", compare_expression = best["expression"]!.GetValue<string>() }));
        Assert.True(proof["compare"]!["same"]!.GetValue<bool>(), proof.ToJsonString());
    }

    [Fact]
    public void A_sequencer_channel_is_read_over_its_playlist_and_a_formula_row_is_the_same_as_the_plain_row()
    {
        string seq = "sequencer 1\nchannel bass gm file=pack:musescore-general program=32 bank=0 vol=0 pan=0\npattern a bars=2\n  bass 1:0:0.5:38:100 1:2:0.5:43:90 2:0:0.5:45:100 2:2:0.5:38:90\npattern b bars=2\n  bass = 1:0:0.5:60:100 1:2:0.5:62:90 |> bars(1..=1) |> repeat(2) |> set(pitch=\"38 43 45 38\", velocity=\"100 90\")\nplaylist\n  clip a track=1 at=1 for=2\n  clip b track=1 at=3 for=2\n";
        AiTask task = NewTask("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nsequencer sq at 1 for 4 {\n  sequence {\n" + string.Join('\n', seq.Split('\n').Select(l => "    " + l)) + "\n  }\n}\n");
        JsonObject one = session.Ai.ResolveNotes(task.ContextToken, Body(new { block = "sq", channel = "bass" }));
        Assert.Equal(8, one["a"]!["notes"]!.GetValue<int>());
        Assert.Equal(4, one["a"]!["bars"]!.GetValue<int>());
    }

    [Fact]
    public void An_expression_can_call_a_helper_of_the_song_and_is_proved_the_same_as_the_pipeline_it_stands_for()
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nhelper canonVoice(Notes source, Interval by, Duration delay) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}\n\nmidi-clip theme at 1 for 4 {\n  bars 4\n  notes 1:0:1:60:100 1:1:1:62:100 2:0:2:64:100\n}\n";
        AiTask task = NewTask(song);
        JsonObject proof = session.Ai.ResolveNotes(task.ContextToken, Body(new { expression = "theme.notes |> canonVoice(+7, 2bars)", compare_expression = "theme.notes |> transpose(+7) |> shift(2bars)" }));
        Assert.True(proof["compare"]!["same"]!.GetValue<bool>(), proof.ToJsonString());
        Assert.Equal(3, proof["a"]!["notes"]!.GetValue<int>());
    }

    private List<string> ProblemsOfProposal(string row)
    {
        string seq = "sequencer 1\nchannel bass gm file=pack:musescore-general program=32 bank=0 vol=0 pan=0\npattern a bars=2\n  bass " + row + "\nplaylist\n  clip a track=1 at=1 for=2\n";
        AiTask task = NewTask("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nsequencer sq at 1 for 2 {\n  sequence {\n" + string.Join('\n', seq.Split('\n').Select(l => "    " + l)) + "\n  }\n}\n");
        MethodInfo check = typeof(AiService).GetMethod("NewProblems", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (List<string>)check.Invoke(session.Ai, [task, "song.amplet"])!;
    }

    [Fact]
    public void The_check_of_a_proposal_reads_formula_rows_like_the_sequencer_does_and_still_refuses_a_broken_one()
    {
        // regression: the check of a proposal parsed every row as plain notes, so a good "= formula" row was reported as AMP5380 while the build and the renderer accepted it
        Assert.DoesNotContain(ProblemsOfProposal("= 1:0:0.5:60:100 1:2:0.5:62:90 |> bars(1..=1) |> repeat(2) |> set(pitch=\"38 43\")"), p => p.Contains("AMP5380"));
        Assert.Contains(ProblemsOfProposal("= 1:0:0.5:60:100 |> nothingLikeIt()"), p => p.Contains("AMP5380") && p.Contains("formula"));
        Assert.Contains(ProblemsOfProposal("1:9:0.5:60:100"), p => p.Contains("AMP5380"));
    }
}
