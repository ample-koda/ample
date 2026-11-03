using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ample.Audio.Engine;

/// <summary>16-bit mono WAV encoding and decoding.</summary>
public static class Wav
{
    /// <summary>Encodes samples as a 16-bit mono PCM WAV file.</summary>
    public static byte[] Encode(ReadOnlySpan<short> samples, int sampleRate)
    {
        byte[] bytes = new byte[44 + samples.Length * 2];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + samples.Length * 2);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), samples[i]);
        }

        return bytes;
    }

    /// <summary>Decodes a WAV written by <see cref="Encode"/>.</summary>
    public static (short[] Samples, int SampleRate) Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes[..4]) != "RIFF")
        {
            throw new InvalidDataException("Not a WAV file.");
        }

        int rate = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        if (ChannelAudio.IsStereo(bytes))
        {
            (StereoBuffer stereo, int stereoRate) = StereoWav.Decode(bytes);
            return (stereo.ToMono(), stereoRate);
        }
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]) / 2;
        short[] samples = new short[length];
        for (int i = 0; i < length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(44 + i * 2)..]);
        }

        return (samples, rate);
    }
}

/// <summary>Integer-only audio helpers so that the built-in renderers give the same samples on every machine: fixed-point oscillators, decimal-computed tables, no floating point and no platform math.</summary>
public static class FixedAudio
{
    private static readonly uint[] PhaseIncrement44100 = BuildIncrements(44100);
    private static readonly int[] GainQ15 = BuildGains();

    /// <summary>Gets the phase increment (of a 32-bit phase) for a MIDI note at 44100 Hz scaled to another rate.</summary>
    public static uint Increment(int midiNote, int sampleRate) => (uint)((ulong)PhaseIncrement44100[Math.Clamp(midiNote, 0, 127)] * 44100UL / (ulong)sampleRate);

    /// <summary>Gets the Q15 gain (32768 = 1.0) for a whole number of decibels from -60 to +12.</summary>
    public static int GainFromDb(int db) => GainQ15[Math.Clamp(db, -60, 12) + 60];

    /// <summary>The highest gain, in decibels, of <see cref="GainFromDbWide"/>.</summary>
    public const int WideMaxDb = 48;

    /// <summary>Gets the Q15 gain (32768 = 1.0) for a whole number of decibels from -60 to +48 (the volume block: a very quiet recording needs more than +12 dB).</summary>
    public static int GainFromDbWide(int db) => GainQ15[Math.Clamp(db, -60, WideMaxDb) + 60];

    /// <summary>Sawtooth from a 32-bit phase, range -32768..32767.</summary>
    public static int Saw(uint phase) => (int)(phase >> 16) - 32768;

    /// <summary>Square wave from a 32-bit phase.</summary>
    public static int Square(uint phase) => phase < 0x80000000u ? 20000 : -20000;

    /// <summary>Triangle wave from a 32-bit phase.</summary>
    public static int Triangle(uint phase)
    {
        int x = (int)(phase >> 16);
        return x < 32768 ? (x * 2) - 32768 : ((65535 - x) * 2) - 32768;
    }

    /// <summary>Clamps a sum to 16 bits.</summary>
    public static short Clip(long value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    /// <summary>Applies a deterministic integer soft knee before 16-bit PCM output, or hard clips when disabled.</summary>
    public static short Limit(long value, bool enabled)
    {
        if (!enabled) return Clip(value);
        long magnitude = value == long.MinValue ? long.MaxValue : Math.Abs(value);
        magnitude = Math.Min(magnitude, 1_000_000_000);
        const int knee = 30_000, range = short.MaxValue - knee;
        if (magnitude > knee)
        {
            long excess = magnitude - knee;
            magnitude = knee + excess * range / (excess + range);
        }
        int limited = (int)magnitude;
        return (short)(value < 0 ? -limited : limited);
    }

    /// <summary>Samples per bar in whole samples: 4 beats at the tempo (in thousandths of a beat per minute).</summary>
    public static int SamplesPerBar(int sampleRate, long tempoMilli) => (int)(4L * 60 * sampleRate * 1000 / tempoMilli);

    /// <summary>Gets the samples of one bar in a meter (3/4: three quarter notes, 6/8: three, 5/4: five, 7/8: three and a half); in 4/4 it is the value without a meter.</summary>
    public static int SamplesPerBar(int sampleRate, long tempoMilli, int numerator, int denominator) => (int)(4L * numerator * 60 * sampleRate * 1000 / ((long)denominator * tempoMilli));

    private static uint[] BuildIncrements(int rate)
    {
        const decimal Semitone = 1.0594630943592952645618252949m;
        uint[] table = new uint[128];
        decimal freq = 440m;
        decimal[] freqs = new decimal[128];
        freqs[69] = freq;
        for (int n = 70; n < 128; n++)
        {
            freqs[n] = freqs[n - 1] * Semitone;
        }

        for (int n = 68; n >= 0; n--)
        {
            freqs[n] = freqs[n + 1] / Semitone;
        }

        for (int n = 0; n < 128; n++)
        {
            table[n] = (uint)Math.Round(freqs[n] * 4294967296m / rate, MidpointRounding.AwayFromZero);
        }

        return table;
    }

    private static int[] BuildGains()
    {
        const decimal Step = 1.1220184543019634355910389464m;
        int[] table = new int[60 + WideMaxDb + 1];
        decimal g = 1m;
        table[60] = 32768;
        for (int db = 1; db <= WideMaxDb; db++)
        {
            g *= Step;
            table[60 + db] = (int)Math.Round(32768m * g, MidpointRounding.AwayFromZero);
        }

        g = 1m;
        for (int db = 1; db <= 60; db++)
        {
            g /= Step;
            table[60 - db] = (int)Math.Round(32768m * g, MidpointRounding.AwayFromZero);
        }

        return table;
    }
}

/// <summary>Level measurements of a mix (informational; not part of the bit-exact path).</summary>
public static class AudioMeter
{
    /// <summary>Gets the sample peak in dB relative to full scale (negative infinity for silence).</summary>
    public static double PeakDbfs(ReadOnlySpan<short> samples)
    {
        int peak = 0;
        foreach (short s in samples)
        {
            peak = Math.Max(peak, Math.Abs((int)s));
        }

        return peak == 0 ? double.NegativeInfinity : 20 * Math.Log10(peak / 32768.0);
    }

    /// <summary>Gets the RMS level in dB relative to full scale.</summary>
    public static double RmsDbfs(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return double.NegativeInfinity;
        }

        double sum = 0;
        foreach (short s in samples)
        {
            sum += (double)s * s;
        }

        return sum == 0 ? double.NegativeInfinity : 10 * Math.Log10(sum / samples.Length / (32768.0 * 32768.0));
    }

    /// <summary>Formats a level for display.</summary>
    public static string Format(double db) => double.IsNegativeInfinity(db) ? "silence" : db.ToString("F1", CultureInfo.InvariantCulture) + " dBFS";
}
