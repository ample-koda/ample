namespace Ample.Speech;

/// <summary>Parameters of the pinned Humanico five-formant cascade and parallel voice.</summary>
public sealed record HumanicoKlattParameters
{
    public double F0 { get; init; } = 100;
    public double Voicing { get; init; }
    public double Aspiration { get; init; }
    public double Frication { get; init; }
    public double Bypass { get; init; }
    public double NasalPole { get; init; } = 270;
    public double NasalPoleBandwidth { get; init; } = 270;
    public double NasalZero { get; init; } = 270;
    public double NasalZeroBandwidth { get; init; } = 270;
    public double GlottalBandwidth { get; init; } = 1200;
    public double GlottalBandwidth2 { get; init; } = 100000;
    public double[] Frequencies { get; init; } = [500, 1500, 2500, 3900, 4500, 4800];
    public double[] CascadeBandwidths { get; init; } = [60, 90, 150, 250, 300];
    public double[] ParallelBandwidths { get; init; } = [250, 300, 400, 500, 600];
    public double[] ParallelAmplitudes { get; init; } = [0, 0, 0, 0, 0];
}

/// <summary>Stateful Humanico voice kernel; supplied noise makes reference comparison reproducible.</summary>
public sealed class HumanicoKlattVoice
{
    private const int SampleRate = 10_000;
    private readonly HumanicoKlattResonator _glottal = new();
    private readonly HumanicoKlattResonator _glottal2 = new();
    private readonly HumanicoKlattResonator _nasalPole = new();
    private readonly HumanicoKlattAntiresonator _nasalZero = new();
    private readonly HumanicoKlattResonator[] _cascade = Enumerable.Range(0, 5)
        .Select(_ => new HumanicoKlattResonator()).ToArray();
    private readonly HumanicoKlattResonator[] _parallel = Enumerable.Range(0, 5)
        .Select(_ => new HumanicoKlattResonator()).ToArray();
    private double _periodPosition = 1;
    private double _previousOutput;

    public double[] RenderFrame(HumanicoKlattParameters p, ReadOnlySpan<double> noise, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.Frequencies.Length != 6 || p.CascadeBandwidths.Length != 5
            || p.ParallelBandwidths.Length != 5 || p.ParallelAmplitudes.Length != 5)
            throw new ArgumentException("Invalid Humanico voice parameter dimensions.", nameof(p));
        token.ThrowIfCancellationRequested();
        HumanicoResonatorCoefficients glottal = HumanicoResonatorCoefficients.Create(0, p.GlottalBandwidth, SampleRate);
        HumanicoResonatorCoefficients glottal2 = HumanicoResonatorCoefficients.Create(0, p.GlottalBandwidth2, SampleRate);
        HumanicoResonatorCoefficients pole = HumanicoResonatorCoefficients.Create(p.NasalPole, p.NasalPoleBandwidth, SampleRate);
        HumanicoResonatorCoefficients zero = HumanicoResonatorCoefficients.Create(p.NasalZero, p.NasalZeroBandwidth, SampleRate);
        HumanicoResonatorCoefficients[] cascade = new HumanicoResonatorCoefficients[5];
        HumanicoResonatorCoefficients[] parallel = new HumanicoResonatorCoefficients[5];
        for (int i = 0; i < 5; i++)
        {
            cascade[i] = HumanicoResonatorCoefficients.Create(p.Frequencies[i], p.CascadeBandwidths[i], SampleRate);
            parallel[i] = HumanicoResonatorCoefficients.Create(p.Frequencies[i + 1], p.ParallelBandwidths[i], SampleRate);
        }
        double[] output = new double[noise.Length];
        for (int i = 0; i < noise.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            _periodPosition += 1;
            double period = SampleRate / Math.Max(p.F0, 20);
            double impulse = 0;
            if (_periodPosition >= period) { _periodPosition -= period; impulse = 1; }
            double voice = p.Voicing > 0
                ? _glottal2.Process(_glottal.Process(impulse, glottal), glottal2) * p.Voicing : 0;
            double x = _nasalZero.Process(_nasalPole.Process(voice + noise[i] * p.Aspiration, pole), zero);
            for (int j = 0; j < 5; j++) x = _cascade[j].Process(x, cascade[j]);
            double frication = noise[i] * p.Frication;
            double sum = x + p.Bypass * frication;
            for (int j = 0; j < 5; j++)
                if (p.ParallelAmplitudes[j] > 0)
                    sum += _parallel[j].Process(frication, parallel[j]) * p.ParallelAmplitudes[j];
            output[i] = sum - _previousOutput;
            _previousOutput = sum;
        }
        token.ThrowIfCancellationRequested();
        return output;
    }
}
