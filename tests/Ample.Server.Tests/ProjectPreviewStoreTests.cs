using System.Text.Json.Nodes;

namespace Ample.Server.Tests;

public sealed class ProjectPreviewStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ample-previews-" + Guid.NewGuid().ToString("N"));

    public ProjectPreviewStoreTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { /* left in the temp folder */ }
    }

    [Fact]
    public async Task OnlyTheLatestStateOfAFileIsKeptAndGoingBackIsWorkedOutAgain()
    {
        ProjectPreviewStore store = ProjectPreviewStore.For(root);
        string full = Path.Combine(root, "tone.wav");
        int made = 0;
        Task<object> Make() { made++; return Task.FromResult<object>(new { made }); }
        async Task<int> Ask() => (int)(await store.GetAsync("tone.wav", full, "peaks", Make))["made"]!;

        File.WriteAllText(full, "state A");
        Assert.Equal(1, await Ask());
        Assert.Equal(1, await Ask());   // the same file: the kept summary

        File.WriteAllText(full, "state B is longer");
        Assert.Equal(2, await Ask());   // changed: replaced
        File.WriteAllText(full, "state A");
        Assert.Equal(3, await Ask());   // back to A: worked out again, nothing older was kept
        Assert.Single(Directory.GetFiles(Path.Combine(root, ".ample", "previews"), "*.json"));
    }

    [Fact]
    public async Task ATouchedFileWithTheSameContentKeepsItsSummary()
    {
        ProjectPreviewStore store = ProjectPreviewStore.For(root);
        string full = Path.Combine(root, "same.wav");
        int made = 0;
        Task<object> Make() { made++; return Task.FromResult<object>(new { made }); }

        File.WriteAllText(full, "the same bytes");
        await store.GetAsync("same.wav", full, "peaks", Make);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(-30));   // touched, not changed
        JsonNode again = await store.GetAsync("same.wav", full, "peaks", Make);
        Assert.Equal(1, made);
        Assert.Equal(1, (int)again["made"]!);
    }

    [Fact]
    public async Task TheEntryOfAFileThatIsGoneIsRemoved()
    {
        ProjectPreviewStore store = ProjectPreviewStore.For(root);
        string full = Path.Combine(root, "gone.wav");
        File.WriteAllText(full, "x");
        await store.GetAsync("gone.wav", full, "peaks", () => Task.FromResult<object>(new { ok = true }));
        string folder = Path.Combine(root, ".ample", "previews");
        Assert.Single(Directory.GetFiles(folder, "*.json"));
        File.Delete(full);
        store.Prune();
        Assert.Empty(Directory.GetFiles(folder, "*.json"));
    }
}
