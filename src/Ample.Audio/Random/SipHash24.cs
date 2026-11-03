using System.Buffers.Binary;
using System.Numerics;

namespace Ample.Audio.Random;

/// <summary>SipHash-2-4 (Aumasson and Bernstein), a keyed 64-bit hash. Used to derive seeds from a master seed and a path.</summary>
public static class SipHash24
{
    /// <summary>Computes the 64-bit SipHash-2-4 of the data with the 128-bit key <c>(k0, k1)</c>.</summary>
    public static ulong Hash(ulong k0, ulong k1, ReadOnlySpan<byte> data)
    {
        ulong v0 = k0 ^ 0x736F6D6570736575UL;
        ulong v1 = k1 ^ 0x646F72616E646F6DUL;
        ulong v2 = k0 ^ 0x6C7967656E657261UL;
        ulong v3 = k1 ^ 0x7465646279746573UL;
        int length = data.Length;
        int end = length - (length % 8);
        for (int i = 0; i < end; i += 8)
        {
            ulong m = BinaryPrimitives.ReadUInt64LittleEndian(data[i..]);
            v3 ^= m;
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        ulong b = (ulong)length << 56;
        for (int j = 0; j < length % 8; j++)
        {
            b |= (ulong)data[end + j] << (8 * j);
        }

        v3 ^= b;
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        v0 ^= b;
        v2 ^= 0xFF;
        for (int r = 0; r < 4; r++)
        {
            Round(ref v0, ref v1, ref v2, ref v3);
        }

        return v0 ^ v1 ^ v2 ^ v3;
    }

    private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        v0 += v1;
        v1 = BitOperations.RotateLeft(v1, 13);
        v1 ^= v0;
        v0 = BitOperations.RotateLeft(v0, 32);
        v2 += v3;
        v3 = BitOperations.RotateLeft(v3, 16);
        v3 ^= v2;
        v0 += v3;
        v3 = BitOperations.RotateLeft(v3, 21);
        v3 ^= v0;
        v2 += v1;
        v1 = BitOperations.RotateLeft(v1, 17);
        v1 ^= v2;
        v2 = BitOperations.RotateLeft(v2, 32);
    }
}
