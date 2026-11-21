using System.Text;

namespace Ample.Speech.Flite.Text;

// Port of the string conventions of Flite (CMU). Translated to C# and modified: see UsText.cs.

/// <summary>
/// The text of the front end as the C program sees it: a string of bytes. A .NET string holds one byte per character (0 to 255, as Latin-1), so the length of a string
/// is <c>cst_strlen</c>, a UTF-8 sequence is several characters, and the C library functions (<c>tolower</c>, <c>isupper</c>, <c>strchr</c>) work on ASCII only, as in the C locale.
/// </summary>
internal static class ByteText
{
    /// <summary>Converts UTF-8 text to a byte string. The C program stops at the first NUL byte, so does this.</summary>
    /// <param name="utf8">The UTF-8 bytes.</param>
    /// <returns>A string with one character per byte.</returns>
    public static string FromUtf8(ReadOnlySpan<byte> utf8)
    {
        int end = utf8.IndexOf((byte)0);
        if (end >= 0)
        {
            utf8 = utf8[..end];
        }

        return string.Create(utf8.Length, utf8.ToArray(), static (span, bytes) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = (char)bytes[i];
            }
        });
    }

    /// <summary>Converts text to a byte string: the UTF-8 bytes of the text, one character per byte.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The byte string.</returns>
    public static string FromText(string text) => FromUtf8(Encoding.UTF8.GetBytes(text));

    /// <summary>Converts a byte string back to bytes.</summary>
    /// <param name="bytes">A string with one character per byte.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ToBytes(string bytes)
    {
        byte[] result = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            result[i] = (byte)bytes[i];
        }

        return result;
    }

    /// <summary>Converts a byte string to text, reading the bytes as UTF-8 (invalid sequences become U+FFFD).</summary>
    /// <param name="bytes">A string with one character per byte.</param>
    /// <returns>The text.</returns>
    public static string ToText(string bytes) => Encoding.UTF8.GetString(ToBytes(bytes));

    /// <summary><c>cst_downcase</c>: ASCII letters only (<c>tolower</c> in the C locale).</summary>
    /// <param name="s">The byte string.</param>
    /// <returns>The lower case string.</returns>
    public static string Downcase(string s)
    {
        char[] r = s.ToCharArray();
        for (int i = 0; i < r.Length; i++)
        {
            if (r[i] is >= 'A' and <= 'Z')
            {
                r[i] = (char)(r[i] + ('a' - 'A'));
            }
        }

        return new string(r);
    }

    /// <summary><c>strchr</c> for a set of characters: the index of the first character of <paramref name="s"/> that is in <paramref name="set"/>, or -1.</summary>
    /// <param name="s">The string.</param>
    /// <param name="set">The characters.</param>
    /// <returns>The index or -1.</returns>
    public static int IndexOfAny(string s, string set) => s.IndexOfAny(set.ToCharArray());

    /// <summary>
    /// <c>atoi</c>: the leading decimal number of the string (optional white space and sign), 0 when there is none; the one definition is <see cref="Core.Values.Atoi(string)"/>
    /// (a number that does not fit a long saturates like <c>strtol</c> of glibc).
    /// </summary>
    /// <param name="s">The string.</param>
    /// <returns>The integer.</returns>
    public static int Atoi(string s) => Core.Values.Atoi(s);
}
