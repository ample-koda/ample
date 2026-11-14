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
}
