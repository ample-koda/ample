using System.Text.Json;

namespace Ample.Server.Tests;

public sealed class QueuedMessagesSnapshotTests
{
    private static AssistantTaskSnapshot Snapshot(string id) => new(
        id, "s1", new AiTaskRequest("modify-file", "file", "prompt", "song.amplet", "song.amplet", null, null, null, null),
        "ready", DateTimeOffset.UtcNow, "id", "manual", "ws", "thread", "summary", null,
        new Dictionary<string, string?>(), [], "ok", string.Empty, true, false, null);

    [Fact]
    public void Queued_messages_survive_saving_the_task_and_reading_it_back()
    {
        AssistantTaskSnapshot withMessages = Snapshot("q1") with { QueuedMessages = ["make the bass quieter", "and add a fill in bar 4"] };

        AssistantTaskSnapshot back = JsonSerializer.Deserialize<AssistantTaskSnapshot>(JsonSerializer.Serialize(withMessages))!;

        Assert.Equal(["make the bass quieter", "and add a fill in bar 4"], back.QueuedMessages);
    }

    [Fact]
    public void A_task_saved_before_queued_messages_were_kept_reads_back_with_none()
    {
        JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot("q2")));
        Dictionary<string, JsonElement> old = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "QueuedMessages")
            .ToDictionary(p => p.Name, p => p.Value);

        AssistantTaskSnapshot back = JsonSerializer.Deserialize<AssistantTaskSnapshot>(JsonSerializer.Serialize(old))!;

        Assert.Empty(back.QueuedMessages);
    }

    [Fact]
    public void A_task_without_queued_messages_saves_an_empty_list()
    {
        string json = JsonSerializer.Serialize(Snapshot("q3"));

        Assert.Equal(0, JsonDocument.Parse(json).RootElement.GetProperty("QueuedMessages").GetArrayLength());
    }

    [Fact]
    public void The_changes_that_accept_selected_applied_survive_saving_and_reading_back()
    {
        AiChange applied = new() { Path = "notes.txt", Status = "modified", Before = "old", After = "new" };
        AssistantTaskSnapshot snapshot = Snapshot("q4") with { AcceptedPaths = ["notes.txt"], AcceptedChanges = [applied] };

        AssistantTaskSnapshot back = JsonSerializer.Deserialize<AssistantTaskSnapshot>(JsonSerializer.Serialize(snapshot))!;

        AiChange change = Assert.Single(back.AcceptedChanges);
        Assert.Equal("notes.txt", change.Path);
        Assert.Equal("old", change.Before);
        Assert.Equal("new", change.After);
    }

    [Fact]
    public void A_task_saved_before_accepted_changes_were_kept_reads_back_with_none()
    {
        JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot("q5")));
        Dictionary<string, JsonElement> old = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "AcceptedChanges")
            .ToDictionary(p => p.Name, p => p.Value);

        AssistantTaskSnapshot back = JsonSerializer.Deserialize<AssistantTaskSnapshot>(JsonSerializer.Serialize(old))!;

        Assert.Empty(back.AcceptedChanges);
    }
}
