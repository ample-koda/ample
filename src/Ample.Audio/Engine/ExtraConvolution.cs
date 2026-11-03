using System.Numerics;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>Convolution reverb: the sound is convolved with an impulse response (a WAV file of the project), so a real room, plate or street can be put around it.</summary>
internal sealed class ConvolutionRenderer : ExtraEffect
{
    public override string BlockType => "convolution";

    public override string Version => "1";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        string value = Text(r, "file");
        string preset = Text(r, "ir");
        string path = ImportAudioRenderer.FileOf(r.ProjectRoot, r.Block, r.AmpleRoot)
            ?? throw new AmpleException("AMP5350", preset is { Length: > 0 } and not "file"
                ? $"The impulse response '{preset}' is not installed: run 'node tools/make-impulse-responses.mjs' to make the impulse-responses pack."
                : string.IsNullOrEmpty(value) ? "The convolution block has no impulse response: choose a WAV file of the project, or one of the ready-made ones." : $"The impulse response '{value}' is not in the project.");
        short[] impulse;
        try
        {
            impulse = SampleSet.ReadWav(AudioFile.ReadWav(path), rate);
        }
        catch (InvalidDataException ex)
        {
            throw new AmpleException("AMP5350", $"The impulse response '{value}' cannot be used: {ex.Message}");
        }

        int maxLength = (int)(Math.Clamp(Num(r, "length", 3), 0.05, 20) * rate);
        double[] ir = Dsp.ToDouble(impulse.Length > maxLength ? impulse[..maxLength] : impulse);
        if (ir.Length == 0)
        {
            return Out(Encode(left, right, rate, stereo));
        }

        for (int i = 0; i < ir.Length; i++)
        {
            ir[i] /= 32768;   // the response on a scale of -1 to +1
        }

        if (Text(r, "normalize") != "off")
        {
            double energy = Math.Sqrt(ir.Sum(v => v * v));
            if (energy > 0)
            {
                for (int i = 0; i < ir.Length; i++)
                {
                    ir[i] /= energy;   // unit energy: a response of any size or length gives about the same wet level
                }
            }
        }

        double mix = Math.Clamp(Num(r, "mix", 30), 0, 100) / 100;
        double level = Dsp.DbToLinear(Num(r, "level", 0));
        int pre = (int)(Math.Clamp(Num(r, "predelay", 0), 0, 500) * rate / 1000);
        short[] l = Blend(left, Convolve(left, ir, cancellationToken), mix, level, pre);
        short[] rr = stereo ? Blend(right, Convolve(right, ir, cancellationToken), mix, level, pre) : l;
        return Out(Encode(l, rr, rate, stereo));
    }

    private static short[] Blend(short[] dry, double[] wet, double mix, double level, int predelay)
    {
        short[] y = new short[dry.Length];
        for (int i = 0; i < y.Length; i++)
        {
            int w = i - predelay;
            double wetSample = w >= 0 && w < wet.Length ? wet[w] * level : 0;
            y[i] = FixedAudio.Clip((long)Math.Round((dry[i] * (1 - mix)) + (wetSample * mix)));
        }

        return y;
    }

    /// <summary>Overlap-add FFT convolution; the result has the length of the input (the tail is cut at the end of the block).</summary>
    private static double[] Convolve(short[] x, double[] ir, CancellationToken token)
    {
        int block = Dsp.NextPow2(Math.Max(ir.Length, 4096));
        int size = block * 2;
        Complex[] filter = new Complex[size];
        for (int i = 0; i < ir.Length; i++)
        {
            filter[i] = ir[i];
        }

        Dsp.Fft(filter, false);
        double[] y = new double[x.Length];
        Complex[] buffer = new Complex[size];
        for (int start = 0; start < x.Length; start += block)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(buffer);
            int count = Math.Min(block, x.Length - start);
            for (int i = 0; i < count; i++)
            {
                buffer[i] = x[start + i];
            }

            Dsp.Fft(buffer, false);
            for (int i = 0; i < size; i++)
            {
                buffer[i] *= filter[i];
            }

            Dsp.Fft(buffer, true);
            for (int i = 0; i < size && start + i < x.Length; i++)
            {
                y[start + i] += buffer[i].Real;
            }
        }

        return y;
    }
}
