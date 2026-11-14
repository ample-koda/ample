using System.Diagnostics;

namespace Ample.Setup.Tests;

public class ProcessRunnerTests
{
    /// <summary>A shell command on this system: the Windows command line or a POSIX shell.</summary>
    private static ProcessStartInfo Shell(string windowsCommand, string posixCommand)
    {
        bool windows = OperatingSystem.IsWindows();
        ProcessStartInfo start = new(windows ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(windows ? "/c" : "-c");
        start.ArgumentList.Add(windows ? windowsCommand : posixCommand);
        return start;
    }

    [Fact]
    public void The_output_and_the_error_text_and_the_exit_code_are_captured()
    {
        string? problem = ProcessRunner.Capture(
            Shell("echo out& echo err 1>&2& exit 3", "echo out; echo err 1>&2; exit 3"), 30000,
            out string stdout, out string stderr, out int code);
        Assert.Null(problem);
        Assert.Equal("out", stdout.Trim());
        Assert.Equal("err", stderr.Trim());
        Assert.Equal(3, code);
    }

    [Fact]
    public void A_child_that_fills_both_pipes_does_not_hang_the_reader()
    {
        // more than a pipe holds on both streams: reading one stream to its end before the other would block the child on the
        // other pipe for ever
        ProcessStartInfo start = Shell(
            "for /L %i in (1,1,30000) do @(echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            + "& echo bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb 1>&2)",
            "i=0; while [ $i -lt 30000 ]; do echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; "
            + "echo bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb 1>&2; i=$((i+1)); done");
        Stopwatch clock = Stopwatch.StartNew();
        string? problem = ProcessRunner.Capture(start, 120000, out string stdout, out string stderr, out int code);
        Assert.Null(problem);
        Assert.Equal(0, code);
        Assert.True(stdout.Length > 1_000_000, "stdout was read to its end: " + stdout.Length);
        Assert.True(stderr.Length > 1_000_000, "stderr was read to its end: " + stderr.Length);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void A_child_that_does_not_finish_is_stopped_at_the_deadline_and_the_reason_is_returned()
    {
        // ping with a count and an unreachable pace, or a sleep: neither ends within the deadline of one second
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            }
            : Shell("", "exec sleep 30");
        Stopwatch clock = Stopwatch.StartNew();
        string? problem = ProcessRunner.Capture(start, 1000, out string stdout, out _, out int code);
        Assert.NotNull(problem);
        Assert.Contains("did not finish within 1 seconds", problem, StringComparison.Ordinal);
        Assert.Equal(-1, code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the deadline was kept: " + clock.Elapsed);
        Assert.NotNull(stdout);
    }
}
