using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

public sealed class AiFileWindowTests
{
    [Fact]
    public async Task File_windows_a_long_text_file_instead_of_silently_cutting_it()
    {
        string? ampleRoot = AppContext.BaseDirectory;
        while (ampleRoot is not null && !File.Exists(Path.Combine(ampleRoot, "global.json"))) ampleRoot = Path.GetDirectoryName(ampleRoot);
        string root = ampleRoot ?? throw new InvalidOperationException("AMPLE root not found.");
        string folder = Path.Combine(Path.GetTempPath(), "ample-ai-filewindow-" + Guid.NewGuid().ToString("N"));
        string library = folder + "-library";
        string long_ = string.Concat(Enumerable.Range(0, 90000).Select(i => (char)('a' + i % 26)));
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "filewindow", null, CancellationToken.None)) { }
            await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project filewindow\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), long_);
            await using ProjectSession session = await ProjectSession.OpenAsync(new ServerOptions(folder, root, LibraryHome: library), new EventBus(), CancellationToken.None);

            var first = AiContext.File_(session, "notes.txt");
            Assert.True(first["truncated"]!.GetValue<bool>());
            Assert.Equal(90000, first["totalLength"]!.GetValue<int>());
            Assert.Equal(0, first["offset"]!.GetValue<int>());
            string firstText = first["text"]!.GetValue<string>();
            Assert.Equal(80000, firstText.Length);
            Assert.DoesNotContain("(cut)", firstText, StringComparison.Ordinal);

            var second = AiContext.File_(session, "notes.txt", offset: 80000);
            string secondText = second["text"]!.GetValue<string>();
            Assert.Equal(10000, secondText.Length);
            Assert.Equal(long_, firstText + secondText);
            Assert.True(second.ContainsKey("truncated"));
            Assert.False(second.ContainsKey("note") && second["note"]!.GetValue<string>().Contains("read on", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(folder, true); Directory.Delete(library, true); }
            catch (IOException) { }
        }
    }
}
