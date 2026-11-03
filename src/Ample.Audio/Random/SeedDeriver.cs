using System.Buffers.Binary;
using System.Text;

namespace Ample.Audio.Random;

/// <summary>Derives per-block, per-purpose seeds from the master seed and a stable path. The path is encoded without ambiguity (tag byte, length prefix, fixed little-endian integers), prefixed with the <see cref="RngVersion"/> and hashed with SipHash-2-4 keyed by the master seed.</summary>
public static class SeedDeriver
{
    /// <summary>The second half of the hash key: the ASCII text "AMPLESEE".</summary>
    public const ulong DomainKey = 0x414D504C45534545UL;

    /// <summary>Derives a 64-bit seed. Path parts are strings (hashed as UTF-8) or non-negative integers.</summary>
    /// <param name="masterSeed">The master seed.</param>
    /// <param name="path">For example a block id, a purpose such as "voice", a take or variant index.</param>
    public static ulong Derive(ulong masterSeed, params object[] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        List<byte> data = [(byte)RngVersion.Current];
        foreach (object part in path)
        {
            switch (part)
            {
                case string text:
                    byte[] utf8 = Encoding.UTF8.GetBytes(text);
                    data.Add(0x01);
                    byte[] length = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)utf8.Length);
                    data.AddRange(length);
                    data.AddRange(utf8);
                    break;
                case byte or ushort or uint or ulong or int or long:
                    data.Add(0x02);
                    byte[] number = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(number, ToUInt64(part));
                    data.AddRange(number);
                    break;
                default:
                    throw new ArgumentException($"Seed path parts must be strings or non-negative integers, not {part?.GetType().Name ?? "null"}.", nameof(path));
            }
        }

        return SipHash24.Hash(masterSeed, DomainKey, [.. data]);
    }

    /// <summary>Derives a 32-bit seed (the low 32 bits of <see cref="Derive"/>), for plugins whose seeds are 0 to 4294967295.</summary>
    public static uint DeriveSeed32(ulong masterSeed, params object[] path) => (uint)(Derive(masterSeed, path) & 0xFFFFFFFFUL);

    private static ulong ToUInt64(object value)
    {
        long signed = value switch { int i => i, long l => l, _ => 0 };
        if (signed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Seed path integers must not be negative.");
        }

        return value switch { byte b => b, ushort u => u, uint ui => ui, ulong ul => ul, _ => (ulong)signed };
    }
}
