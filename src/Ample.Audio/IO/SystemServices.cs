using Ample.Core.Abstractions;

namespace Ample.Audio.IO;

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Creates UUID version 7 identifiers.</summary>
public sealed class GuidV7Generator : IIdGenerator
{
    /// <inheritdoc />
    public Guid NewId() => Guid.CreateVersion7();
}
