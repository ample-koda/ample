using System.Text.Json;

namespace Ample.Server.Tests;

public sealed class PeaksTests
{
    /// <summary>A seekable 16-bit mono WAV of a sine wave that exists only as arithmetic, so a ten-minute take costs no memory.</summary>
    private sealed class SineWav(int seconds, int rate = 44100) : Stream
    {
        private readonly long dataLength = (long)seconds * rate * 2;
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => 44 + dataLength;

        public override long Position { get => position; set => position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count && position < Length)
            {
                buffer[offset + n++] = ByteAt(position++);
            }

            return n;
        }

        public override long Seek(long off, SeekOrigin origin) => position = origin switch { SeekOrigin.Begin => off, SeekOrigin.Current => position + off, _ => Length + off };

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private byte ByteAt(long p)
        {
            if (p < 44)
            {
                byte[] header = new byte[44];
                System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
                BitConverter.GetBytes((int)(Length - 8)).CopyTo(header, 4);
                System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
                BitConverter.GetBytes(16).CopyTo(header, 16);
                BitConverter.GetBytes((short)1).CopyTo(header, 20);
                BitConverter.GetBytes((short)1).CopyTo(header, 22);
                BitConverter.GetBytes(rate).CopyTo(header, 24);
                BitConverter.GetBytes(rate * 2).CopyTo(header, 28);
                BitConverter.GetBytes((short)2).CopyTo(header, 32);
                BitConverter.GetBytes((short)16).CopyTo(header, 34);
                System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
                BitConverter.GetBytes((int)dataLength).CopyTo(header, 40);
                return header[p];
            }

            long sample = (p - 44) / 2;
            short v = (short)(Math.Sin(sample * 2 * Math.PI * 440 / rate) * 16000);
            return (p - 44) % 2 == 0 ? (byte)(v & 0xFF) : (byte)((v >> 8) & 0xFF);
        }
    }

    private static JsonElement Run(Stream s, int points)
    {
        object o = Routes.Peaks(s, points);
        return JsonSerializer.SerializeToElement(o);
    }

    [Fact]
    public void A_ten_minute_take_is_summarised_in_one_pass_without_being_held_in_memory()
    {
        using SineWav wav = new(600);
        long before = GC.GetAllocatedBytesForCurrentThread();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        JsonElement r = Run(wav, 800);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(600.0, r.GetProperty("seconds").GetDouble(), 1);
        Assert.Equal(800, r.GetProperty("points").GetInt32());
        Assert.Equal(1600, r.GetProperty("peaks").GetArrayLength());
        Assert.InRange(r.GetProperty("peak").GetDouble(), 0.45, 0.5);
        Assert.InRange(r.GetProperty("rms").GetDouble(), 0.3, 0.4);
        Assert.True(allocated < 40_000_000, $"allocated {allocated} bytes for a 53 MB file");
        Console.WriteLine($"peaks of a ten-minute take: {sw.ElapsedMilliseconds} ms, {allocated / 1024} KiB allocated");
    }

    [Fact]
    public void Peaks_are_min_max_pairs_in_range()
    {
        using SineWav wav = new(2);
        JsonElement r = Run(wav, 64);
        double[] p = [.. r.GetProperty("peaks").EnumerateArray().Select(x => x.GetDouble())];
        for (int i = 0; i < p.Length; i += 2)
        {
            Assert.True(p[i] <= p[i + 1]);
            Assert.InRange(p[i], -1, 1);
            Assert.InRange(p[i + 1], -1, 1);
        }
    }

    [Fact]
    public void Data_that_is_not_wav_is_refused()
    {
        using MemoryStream notWav = new(new byte[100]);
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => Routes.Peaks(notWav, 10));
    }
}
