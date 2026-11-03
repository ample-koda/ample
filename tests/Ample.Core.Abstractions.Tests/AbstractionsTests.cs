using Ample.Testing;

namespace Ample.Core.Abstractions.Tests;

public sealed class AbstractionsTests
{
    [Fact]
    public async Task Fakes_are_injectable()
    {
        DateTimeOffset start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        FakeClock clock = new(start);
        InMemoryFileSystem fileSystem = new();
        Guid expectedId = Guid.Parse("0199a5df-4c00-7000-8000-000000000001");
        SequentialIdGenerator ids = new([expectedId]);

        clock.Advance(TimeSpan.FromMinutes(1));
        await fileSystem.WriteAllTextAsync("song.amplet", "song", CancellationToken.None);

        Assert.Equal(start.AddMinutes(1), clock.UtcNow);
        Assert.Equal("song", await fileSystem.ReadAllTextAsync("song.amplet", CancellationToken.None));
        Assert.Equal(expectedId, ids.NewId());
    }

    [Fact]
    public void Runtime_errors_require_stable_codes()
    {
        AmpleException exception = new("AMP5001", "Failure.");

        Assert.Equal("AMP5001", exception.DiagnosticCode);
    }
}
