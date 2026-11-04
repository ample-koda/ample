using Ample.Core.Abstractions;

namespace Ample.Server.Tests;

/// <summary>The notes the assistant keeps for later tasks: project and song level, outside the song, and bounded.</summary>
public sealed class AiMemoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ample-ai-memory-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Day = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public AiMemoryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { }
    }

    [Fact]
    public void Project_and_song_memories_are_kept_in_the_ample_folder_and_not_in_the_song()
    {
        AiMemory.Write(root, "project", "Songs/a.amplet", "Warm and intimate voices, never bright.", null, Day);
        AiMemory.Write(root, "song", "Songs/a.amplet", "The preamble: no lyrics yet.", null, Day);

        System.Text.Json.Nodes.JsonObject project = AiMemory.Read(root, "project", "Songs/a.amplet");
        System.Text.Json.Nodes.JsonObject song = AiMemory.Read(root, "song", "Songs/a.amplet");
        Assert.Contains("2026-10-11: Warm and intimate voices", project["text"]!.GetValue<string>());
        Assert.Contains("The preamble", song["text"]!.GetValue<string>());
        Assert.DoesNotContain("preamble", project["text"]!.GetValue<string>());

        string[] files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))];
        Assert.Equal(["ample/ai/memory/project.md".Replace("ample/", ".ample/"), ".ample/ai/memory/songs/Songs__a.amplet.md"], [.. files.Order(StringComparer.Ordinal)]);
        Assert.DoesNotContain(files, f => f.EndsWith(".amplet", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_song_has_its_own_memory_and_append_keeps_earlier_notes()
    {
        AiMemory.Write(root, "song", "a.amplet", "first", null, Day);
        AiMemory.Write(root, "song", "a.amplet", "second", "append", Day);
        AiMemory.Write(root, "song", "b.amplet", "other", null, Day);
        string a = AiMemory.Read(root, "song", "a.amplet")["text"]!.GetValue<string>();
        Assert.Contains("first", a);
        Assert.Contains("second", a);
        Assert.DoesNotContain("other", a);
    }

    [Fact]
    public void Replace_rewrites_and_empty_text_clears_the_memory()
    {
        AiMemory.Write(root, "project", "a.amplet", "one", null, Day);
        AiMemory.Write(root, "project", "a.amplet", "# Tidy\nonly this", "replace", Day);
        Assert.Equal("# Tidy\nonly this\n", AiMemory.Read(root, "project", "a.amplet")["text"]!.GetValue<string>());
        AiMemory.Write(root, "project", "a.amplet", string.Empty, "replace", Day);
        Assert.False(AiMemory.Read(root, "project", "a.amplet")["exists"]!.GetValue<bool>());
    }

    [Fact]
    public void Bad_scope_missing_song_and_too_much_text_are_refused()
    {
        Assert.Throws<AmpleException>(() => AiMemory.Read(root, "universe", "a.amplet"));
        Assert.Throws<AmpleException>(() => AiMemory.Read(root, "song", string.Empty));
        Assert.Throws<AmpleException>(() => AiMemory.Write(root, "project", "a.amplet", new string('x', AiMemory.MaxWrite + 1), null, Day));
        Assert.Throws<AmpleException>(() => AiMemory.Write(root, "project", "a.amplet", "x", "delete", Day));
        for (int i = 0; i < 4; i++) AiMemory.Write(root, "project", "a.amplet", new string('y', AiMemory.MaxWrite - 100), null, Day);
        AmpleException full = Assert.Throws<AmpleException>(() => AiMemory.Write(root, "project", "a.amplet", new string('z', AiMemory.MaxWrite - 100), null, Day));
        Assert.Contains("mode=replace", full.Message);
    }
}
