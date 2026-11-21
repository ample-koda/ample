namespace Ample.Speech;

/// <summary>Versioned deterministic seed derivation shared by text and Notes speech.</summary>
public static class SpeechSeed
{
    public const string AlgorithmIdentity = "SplitMix64/ample-2";

    public static ulong ForWord(ulong seed, int wordIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wordIndex);
        ulong z = unchecked(seed + ((ulong)wordIndex + 1) * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
