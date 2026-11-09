namespace Ample.Server.Tests;

public sealed class AiReadCacheTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-stamp-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void The_state_of_the_working_folder_changes_when_a_file_changes_or_is_added_so_a_read_after_a_change_is_fetched_again()
    {
        AiTask task = new() { Workspace = folder };
        Directory.CreateDirectory(task.ProjectDir);
        string song = Path.Combine(task.ProjectDir, "song.amplet");
        File.WriteAllText(song, "project demo { tempo 100 }\n");
        string first = AiService.WorkspaceStamp(task);
        Assert.Equal(first, AiService.WorkspaceStamp(task));   // nothing changed: the same state, so the same read is answered shortly

        File.WriteAllText(song, "project demo { tempo 100 }\n# a comment\n");
        string edited = AiService.WorkspaceStamp(task);
        Assert.NotEqual(first, edited);                          // an edit: the next read is fetched again, with the change

        File.WriteAllText(Path.Combine(task.ProjectDir, "extra.seq"), "channel kick drum\n");
        Assert.NotEqual(edited, AiService.WorkspaceStamp(task)); // a new file counts too
    }

    [Fact]
    public void A_new_turn_forgets_the_earlier_reads_so_the_model_gets_the_whole_answer_again()
    {
        AiTask task = new() { Workspace = folder };
        task.ReadCache["song"] = ("stamp", 1);   // an earlier turn read the song
        AiService.BeginTurnReads(task);
        Assert.Empty(task.ReadCache);            // the first read of the new turn is answered in full, not with 'unchanged'
    }
}
