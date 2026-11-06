using Ample.Storage.Projects;

namespace Ample.Hub.Core.Tests;

public sealed class HubStateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ServeArgs = ["serve", "--port", "5000"];
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-hub-" + Guid.NewGuid().ToString("N"));

    private static InstanceRecord Record(string folder, int port, bool noHub = false, bool recipe = false, int minutes = 0) =>
        new(1, Guid.NewGuid().ToString("D"), Path.GetFileName(folder), folder, 100 + port, Now, port, Now.AddMinutes(minutes), Now, "0.1.3", "pc", "cli", null, null, recipe ? new InstanceRelaunch("ample.exe", ServeArgs) : null, noHub);

    [Fact]
    public void The_main_instance_is_found_by_its_folder_and_the_others_are_listed_in_start_order()
    {
        string main = Path.Combine(home, "projects", "Welcome");
        HubState state = HubState.Build([Record(Path.Combine(home, "b"), 5002, minutes: 2), Record(main, 5001), Record(Path.Combine(home, "a"), 5003, minutes: 1)], new HubSettings(), main);

        Assert.Equal(5001, state.Main!.Record.Port);
        Assert.True(state.Main.IsMain);
        Assert.Equal([5003, 5002], state.Others.Select(o => o.Record.Port).ToArray());
    }

    [Fact]
    public void A_stopped_main_instance_is_null_and_an_instance_that_wants_no_hub_is_left_out()
    {
        HubState state = HubState.Build([Record(Path.Combine(home, "x"), 5010, noHub: true), Record(Path.Combine(home, "y"), 5011)], new HubSettings(), Path.Combine(home, "main"));

        Assert.Null(state.Main);
        Assert.Equal([5011], state.Others.Select(o => o.Record.Port).ToArray());
    }

    [Fact]
    public void Other_instances_are_hidden_when_the_setting_says_so()
    {
        HubState state = HubState.Build([Record(Path.Combine(home, "y"), 5011)], new HubSettings(AcceptOthers: false), Path.Combine(home, "main"));

        Assert.Empty(state.Others);
    }

    [Fact]
    public void An_other_instance_can_only_be_restarted_when_it_announced_how()
    {
        HubState state = HubState.Build([Record(Path.Combine(home, "a"), 5001), Record(Path.Combine(home, "b"), 5002, recipe: true)], new HubSettings(), Path.Combine(home, "main"));

        Assert.False(state.Others.Single(o => o.Record.Port == 5001).CanRestart);
        Assert.True(state.Others.Single(o => o.Record.Port == 5002).CanRestart);
    }

    [Fact]
    public void The_report_names_every_instance_and_says_when_the_main_one_is_stopped()
    {
        string report = HubState.Build([Record(Path.Combine(home, "a"), 5001, minutes: -90)], new HubSettings(), Path.Combine(home, "main")).ToReport(Now);

        Assert.Contains("Main instance: stopped", report, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:5001", report, StringComparison.Ordinal);
        Assert.Contains("up 1h 30m", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_round_trip_and_a_damaged_file_gives_the_defaults()
    {
        new HubSettings(MainPort: 5555, MainLogin: true, AcceptOthers: false, StartAtLogin: false).Save(home);
        Assert.Equal(new HubSettings(5555, true, false, false), HubSettings.Load(home));

        File.WriteAllText(HubSettings.PathIn(home), "{ not json");
        Assert.Equal(new HubSettings(), HubSettings.Load(home));
    }

    [Fact]
    public void The_token_comes_out_of_a_launch_url()
    {
        Assert.Equal("abc", InstanceControl.TokenOf("http://127.0.0.1:5000/#token=abc"));
        Assert.Null(InstanceControl.TokenOf("http://127.0.0.1:5000/"));
        Assert.Null(InstanceControl.TokenOf(null));
    }

    [Fact]
    public void The_main_server_starts_on_its_port_with_sign_in_only_when_asked()
    {
        MainInstance main = new(home, "ample.exe");

        IReadOnlyList<string> plain = main.Arguments(new HubSettings());
        IReadOnlyList<string> custom = main.Arguments(new HubSettings(MainPort: 5555, MainLogin: true));

        Assert.DoesNotContain("--port", plain);
        Assert.DoesNotContain("--login", plain);
        Assert.Contains("--no-browser", plain);
        Assert.Equal("5555", custom[custom.ToList().IndexOf("--port") + 1]);
        Assert.Contains("--login", custom);
    }

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }
}
