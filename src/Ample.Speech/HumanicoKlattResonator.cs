namespace Ample.Speech;

public readonly record struct HumanicoResonatorCoefficients(double A, double B, double C)
{
    public static HumanicoResonatorCoefficients Create(double frequencyHz, double bandwidthHz, int sampleRate)
    {
        if (!double.IsFinite(frequencyHz) || frequencyHz < 0 || !double.IsFinite(bandwidthHz) || bandwidthHz <= 0 || sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        double radius = Math.Exp(-Math.PI * bandwidthHz / sampleRate);
        double c = -(radius * radius);
        double b = 2 * radius * Math.Cos(2 * Math.PI * frequencyHz / sampleRate);
        return new(1 - b - c, b, c);
    }
}

/// <summary>Stateful Klatt 1980 two-pole resonator primitive at the Humanico 10 kHz clock.</summary>
public sealed class HumanicoKlattResonator
{
    private double _previous;
    private double _beforePrevious;

    public double Process(double input, HumanicoResonatorCoefficients coefficients)
    {
        double output = coefficients.A * input + coefficients.B * _previous + coefficients.C * _beforePrevious;
        _beforePrevious = _previous;
        _previous = output;
        return output;
    }
}

/// <summary>Inverse of the Klatt two-pole resonator, for the nasal spectral zero.</summary>
public sealed class HumanicoKlattAntiresonator
{
    private double _previous;
    private double _beforePrevious;

    public double Process(double input, HumanicoResonatorCoefficients coefficients)
    {
        if (coefficients.A == 0 || !double.IsFinite(coefficients.A))
            throw new ArgumentOutOfRangeException(nameof(coefficients));
        double output = (input - coefficients.B * _previous - coefficients.C * _beforePrevious)
            / coefficients.A;
        _beforePrevious = _previous;
        _previous = input;
        return output;
    }
}

/// <summary>Three-formant cascade state used by the later audible Humanico kernel.</summary>
public sealed class HumanicoKlattCascade
{
    private readonly HumanicoKlattResonator _f1 = new();
    private readonly HumanicoKlattResonator _f2 = new();
    private readonly HumanicoKlattResonator _f3 = new();

    public double Process(double source, SpeechFrameParameters parameters, int sampleRate = 10_000)
    {
        if (!parameters.Voiced) return source;
        double first = _f1.Process(source, HumanicoResonatorCoefficients.Create(parameters.F1Hz, 60, sampleRate));
        double second = _f2.Process(first, HumanicoResonatorCoefficients.Create(parameters.F2Hz, 90, sampleRate));
        return _f3.Process(second, HumanicoResonatorCoefficients.Create(parameters.F3Hz, 150, sampleRate));
    }
}
