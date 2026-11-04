using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Ample.Server.Tests;

/// <summary>A task with scope file only stages its main file; the assistant must still be able to copy a linked sequence (to give one song its own timbres) from the project.</summary>
public sealed class AiCopyUnstagedFileTests : IDisposable
{
    private readonly string live = Path.Combine(Path.GetTempPath(), "ample-ai-copy-" + Guid.NewGuid().ToString("N"));
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "ample-ai-copy-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(live, true); Directory.Delete(workspace, true); }
        catch (IOException) { }
    }

    private AiTask NewTask()
    {
        AiTask task = new() { Request = new AiTaskRequest("comment", "file", "swap the violins", "song.amplet", "song.amplet", null, null, null, null), Workspace = workspace };
        Directory.CreateDirectory(task.ProjectDir);
        FieldInfo tokens = typeof(AiService).GetField("ContextTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((ConcurrentDictionary<string, AiTask>)tokens.GetValue(null)!)[task.ContextToken] = task;
        return task;
    }

    private static JsonElement Body(string source, string destination) => JsonSerializer.SerializeToElement(new { source, destination });

    [Fact]
    public void A_linked_file_that_was_not_staged_is_copied_from_the_project()
    {
        Directory.CreateDirectory(Path.Combine(live, "sequences"));
        File.WriteAllText(Path.Combine(live, "sequences", "strings.ampleseq"), "program 40\n");
        AiTask task = NewTask();

        AiService.CopyFile(task.ContextToken, Body("sequences/strings.ampleseq", "sequences/strings-trumpet.ampleseq"), live);

        Assert.Equal("program 40\n", File.ReadAllText(Path.Combine(task.ProjectDir, "sequences", "strings-trumpet.ampleseq")));
        Assert.True(File.Exists(Path.Combine(live, "sequences", "strings.ampleseq")));
    }

    [Fact]
    public void Without_a_live_root_an_unstaged_source_is_still_refused()
    {
        AiTask task = NewTask();
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => AiService.CopyFile(task.ContextToken, Body("sequences/none.ampleseq", "sequences/copy.ampleseq")));
    }
}
