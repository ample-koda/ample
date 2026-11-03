namespace Ample.Core.Tests.Instruments;

/// <summary>Tiny deterministic generator for fuzz tests (same sequence on every platform).</summary>
internal sealed class Lcg(uint seed)
{
    private uint _s = seed;

    public int Next(int max)
    {
        _s = unchecked((_s * 1664525u) + 1013904223u);
        return (int)((_s >> 8) % (uint)Math.Max(max, 1));
    }

    public int Next(int min, int max) => min + Next(max - min);
}
