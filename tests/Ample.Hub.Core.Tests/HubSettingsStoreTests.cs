using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Ample.Hub.Core.Tests;

public sealed class HubSettingsStoreTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-hubjson-" + Guid.NewGuid().ToString("N"));
    private readonly string mutexName = "ample-test-" + Guid.NewGuid().ToString("N");

    private string File_ => HubSettings.PathIn(home);

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    private void Write(string text)
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(File_, text);
    }

    [Fact]
    public void Fields_this_program_does_not_know_survive_a_save()
    {
        Write("""{"mainPort":5000,"mainLogin":true,"acceptOthers":true,"startAtLogin":true,"futureField":{"a":[1,2]},"note":"keep"}""");

        HubSettingsStore.Update(home, s => s with { MainPort = 6000 }, mutexName);

        JsonObject saved = JsonNode.Parse(File.ReadAllText(File_))!.AsObject();
        Assert.Equal(6000, (int)saved["mainPort"]!);
        Assert.True((bool)saved["mainLogin"]!);
        Assert.Equal("keep", (string)saved["note"]!);
        Assert.Equal(2, saved["futureField"]!["a"]!.AsArray().Count);
    }

    [Fact]
    public void The_change_is_applied_to_what_is_in_the_file_now_so_a_setup_change_is_not_undone()
    {
        Write("""{"mainPort":0,"mainLogin":false,"acceptOthers":true,"startAtLogin":true}""");
        HubSettings stale = HubSettings.Load(home);
        Write("""{"mainPort":0,"mainLogin":true,"acceptOthers":true,"startAtLogin":true}""");

        HubSettingsStore.Update(home, s => s with { MainPort = 7000 }, mutexName);

        HubSettings result = HubSettings.Load(home);
        Assert.False(stale.MainLogin);
        Assert.True(result.MainLogin);
        Assert.Equal(7000, result.MainPort);
    }

    [Fact]
    public void Keys_written_with_another_case_are_replaced_not_duplicated()
    {
        Write("""{"MainPort":1,"MAINLOGIN":true}""");

        HubSettingsStore.Update(home, s => s with { MainPort = 2 }, mutexName);

        JsonObject saved = JsonNode.Parse(File.ReadAllText(File_))!.AsObject();
        Assert.Equal(1, saved.Count(p => string.Equals(p.Key, "mainPort", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, saved.Count(p => string.Equals(p.Key, "mainLogin", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, HubSettings.Load(home).MainPort);
        Assert.True(HubSettings.Load(home).MainLogin);
    }

    [Fact]
    public void A_missing_or_empty_file_is_created_with_the_defaults_and_the_change()
    {
        HubSettingsStore.Update(home, s => s with { MainPort = 8000 }, mutexName);
        Assert.Equal(new HubSettings(MainPort: 8000), HubSettings.Load(home));

        Write("   ");
        HubSettingsStore.Update(home, s => s with { MainLogin = true }, mutexName);
        Assert.True(HubSettings.Load(home).MainLogin);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("""{"mainPort":"abc"}""")]
    public void A_file_that_is_not_valid_settings_is_refused_and_left_untouched(string text)
    {
        Write(text);

        Assert.Throws<InvalidDataException>(() => HubSettingsStore.Update(home, s => s with { MainPort = 1 }, mutexName));

        Assert.Equal(text, File.ReadAllText(File_));
        Assert.Empty(Directory.GetFiles(home, "*.tmp"));
    }

    [Fact]
    public void Save_goes_through_the_same_store_and_keeps_unknown_fields()
    {
        Write("""{"mainPort":1,"extra":true}""");

        new HubSettings(MainPort: 9).Save(home);

        JsonObject saved = JsonNode.Parse(File.ReadAllText(File_))!.AsObject();
        Assert.Equal(9, (int)saved["mainPort"]!);
        Assert.True((bool)saved["extra"]!);
    }

    [Fact]
    public void A_mutex_that_stays_held_only_delays_the_write_and_does_not_block_it()
    {
        using ManualResetEventSlim held = new();
        using ManualResetEventSlim letGo = new();
        Thread holder = new(() =>
        {
            using Mutex other = new(false, mutexName);
            other.WaitOne();
            held.Set();
            letGo.Wait(TimeSpan.FromSeconds(30));
            other.ReleaseMutex();
        });
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            HubSettingsStore.Update(home, s => s with { MainPort = 4242 }, mutexName, TimeSpan.FromMilliseconds(150));
        }
        finally
        {
            letGo.Set();
            holder.Join();
        }

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(100), "it waited for the mutex first");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(4242, HubSettings.Load(home).MainPort);
    }

    [Fact]
    public void The_mutex_is_free_again_after_a_write_and_after_a_refused_write()
    {
        Write("{broken");
        Assert.Throws<InvalidDataException>(() => HubSettingsStore.Update(home, s => s, mutexName));
        File.Delete(File_);
        HubSettingsStore.Update(home, s => s, mutexName);

        bool acquired = false;
        Thread probe = new(() =>
        {
            using Mutex other = new(false, mutexName);
            acquired = other.WaitOne(TimeSpan.FromSeconds(5));
            if (acquired) other.ReleaseMutex();
        });
        probe.Start();
        probe.Join();

        Assert.True(acquired);
    }
}
