namespace Ample.Audio.Engine;

/// <summary>Integer equal-power pan table: positions -32 (left) to +32 (right), gains in Q15 from an integer square root, so every machine gets the same values.</summary>
public static class PanTable
{
    /// <summary>Furthest pan position either way.</summary>
    public const int Range = 32;

    private static readonly int[] Left = Build(left: true);
    private static readonly int[] Right = Build(left: false);

    /// <summary>Gets the left gain (Q15) for a pan position, clamped to the range.</summary>
    public static int LeftGain(int pan) => Left[Math.Clamp(pan, -Range, Range) + Range];

    /// <summary>Gets the right gain (Q15) for a pan position, clamped to the range.</summary>
    public static int RightGain(int pan) => Right[Math.Clamp(pan, -Range, Range) + Range];

    /// <summary>Applies Q15 gain to a sample.</summary>
    public static int Apply(int sample, int gainQ15) => (int)((long)sample * gainQ15 >> 15);

    private static int[] Build(bool left)
    {
        int[] table = new int[(2 * Range) + 1];
        for (int i = 0; i < table.Length; i++)
        {
            long t = left ? table.Length - 1 - i : i;
            table[i] = (int)ISqrt(t << 24); // sqrt(t/64) * 32768
        }

        return table;
    }

    private static long ISqrt(long value)
    {
        long x = 0;
        for (long bit = 1L << 30; bit > 0; bit >>= 1)
        {
            if ((x + bit) * (x + bit) <= value)
            {
                x += bit;
            }
        }

        return x;
    }
}
