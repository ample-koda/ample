using System.Globalization;
using System.Text;

namespace Ample.Server;

/// <summary>Line based comparison of two texts: a unified diff to show, and a three-way merge to bring a proposal up to date with a project that changed in the meantime.</summary>
internal static class AiDiff
{
    /// <summary>The lines of a text (a final line break does not make an extra empty line).</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>The pairs of equal lines (index in a, index in b) that form a longest common subsequence (Myers).</summary>
    public static List<(int A, int B)> Matches(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        List<(int A, int B)> result = [];
        if (max == 0)
        {
            return result;
        }

        int offset = max + 1;
        int[] v = new int[(2 * max) + 3];
        List<int[]> trace = [];
        for (int d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(a, b, trace, d, offset);
                }
            }
        }

        return result;
    }

    private static List<(int A, int B)> Backtrack(string[] a, string[] b, List<int[]> trace, int dEnd, int offset)
    {
        List<(int A, int B)> matches = [];
        int x = a.Length, y = b.Length;
        for (int d = dEnd; d > 0; d--)
        {
            int[] v = trace[d];
            int k = x - y;
            int prevK = (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) ? k + 1 : k - 1;
            int prevX = v[offset + prevK], prevY = prevX - prevK;
            while (x > prevX && y > prevY && x > 0 && y > 0 && a[x - 1] == b[y - 1])
            {
                x--;
                y--;
                matches.Add((x, y));
            }

            x = prevX;
            y = prevY;
        }

        while (x > 0 && y > 0 && a[x - 1] == b[y - 1])
        {
            x--;
            y--;
            matches.Add((x, y));
        }

        matches.Reverse();
        return matches;
    }

    /// <summary>A unified diff of two texts with three lines of context; empty when they are equal.</summary>
    public static string Unified(string before, string after, string name)
    {
        string[] a = Lines(before), b = Lines(after);
        if (a.SequenceEqual(b))
        {
            return string.Empty;
        }

        List<(int A, int B)> match = Matches(a, b);
        List<(char Kind, string Text)> script = [];
        int i = 0, j = 0;
        foreach ((int ma, int mb) in match)
        {
            while (i < ma)
            {
                script.Add(('-', a[i++]));
            }

            while (j < mb)
            {
                script.Add(('+', b[j++]));
            }

            script.Add((' ', a[ma]));
            i = ma + 1;
            j = mb + 1;
        }

        while (i < a.Length)
        {
            script.Add(('-', a[i++]));
        }

        while (j < b.Length)
        {
            script.Add(('+', b[j++]));
        }

        const int context = 3;
        bool[] show = new bool[script.Count];
        for (int s = 0; s < script.Count; s++)
        {
            if (script[s].Kind != ' ')
            {
                for (int c = Math.Max(0, s - context); c <= Math.Min(script.Count - 1, s + context); c++)
                {
                    show[c] = true;
                }
            }
        }

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"--- a/{name}\n+++ b/{name}\n");
        int line = 1;
        int lineA = 1, lineB = 1;
        for (int s = 0; s < script.Count; s++)
        {
            if (!show[s])
            {
                (char kind, _) = script[s];
                if (kind != '+')
                {
                    lineA++;
                }

                if (kind != '-')
                {
                    lineB++;
                }

                continue;
            }

            if (s == 0 || !show[s - 1])
            {
                int end = s;
                int countA = 0, countB = 0;
                while (end < script.Count && show[end])
                {
                    if (script[end].Kind != '+')
                    {
                        countA++;
                    }

                    if (script[end].Kind != '-')
                    {
                        countB++;
                    }

                    end++;
                }

                text.Append(CultureInfo.InvariantCulture, $"@@ -{lineA},{countA} +{lineB},{countB} @@\n");
            }

            text.Append(script[s].Kind).Append(script[s].Text).Append('\n');
            if (script[s].Kind != '+')
            {
                lineA++;
            }

            if (script[s].Kind != '-')
            {
                lineB++;
            }

            line++;
        }

        _ = line;
        return text.ToString();
    }

    /// <summary>How many lines were added and removed.</summary>
    public static (int Added, int Removed) Count(string before, string after)
    {
        string[] a = Lines(before), b = Lines(after);
        int common = Matches(a, b).Count;
        return (b.Length - common, a.Length - common);
    }

    /// <summary>One stretch of a three-way merge: lines both sides agree on (or that merged by themselves), or a conflict with the three versions of the lines.</summary>
    public sealed record MergeRegion(bool Conflict, string[] Common, string[] Base, string[] Mine, string[] Theirs);

    /// <summary>Three-way merge as regions: what merges by itself is folded into common stretches, what both sides changed differently is a conflict region to choose from.</summary>
    public static List<MergeRegion> Regions(string baseText, string mine, string theirs)
    {
        string[] o = Lines(baseText), a = Lines(mine), b = Lines(theirs);
        Dictionary<int, int> oa = Matches(o, a).ToDictionary(x => x.A, x => x.B), ob = Matches(o, b).ToDictionary(x => x.A, x => x.B);
        List<(int O, int A, int B)> sync = [.. oa.Where(x => ob.ContainsKey(x.Key)).OrderBy(x => x.Key).Select(x => (x.Key, x.Value, ob[x.Key]))];
        sync.Add((o.Length, a.Length, b.Length));
        List<MergeRegion> regions = [];
        List<string> common = [];
        void Flush()
        {
            if (common.Count > 0)
            {
                regions.Add(new MergeRegion(false, [.. common], [], [], []));
                common.Clear();
            }
        }

        int po = 0, pa = 0, pb = 0;
        foreach ((int io, int ia, int ib) in sync)
        {
            string[] so = o[po..io], sa = a[pa..ia], sb = b[pb..ib];
            if (sa.SequenceEqual(so))
            {
                common.AddRange(sb);
            }
            else if (sb.SequenceEqual(so) || sa.SequenceEqual(sb))
            {
                common.AddRange(sa);
            }
            else
            {
                Flush();
                regions.Add(new MergeRegion(true, [], so, sa, sb));
            }

            if (io < o.Length)
            {
                common.Add(o[io]);
            }

            po = io + 1;
            pa = ia + 1;
            pb = ib + 1;
        }

        Flush();
        return regions;
    }

    /// <summary>Three-way merge of two changes of the same base. Returns the merged text, or null with the number of conflicting regions.</summary>
    public static string? Merge(string baseText, string ours, string theirs, out int conflicts)
    {
        conflicts = 0;
        if (ours == baseText)
        {
            return theirs;
        }

        if (theirs == baseText || ours == theirs)
        {
            return ours;
        }

        string[] o = Lines(baseText), a = Lines(ours), b = Lines(theirs);
        Dictionary<int, int> oa = Matches(o, a).ToDictionary(x => x.A, x => x.B), ob = Matches(o, b).ToDictionary(x => x.A, x => x.B);
        List<(int O, int A, int B)> sync = [.. oa.Where(x => ob.ContainsKey(x.Key)).OrderBy(x => x.Key).Select(x => (x.Key, x.Value, ob[x.Key]))];
        sync.Add((o.Length, a.Length, b.Length));
        List<string> merged = [];
        int po = 0, pa = 0, pb = 0;
        foreach ((int io, int ia, int ib) in sync)
        {
            string[] so = o[po..io], sa = a[pa..ia], sb = b[pb..ib];
            if (sa.SequenceEqual(so))
            {
                merged.AddRange(sb);
            }
            else if (sb.SequenceEqual(so) || sa.SequenceEqual(sb))
            {
                merged.AddRange(sa);
            }
            else
            {
                conflicts++;
            }

            if (io < o.Length)
            {
                merged.Add(o[io]);
            }

            po = io + 1;
            pa = ia + 1;
            pb = ib + 1;
        }

        return conflicts > 0 ? null : string.Join('\n', merged) + (merged.Count > 0 ? "\n" : string.Empty);
    }
}
