namespace Ample.Core.Abstractions;

/// <summary>Creates time-ordered UUID version 7 identifiers.</summary>
public interface IIdGenerator
{
    /// <summary>Creates the next identifier.</summary>
    Guid NewId();
}
