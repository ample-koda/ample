using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

public sealed class WorkingTextsBranchTests
{
    [Fact]
    public async Task After_the_working_files_change_a_clean_working_copy_is_read_from_the_files_again_and_a_draft_stays()
    {
        string? ampleRoot = AppContext.BaseDirectory;
        while (ampleRoot is not null && !File.Exists(Path.Combine(ampleRoot, "global.json"))) ampleRoot = Path.GetDirectoryName(ampleRoot);
        string root = ampleRoot ?? throw new InvalidOperationException("AMPLE root not found.");
        string folder = Path.Combine(Path.GetTempPath(), "ample-texts-branch-" + Guid.NewGuid().ToString("N"));
        string library = folder + "-library";
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(
                new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "texts", null, CancellationToken.None))
            {
                _ = project;
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project texts\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "branch A");
            await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), "saved b");
            await using ProjectSession session = await ProjectSession.OpenAsync(
                new ServerOptions(folder, root, LibraryHome: library), new EventBus(), CancellationToken.None);

            (string opened, _, _, _) = session.Texts.Get("a.txt");
            Assert.Equal("branch A", opened);
            session.Texts.Set("b.txt", "my draft");

            // the working files change to another line
            await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "branch B");
            session.ForgetDocs();

            (string reread, _, _, bool aDirty) = session.Texts.Get("a.txt");
            Assert.Equal("branch B", reread);
            Assert.False(aDirty);
            (string draft, _, _, bool bDirty) = session.Texts.Get("b.txt");
            Assert.Equal("my draft", draft);
            Assert.True(bDirty);
        }
        finally
        {
            try { Directory.Delete(folder, true); Directory.Delete(library, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_file_changed_by_another_program_updates_a_clean_copy_and_is_never_overwritten_by_a_draft()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root);
        string folder = Path.Combine(Path.GetTempPath(), "ample-texts-disk-" + Guid.NewGuid().ToString("N"));
        string library = folder + "-library";
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(
                new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "texts", null, CancellationToken.None))
            {
                _ = project;
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project texts\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "one\ntwo\nthree\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), "one\ntwo\nthree\n");
            await using ProjectSession session = await ProjectSession.OpenAsync(
                new ServerOptions(folder, root!, LibraryHome: library), new EventBus(), CancellationToken.None);

            Assert.Equal("one\ntwo\nthree\n", session.Texts.Get("a.txt").Text);
            await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "one\ntwo\nthree\nfour\n");   // saved from Visual Studio
            (string refreshed, _, _, bool dirty) = session.Texts.Get("a.txt");
            Assert.Equal("one\ntwo\nthree\nfour\n", refreshed);
            Assert.False(dirty);

            // a draft in another place than the change on disk: both survive
            session.Texts.Get("b.txt");
            session.Texts.Set("b.txt", "ONE\ntwo\nthree\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), "one\ntwo\nthree\nTHREE\n");
            await session.Texts.SaveAsync("b.txt", CancellationToken.None);
            Assert.Equal("ONE\ntwo\nthree\nTHREE\n", await File.ReadAllTextAsync(Path.Combine(folder, "b.txt")));

            // a draft in the same place: refused, the file on disk is untouched
            session.Texts.Set("b.txt", "mine\ntwo\nthree\nTHREE\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), "theirs\ntwo\nthree\nTHREE\n");
            await Assert.ThrowsAsync<AmpleException>(() => session.Texts.SaveAsync("b.txt", CancellationToken.None));
            Assert.Equal("theirs\ntwo\nthree\nTHREE\n", await File.ReadAllTextAsync(Path.Combine(folder, "b.txt")));
        }
        finally
        {
            try { Directory.Delete(folder, true); Directory.Delete(library, true); }
            catch (IOException) { }
        }
    }
}
