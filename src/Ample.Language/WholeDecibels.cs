namespace Ample.Language;

/// <summary>
/// The levels in decibels that the effects use as whole decibels: the eq bands, the threshold and makeup of a compressor, the ceiling of a limiter, the threshold of a gate
/// and a de-esser, the depth of a duck and the limiter of the master. A decimal part is rounded to the nearest whole decibel (halves away from zero), as the mixer does;
/// before the eq 3, compressor 7, limiter 2, gate 2, de-esser 3, duck 4 and master 5 renderers it was cut off, so -2.8 dB played as -2 and anything between -1 and 1 as 0.
/// The engine (AudioEffects), the parser (AMP2098) and the Studio knobs (web/lib/studio-math.js wholeDb) follow this one table.
/// </summary>
public static class WholeDecibels
{
    /// <summary>True when the parameter of the block type is used in whole decibels.</summary>
    /// <param name="blockType">The type of the block (eq, compressor...).</param>
    /// <param name="name">The name of the parameter.</param>
    public static bool Is(string blockType, string name) => (blockType, name) is ("eq", "low" or "mid" or "high") or ("compressor", "threshold" or "makeup")
        or ("limiter", "ceiling") or ("gate", "threshold") or ("deesser", "threshold") or ("duck", "depth") or ("master", "limiter");
}
