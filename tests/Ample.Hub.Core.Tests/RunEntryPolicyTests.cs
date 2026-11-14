using System.Security;

namespace Ample.Hub.Core.Tests;

public sealed class RunEntryPolicyTests
{
    private const string Exe = @"C:\Program Files\AMPLE\Ample.Hub.exe";

    private sealed class FakeStore : IRunEntryStore
    {
        public string? Value { get; set; }

        public Exception? ReadFails { get; set; }

        public Exception? WriteFails { get; set; }

        public int Writes { get; private set; }

        public int Deletes { get; private set; }

        public string? Read() => ReadFails is null ? Value : throw ReadFails;

        public void Write(string value)
        {
            if (WriteFails is not null) throw WriteFails;
            Value = value;
            Writes++;
        }

        public void Delete()
        {
            Value = null;
            Deletes++;
        }
    }

    [Fact]
    public void Without_hub_json_the_preference_follows_the_entry_the_installer_wrote_or_did_not_write()
    {
        Assert.True(RunEntryPolicy.InitialPreference(new FakeStore { Value = "\"x\"" }, false));
        Assert.False(RunEntryPolicy.InitialPreference(new FakeStore(), true));
    }

    [Fact]
    public void An_entry_with_another_path_still_counts_as_a_yes()
    {
        Assert.True(RunEntryPolicy.InitialPreference(new FakeStore { Value = "\"D:\\other\\Ample.Hub.exe\"" }, false));
    }

    [Fact]
    public void An_unreadable_entry_gives_the_fallback()
    {
        Assert.True(RunEntryPolicy.InitialPreference(new FakeStore { ReadFails = new SecurityException("no") }, true));
        Assert.False(RunEntryPolicy.InitialPreference(new FakeStore { ReadFails = new UnauthorizedAccessException() }, false));
    }

    [Fact]
    public void An_unchanged_entry_is_not_written_again_even_when_the_case_differs()
    {
        FakeStore store = new() { Value = RunEntryPolicy.ValueFor(Exe.ToUpperInvariant()) };

        Assert.Null(RunEntryPolicy.Apply(store, true, Exe));

        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void A_missing_entry_or_one_with_another_path_is_written_with_the_quoted_own_path()
    {
        FakeStore missing = new();
        FakeStore other = new() { Value = "\"D:\\old\\Ample.Hub.exe\"" };

        Assert.Null(RunEntryPolicy.Apply(missing, true, Exe));
        Assert.Null(RunEntryPolicy.Apply(other, true, Exe));

        Assert.Equal("\"" + Exe + "\"", missing.Value);
        Assert.Equal("\"" + Exe + "\"", other.Value);
        Assert.Equal(1, other.Writes);
    }

    [Fact]
    public void Off_removes_an_entry_that_is_there_and_does_nothing_otherwise()
    {
        FakeStore present = new() { Value = "\"x\"" };
        FakeStore absent = new();

        Assert.Null(RunEntryPolicy.Apply(present, false, Exe));
        Assert.Null(RunEntryPolicy.Apply(absent, false, Exe));

        Assert.Equal(1, present.Deletes);
        Assert.Equal(0, absent.Deletes);
    }

    [Fact]
    public void A_failing_write_is_reported_and_does_not_throw()
    {
        FakeStore store = new() { WriteFails = new UnauthorizedAccessException("denied") };

        string? problem = RunEntryPolicy.Apply(store, true, Exe);

        Assert.Contains("denied", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_program_path_changes_nothing()
    {
        FakeStore store = new() { Value = "\"x\"" };

        Assert.NotNull(RunEntryPolicy.Apply(store, true, null));

        Assert.Equal("\"x\"", store.Value);
        Assert.Equal(0, store.Writes);
    }
}
