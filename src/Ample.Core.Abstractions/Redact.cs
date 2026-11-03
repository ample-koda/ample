namespace Ample.Core.Abstractions;

/// <summary>Prevents sensitive values from being written to logs.</summary>
public static class Redact
{
    /// <summary>Returns a fixed marker instead of the supplied sensitive value.</summary>
    public static string Sensitive<T>(T value)
    {
        _ = value;
        return "[REDACTED]";
    }
}
