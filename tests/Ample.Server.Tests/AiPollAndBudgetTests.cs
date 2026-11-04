using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Ample.Server.Tests;

/// <summary>The task log is polled every second: it must stay light (no input and output of tool calls, only what is new), and a measuring call must never hold a tool call for longer than its budget.</summary>
public sealed class AiPollAndBudgetTests
{
    private static AiTask NewTask()
    {
        AiTask task = new() { Request = new AiTaskRequest("comment", "file", "x", "song.amplet", "song.amplet", null, null, null, null), Mode = "manual" };
        FieldInfo tokens = typeof(AiService).GetField("ContextTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((ConcurrentDictionary<string, AiTask>)tokens.GetValue(null)!)[task.ContextToken] = task;
        return task;
    }

    [Fact]
    public void Codex_receives_model_specific_ace_baselines_alongside_seed_requirements()
    {
        JsonObject baseline = AiContext.AceBaseline();
        Assert.Equal(7, baseline["sft"]!["guidance"]!.GetValue<int>());
        Assert.Equal(7, baseline["base"]!["guidance"]!.GetValue<int>());
        Assert.Equal(1, baseline["turbo"]!["guidance"]!.GetValue<int>());
        Assert.Contains("lm_seed", baseline["note"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_polled_log_leaves_out_tool_details_and_can_ask_for_what_is_new()
    {
        AiTask task = NewTask();
        task.Add("info", "first");
        task.AddOperation("tool", "ample_song: completed", new JsonObject { ["arguments"] = new JsonObject { ["path"] = "song.amplet" }, ["result"] = new string('x', 100_000) });
        task.Add("info", "third");

        JsonObject light = AiService.TaskJson(task, false);
        JsonArray log = light["log"]!.AsArray();
        Assert.Equal(3, log.Count);
        Assert.True(log[1]!["hasDetails"]!.GetValue<bool>());
        Assert.Null(log[1]!["details"]);
        Assert.True(light.ToJsonString().Length < 5_000);

        long second = log[1]!["seq"]!.GetValue<long>();
        JsonObject newer = AiService.TaskJson(task, false, second);
        Assert.Equal(["third"], newer["log"]!.AsArray().Select(x => x!["text"]!.GetValue<string>()).ToArray());
        Assert.Equal(task.LogSeq, newer["logSeq"]!.GetValue<long>());

        JsonObject full = AiService.TaskJson(task, false, null, details: true);
        Assert.NotNull(full["log"]![1]!["details"]);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public void A_stopped_task_with_a_saved_thread_can_be_resumed(string status)
    {
        AiTask task = NewTask();
        task.Status = status;
        task.Workspace = Directory.CreateTempSubdirectory("ample-resume-").FullName;
        try
        {
            Assert.False(AiService.TaskJson(task, false)["resumable"]!.GetValue<bool>());
            task.ThreadId = "saved-thread";
            Assert.True(AiService.TaskJson(task, false)["resumable"]!.GetValue<bool>());
        }
        finally { Directory.Delete(task.Workspace, recursive: true); }
    }

    [Fact]
    public async Task Completed_tool_activity_records_elapsed_time_in_text_and_details()
    {
        AiTask task = NewTask();
        MethodInfo notify = typeof(AiService).GetMethod("OnNotification", BindingFlags.NonPublic | BindingFlags.Static)!;
        void Send(string method, string json) => notify.Invoke(null, [task, method, JsonNode.Parse(json)!.AsObject()]);

        Send("item/started", """{"item":{"id":"tool-1","type":"mcpToolCall","tool":"ample_build"}}""");
        await Task.Delay(20);
        Send("item/completed", """{"item":{"id":"tool-1","type":"mcpToolCall","tool":"ample_build","status":"completed","arguments":{"block":"mix"}}}""");

        JsonObject entry = AiService.TaskJson(task, false, null, details: true)["log"]!.AsArray().Last()!.AsObject();
        Assert.Contains("ample_build", entry["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("ms", entry["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(entry["details"]!["ampleElapsedMs"]!.GetValue<long>() >= 1);
    }

    [Fact]
    public void A_single_new_song_is_the_preview_target_but_ambiguous_additions_keep_the_task_song()
    {
        AiTask task = NewTask();
        task.Changes.Add(new AiChange { Path = "song.amplet", Status = "modified" });
        task.Changes.Add(new AiChange { Path = "first-bell.amplet", Status = "added" });
        Assert.Equal("first-bell.amplet", AiService.TaskJson(task, false)["build"]!["song"]!.GetValue<string>());

        task.Changes.Add(new AiChange { Path = "second-bell.amplet", Status = "added" });
        Assert.Equal("song.amplet", AiService.TaskJson(task, false)["build"]!["song"]!.GetValue<string>());
    }

    [Fact]
    public void The_last_rendered_proposal_song_wins_when_a_task_adds_multiple_songs()
    {
        AiTask task = NewTask();
        task.Workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(task.ProjectDir);
        File.WriteAllText(Path.Combine(task.ProjectDir, "second-bell.amplet"), "project \"test\" {}\n");
        task.Changes.Add(new AiChange { Path = "first-bell.amplet", Status = "added" });
        task.Changes.Add(new AiChange { Path = "second-bell.amplet", Status = "added" });
        task.OutputSong = "second-bell.amplet";

        Assert.Equal("second-bell.amplet", AiService.TaskJson(task, false)["build"]!["song"]!.GetValue<string>());
    }

    [Fact]
    public void Task_instructions_keep_selected_blocks_as_a_hint_and_allow_needed_scope_expansion()
    {
        AiTask task = new() { Request = new AiTaskRequest("custom", "block", "Make mastering changes", "song.amplet", "song.amplet", null, null, null, null, Blocks: ["master", "limiter"]) };
        string text = (string)typeof(AiService).GetMethod("TaskText", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [task, new Dictionary<string, string>()])!;

        Assert.Contains("initial block context: master, limiter", text, StringComparison.Ordinal);
        Assert.Contains("expand to connected blocks or broader project structure", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_instructions_treat_flemish_sound_as_an_optional_iterative_direction()
    {
        string instructions = (string)typeof(AiService).GetMethod("Instructions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [NewTask()])!;

        Assert.Contains("not one fixed genre or a caricature", instructions, StringComparison.Ordinal);
        Assert.Contains("genuinely distinct short variants", instructions, StringComparison.Ordinal);
        Assert.Contains("optional multi-step workflow", instructions, StringComparison.Ordinal);
        Assert.Contains("Do not add rounds to a narrow request", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Obsolete_sample_reason_is_part_of_the_task_view()
    {
        AiTask task = NewTask();
        task.Clips.Add(new AiClip { Id = "clip-test", Label = "Old master", Block = "master", File = "clip.wav", ObsoleteReason = "The limiter changed after this was rendered." });

        JsonObject clip = AiService.TaskJson(task, false)["clips"]!.AsArray().Single()!.AsObject();
        Assert.Equal("The limiter changed after this was rendered.", clip["obsoleteReason"]!.GetValue<string>());
    }

    [Fact]
    public void Invalidating_a_proposal_clears_its_previous_build_audio_and_block_results()
    {
        AiTask task = NewTask();
        task.AudioFile = "old.wav";
        task.BuildBlocks["mix"] = ("rendered", true);
        task.BuildTakes["mix"] = "old-take";
        task.BuildFailures.Add("old failure");
        task.BuildDetails.Add(new JsonObject { ["message"] = "old detail" });

        typeof(AiService).GetMethod("ClearBuildOutput", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [task]);

        Assert.Null(task.AudioFile);
        Assert.Empty(task.BuildBlocks);
        Assert.Empty(task.BuildTakes);
        Assert.Empty(task.BuildFailures);
        Assert.Empty(task.BuildDetails);
    }

    [Theory]
    [InlineData(null, "Songs/main.amplet")]
    [InlineData("", "Songs/main.amplet")]
    [InlineData("Songs/new.amplet", "Songs/new.amplet")]
    [InlineData("project/Songs/new.amplet", "Songs/new.amplet")]
    [InlineData("Songs/notes.txt", "Songs/main.amplet")]
    public void The_measuring_tools_work_on_the_song_that_path_names_and_else_on_the_song_of_the_task(string? path, string expected)
    {
        AiTask task = new() { Request = new AiTaskRequest("custom", "project", "x", "Songs/main.amplet", "Songs/main.amplet", null, null, null, null), Mode = "manual" };
        MethodInfo method = typeof(AiService).GetMethod("SongOf", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (string)method.Invoke(null, [task, path])!);
    }

    [Fact]
    public async Task A_measuring_call_that_takes_too_long_answers_pending_and_hands_over_its_result_later()
    {
        AiTask task = NewTask();
        TimeSpan was = AiService.ToolBudget;
        AiService.ToolBudget = TimeSpan.FromMilliseconds(150);
        try
        {
            MethodInfo method = typeof(AiService).GetMethod("WithinToolBudgetAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            int runs = 0;
            Func<CancellationToken, Task<JsonObject>> work = async stop =>
            {
                Interlocked.Increment(ref runs);
                await Task.Delay(600, stop);
                return new JsonObject { ["ok"] = true, ["n"] = runs };
            };
            Task<JsonObject> Call() => (Task<JsonObject>)method.Invoke(null, [task.ContextToken, "k", work, CancellationToken.None])!;

            JsonObject first = await Call();
            Assert.True(first["pending"]!.GetValue<bool>());
            JsonObject joined = await Call();   // still running: joins the same work, and also gives up in time
            Assert.True(joined["pending"]!.GetValue<bool>());
            await Task.Delay(800);
            JsonObject result = await Call();   // finished meanwhile: the answer is handed over, nothing runs again
            Assert.True(result["ok"]!.GetValue<bool>());
            Assert.Equal(1, runs);
            JsonObject again = await Call();    // a new identical call is a new run
            Assert.True(again["pending"]!.GetValue<bool>());
            Assert.Equal(2, runs);
        }
        finally
        {
            AiService.ToolBudget = was;
            task.Cancel.Cancel();
        }
    }
}
