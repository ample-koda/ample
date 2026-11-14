using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

public sealed class AiProposalFilesTests
{
    private static string AmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }

    private static async Task<(ProjectSession Session, string Folder)> OpenAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-ai-files-" + Guid.NewGuid().ToString("N"));
        using (OpenProject project = await ProjectStore.CreateAsync(
            new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "files", null, CancellationToken.None))
        {
            _ = project;
        }

        ProjectSession session = await ProjectSession.OpenAsync(
            new ServerOptions(folder, AmpleRoot(), LibraryHome: folder + "-library"), new EventBus(), CancellationToken.None);
        return (session, folder);
    }

    private static AiTask TaskFor(string song) => new()
    {
        Request = new AiTaskRequest("modify-file", "file", "prompt", song, song, null, null, null, null),
        Status = "ready",
    };

    private static void Cleanup(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
            Directory.Delete(folder + "-library", true);
        }
        catch (IOException)
        {
            // a locked file must not fail the run
        }
    }

    private static string SongText() =>
        "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\n\nimport \"fx\"\n\n"
            + $"midi-clip theme at 1 for 1 {{\n  notes 1:0:1:60:100\n}}\n\ndemo.fx.twice twice at 2 for 3 {{\n  from theme.notes -> source\n}}\n";

    private static string ComponentText() =>
        "component demo.fx.twice version 1.0.0 {\n  id \"com.demo.twice\"\n  input source: Notes\n  property copies: Int = 2\n"
            + $"  output notes: Notes\n  process {{\n    for i in 0..copies {{\n      emit source |> shift(i * 1bar)\n    }}\n  }}\n}}\n";

    [Fact]
    public async Task A_song_that_imports_a_component_added_by_the_same_proposal_is_not_reported_as_broken()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                string original = "project files" + "\n";
                await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), original);
                AiTask together = TaskFor("song.amplet");
                together.Baseline["song.amplet"] = original;
                together.Changes.Add(new AiChange { Path = "song.amplet", Before = original, After = SongText() });
                together.Changes.Add(new AiChange { Path = "components/fx.amplec", Status = "added", Before = null, After = ComponentText() });
                AiTask alone = TaskFor("song.amplet");
                alone.Baseline["song.amplet"] = original;
                alone.Changes.Add(new AiChange { Path = "song.amplet", Before = original, After = SongText() });

                await session.Ai.ValidateAsync(together);
                await session.Ai.ValidateAsync(alone);

                Assert.DoesNotContain(together.Diagnostics, d => d.Contains(" error ", StringComparison.Ordinal));
                Assert.Contains(alone.Diagnostics, d => d.Contains(" error ", StringComparison.Ordinal));   // without the file the import is a real problem
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task A_component_file_that_the_proposal_deletes_is_not_read_from_the_project()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                string original = "project files" + "\n";
                Directory.CreateDirectory(Path.Combine(folder, "components"));
                await File.WriteAllTextAsync(Path.Combine(folder, "components", "fx.amplec"), ComponentText());
                AiTask task = TaskFor("song.amplet");
                task.Baseline["song.amplet"] = original;
                task.Changes.Add(new AiChange { Path = "song.amplet", Before = original, After = SongText() });
                task.Changes.Add(new AiChange { Path = "components/fx.amplec", Status = "deleted", Before = ComponentText(), After = null });

                await session.Ai.ValidateAsync(task);

                Assert.Contains(task.Diagnostics, d => d.Contains(" error ", StringComparison.Ordinal));
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task Rebasing_a_proposal_also_updates_the_working_copy_of_the_assistant_so_both_edits_survive_a_follow_up()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                string original = "line one\nline two\nline three\nline four\nline five\n";
                string assistant = "line one\nline two\nline three\nline four\nline five by the assistant\n";
                await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "line one by the user\nline two\nline three\nline four\nline five\n");
                AiTask task = TaskFor("song.amplet");
                task.Workspace = Path.Combine(folder, ".ample", "test-workspace");
                Directory.CreateDirectory(task.ProjectDir);
                await File.WriteAllTextAsync(Path.Combine(task.ProjectDir, "notes.txt"), assistant);
                task.Baseline["notes.txt"] = original;
                task.Changes.Add(new AiChange { Path = "notes.txt", Before = original, After = assistant });
                session.Ai.Adopt(task);

                await session.Ai.RebaseAsync(task.Id);

                string merged = "line one by the user\nline two\nline three\nline four\nline five by the assistant\n";
                Assert.Equal(merged, task.Changes.Single().After);
                Assert.Equal(merged, await File.ReadAllTextAsync(Path.Combine(task.ProjectDir, "notes.txt")));
                Assert.False(task.Stale);
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task A_proposal_cannot_be_rebased_while_the_assistant_is_still_working_on_it()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                AiTask task = TaskFor("song.amplet");
                task.Status = "running";
                session.Ai.Adopt(task);

                AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await session.Ai.RebaseAsync(task.Id));

                Assert.Equal("AMP5458", error.DiagnosticCode);
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task The_unsaved_text_of_a_component_file_is_what_the_proposal_is_compared_with()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                Directory.CreateDirectory(Path.Combine(folder, "components"));
                await File.WriteAllTextAsync(Path.Combine(folder, "components", "fx.amplec"), "saved text");
                session.Texts.Set("components/fx.amplec", "unsaved text");
                AiTask fromWorkingCopy = TaskFor("song.amplet");   // captured from the working copy: nothing changed since
                fromWorkingCopy.Baseline["components/fx.amplec"] = "unsaved text";
                fromWorkingCopy.Changes.Add(new AiChange { Path = "components/fx.amplec", Before = "unsaved text", After = "proposed text" });
                AiTask fromDisk = TaskFor("song.amplet");   // captured from the disk: the person changed it in the editor since
                fromDisk.Baseline["components/fx.amplec"] = "saved text";
                fromDisk.Changes.Add(new AiChange { Path = "components/fx.amplec", Before = "saved text", After = "proposed text" });

                await session.Ai.CheckStaleAsync(fromWorkingCopy);
                await session.Ai.CheckStaleAsync(fromDisk);

                Assert.False(fromWorkingCopy.Stale);
                Assert.True(fromDisk.Stale);
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task Forgetting_a_file_after_a_proposal_wrote_it_drops_its_old_working_copy()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                Directory.CreateDirectory(Path.Combine(folder, "components"));
                await File.WriteAllTextAsync(Path.Combine(folder, "components", "fx.amplec"), "saved text");
                session.Texts.Set("components/fx.amplec", "unsaved text");

                session.ForgetDoc("components/fx.amplec");

                Assert.Null(session.Texts.Read("components/fx.amplec"));
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    private static AiTask SavedTask(string folder, bool applying)
    {
        AssistantSessionStore store = new(folder);
        AssistantSession assistant = store.Create("accept test", archiveConversation: false);
        AiTask saved = new()
        {
            Request = new AiTaskRequest("modify-file", "file", "prompt", "song.amplet", "song.amplet", null, null, null, null),
            Status = "ready",
            SessionId = assistant.Id,
            Applying = applying,
        };
        saved.Undo["notes.txt"] = "original text";
        saved.Undo["created.txt"] = null;
        saved.IncludedPaths.Add("samples/kick.wav");
        store.SaveTask(saved);
        return saved;
    }

    [Fact]
    public async Task A_proposal_that_was_being_applied_when_the_server_stopped_is_put_back_when_it_is_read_again()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                AiTask saved = SavedTask(folder, applying: true);
                await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "half applied text");   // what the interrupted Accept had written
                await File.WriteAllTextAsync(Path.Combine(folder, "created.txt"), "new file");
                Directory.CreateDirectory(Path.Combine(folder, "samples"));
                await File.WriteAllTextAsync(Path.Combine(folder, "samples", "kick.wav"), "bytes");

                AiTask task = session.Ai.GetTask(saved.Id);

                Assert.Equal("original text", await File.ReadAllTextAsync(Path.Combine(folder, "notes.txt")));
                Assert.False(File.Exists(Path.Combine(folder, "created.txt")));
                Assert.False(File.Exists(Path.Combine(folder, "samples", "kick.wav")));
                Assert.False(task.Applying);
                Assert.False(task.Applied);
                Assert.Empty(task.Undo);
                Assert.Empty(task.IncludedPaths);
                Assert.False(Assert.Single(new AssistantSessionStore(folder).SavedTasks(saved.SessionId!)).Applying);
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task A_proposal_that_was_not_being_applied_keeps_what_is_on_disk_when_it_is_read_again()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                AiTask saved = SavedTask(folder, applying: false);
                await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "text of the person");

                _ = session.Ai.GetTask(saved.Id);

                Assert.Equal("text of the person", await File.ReadAllTextAsync(Path.Combine(folder, "notes.txt")));
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }

    [Fact]
    public async Task An_accept_that_fails_half_way_puts_the_files_back_and_is_not_left_applying()
    {
        (ProjectSession session, string folder) = await OpenAsync();
        try
        {
            await using (session)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "original text");
                Directory.CreateDirectory(Path.Combine(folder, "blocked"));   // a folder where the proposal wants to write a file
                AiTask task = TaskFor("song.amplet");
                task.BuildStatus = "skipped";
                task.Baseline["notes.txt"] = "original text";
                task.Changes.Add(new AiChange { Path = "notes.txt", Before = "original text", After = "proposed text" });
                task.Changes.Add(new AiChange { Path = "blocked", Status = "added", Before = null, After = "proposed too" });
                session.Ai.Adopt(task);

                await Assert.ThrowsAnyAsync<Exception>(async () => await session.Ai.AcceptAsync(task.Id, true, null, CancellationToken.None));

                Assert.Equal("original text", await File.ReadAllTextAsync(Path.Combine(folder, "notes.txt")));
                Assert.False(task.Applying);
                Assert.False(task.Applied);
                Assert.Empty(task.Undo);
            }
        }
        finally
        {
            Cleanup(folder);
        }
    }
}
