using System.Text;

namespace Ample.Audio.Instruments;

/// <summary>Reads the supported subset of SFZ into regions. Pure: no audio is loaded, no floating point is used.</summary>
internal static class SfzReader
{
    private const int MaxIncludeDepth = 16;
    private const int MaxRegions = 100_000;
    private const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>Reads a file; #include paths resolve against the including file's folder.</summary>
    public static SfzInstrument Read(string path)
    {
        if (!File.Exists(path)) throw new SfzFormatException("AMP5301: SFZ file not found");
        return Parse(LoadText(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", LoadIncludeFile);
    }

    private static string LoadText(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new SfzFormatException("AMP5301: SFZ file too large");
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException) { throw new SfzFormatException("AMP5301: SFZ file unreadable"); }
        catch (UnauthorizedAccessException) { throw new SfzFormatException("AMP5301: SFZ file unreadable"); }
    }

    private static string? LoadIncludeFile(string fullPath)
    {
        try { return File.Exists(fullPath) && new FileInfo(fullPath).Length <= MaxFileBytes ? File.ReadAllText(fullPath, Encoding.UTF8) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Parses text. <paramref name="loadInclude"/> maps a full include path to its text (or null when missing).</summary>
    public static SfzInstrument Parse(string text, string baseDir, Func<string, string?>? loadInclude = null)
    {
        var result = new SfzInstrument();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Warn(string code, string msg) { if (seen.Add(code + msg)) result.Warnings.Add(new SfzWarning(code, msg)); }

        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = new List<(string Header, List<(string Name, string Value)> Ops)>();
        var budget = new int[] { 64 };
        Tokenize(text, baseDir, loadInclude, defines, tokens, 0, Warn, budget);

        var global = new List<(string, string)>();
        var master = new List<(string, string)>();
        var group = new List<(string, string)>();
        var control = new List<(string, string)>();
        var defaultPath = "";
        foreach (var (header, ops) in tokens)
        {
            switch (header)
            {
                case "control":
                    control.AddRange(ops);
                    foreach (var (n, v) in ops)
                        if (n == "default_path") defaultPath = v.Length == 0 || v.EndsWith('/') || v.EndsWith('\\') ? v.Replace('\\', '/') : v.Replace('\\', '/') + "/";
                        else if (n != "octave_offset" && n != "note_offset") Warn("AMP5302", $"unsupported control opcode '{n}'");
                    break;
                case "global": global = new(ops); master = new(); group = new(); break;
                case "master": master = new(ops); group = new(); break;
                case "group": group = new(ops); break;
                case "region":
                    if (result.Regions.Count >= MaxRegions) { Warn("AMP5303", "too many regions, rest ignored"); break; }
                    var all = new List<(string Name, string Value)>(global.Count + master.Count + group.Count + ops.Count);
                    all.AddRange(global); all.AddRange(master); all.AddRange(group); all.AddRange(ops);
                    var region = BuildRegion(all, defaultPath, Warn);
                    if (region == null) Warn("AMP5304", "region without sample skipped");
                    else result.Regions.Add(region);
                    break;
                default:
                    Warn("AMP5302", $"unsupported header '<{header}>'");
                    break;
            }
        }
        return result;
    }

    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                if (i < s.Length) sb.Append('\n');
            }
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 1;
                sb.Append(' ');
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static void Tokenize(string text, string baseDir, Func<string, string?>? loadInclude,
        Dictionary<string, string> defines, List<(string, List<(string, string)>)> output, int depth, Action<string, string> warn, int[] budget)
    {
        var sb = new StringBuilder();
        foreach (var rawLine in StripComments(text).Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith("#define", StringComparison.Ordinal))
            {
                var parts = line[7..].Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].StartsWith('$')) defines[parts[0]] = parts[1].Trim();
                else warn("AMP5302", "malformed #define ignored");
                continue;
            }
            if (line.StartsWith("#include", StringComparison.Ordinal))
            {
                var q1 = line.IndexOf('"', StringComparison.Ordinal);
                var q2 = q1 < 0 ? -1 : line.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0) { warn("AMP5302", "malformed #include ignored"); continue; }
                var rel = line[(q1 + 1)..q2].Replace('\\', '/');
                string? inc = null;
                if (depth < MaxIncludeDepth && budget[0]-- > 0 && loadInclude != null && !rel.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                    inc = loadInclude(Path.Combine(baseDir, rel));
                if (inc == null) { warn("AMP5305", "#include not loaded"); continue; }
                // Flush what we have so header order stays right, then recurse.
                ParseFlat(sb.ToString(), defines, output);
                sb.Clear();
                Tokenize(inc, Path.GetDirectoryName(Path.Combine(baseDir, rel)) ?? baseDir, loadInclude, defines, output, depth + 1, warn, budget);
                continue;
            }
            sb.Append(Substitute(line, defines)).Append('\n');
        }
        ParseFlat(sb.ToString(), defines, output);
    }

    private static string Substitute(string line, Dictionary<string, string> defines)
    {
        if (defines.Count == 0 || !line.Contains('$', StringComparison.Ordinal)) return line;
        // Longest names first so $A does not eat $AB.
        foreach (var kv in defines.OrderByDescending(k => k.Key.Length).ThenBy(k => k.Key, StringComparer.Ordinal))
            line = line.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
        return line;
    }

    private static void ParseFlat(string s, Dictionary<string, string> defines, List<(string, List<(string, string)>)> output)
    {
        _ = defines;
        List<(string, string)>? current = null;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '<')
            {
                var end = s.IndexOf('>', i);
                if (end < 0) return;
                current = new();
                output.Add((s[(i + 1)..end].Trim().ToLowerInvariant(), current));
                i = end + 1;
                continue;
            }
            var eq = s.IndexOf('=', i);
            if (eq < 0) return;
            var name = s[i..eq].Trim();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace)) { i = NextToken(s, i + 1); continue; }
            // Value runs until the next "word=" start or a header.
            var vstart = eq + 1;
            var j = vstart;
            var vend = s.Length;
            while (j < s.Length)
            {
                if (s[j] == '<') { vend = j; break; }
                if (char.IsWhiteSpace(s[j]))
                {
                    var k = j;
                    while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
                    if (k < s.Length && s[k] == '<') { vend = j; break; }
                    var w = k;
                    while (w < s.Length && (char.IsLetterOrDigit(s[w]) || s[w] == '_')) w++;
                    if (w > k && w < s.Length && s[w] == '=') { vend = j; break; }
                    j = k;
                    continue;
                }
                j++;
            }
            current?.Add((name.ToLowerInvariant(), s[vstart..vend].Trim()));
            i = vend;
        }
    }

    private static int NextToken(string s, int i)
    {
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int? ParseKey(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (FixedParse.TryParse(v, 1, out var n) && !v.Contains('.', StringComparison.Ordinal)) return (int)Math.Clamp(n, -1, 127);
        if (v.Length < 2) return null;
        int[] semis = [9, 11, 0, 2, 4, 5, 7];
        var idx = v[0] - 'a';
        if (idx < 0 || idx > 6) return null;
        var pos = 1;
        var acc = 0;
        if (v[pos] == '#') { acc = 1; pos++; }
        else if (v[pos] == 'b') { acc = -1; pos++; }
        if (pos >= v.Length || !int.TryParse(v[pos..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var oct)) return null;
        return Math.Clamp(((oct + 1) * 12) + semis[idx] + acc, -1, 127);
    }

    private static SfzRegion? BuildRegion(List<(string Name, string Value)> ops, string defaultPath, Action<string, string> warn)
    {
        var r = new SfzRegion();
        var hasSample = false;
        // Later opcodes override earlier (inheritance order), so apply in sequence.
        foreach (var (name, value) in ops)
        {
            bool ok = true;
            long n;
            switch (name)
            {
                case "sample": r.Sample = defaultPath + value.Replace('\\', '/'); hasSample = value.Length > 0; break;
                case "lokey": ok = SetKey(value, k => r.LoKey = k); break;
                case "hikey": ok = SetKey(value, k => r.HiKey = k); break;
                case "pitch_keycenter": ok = SetKey(value, k => r.PitchKeycenter = k); break;
                case "key": ok = SetKey(value, k => { r.LoKey = k; r.HiKey = k; r.PitchKeycenter = k; }); break;
                case "lovel": ok = Int(value, 0, 127, v => r.LoVel = v); break;
                case "hivel": ok = Int(value, 0, 127, v => r.HiVel = v); break;
                case "loop_mode":
                case "loopmode":
                    var lm = value.Trim().ToLowerInvariant();
                    ok = lm is "no_loop" or "one_shot" or "loop_continuous" or "loop_sustain";
                    if (ok) r.LoopMode = lm;
                    break;
                case "loop_start": case "loopstart": ok = Fixed(value, 1, out n); if (ok) r.LoopStart = Math.Max(n, 0); break;
                case "loop_end": case "loopend": ok = Fixed(value, 1, out n); if (ok) r.LoopEnd = Math.Max(n, 0); break;
                case "seq_length": ok = Int(value, 1, 1024, v => r.SeqLength = v); break;
                case "seq_position": ok = Int(value, 1, 1024, v => r.SeqPosition = v); break;
                case "lorand": ok = Fixed(value, 1_000_000, out n); if (ok) r.LoRand = Math.Clamp(n, 0, 1_000_000); break;
                case "hirand": ok = Fixed(value, 1_000_000, out n); if (ok) r.HiRand = Math.Clamp(n, 0, 1_000_000); break;
                case "offset": ok = Fixed(value, 1, out n); if (ok) r.Offset = Math.Max(n, 0); break;
                case "end": ok = Fixed(value, 1, out n); if (ok) r.End = n; break;
                case "tune": ok = Int(value, -100_000, 100_000, v => r.TuneCents = v); break;
                case "transpose": ok = Int(value, -127, 127, v => r.Transpose = v); break;
                case "volume": ok = Fixed(value, 100, out n); if (ok) r.VolumeCentiDb = Math.Clamp(n, -14_400, 2_400); break;
                case "ampeg_delay": ok = Fixed(value, 1_000_000, out n); if (ok) r.AmpegDelayUs = Math.Max(n, 0); break;
                case "ampeg_attack": ok = Fixed(value, 1_000_000, out n); if (ok) r.AmpegAttackUs = Math.Max(n, 0); break;
                case "ampeg_hold": ok = Fixed(value, 1_000_000, out n); if (ok) r.AmpegHoldUs = Math.Max(n, 0); break;
                case "ampeg_decay": ok = Fixed(value, 1_000_000, out n); if (ok) r.AmpegDecayUs = Math.Max(n, 0); break;
                case "ampeg_sustain": ok = Fixed(value, 1000, out n); if (ok) r.AmpegSustainMilliPercent = Math.Clamp(n, 0, 100_000); break;
                case "ampeg_release": ok = Fixed(value, 1_000_000, out n); if (ok) r.AmpegReleaseUs = Math.Max(n, 0); break;
                default:
                    r.Unknown[name] = value;
                    warn("AMP5302", $"unsupported opcode '{name}'");
                    break;
            }
            if (!ok) warn("AMP5306", $"bad value for opcode '{name}'");
        }
        return hasSample ? r : null;

        static bool Fixed(string v, long scale, out long n) => FixedParse.TryParse(v, scale, out n);
        static bool Int(string v, int min, int max, Action<int> set)
        {
            if (!FixedParse.TryParse(v, 1, out var n)) return false;
            set((int)Math.Clamp(n, min, max));
            return true;
        }
        static bool SetKey(string v, Action<int> set)
        {
            var k = ParseKey(v);
            if (k == null) return false;
            set(Math.Clamp(k.Value, 0, 127));
            return true;
        }
    }
}
