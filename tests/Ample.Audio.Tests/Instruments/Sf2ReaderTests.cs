using Ample.Audio.Instruments;

namespace Ample.Core.Tests.Instruments;

[Trait("Category", "Instruments")]
public sealed class Sf2ReaderTests
{
    [Fact]
    public void ReadsHeadersZonesAndSamples()
    {
        var bank = Sf2Reader.Read(Sf2Fixture.Tiny());
        Assert.Equal("Tiny", bank.Name);
        Assert.Equal((2, 1), (bank.VersionMajor, bank.VersionMinor));
        var p = Assert.Single(bank.Presets);
        Assert.Equal(("Tiny Lead", 5, 0), (p.Name, p.Program, p.Bank));
        Assert.Equal(41, p.Zones[0].Gens[0].Oper);
        var inst = Assert.Single(bank.Instruments);
        Assert.Equal(2, inst.Zones.Count);
        var kr = inst.Zones[1].Find(Sf2Fixture.KeyRange)!.Value;
        Assert.Equal((60, 127), (kr.RangeLo, kr.RangeHi));
        Assert.Equal(2, bank.Samples.Count);
        Assert.Equal((100u, 10u, 90u, 44100u, 60), (bank.Samples[0].End - bank.Samples[0].Start, bank.Samples[0].LoopStart - bank.Samples[0].Start, bank.Samples[0].LoopEnd - bank.Samples[0].Start, bank.Samples[0].Rate, bank.Samples[0].OriginalPitch));
    }

    [Fact]
    public void Skips_preset_zones_with_empty_key_or_velocity_intersections()
    {
        byte[] bytes = Sf2Fixture.Build(
            [("sample", 100, (10u, 90u), 60)],
            [("instrument", [new Sf2Fixture.Zone((Sf2Fixture.KeyRange, Sf2Fixture.Range(60, 127)), (Sf2Fixture.VelRange, Sf2Fixture.Range(51, 127)), (Sf2Fixture.SampleId, 0))])],
            [("preset", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.KeyRange, Sf2Fixture.Range(0, 59)), (Sf2Fixture.Instrument, 0)), new Sf2Fixture.Zone((Sf2Fixture.KeyRange, Sf2Fixture.Range(60, 127)), (Sf2Fixture.VelRange, Sf2Fixture.Range(0, 50)), (Sf2Fixture.Instrument, 0)), new Sf2Fixture.Zone((Sf2Fixture.KeyRange, Sf2Fixture.Range(60, 127)), (Sf2Fixture.VelRange, Sf2Fixture.Range(51, 127)), (Sf2Fixture.Instrument, 0))])]);
        Sf2Bank bank = Sf2Reader.Read(bytes);

        Sf2PlayableRegion region = Assert.Single(Sf2Program.Resolve(bank, 0, 5));
        Assert.Equal((60, 127, 51, 127), (region.KeyLo, region.KeyHi, region.VelocityLo, region.VelocityHi));
    }

    [Fact]
    public void Uses_one_shot_playback_when_zone_offsets_put_loop_points_outside_the_sample()
    {
        byte[] bytes = Sf2Fixture.Build(
            [("sample", 100, (10u, 90u), 60)],
            [("instrument", [new Sf2Fixture.Zone((Sf2Fixture.SampleModes, 1), (Sf2Fixture.StartOffset, 20), (Sf2Fixture.SampleId, 0))])],
            [("preset", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]);
        Sf2PlayableRegion region = Assert.Single(Sf2Program.Resolve(Sf2Reader.Read(bytes), 0, 5));

        Assert.False(region.Loop);
        Assert.True(region.InvalidLoop);
    }

    [Fact]
    public void LoadsOnlyTheAskedSample()
    {
        var bytes = Sf2Fixture.Tiny();
        var opened = 0L;
        var bank = Sf2Reader.Read(() => new CountingStream(new MemoryStream(bytes), n => opened += n));
        var headerBytes = opened;
        var data = bank.ReadSampleData(1);
        Assert.Equal(50, data.Length);
        Assert.Equal((short)6400, data[0]);
        Assert.Equal((short)6449, data[49]);
        Assert.Equal(100, opened - headerBytes);
    }

    [Fact]
    public void BadSampleIndexAndRangesAreDiagnostics()
    {
        var bank = Sf2Reader.Read(Sf2Fixture.Tiny());
        Assert.Throws<Sf2FormatException>(() => bank.ReadSampleData(9));
        bank.Samples[0].End = uint.MaxValue;
        Assert.Throws<Sf2FormatException>(() => bank.ReadSampleData(0));
    }

    [Fact]
    public void NotASoundFontIsADiagnostic()
    {
        Assert.StartsWith("AMP5311", Assert.Throws<Sf2FormatException>(() => Sf2Reader.Read([1, 2, 3])).Message, StringComparison.Ordinal);
        Assert.Throws<Sf2FormatException>(() => Sf2Reader.Read(Path.Combine(Path.GetTempPath(), "ample-no-such.sf2")));
        Assert.Throws<Sf2FormatException>(() => Sf2Reader.Read("RIFF\0\0\0\0WAVEfmt "u8.ToArray()));
    }

    [Fact]
    public void EveryTruncationEndsInADiagnosticOrAValidBank()
    {
        var bytes = Sf2Fixture.Tiny();
        for (var n = 0; n < bytes.Length; n += 3) TryRead(bytes[..n]);
    }

    [Fact]
    public void RandomCorruptionNeverCrashes()
    {
        var good = Sf2Fixture.Tiny();
        var rnd = new Lcg(99);
        for (var i = 0; i < 3000; i++)
        {
            var b = (byte[])good.Clone();
            for (var k = rnd.Next(1, 6); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
            TryRead(b);
        }
        for (var i = 0; i < 300; i++) TryRead(Enumerable.Range(0, rnd.Next(0, 400)).Select(_ => (byte)rnd.Next(256)).ToArray());
    }

    private static void TryRead(byte[] b)
    {
        try
        {
            var bank = Sf2Reader.Read(b);
            for (var s = 0; s < bank.Samples.Count; s++)
                try { _ = bank.ReadSampleData(s); } catch (Sf2FormatException) { }
        }
        catch (Sf2FormatException) { }
    }

    private sealed class CountingStream(Stream inner, Action<long> counted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); counted(n); return n; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
