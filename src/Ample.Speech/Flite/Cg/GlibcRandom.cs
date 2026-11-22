namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), the use of rand() in src/cg/cst_mlsa.c (plus_or_minus_one).
// Translated to C# and modified: the generator is the additive feedback generator of the GNU C library
// (the default of rand and random), written here from its published description, so that a render gives the same numbers on every platform.

/// <summary>The sequence of the C library function <c>rand()</c> of the GNU C library (the TYPE_3 additive feedback generator, 31 values of state, <c>r[i] = r[i-3] + r[i-31]</c>), made in C#.
/// A new generator is what a fresh process has (<c>srand(1)</c>); the C program of Flite never seeds it again, so the numbers of several utterances of one program run follow each other.</summary>
internal sealed class GlibcRandom
{
    /// <summary>The largest value that <see cref="Next"/> returns (<c>RAND_MAX</c>).</summary>
    public const int Max = 2147483647;

    private readonly uint[] state = new uint[31];
    private int position;

    /// <summary>Starts the sequence as <c>srand(seed)</c> does (a seed of 0 is taken as 1).</summary>
    /// <param name="seed">The seed.</param>
    public GlibcRandom(uint seed = 1)
    {
        if (seed == 0) seed = 1;

        // r[0] = seed; r[i] = (16807 * r[i-1]) mod 2147483647 for the next 30 values (done without a 64 bit product, as the original, in signed 32 bit arithmetic)
        int word = unchecked((int)seed);
        state[0] = seed;
        for (int i = 1; i < 31; i++)
        {
            long hi = word / 127773;
            long lo = word % 127773;
            long next = (16807 * lo) - (2836 * hi);
            if (next < 0) next += 2147483647;
            word = (int)next;
            state[i] = unchecked((uint)word);
        }

        // r[31..33] repeat r[0..2], so the ring is at the step i = 34 (slot 34 mod 31 = 3); then 310 values (r[34] to r[343]) are thrown away
        position = 3;
        for (int i = 0; i < 310; i++) Advance();
    }

    /// <summary>One step of the generator: the new value is the sum of the values 3 and 31 steps back (modulo 2^32).</summary>
    private uint Advance()
    {
        // the ring holds r[i-31] .. r[i-1]: position is the place of r[i-31]; r[i-3] is 28 places on
        int a = position, b = (position + 28) % 31;
        uint value = unchecked(state[a] + state[b]);
        state[a] = value;
        position = (position + 1) % 31;
        return value;
    }

    /// <summary>The next value of <c>rand()</c>, 0 to 2147483647.</summary>
    public int Next() => (int)(Advance() >> 1);
}
