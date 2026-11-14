using System.Text.Json;

namespace Ample.Server.Tests;

public sealed class AcceptRecoveryTests
{
    [Fact]
    public void Nothing_is_changed_when_every_file_is_as_accept_left_it()
    {
        Dictionary<string, string?> recorded = new() { ["notes.txt"] = "aa", ["samples/kick.wav"] = "bb", ["gone.txt"] = null };
        Dictionary<string, string?> now = new() { ["notes.txt"] = "aa", ["samples/kick.wav"] = "bb", ["gone.txt"] = null };

        Assert.Empty(AcceptRecovery.ChangedSince(recorded, path => now[path]));
    }

    [Fact]
    public void A_file_that_was_edited_after_accept_is_reported_so_undo_does_not_overwrite_it()
    {
        Dictionary<string, string?> recorded = new() { ["notes.txt"] = "aa", ["other.txt"] = "cc" };
        Dictionary<string, string?> now = new() { ["notes.txt"] = "edited later", ["other.txt"] = "cc" };

        Assert.Equal(["notes.txt"], AcceptRecovery.ChangedSince(recorded, path => now[path]));
    }

    [Fact]
    public void A_file_that_accept_left_and_that_was_deleted_or_that_accept_removed_and_that_came_back_counts_as_changed()
    {
        Dictionary<string, string?> recorded = new() { ["kept.txt"] = "aa", ["removed.txt"] = null };
        Dictionary<string, string?> now = new() { ["kept.txt"] = null, ["removed.txt"] = "dd" };

        Assert.Equal(["kept.txt", "removed.txt"], AcceptRecovery.ChangedSince(recorded, path => now[path]));
    }

    [Fact]
    public void An_included_sample_that_was_replaced_is_reported()
    {
        Dictionary<string, string?> recorded = new() { ["samples/new.wav"] = "11" };

        Assert.Equal(["samples/new.wav"], AcceptRecovery.ChangedSince(recorded, _ => "22"));
    }

    [Theory]
    [InlineData(true, 0, 0, 2, true)]    // applied, other files changed, nothing kept: the data is gone (issue 222)
    [InlineData(true, 1, 0, 2, false)]   // an earlier text was kept
    [InlineData(true, 0, 1, 2, false)]   // a brought-along file was kept
    [InlineData(true, 0, 0, 0, false)]   // only the song changed: there is nothing else to put back
    [InlineData(false, 0, 0, 3, false)]  // not applied
    public void The_recovery_data_is_missing_only_for_an_applied_proposal_that_changed_other_files_and_kept_nothing(
        bool applied, int undo, int included, int others, bool expected)
    {
        Assert.Equal(expected, AcceptRecovery.RecoveryMissing(applied, undo, included, others));
    }

    [Fact]
    public void The_hash_of_a_missing_file_is_null_and_of_a_file_is_its_sha256()
    {
        string folder = Directory.CreateTempSubdirectory("ample-recovery-").FullName;
        try
        {
            string file = Path.Combine(folder, "a.txt");
            Assert.Null(AcceptRecovery.HashOfFile(file));
            File.WriteAllText(file, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AcceptRecovery.HashOfFile(file));
            File.WriteAllText(file, "abd");
            Assert.NotEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AcceptRecovery.HashOfFile(file));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_undo_data_survives_saving_the_task_and_reading_it_back()
    {
        AiTaskRequest request = new("modify-file", "file", "prompt", "song.amplet", "song.amplet", null, null, null, null);
        AssistantTaskSnapshot snapshot = new(
            "t1", "s1", request, "ready", DateTimeOffset.UtcNow, "id", "manual", "ws", "thread", "summary", null,
            new Dictionary<string, string?>(), [], "ok", string.Empty, true, false, null)
        {
            Undo = new Dictionary<string, string?> { ["notes.txt"] = "before", ["new.txt"] = null },
            Written = new Dictionary<string, string?> { ["notes.txt"] = "aa", ["new.txt"] = "bb" },
            IncludedPaths = ["samples/kick.wav"],
            SongBefore = "old song",
        };

        string json = JsonSerializer.Serialize(snapshot);
        AssistantTaskSnapshot back = JsonSerializer.Deserialize<AssistantTaskSnapshot>(json)!;

        Assert.Equal("before", back.Undo["notes.txt"]);
        Assert.Null(back.Undo["new.txt"]);
        Assert.Equal(2, back.Written.Count);
        Assert.Equal(["samples/kick.wav"], back.IncludedPaths);
        Assert.Equal("old song", back.SongBefore);
        Assert.True(back.Applied);
    }

    [Fact]
    public void A_task_saved_before_this_data_existed_reads_back_with_empty_recovery_data()
    {
        AiTaskRequest request = new("modify-file", "file", "prompt", "song.amplet", "song.amplet", null, null, null, null);
        AssistantTaskSnapshot snapshot = new(
            "t2", "s1", request, "ready", DateTimeOffset.UtcNow, "id", "manual", "ws", "thread", "summary", null,
            new Dictionary<string, string?>(), [], "ok", string.Empty, true, false, null);
        JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Dictionary<string, JsonElement> old = document.RootElement.EnumerateObject()
            .Where(p => p.Name is not ("Undo" or "Written" or "IncludedPaths" or "SongBefore"))
            .ToDictionary(p => p.Name, p => p.Value);

        AssistantTaskSnapshot back = JsonSerializer.Deserialize<AssistantTaskSnapshot>(JsonSerializer.Serialize(old))!;

        Assert.Empty(back.Undo);
        Assert.Empty(back.Written);
        Assert.Empty(back.IncludedPaths);
        Assert.Null(back.SongBefore);
    }
}
