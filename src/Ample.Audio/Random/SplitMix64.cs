namespace Ample.Audio.Random;

/// <summary>The SplitMix64 generator (Steele, Lea, Flood; as published by Vigna). Used only to expand a seed into generator state.</summary>
public struct SplitMix64
{
    private ulong state;

    /// <summary>Initializes a new instance of the <see cref="SplitMix64"/> struct.</summary>
    /// <param name="seed">The seed (any 64-bit value).</param>
    public SplitMix64(ulong seed) => state = seed;

    /// <summary>Returns the next 64-bit value.</summary>
    public ulong Next()
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
