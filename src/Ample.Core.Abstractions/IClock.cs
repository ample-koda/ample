namespace Ample.Core.Abstractions;

/// <summary>Provides the current time without binding callers to the system clock.</summary>
public interface IClock
{
    /// <summary>Gets the current time in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
