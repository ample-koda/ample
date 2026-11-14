using System.Collections.Generic;
using System.Text.Json;

namespace Ample.Setup.Tests;

public class LoadGateTests
{
    [Fact]
    public void A_list_that_could_not_be_read_is_not_started_again_at_once_but_on_the_next_visit()
    {
        LoadGate gate = new();
        Assert.True(gate.ShouldStart(onPage: true));
        gate.Started();
        Assert.False(gate.ShouldStart(onPage: true));   // under way: not twice
        gate.Finished(ok: false);

        Assert.False(gate.ShouldStart(onPage: true));   // the end of a load shows the page again: that must not start the load again
        Assert.False(gate.Loaded);
        Assert.False(gate.Loading);

        Assert.False(gate.ShouldStart(onPage: false));  // the person leaves the page ...
        Assert.True(gate.ShouldStart(onPage: true));    // ... and comes back: a new try
    }

    [Fact]
    public void Showing_the_page_after_every_failed_load_starts_exactly_one_load_per_visit()
    {
        // what the wizard does: Show starts the load, the end of the load calls Show again. With a list that never can be read this used to be a loop without end.
        LoadGate gate = new();
        int starts = 0;
        void Show(int depth)
        {
            Assert.True(depth < 50, "the load started itself over and over");
            if (!gate.ShouldStart(onPage: true)) return;
            starts++;
            gate.Started();
            gate.Finished(ok: false);   // the read fails at once
            Show(depth + 1);
        }

        Show(0);
        Assert.Equal(1, starts);
        gate.ShouldStart(onPage: false);
        Show(0);
        Assert.Equal(2, starts);
    }

    [Fact]
    public void A_list_that_was_read_stays_read()
    {
        LoadGate gate = new();
        Assert.True(gate.ShouldStart(onPage: true));
        gate.Started();
        gate.Finished(ok: true);
        Assert.True(gate.Loaded);
        Assert.False(gate.ShouldStart(onPage: true));
        gate.ShouldStart(onPage: false);
        Assert.False(gate.ShouldStart(onPage: true));
    }
}

public class PackOutcomeTests
{
    [Fact]
    public void A_pack_that_was_installed_is_installed_also_when_stop_arrived_late()
    {
        Assert.Null(PackOutcome.Describe(stopRequested: false, exitCode: 0, lastLine: null));
        Assert.Null(PackOutcome.Describe(stopRequested: true, exitCode: 0, lastLine: "done"));
    }

    [Fact]
    public void A_download_that_did_not_finish_after_stop_is_cancelled()
    {
        Assert.Equal("Cancelled by you.", PackOutcome.Describe(stopRequested: true, exitCode: -1, lastLine: "downloading 40%"));
    }

    [Fact]
    public void A_failed_download_says_the_last_line_or_the_exit_code()
    {
        Assert.Equal("no space left", PackOutcome.Describe(stopRequested: false, exitCode: 1, lastLine: "no space left"));
        Assert.Equal("ample stopped with exit code 5.", PackOutcome.Describe(stopRequested: false, exitCode: 5, lastLine: null));
    }
}

public class HubSettingsMergeTests
{
    private static Dictionary<string, object> Parse(string text) => JsonSerializer.Deserialize<Dictionary<string, object>>(text);

    private static string Serialize(Dictionary<string, object> settings) => JsonSerializer.Serialize(settings);

    private static JsonElement Merged(string existing, bool login) => JsonDocument.Parse(HubSettingsMerge.Merge(existing, login, Parse, Serialize)).RootElement.Clone();

    [Fact]
    public void Only_the_sign_in_option_changes_and_every_other_field_is_kept()
    {
        JsonElement result = Merged("{\"mainPort\": 5180, \"acceptOthers\": false, \"startAtLogin\": false, \"theme\": \"dark\", \"nested\": {\"a\": [1, 2]}, \"mainLogin\": false}", login: true);
        Assert.True(result.GetProperty("mainLogin").GetBoolean());
        Assert.Equal(5180, result.GetProperty("mainPort").GetInt32());
        Assert.False(result.GetProperty("acceptOthers").GetBoolean());
        Assert.False(result.GetProperty("startAtLogin").GetBoolean());
        Assert.Equal("dark", result.GetProperty("theme").GetString());   // a field this program does not know is not lost
        Assert.Equal(2, result.GetProperty("nested").GetProperty("a")[1].GetInt32());
    }

    [Fact]
    public void Without_a_file_the_defaults_are_written()
    {
        foreach (string? none in new string?[] { null, "", "  \r\n " })
        {
            JsonElement result = Merged(none, login: false);
            Assert.False(result.GetProperty("mainLogin").GetBoolean());
            Assert.Equal(0, result.GetProperty("mainPort").GetInt32());
            Assert.True(result.GetProperty("acceptOthers").GetBoolean());
            Assert.True(result.GetProperty("startAtLogin").GetBoolean());
        }
    }

    [Fact]
    public void A_missing_start_at_sign_in_follows_the_installer_and_a_value_that_is_there_is_never_replaced()
    {
        // the installer chose no start at sign-in (no Run entry): a hub.json made now says so
        Assert.False(JsonDocument.Parse(HubSettingsMerge.Merge(null, true, Parse, Serialize, startAtLoginDefault: false)).RootElement.GetProperty("startAtLogin").GetBoolean());
        Assert.False(JsonDocument.Parse(HubSettingsMerge.Merge("{\"mainPort\": 1}", true, Parse, Serialize, startAtLoginDefault: false)).RootElement.GetProperty("startAtLogin").GetBoolean());
        Assert.True(JsonDocument.Parse(HubSettingsMerge.Merge("{\"mainPort\": 1}", true, Parse, Serialize, startAtLoginDefault: true)).RootElement.GetProperty("startAtLogin").GetBoolean());
        Assert.True(Merged(null, login: false).GetProperty("startAtLogin").GetBoolean());   // without a choice the answer is yes, as it was

        // the person's own choice in hub.json wins over the installer's, both ways
        Assert.True(JsonDocument.Parse(HubSettingsMerge.Merge("{\"startAtLogin\": true}", true, Parse, Serialize, startAtLoginDefault: false)).RootElement.GetProperty("startAtLogin").GetBoolean());
        Assert.False(JsonDocument.Parse(HubSettingsMerge.Merge("{\"startAtLogin\": false}", true, Parse, Serialize, startAtLoginDefault: true)).RootElement.GetProperty("startAtLogin").GetBoolean());
    }

    [Fact]
    public void A_file_that_is_not_a_json_object_is_refused_and_left_alone()
    {
        InvalidOperationException broken = Assert.Throws<InvalidOperationException>(() => HubSettingsMerge.Merge("{ not json", true, Parse, Serialize));
        Assert.Contains("left alone", broken.Message, StringComparison.Ordinal);
        InvalidOperationException array = Assert.Throws<InvalidOperationException>(() => HubSettingsMerge.Merge("[1, 2]", true, Parse, Serialize));
        Assert.Contains("left alone", array.Message, StringComparison.Ordinal);
        InvalidOperationException nothing = Assert.Throws<InvalidOperationException>(() => HubSettingsMerge.Merge("null", true, Parse, Serialize));
        Assert.Contains("not a JSON object", nothing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_ends_with_a_newline_and_a_field_in_another_case_is_the_same_field()
    {
        string text = HubSettingsMerge.Merge("{\"MainLogin\": false, \"mainport\": 7}", true, Parse, Serialize);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        JsonElement result = JsonDocument.Parse(text).RootElement;
        Assert.Equal(7, result.GetProperty("mainport").GetInt32());   // the existing spelling is kept and no second mainPort is added
        Assert.False(result.TryGetProperty("mainPort", out _));
    }
}
