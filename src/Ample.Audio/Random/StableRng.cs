using System.Numerics;

namespace Ample.Audio.Random;

/// <summary>A deterministic random stream (xoshiro256** by Blackman and Vigna, seeded through SplitMix64). Integer-only: the same seed gives the same numbers on every machine, operating system and runtime, forever (for <see cref="RngVersion.Current"/>).</summary>
public sealed partial class StableRng
{
    private ulong s0;
    private ulong s1;
    private ulong s2;
    private ulong s3;

    /// <summary>Initializes a new instance of the <see cref="StableRng"/> class.</summary>
    /// <param name="seed">The 64-bit seed.</param>
    public StableRng(ulong seed)
    {
        SplitMix64 expander = new(seed);
        s0 = expander.Next();
        s1 = expander.Next();
        s2 = expander.Next();
        s3 = expander.Next();
    }

    /// <summary>Returns the next 64-bit value.</summary>
    public ulong Next()
    {
        ulong result = BitOperations.RotateLeft(s1 * 5, 7) * 9;
        ulong t = s1 << 17;
        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = BitOperations.RotateLeft(s3, 45);
        return result;
    }

    /// <summary>Returns an unbiased value in <c>[0, bound)</c> by rejection: values below <c>(2^64 - bound) mod bound</c> are discarded.</summary>
    /// <param name="bound">The exclusive upper bound; must be greater than zero.</param>
    public ulong NextUInt(ulong bound)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bound);
        ulong threshold = (0UL - bound) % bound;
        while (true)
        {
            ulong r = Next();
            if (r >= threshold)
            {
                return r % bound;
            }
        }
    }

    /// <summary>Shuffles a list in place (Fisher-Yates from the end, using <see cref="NextUInt"/>).</summary>
    public void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = (int)NextUInt((ulong)i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>Picks one element using <see cref="NextUInt"/>.</summary>
    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items[(int)NextUInt((ulong)items.Count)];
    }
}
