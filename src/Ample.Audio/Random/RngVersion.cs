namespace Ample.Audio.Random;

/// <summary>The version of the stable random algorithms. Every plan stores it. The algorithms of a released version are frozen; a change creates a new version and the old one stays available.</summary>
public static class RngVersion
{
    /// <summary>Gets the current version: SplitMix64 seeding, xoshiro256** streams, SipHash-2-4 seed derivation.</summary>
    public const int Current = 1;
}
