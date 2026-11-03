namespace Ample.Core.Abstractions;

/// <summary>Represents an AMPLE runtime failure with a stable diagnostic code.</summary>
public sealed class AmpleException : Exception
{
    /// <summary>Initializes a new runtime failure.</summary>
    public AmpleException(string diagnosticCode, string message)
        : base(message)
    {
        if (diagnosticCode.Length != 7 || !diagnosticCode.StartsWith("AMP5", StringComparison.Ordinal) ||
            !int.TryParse(diagnosticCode.AsSpan(4), out _))
        {
            throw new ArgumentException("Runtime diagnostic codes must use the AMP5xxx form.", nameof(diagnosticCode));
        }

        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Initializes a new runtime failure that wraps the exception behind it.</summary>
    public AmpleException(string diagnosticCode, string message, Exception inner)
        : this(diagnosticCode, message)
    {
        InnerExceptionValue = inner;
    }

    /// <summary>Gets the exception behind this failure, if any.</summary>
    public Exception? InnerExceptionValue { get; }

    /// <summary>Gets the stable AMP5xxx runtime diagnostic code.</summary>
    public string DiagnosticCode { get; }
}
