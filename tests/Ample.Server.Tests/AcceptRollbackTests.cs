using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

public sealed class AcceptRollbackTests
{
    [Fact]
    public async Task A_failed_accept_puts_back_the_files_that_were_written_and_removes_the_new_ones()
    {
        string? ampleRoot = AppContext.BaseDirectory;
        while (ampleRoot is not null && !File.Exists(Path.Combine(ampleRoot, "global.json"))) ampleRoot = Path.GetDirectoryName(ampleRoot);
        string root = ampleRoot ?? throw new InvalidOperationException("AMPLE root not found.");
        string folder = Path.Combine(Path.GetTempPath(), "ample-ai-rollback-" + Guid.NewGuid().ToString("N"));
        string library = folder + "-library";
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(
                new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "rollback", null, CancellationToken.None))
            {
                _ = project;
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project rollback\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "accepted text");
            await File.WriteAllTextAsync(Path.Combine(folder, "created.txt"), "made by the proposal");
            Directory.CreateDirectory(Path.Combine(folder, "samples"));
            await File.WriteAllTextAsync(Path.Combine(folder, "samples", "kick.wav"), "bytes");
            await using ProjectSession session = await ProjectSession.OpenAsync(
                new ServerOptions(folder, root, LibraryHome: library), new EventBus(), CancellationToken.None);

            AiTask task = new();
            task.Undo["notes.txt"] = "original text";
            task.Undo["created.txt"] = null;
            task.IncludedPaths.Add("samples/kick.wav");

            await session.Ai.RollbackWrittenAsync(task, CancellationToken.None);

            Assert.Equal("original text", await File.ReadAllTextAsync(Path.Combine(folder, "notes.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "created.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "samples", "kick.wav")));
            Assert.Empty(task.Undo);
            Assert.Empty(task.IncludedPaths);
        }
        finally
        {
            try { Directory.Delete(folder, true); Directory.Delete(library, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Rolling_back_with_nothing_written_changes_nothing()
    {
        string? ampleRoot = AppContext.BaseDirectory;
        while (ampleRoot is not null && !File.Exists(Path.Combine(ampleRoot, "global.json"))) ampleRoot = Path.GetDirectoryName(ampleRoot);
        string root = ampleRoot ?? throw new InvalidOperationException("AMPLE root not found.");
        string folder = Path.Combine(Path.GetTempPath(), "ample-ai-rollback-" + Guid.NewGuid().ToString("N"));
        string library = folder + "-library";
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(
                new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "rollback", null, CancellationToken.None))
            {
                _ = project;
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project rollback\n");
            await using ProjectSession session = await ProjectSession.OpenAsync(
                new ServerOptions(folder, root, LibraryHome: library), new EventBus(), CancellationToken.None);

            await session.Ai.RollbackWrittenAsync(new AiTask(), CancellationToken.None);

            Assert.Equal("project rollback\n", await File.ReadAllTextAsync(Path.Combine(folder, "song.amplet")));
        }
        finally
        {
            try { Directory.Delete(folder, true); Directory.Delete(library, true); }
            catch (IOException) { }
        }
    }
}
