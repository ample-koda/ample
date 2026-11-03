using System.Globalization;

namespace Ample.Audio.Engine;

/// <summary>Tremolo (the level moves) or autopan (the sound moves between left and right), with a rate in Hz or locked to the tempo.</summary>
internal sealed class TremoloRenderer : ExtraEffect
{
    public override string BlockType => "tremolo";

    public override string Version => "1";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        bool pan = Text(r, "mode") == "autopan";
        double depth = Math.Clamp(Num(r, "depth", 50), 0, 100) / 100;
        double hz = Math.Clamp(Num(r, "rate", 4), 0.05, 40);
        string sync = Text(r, "sync");
        if (sync is { Length: > 0 } && sync != "off" && sync.Contains('/', StringComparison.Ordinal) && int.TryParse(sync.Split('/')[1], NumberStyles.None, CultureInfo.InvariantCulture, out int division) && division > 0)
        {
            double periodSamples = r.SamplesPerBar * 4.0 / r.BeatsPerBar / division;   // 1/4 is a quarter note: a whole note is four beats, whatever the meter
            hz = rate / periodSamples;
        }

        double phase0 = Num(r, "phase", 0) / 360;
        string shape = Text(r, "shape");
        string pattern = shape == "gate" ? Text(r, "pattern") : string.Empty;
        double stepLength = r.SamplesPerBar / (4.0 * r.BeatsPerBar);
        if (shape == "gate" && sync.Contains('/', StringComparison.Ordinal) && int.TryParse(sync.Split('/')[1], NumberStyles.None, CultureInfo.InvariantCulture, out int stepDivision) && stepDivision > 0) stepLength = r.SamplesPerBar / (double)stepDivision;
        long origin = (long)(r.FirstBar - 1) * r.SamplesPerBar;
        int n = left.Length;
        short[] l = new short[n];
        short[] rr = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = ((origin + i) * hz / rate) + phase0;
            double frac = t - Math.Floor(t);
            double lfo = shape == "gate" ? GateLevel(pattern, origin + i, stepLength, rate) : shape switch
            {
                "triangle" => 1 - (4 * Math.Abs(frac - 0.5)) + 0,   // -1..1, starts at its low
                "square" => frac < 0.5 ? 1 : -1,
                _ => Math.Sin(2 * Math.PI * frac),
            };
            if (shape == "triangle")
            {
                lfo = (4 * Math.Abs(frac - 0.5)) - 1;
            }

            double up = 1 - (depth * (1 - lfo) / 2);
            if (pan)
            {
                double gl = up, gr = 1 - (depth * (1 + lfo) / 2);
                l[i] = FixedAudio.Clip((long)Math.Round(left[i] * gl));
                rr[i] = FixedAudio.Clip((long)Math.Round((stereo ? right[i] : left[i]) * gr));
            }
            else
            {
                l[i] = FixedAudio.Clip((long)Math.Round(left[i] * up));
                rr[i] = FixedAudio.Clip((long)Math.Round(right[i] * up));
            }
        }

        return Out(Encode(l, rr, rate, stereo || pan));
    }

    /// <summary>How open one pattern character is, from 0 to 1: x, X and 1 are fully open (1 stays open so older patterns keep their meaning), a to i are 10 to 90 percent, anything else (like .) is closed.</summary>
    /// <param name="c">The pattern character.</param>
    /// <returns>The openness.</returns>
    internal static double GateOpenness(char c) => c is 'x' or 'X' or '1' ? 1 : c is >= 'a' and <= 'i' ? (c - 'a' + 1) / 10.0 : 0;

    /// <summary>The trance gate as a value from -1 (closed) to 1 (open) at a song position: one character of the pattern per step, a 2 ms linear fade at every change.</summary>
    /// <param name="pattern">The step pattern (x is open, a to i are levels, anything else closed).</param>
    /// <param name="position">The position in samples from the start of the song.</param>
    /// <param name="stepLength">The length of a step in samples.</param>
    /// <param name="rate">The sample rate.</param>
    internal static double GateLevel(string pattern, long position, double stepLength, int rate)
    {
        if (pattern.Length == 0) return 1;
        long step = (long)Math.Floor(position / stepLength);
        double within = position - (step * stepLength);
        double Value(long s) => (GateOpenness(pattern[(int)(((s % pattern.Length) + pattern.Length) % pattern.Length)]) * 2) - 1;
        double ramp = Math.Min(Math.Floor(rate * 0.002), stepLength / 2);
        double current = Value(step);
        return within < ramp ? Value(step - 1) + ((current - Value(step - 1)) * (within / ramp)) : current;
    }
}
