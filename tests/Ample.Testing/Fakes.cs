using Ample.Core.Abstractions;

namespace Ample.Testing;

/// <summary>Provides a manually controlled clock for tests.</summary>
public sealed class FakeClock : IClock
{
    /// <summary>Initializes a clock at the supplied UTC time.</summary>
    public FakeClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>Moves the clock forward by the supplied duration.</summary>
    public void Advance(TimeSpan duration)
    {
        UtcNow += duration;
    }
}


/// <summary>Returns a predefined sequence of identifiers for tests.</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private readonly Queue<Guid> identifiers;

    /// <summary>Initializes the generator with identifiers in return order.</summary>
    public SequentialIdGenerator(IEnumerable<Guid> identifiers)
    {
        this.identifiers = new Queue<Guid>(identifiers);
    }

    /// <inheritdoc />
    public Guid NewId() => identifiers.Dequeue();
}
