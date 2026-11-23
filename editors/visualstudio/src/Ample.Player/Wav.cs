namespace Ample.Player;

/// <summary>A WAV file held in memory as 16-bit samples (the other PCM and float formats are converted when it is read).</summary>
internal sealed class WavData
{
    public int Rate { get; private set; }
    public int Channels { get; private set; }
    public short[] Samples { get; private set; } = [];
    public int Frames => Channels == 0 ? 0 : Samples.Length / Channels;
    public double Seconds => Rate == 0 ? 0 : (double)Frames / Rate;

    /// <summary>Reads a PCM (8/16/24/32 bit) or float WAV file.</summary>
    public static WavData Load(string path)
    {
        byte[] file = System.IO.File.ReadAllBytes(path);
        if (file.Length < 44 || System.Text.Encoding.ASCII.GetString(file, 0, 4) != "RIFF") throw new System.IO.InvalidDataException("Not a WAV file.");
        int tag = 0, channels = 0, rate = 0, bits = 0, dataStart = -1, dataLength = 0;
        int at = 12;
        while (at + 8 <= file.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(file, at, 4);
            int size = System.BitConverter.ToInt32(file, at + 4);
            int body = at + 8;
            if (id == "fmt ")
            {
                tag = System.BitConverter.ToUInt16(file, body);
                channels = System.BitConverter.ToUInt16(file, body + 2);
                rate = System.BitConverter.ToInt32(file, body + 4);
                bits = System.BitConverter.ToUInt16(file, body + 14);
                if (tag == 0xFFFE && size >= 26) tag = System.BitConverter.ToUInt16(file, body + 24);
            }
            else if (id == "data")
            {
                dataStart = body;
                dataLength = size < 0 || body + size > file.Length ? file.Length - body : size;
                break;
            }

            at = body + size + (size & 1);
        }

        if (dataStart < 0 || channels < 1 || channels > 2 || rate <= 0) throw new System.IO.InvalidDataException("Unsupported WAV layout.");
        int bytes = bits / 8;
        if (bytes < 1 || (tag != 1 && !(tag == 3 && bits == 32))) throw new System.IO.InvalidDataException("Unsupported WAV sample format.");
        int count = dataLength / bytes / channels * channels;
        short[] samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            int p = dataStart + i * bytes;
            samples[i] = tag == 3 ? Clamp(System.BitConverter.ToSingle(file, p) * 32767f)
                : bytes switch
                {
                    1 => (short)((file[p] - 128) << 8),
                    2 => System.BitConverter.ToInt16(file, p),
                    3 => (short)(file[p + 1] | (file[p + 2] << 8)),
                    _ => (short)(System.BitConverter.ToInt32(file, p) >> 16),
                };
        }

        return new WavData { Rate = rate, Channels = channels, Samples = samples };
    }

    private static short Clamp(float value) => (short)System.Math.Max(-32768f, System.Math.Min(32767f, value));

    /// <summary>
    /// For each of <paramref name="columns"/> equal parts of the file and each channel: the lowest and highest sample (-1..1) and the RMS (the
    /// average loudness), for the waveform. The result is indexed [channel][column].
    /// </summary>
    public (float[][] Min, float[][] Max, float[][] Rms) Envelope(int columns)
    {
        float[][] min = new float[Channels][], max = new float[Channels][], rms = new float[Channels][];
        int frames = Frames;
        for (int ch = 0; ch < Channels; ch++)
        {
            min[ch] = new float[columns];
            max[ch] = new float[columns];
            rms[ch] = new float[columns];
            if (frames == 0) continue;
            for (int c = 0; c < columns; c++)
            {
                int from = (int)((long)frames * c / columns), to = System.Math.Max(from + 1, (int)((long)frames * (c + 1) / columns));
                int lo = 0, hi = 0;
                double sum = 0;
                int count = 0;
                for (int f = from; f < to && f < frames; f++, count++)
                {
                    int s = Samples[f * Channels + ch];
                    lo = System.Math.Min(lo, s);
                    hi = System.Math.Max(hi, s);
                    sum += (double)s * s;
                }

                min[ch][c] = lo / 32768f;
                max[ch][c] = hi / 32768f;
                rms[ch][c] = count == 0 ? 0 : (float)(System.Math.Sqrt(sum / count) / 32768);
            }
        }

        return (min, max, rms);
    }
}
