namespace Ample.Speech;

/// <summary>Deterministic impulse-train source matching the pinned Humanico glottal model's clock contract.</summary>
public sealed class HumanicoKlattSource(int sampleRate = 10_000)
{
    private double _phase;
    private double _lowpass;

    public double Process(double fundamentalHz, double amplitude, double bandwidthHz = 1_200)
    {
        if (!double.IsFinite(fundamentalHz) || fundamentalHz < 20 || !double.IsFinite(amplitude) || amplitude < 0 || !double.IsFinite(bandwidthHz) || bandwidthHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(fundamentalHz));
        double period = sampleRate / fundamentalHz;
        _phase += 1;
        double impulse = 0;
        if (_phase >= period)
        {
            _phase -= period;
            impulse = amplitude;
        }
        double coefficient = 1 - Math.Exp(-2 * Math.PI * bandwidthHz / sampleRate);
        _lowpass += coefficient * (impulse - _lowpass);
        return _lowpass;
    }
}
