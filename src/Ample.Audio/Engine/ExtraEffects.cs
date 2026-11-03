using System.Globalization;
using System.Numerics;

namespace Ample.Audio.Engine;

/// <summary>
/// The newer audio blocks: normalize, reverse, tremolo (and autopan), denoise, convolution, granular, ambience and meter. They work on doubles (an FFT is
/// involved in some), so their determinism claim is machine-exact for those: the same machine gives the same bytes.
/// </summary>
public static class ExtraEffects
{
    /// <summary>Gets the renderers of the newer audio blocks.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } =
    [
        new AudioRateRenderer(new ReverseRenderer()),
        new AudioRateRenderer(new NormalizeRenderer()),
        new AudioRateRenderer(new MeterRenderer()),
        new AudioRateRenderer(new TremoloRenderer()),
        new AudioRateRenderer(new PeqRenderer()),
        new AmbienceRenderer(),
        new AudioRateRenderer(new GranularRenderer()),
        new AudioRateRenderer(new DenoiseRenderer()),
        new AudioRateRenderer(new ConvolutionRenderer()),
        new AudioRateRenderer(new RewindRenderer()),
        new AudioRateRenderer(new AutotuneRenderer()),
        new AudioRateRenderer(new VocoderRenderer()),
        new AudioRateRenderer(new ChopRenderer())
    ];

    /// <summary>The block types of <see cref="All"/>.</summary>
    internal static readonly HashSet<string> Types = new(["reverse", "normalize", "meter", "tremolo", "peq", "ambience", "granular", "denoise", "convolution", "rewind", "autotune", "vocoder", "chop"], StringComparer.Ordinal);
}

internal static class Dsp
{
    internal static int NextPow2(int n)
    {
        int p = 1;
        while (p < n)
        {
            p <<= 1;
        }

        return p;
    }

    /// <summary>In-place radix-2 FFT (the inverse one is scaled by 1/n).</summary>
    internal static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            Complex step = new(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k], v = a[i + k + (len / 2)] * w;
                    a[i + k] = u + v;
                    a[i + k + (len / 2)] = u - v;
                    w *= step;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n; i++)
            {
                a[i] /= n;
            }
        }
    }

    internal static double[] ToDouble(short[] s)
    {
        double[] d = new double[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            d[i] = s[i];
        }

        return d;
    }

    internal static short[] ToShort(double[] d)
    {
        short[] s = new short[d.Length];
        for (int i = 0; i < d.Length; i++)
        {
            s[i] = FixedAudio.Clip((long)Math.Round(d[i]));
        }

        return s;
    }

    internal static double DbToLinear(double db) => Math.Pow(10, db / 20);
}

internal abstract class ExtraEffect : IBlockRenderer
{
    public abstract string BlockType { get; }

    public string Id => "builtin." + BlockType;

    public abstract string Version { get; }

    public virtual string Determinism => "machine-exact";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        if (r.Template.Parameters.ContainsKey("enabled") && Text(r, "enabled") == "off")
        {
            byte[] same = r.Inputs.TryGetValue("in", out byte[]? input) ? input : Wav.Encode(new short[r.BarCount * r.SamplesPerBar], r.SampleRate);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", same), new RenderOutput("audio", same)]);
        }

        return ValueTask.FromResult(Process(r, cancellationToken));
    }

    protected abstract IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken);

    protected static string Text(RenderRequest r, string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;

    /// <summary>Reads a number that may be followed by a unit word (dB, %, s, ms, Hz, LUFS).</summary>
    protected static double Num(RenderRequest r, string name, double fallback)
    {
        string t = Text(r, name);
        int end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] is '.' || (end == 0 && t[end] is '-' or '+')))
        {
            end++;
        }

        return double.TryParse(t[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }

    protected static (short[] Left, short[] Right, int Rate, bool Stereo) Input(RenderRequest r)
    {
        if (!r.Inputs.TryGetValue("in", out byte[]? input))
        {
            short[] silence = new short[r.BarCount * r.SamplesPerBar];
            return (silence, silence, r.SampleRate, false);
        }

        (StereoBuffer channels, int rate) = ChannelAudio.Decode(input);
        bool stereo = ChannelAudio.IsStereo(input);
        return (channels.Left, stereo ? channels.Right : channels.Left, rate, stereo);
    }

    protected static byte[] Encode(short[] left, short[] right, int rate, bool stereo) => stereo ? StereoWav.Encode(new StereoBuffer(left, right), rate) : Wav.Encode(left, rate);

    protected static IReadOnlyList<RenderOutput> Out(byte[] audio) => [new RenderOutput("out", audio), new RenderOutput("audio", audio)];
}

