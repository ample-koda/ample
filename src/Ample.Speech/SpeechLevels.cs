namespace Ample.Speech;

/// <summary>
/// The loudness offsets of the speech engines. The three engines make speech of different loudness for the same <c>gain</c>: with <c>level matched</c> a block adds the offset
/// of its engine to its gain, so that retro, Humanico and Flite sound equally loud at the same gain. <c>level matched</c> is the default; <c>level engine</c> adds nothing and keeps
/// the loudness of every engine as it was made before renderer 1.8.
/// </summary>
/// <remarks>
/// Method (repeated by the test <c>SpeechLevelsTests</c>, so a change of an engine shows up there): 20 plain English sentences are spoken by every engine at its own default
/// pitch and rate and at 22,050 Hz, joined with half a second of silence between them; the integrated loudness of that signal (ITU-R BS.1770 with gating, as the
/// master report measures it) is taken. Humanico is the reference (offset 0); the offset of another engine is the reference loudness minus its loudness, rounded to 0.5 dB.
/// </remarks>
public static class SpeechLevels
{
    /// <summary>Gets the value of <c>level</c> that keeps the loudness of every engine as the engine makes it.</summary>
    public const string Engine = "engine";

    /// <summary>Gets the value of <c>level</c> that adds the offset of the engine to the gain.</summary>
    public const string Matched = "matched";

    /// <summary>Gets the value of <c>level</c> that a block without a <c>level</c> line has.</summary>
    public const string Default = Matched;

    /// <summary>Gets the offsets in dB per engine; they change only together with a version of the speech renderer.</summary>
    public static IReadOnlyDictionary<string, double> Table { get; } = new Dictionary<string, double>
    {
        ["retro"] = 5,
        ["humanico"] = 0,
        ["flite"] = -3,
    };

    /// <summary>Gets the offset of an engine in dB.</summary>
    /// <param name="engine">The engine: <c>retro</c>, <c>humanico</c> or <c>flite</c>.</param>
    /// <returns>The offset that <c>level matched</c> adds to the gain; 0 for an engine that is not in the table.</returns>
    public static double OffsetDb(string engine) => Table.TryGetValue(engine, out double offset) ? offset : 0;
}
