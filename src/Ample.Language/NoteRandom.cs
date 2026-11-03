using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Ample.Language;

/// <summary>
/// The seed model of AMPLE (docs/language/13-randomness.md): a seed is a 64-bit key in a tree of keys, derived by hashing (SHA-256), never by adding offsets, and a draw is addressed by what is drawn
/// and where, never by "the n-th random call so far". Everything here is stateless, exact (no floating point) and the same in the JavaScript twin (web/lib/note-random.mjs).
/// </summary>
public static class NoteRandom
{
    private const string Domain = "ample-seed/1";

    /// <summary>The first 8 bytes (big endian) of the SHA-256 of the domain and the parts, one per line.</summary>
    /// <param name="parts">The parts.</param>
    /// <returns>A 64-bit key.</returns>
    public static ulong Hash64(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Domain + "\n" + string.Join('\n', parts)));
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    /// <summary>The key as 16 hexadecimal digits.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The text.</returns>
    public static string Hex(ulong key) => key.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>The root of the song: derived from the master-seed.</summary>
    /// <param name="master">The master-seed.</param>
    /// <returns>The root key.</returns>
    public static ulong Root(ulong master) => Hash64("root", master.ToString(CultureInfo.InvariantCulture));

    /// <summary>An explicit, pinned seed: it depends only on the number, not on the master-seed.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The key.</returns>
    public static ulong Explicit(BigInteger number) => Hash64("explicit", number.ToString(CultureInfo.InvariantCulture));

    /// <summary>The key of a song-global stable namespace: it follows the master-seed.</summary>
    /// <param name="master">The master-seed.</param>
    /// <param name="name">The name.</param>
    /// <returns>The key.</returns>
    public static ulong Stable(ulong master, string name) => Hash64("stable", Hex(Root(master)), name);

    /// <summary>A child key of a key (a path step, not a cursor). Whole-number labels are written with a leading # so that fork(3) and fork("3") are different.</summary>
    /// <param name="key">The parent.</param>
    /// <param name="label">The label.</param>
    /// <returns>The child key.</returns>
    public static ulong Fork(ulong key, string label) => Hash64("fork", Hex(key), label);

    /// <summary>One draw from a key: what is drawn (the purpose) and where (the address) are part of the derivation; nothing else is.</summary>
    /// <param name="key">The seed.</param>
    /// <param name="purpose">What is drawn (random, choose, chance, humanize.timing, ...).</param>
    /// <param name="address">Where: a round number, or the identity of a note, or a bar.</param>
    /// <returns>64 random bits.</returns>
    public static ulong Draw(ulong key, string purpose, string address) => Hash64("draw", Hex(key), purpose, address);

    /// <summary>A whole number below <paramref name="n"/> from 64 random bits (the modulo bias is below 2^-40 for any n a song can ask).</summary>
    /// <param name="bits">The bits.</param>
    /// <param name="n">The bound (positive).</param>
    /// <returns>The number.</returns>
    public static BigInteger Below(ulong bits, BigInteger n) => new BigInteger(bits) % n;

    /// <summary>The numerator of a number in [0, 1) as a fraction of 2^53 (the top 53 bits).</summary>
    /// <param name="bits">The bits.</param>
    /// <returns>The numerator (the denominator is <see cref="UnitDenominator"/>).</returns>
    public static BigInteger UnitNumerator(ulong bits) => bits >> 11;

    /// <summary>The denominator of <see cref="UnitNumerator"/>: 2^53.</summary>
    public static BigInteger UnitDenominator => BigInteger.One << 53;
}

/// <summary>Scales, chord symbols and pitch names for the generators and the helpers that keep notes in a set (the same tables as web/lib/scales.mjs and spec/scales.json).</summary>
public static class NoteTheory
{
    /// <summary>Scale id to ascending semitone offsets from the root.</summary>
    public static readonly IReadOnlyDictionary<string, int[]> Scales = new Dictionary<string, int[]>(StringComparer.Ordinal)
    {
        ["major"] = [0, 2, 4, 5, 7, 9, 11],
        ["minor"] = [0, 2, 3, 5, 7, 8, 10],
        ["harmonic-minor"] = [0, 2, 3, 5, 7, 8, 11],
        ["melodic-minor"] = [0, 2, 3, 5, 7, 9, 11],
        ["dorian"] = [0, 2, 3, 5, 7, 9, 10],
        ["phrygian"] = [0, 1, 3, 5, 7, 8, 10],
        ["lydian"] = [0, 2, 4, 6, 7, 9, 11],
        ["mixolydian"] = [0, 2, 4, 5, 7, 9, 10],
        ["locrian"] = [0, 1, 3, 5, 6, 8, 10],
        ["pentatonic-major"] = [0, 2, 4, 7, 9],
        ["pentatonic-minor"] = [0, 3, 5, 7, 10],
        ["blues"] = [0, 3, 5, 6, 7, 10],
        ["whole-tone"] = [0, 2, 4, 6, 8, 10],
        ["diminished-wh"] = [0, 2, 3, 5, 6, 8, 9, 11],
        ["diminished-hw"] = [0, 1, 3, 4, 6, 7, 9, 10],
        ["chromatic"] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
    };

    // longest suffix first, so that "m7b5" is not read as "m"
    private static readonly (string Suffix, int[] Intervals)[] Qualities =
    [
        ("maj7", [0, 4, 7, 11]), ("m7b5", [0, 3, 6, 10]), ("dim7", [0, 3, 6, 9]), ("sus2", [0, 2, 7]), ("sus4", [0, 5, 7]), ("add9", [0, 4, 7, 14]),
        ("min", [0, 3, 7]), ("maj", [0, 4, 7]), ("dim", [0, 3, 6]), ("aug", [0, 4, 8]), ("m7", [0, 3, 7, 10]), ("m6", [0, 3, 7, 9]),
        ("7", [0, 4, 7, 10]), ("6", [0, 4, 7, 9]), ("9", [0, 4, 7, 10, 14]), ("m", [0, 3, 7]), (string.Empty, [0, 4, 7]),
    ];

    private static readonly int[] NaturalRoots = [9, 11, 0, 2, 4, 5, 7];   // A B C D E F G

    /// <summary>Reads a root name (C, c#, Db, bb ...) as a pitch class 0-11.</summary>
    /// <param name="text">The name.</param>
    /// <param name="pitchClass">The pitch class.</param>
    /// <returns>True when it is a root.</returns>
    public static bool TryRoot(string text, out int pitchClass)
    {
        pitchClass = 0;
        string t = text.Trim();
        if (t.Length is < 1 or > 2 || char.ToLowerInvariant(t[0]) is < 'a' or > 'g') return false;
        int pc = NaturalRoots[char.ToLowerInvariant(t[0]) - 'a'];
        if (t.Length == 2)
        {
            if (t[1] == '#') pc++;
            else if (t[1] == 'b') pc--;
            else return false;
        }

        pitchClass = ((pc % 12) + 12) % 12;
        return true;
    }

    /// <summary>Reads a chord symbol (Am, F, G7, C#m7, Bbmaj7, Esus4 ...).</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="root">The root pitch class.</param>
    /// <param name="intervals">The intervals above the root.</param>
    /// <returns>True when it is a chord.</returns>
    public static bool TryChord(string symbol, out int root, out int[] intervals)
    {
        root = 0;
        intervals = [];
        if (symbol.Length == 0) return false;
        int rootLength = symbol.Length > 1 && symbol[1] is '#' or 'b' ? 2 : 1;
        if (!TryRoot(symbol[..rootLength], out root)) return false;
        string rest = symbol[rootLength..];
        foreach ((string suffix, int[] chord) in Qualities)
        {
            if (rest == suffix) { intervals = chord; return true; }
        }

        return false;
    }
}
