namespace Ample.Language;

/// <summary>
/// The syntax of the diagnostics work (plan/diagnostics/PLAN.md, D17): a feature is switched on only when its runtime, its tests and its documentation exist in every runtime. A feature that is off is
/// recognized and reported as AMP6020 and never produces a node, so nothing can parse but be ignored.
/// </summary>
[Flags]
public enum SyntaxFeatures
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>log, debug, trace and warn.</summary>
    Observe = 1,

    /// <summary>assert.</summary>
    Assert = 2,

    /// <summary>require.</summary>
    Require = 4,

    /// <summary>throw.</summary>
    Throw = 8,

    /// <summary>try, catch and finally.</summary>
    TryCatch = 16,

    /// <summary>fail.</summary>
    Fail = 32,

    /// <summary>error declarations.</summary>
    ErrorDecl = 64,

    /// <summary>Record literals.</summary>
    Record = 128,
}

/// <summary>Which of the <see cref="SyntaxFeatures"/> are on. The shipped set only grows in the work package that completes a feature.</summary>
public static class SyntaxGate
{
    /// <summary>The features this build has parser support for (grows with the work packages).</summary>
    public const SyntaxFeatures Implemented = SyntaxFeatures.Observe | SyntaxFeatures.Assert | SyntaxFeatures.Record
        | SyntaxFeatures.Fail | SyntaxFeatures.Require | SyntaxFeatures.Throw | SyntaxFeatures.TryCatch | SyntaxFeatures.ErrorDecl;

    /// <summary>The features that are on in a shipped build.</summary>
    public const SyntaxFeatures Shipped = SyntaxFeatures.Observe | SyntaxFeatures.Record
        | SyntaxFeatures.Fail | SyntaxFeatures.Require | SyntaxFeatures.Throw | SyntaxFeatures.TryCatch | SyntaxFeatures.ErrorDecl | SyntaxFeatures.Assert;

    [ThreadStatic]
    private static SyntaxFeatures? current;

    /// <summary>Gets the features that are on now: the shipped set, or the set of the innermost <see cref="Use"/> on this thread.</summary>
    public static SyntaxFeatures Enabled => current ?? Shipped;

    /// <summary>Whether a feature is on and implemented.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when the parser may produce nodes for it.</returns>
    public static bool Supports(SyntaxFeatures feature) => (Enabled & Implemented & feature) == feature;

    /// <summary>Switches features on for the current thread until the result is disposed (tests, and hosts that enable a feature in step with a work package).</summary>
    /// <param name="features">The features.</param>
    /// <returns>The scope.</returns>
    public static IDisposable Use(SyntaxFeatures features)
    {
        SyntaxFeatures? before = current;
        current = features;
        return new Scope(before);
    }

    private sealed class Scope(SyntaxFeatures? before) : IDisposable
    {
        public void Dispose() => current = before;
    }
}
