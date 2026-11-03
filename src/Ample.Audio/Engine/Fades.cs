using System.Globalization;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>The shape of a fade: how the amount (0 to 1) follows the position in the fade.</summary>
public enum FadeShape
{
    /// <summary>A straight line in amplitude.</summary>
    Linear,

    /// <summary>Equal power: a fade in and a fade out of the same length add up to constant power for unrelated sounds.</summary>
    Equal,

    /// <summary>Starts slowly and ends fast (the square of the position).</summary>
    Exp,
}

/// <summary>A start or a length of a fade, in millionths of a bar or of a second.</summary>
/// <param name="Micro">The value in millionths.</param>
/// <param name="Seconds">True for seconds, false for bars.</param>
public readonly record struct FadePosition(long Micro, bool Seconds);

/// <summary>A parsed fade: fade in or out, where it starts, how long it takes and its shape.</summary>
/// <param name="In">True for a fade in, false for a fade out.</param>
/// <param name="Start">The start (a bar position where 1 is the first bar, or seconds from the beginning of the song).</param>
/// <param name="Length">The length in bars or seconds.</param>
/// <param name="Shape">The shape.</param>
public sealed record FadeSpec(bool In, FadePosition Start, FadePosition Length, FadeShape Shape);

/// <summary>
/// The fade engine (plan fades F01): <c>fadein(start, length [, shape])</c> and <c>fadeout(...)</c> as the value of a volume gain or an effect mix, and the
/// <c>fade</c> and <c>crossfade</c> blocks, which build the same text and use the same code. Positions are bars (5, 5.5) or seconds (12s, 0.5s). All arithmetic
/// is integer (the equal power shape uses an integer square root), so every machine gets the same bytes.
/// </summary>
public static partial class Fades
{
    private const int One = 32768;

    /// <summary>Gets whether the text is a fade expression.</summary>
    public static bool Is(string text) => text.TrimStart().StartsWith("fadein(", StringComparison.Ordinal) || text.TrimStart().StartsWith("fadeout(", StringComparison.Ordinal);

    /// <summary>Reads a fade expression.</summary>
    /// <exception cref="AmpleException">AMP5371 when it is malformed.</exception>
    public static FadeSpec Parse(string text)
    {
        string t = text.Trim();
        bool fadeIn = t.StartsWith("fadein(", StringComparison.Ordinal);
        if ((!fadeIn && !t.StartsWith("fadeout(", StringComparison.Ordinal)) || !t.EndsWith(')')) throw Bad(text);
        string[] parts = t[(fadeIn ? 7 : 8)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3) throw Bad(text);
        FadePosition start = Position(parts[0], text), length = Position(parts[1], text);
        if (length.Micro <= 0 || (!start.Seconds && start.Micro < 1_000_000)) throw Bad(text);
        FadeShape shape = FadeShape.Linear;
        if (parts.Length == 3)
        {
            shape = parts[2].ToLowerInvariant() switch { "lin" or "linear" => FadeShape.Linear, "equal" => FadeShape.Equal, "exp" => FadeShape.Exp, _ => throw Bad(text) };
        }

        return new FadeSpec(fadeIn, start, length, shape);
    }

    /// <summary>The expression text of a fade, from the parameters of the <c>fade</c> and <c>crossfade</c> blocks.</summary>
    public static string Expression(bool fadeIn, string start, string length, string shape) => $"{(fadeIn ? "fadein" : "fadeout")}({start.Trim()}, {length.Trim()}, {shape.Trim()})";

    /// <summary>The amount (0 to 32768, Q15) of the fade at every sample of a block that starts at bar <paramref name="firstBar"/> and is <paramref name="length"/> samples long.</summary>
    public static int[] Amount(FadeSpec spec, int sampleRate, int samplesPerBar, int firstBar, int length)
    {
        long start = spec.Start.Seconds ? spec.Start.Micro * sampleRate / 1_000_000 : (spec.Start.Micro - 1_000_000) * samplesPerBar / 1_000_000;
        long span = Math.Max(1, spec.Length.Seconds ? spec.Length.Micro * sampleRate / 1_000_000 : spec.Length.Micro * samplesPerBar / 1_000_000);
        long end = start + span;
        long origin = (long)(firstBar - 1) * samplesPerBar;
        int[] amount = new int[length];
        for (int i = 0; i < length; i++)
        {
            long at = origin + i;
            int p = at <= start ? 0 : at >= end ? One : (int)((at - start) * One / span);
            amount[i] = spec.In ? ShapeOf(spec.Shape, p) : ShapeOf(spec.Shape, One - p);
        }

        return amount;
    }

    /// <summary>The amount (Q15) of a shape for a position p (Q15) in the fade: 0 gives 0 and 32768 gives 32768, exactly.</summary>
    public static int ShapeOf(FadeShape shape, int p) => shape switch
    {
        FadeShape.Equal => (int)ISqrt((long)p << 15),
        FadeShape.Exp => (int)((long)p * p >> 15),
        _ => p,
    };

    private static FadePosition Position(string text, string whole)
    {
        Match m = PositionPattern().Match(text);
        if (!m.Success) throw Bad(whole);
        long micro = long.Parse(m.Groups["whole"].Value, NumberStyles.None, CultureInfo.InvariantCulture) * 1_000_000;
        if (m.Groups["frac"].Success) micro += long.Parse(m.Groups["frac"].Value.PadRight(6, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        return new FadePosition(micro, m.Groups["s"].Success);
    }

    private static AmpleException Bad(string text) => new("AMP5371", $"The fade '{text}' must look like fadein(5, 2) or fadeout(12s, 1.5s, equal): a start and a length in bars (5 or 5.5) or in seconds (12s), and optionally the shape lin, equal or exp. fadein and fadeout work on a volume gain and on an effect mix.");

    private static long ISqrt(long value)
    {
        long x = 0;
        for (long bit = 1L << 30; bit > 0; bit >>= 1)
        {
            if ((x + bit) * (x + bit) <= value) x += bit;
        }

        return x;
    }

    [GeneratedRegex(@"^(?<whole>\d{1,6})(?:\.(?<frac>\d{1,6}))?\s*(?<s>s)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PositionPattern();
}

/// <summary>The <c>fade</c> block: one audio input faded in or out with the fade engine.</summary>
internal sealed class FadeRenderer : IBlockRenderer
{
    public string BlockType => "fade";
    public string Id => "builtin.fade";
    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        string Value(string name) => request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? request.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        byte[] input = request.Inputs.TryGetValue("in", out byte[]? connected) ? connected : Wav.Encode(new short[request.BarCount * request.SamplesPerBar], request.SampleRate);
        (StereoBuffer channels, int rate) = ChannelAudio.Decode(input);
        if (Value("enabled") != "off")
        {
            FadeSpec spec = Fades.Parse(Fades.Expression(Value("direction") == "in", Value("start"), Value("length"), Value("shape")));
            int[] amount = Fades.Amount(spec, request.SampleRate, request.SamplesPerBar, request.FirstBar, channels.Length);
            for (int i = 0; i < channels.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                channels.Left[i] = FixedAudio.Clip((long)channels.Left[i] * amount[i] / 32768);
                channels.Right[i] = FixedAudio.Clip((long)channels.Right[i] * amount[i] / 32768);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(channels, rate))]);
    }
}

/// <summary>The <c>crossfade</c> block: input a fades out while input b fades in, over the same stretch.</summary>
internal sealed class CrossfadeRenderer : IBlockRenderer
{
    public string BlockType => "crossfade";
    public string Id => "builtin.crossfade";
    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        string Value(string name) => request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? request.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        int rate = request.SampleRate;
        StereoBuffer Read(string port)
        {
            if (!request.Inputs.TryGetValue(port, out byte[]? bytes)) return new StereoBuffer([], []);
            (StereoBuffer buffer, int found) = ChannelAudio.Decode(bytes);
            rate = found;
            return buffer;
        }

        StereoBuffer a = Read("a"), b = Read("b");
        int length = Math.Max(Math.Max(a.Length, b.Length), request.BarCount * request.SamplesPerBar);
        string start = Value("start"), span = Value("length"), shape = Value("shape");
        int[] outAmount = Fades.Amount(Fades.Parse(Fades.Expression(false, start, span, shape)), request.SampleRate, request.SamplesPerBar, request.FirstBar, length);
        int[] inAmount = Fades.Amount(Fades.Parse(Fades.Expression(true, start, span, shape)), request.SampleRate, request.SamplesPerBar, request.FirstBar, length);
        short[] left = new short[length], right = new short[length];
        for (int i = 0; i < length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            long l = (i < a.Length ? (long)a.Left[i] * outAmount[i] : 0) + (i < b.Length ? (long)b.Left[i] * inAmount[i] : 0);
            long r = (i < a.Length ? (long)a.Right[i] * outAmount[i] : 0) + (i < b.Length ? (long)b.Right[i] * inAmount[i] : 0);
            left[i] = FixedAudio.Clip(l / 32768);
            right[i] = FixedAudio.Clip(r / 32768);
        }

        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer(left, right), rate))]);
    }
}
