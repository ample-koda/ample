using System.Globalization;
using Ample.Speech.Flite.Core;

namespace Ample.Speech.Tests.Flite;

/// <summary>
/// Reads the golden files that the reference build of Flite wrote (plan/speech/flite/reference/m2): the six English trees with the US phoneset,
/// and the log of every tree call while the corpus was synthesised. The values are written as <c>i:3</c>, <c>f:0.5</c>, <c>s:text</c>, <c>N</c> (null).
/// </summary>
internal static class FliteReferenceFixtures
{
    public sealed record TreeCall(string Tree, List<(string Path, Value? Value)> Asked, Value? Answer);

    public sealed record RegexProbe(string Text, bool Digits, bool Double);

    public sealed record Golden(Dictionary<string, Cart> Trees, Phoneset Phoneset, List<RegexProbe> Probes);

    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static Value? ParseValue(string text)
    {
        if (text == "N")
        {
            return null;
        }

        if (text.StartsWith("i:", StringComparison.Ordinal))
        {
            return new IntValue(int.Parse(text.AsSpan(2), CultureInfo.InvariantCulture));
        }

        if (text.StartsWith("f:", StringComparison.Ordinal))
        {
            return new FloatValue(float.Parse(text.AsSpan(2), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        if (text.StartsWith("s:", StringComparison.Ordinal))
        {
            return new StringValue(Unescape(text[2..]));
        }

        throw new FormatException("unsupported value in the fixture: " + text);
    }

    // the call log writes a backslash, a tab and a newline of a string as \\, \t and \n
    private static string Unescape(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal))
        {
            return s;
        }

        System.Text.StringBuilder sb = new();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 't' => '\t', 'n' => '\n', _ => s[i] });
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    public static Golden LoadCarts()
    {
        Dictionary<string, Cart> trees = [];
        List<RegexProbe> probes = [];
        string? tree = null;
        List<string> feats = [];
        List<CartNode> nodes = [];
        string psName = string.Empty, psSilence = string.Empty;
        List<string> psFeats = [], psPhones = [];
        List<Value> psVals = [];
        List<int[]> psTable = [];

        void Finish()
        {
            if (tree is not null)
            {
                trees[tree] = new Cart([.. nodes], [.. feats]);
            }

            feats = [];
            nodes = [];
        }

        foreach (string line in File.ReadLines(Path("flite-m2-carts.txt")))
        {
            string[] f = line.Split('\t');
            switch (f[0])
            {
                case "#TREE":
                    Finish();
                    tree = f[1];
                    break;
                case "#FEAT":
                    feats.Add(f[2]);
                    break;
                case "#NODE":
                    nodes.Add(new CartNode(byte.Parse(f[2], CultureInfo.InvariantCulture), byte.Parse(f[3], CultureInfo.InvariantCulture), ushort.Parse(f[4], CultureInfo.InvariantCulture), ParseValue(f[5])));
                    break;
                case "#PS":
                    Finish();
                    tree = null;
                    psName = f[1];
                    psSilence = f[2];
                    break;
                case "#PSFEAT":
                    psFeats.Add(f[2]);
                    break;
                case "#PSVAL":
                    psVals.Add(ParseValue(f[2])!);
                    break;
                case "#PHONE":
                    psPhones.Add(f[2]);
                    psTable.Add([.. f.Skip(3).Select(x => int.Parse(x, CultureInfo.InvariantCulture))]);
                    break;
                case "#RX":
                    probes.Add(new RegexProbe(f[1], f[2] == "1", f[3] == "1"));
                    break;
                default:
                    break;
            }
        }

        Finish();
        return new Golden(trees, new Phoneset(psName, [.. psFeats], [.. psVals], [.. psPhones], psSilence, [.. psTable]), probes);
    }

    public static IEnumerable<TreeCall> LoadCalls()
    {
        foreach (string line in File.ReadLines(Path("flite-m2-calls.txt")))
        {
            string[] f = line.Split('	');
            List<(string Path, Value? Value)> asked = [];
            int i = 1;
            while (f[i] != "=>")
            {
                asked.Add((f[i], ParseValue(f[i + 1])));
                i += 2;
            }

            yield return new TreeCall(f[0], asked, ParseValue(f[i + 1]));
        }
    }
}
