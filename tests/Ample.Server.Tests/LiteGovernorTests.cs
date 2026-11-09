using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>The abuse protection and the small queue of Lite (rate windows, the byte budget, the build queue, the encoder slot) and the small cache of the outputs.</summary>
public sealed class LiteGovernorTests
{
    private static readonly string[] Alice = ["c:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "ip:10.0.0.1"];
    private static readonly string[] AliceAgain = ["c:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "ip:10.0.0.1"];   // a new cookie, the same address
    private static readonly string[] Bob = ["c:cccccccccccccccccccccccccccccccc", "ip:10.0.0.2"];

    [Fact]
    public void A_client_may_do_something_only_so_often_and_is_told_how_long_to_wait()
    {
        using LiteGovernor governor = new(new LiteLimits(BuildsPerMinute: 3, BuildsPerHour: 100));
        for (int i = 0; i < 3; i++) governor.Spend("build", Alice, 3, 100);
        LiteRejectedException refused = Assert.Throws<LiteRejectedException>(() => governor.Spend("build", Alice, 3, 100));
        Assert.Equal("AMPL932", refused.Code);
        Assert.Equal(429, refused.Status);
        Assert.InRange(refused.RetryAfterSeconds, 1, 60);
        governor.Spend("build", Bob, 3, 100);   // somebody else is not held back
    }

    [Fact]
    public void Builds_have_a_limit_for_a_minute_for_5_minutes_and_for_15_minutes()
    {
        (TimeSpan, int, string)[] rules = [(TimeSpan.FromMinutes(1), 100, "a minute"), (TimeSpan.FromMinutes(5), 12, "5 minutes"), (TimeSpan.FromMinutes(15), 100, "15 minutes")];
        using LiteGovernor governor = new(new LiteLimits());
        for (int i = 0; i < 12; i++) governor.Spend("build", Alice, rules);   // 12 are allowed in 5 minutes
        LiteRejectedException refused = Assert.Throws<LiteRejectedException>(() => governor.Spend("build", Alice, rules));
        Assert.Equal("AMPL932", refused.Code);
        Assert.Contains("5 minutes", refused.Message);
        governor.Spend("build", Bob, rules);
    }

    [Fact]
    public void Clearing_the_cookie_does_not_get_around_the_limit_because_the_address_counts_too()
    {
        using LiteGovernor governor = new(new LiteLimits());
        for (int i = 0; i < 2; i++) governor.Spend("download", Alice, 2, 100);
        Assert.Throws<LiteRejectedException>(() => governor.Spend("download", AliceAgain, 2, 100));
    }

    [Fact]
    public void The_hour_has_its_own_limit_and_kinds_are_counted_apart()
    {
        using LiteGovernor governor = new(new LiteLimits());
        for (int i = 0; i < 2; i++) governor.Spend("build", Alice, 100, 2);
        Assert.Throws<LiteRejectedException>(() => governor.Spend("build", Alice, 100, 2));
        governor.Spend("download", Alice, 100, 2);
    }

    [Fact]
    public void Encoded_audio_is_limited_by_the_megabytes_of_the_hour()
    {
        using LiteGovernor governor = new(new LiteLimits(EncodedMegabytesPerHour: 2));
        governor.SpendBytes(Alice, 1024 * 1024);
        governor.SpendBytes(Alice, 1024 * 1024);
        LiteRejectedException refused = Assert.Throws<LiteRejectedException>(() => governor.SpendBytes(Alice, 1));
        Assert.Equal(429, refused.Status);
        Assert.Contains("2 MB an hour", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Builds_wait_in_line_in_order_and_a_full_line_refuses_at_once()
    {
        using LiteGovernor governor = new(new LiteLimits(ConcurrentBuilds: 1, QueueWaiting: 1, QueueWaitSeconds: 20));
        using IDisposable running = await governor.EnterBuildAsync(Alice, null, CancellationToken.None);
        List<int> places = [];
        Task<IDisposable> second = governor.EnterBuildAsync(Bob, (place, _) => { places.Add(place); return Task.CompletedTask; }, CancellationToken.None);
        await Task.Delay(500);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, governor.Waiting);
        string[] carol = ["c:dddddddddddddddddddddddddddddddd", "ip:10.0.0.3"];
        LiteRejectedException full = await Assert.ThrowsAsync<LiteRejectedException>(() => governor.EnterBuildAsync(carol, null, CancellationToken.None));
        Assert.Equal("AMPL930", full.Code);
        running.Dispose();
        using IDisposable turn = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([1], places);
    }

    [Fact]
    public async Task A_client_has_at_most_one_build_running_or_waiting()
    {
        using LiteGovernor governor = new(new LiteLimits(ConcurrentBuilds: 2));
        using IDisposable first = await governor.EnterBuildAsync(Alice, null, CancellationToken.None);
        LiteRejectedException again = await Assert.ThrowsAsync<LiteRejectedException>(() => governor.EnterBuildAsync(AliceAgain, null, CancellationToken.None));
        Assert.Equal("AMPL933", again.Code);
        first.Dispose();
        using IDisposable next = await governor.EnterBuildAsync(Alice, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_build_that_gives_up_leaves_the_line()
    {
        using LiteGovernor governor = new(new LiteLimits(ConcurrentBuilds: 1, QueueWaiting: 2));
        using IDisposable running = await governor.EnterBuildAsync(Alice, null, CancellationToken.None);
        using CancellationTokenSource cancel = new();
        Task<IDisposable> waiting = governor.EnterBuildAsync(Bob, null, cancel.Token);
        await Task.Delay(400);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, governor.Waiting);
        running.Dispose();
        using IDisposable bobAgain = await governor.EnterBuildAsync(Bob, null, CancellationToken.None);   // his place and his key were given up
    }

    [Fact]
    public async Task The_encoder_takes_one_job_at_a_time_and_refuses_when_nobody_may_wait()
    {
        using LiteGovernor governor = new(new LiteLimits(ConcurrentEncodes: 1, EncodeWaiting: 0));
        using IDisposable job = await governor.EnterEncodeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<LiteRejectedException>(() => governor.EnterEncodeAsync(CancellationToken.None));
    }

    [Fact]
    public void The_output_cache_has_a_total_size_and_the_encoded_versions_go_with_their_build()
    {
        LiteLimits limits = new(MaxKeptBytes: 1000, MaxKeptOutputs: 10);
        LiteOutputs outputs = new();
        string first = outputs.Put(new byte[400], limits);
        outputs.PutVariant(first, "mp3", new byte[100], limits);
        Assert.Equal(100, outputs.GetVariant(first, "mp3")!.Length);
        string second = outputs.Put(new byte[400], limits);
        string third = outputs.Put(new byte[400], limits);   // 1300 bytes: the oldest build and its mp3 go
        Assert.Null(outputs.Get(first));
        Assert.Null(outputs.GetVariant(first, "mp3"));
        Assert.NotNull(outputs.Get(second));
        Assert.NotNull(outputs.Get(third));
        Assert.True(outputs.Bytes <= 1000);
    }

    [Fact]
    public void The_encoder_arguments_are_fixed_and_hold_nothing_of_the_user()
    {
        foreach (string format in LiteEncoder.Formats)
        {
            IReadOnlyList<string> arguments = LiteEncoder.Arguments(format);
            Assert.Equal("pipe:0", arguments[arguments.ToList().IndexOf("-i") + 1]);
            Assert.Equal("pipe:1", arguments[^1]);
            Assert.DoesNotContain(arguments, a => a.Contains('/', StringComparison.Ordinal) || a.Contains('\\', StringComparison.Ordinal));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => LiteEncoder.Arguments("ogg"));
    }

    [Fact]
    public async Task A_wav_is_turned_into_mp3_and_flac_when_the_encoder_is_installed()
    {
        string[] probe = ["-version"];
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("AMPLE_FFMPEG") ?? "ffmpeg", probe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            await process.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;   // no ffmpeg on this machine: the refusal is tested below
        }

        byte[] wav = Ample.Audio.Engine.Wav.Encode(new short[44100], 44100);
        byte[] mp3 = await LiteEncoder.EncodeAsync(wav, "mp3", LiteLimits.Default, CancellationToken.None);
        Assert.True((mp3[0] == (byte)'I' && mp3[1] == (byte)'D') || mp3[0] == 0xFF, "an MP3 starts with an ID3 tag or a frame");
        byte[] flac = await LiteEncoder.EncodeAsync(wav, "flac", LiteLimits.Default, CancellationToken.None);
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(flac, 0, 4));
    }
}
