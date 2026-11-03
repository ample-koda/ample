using Ample.Audio.Engine;

namespace Ample.Core.Tests;

[Trait("Category", "Envelope")]
[Trait("Category", "Sampler")]
public sealed class SamplerModuleTests
{
    [Fact]
    public void Adsr_Shape_IsIntegerAndExact()
    {
        IntegerEnvelope e = new(100, 100, 16384, 200);
        Assert.Equal(0, e.Gain(0, 1000));
        Assert.Equal(16384, e.Gain(50, 1000));
        Assert.Equal(32768, e.Gain(100, 1000));
        Assert.Equal(24576, e.Gain(150, 1000));
        Assert.Equal(16384, e.Gain(500, 1000));
        Assert.Equal(8192, e.Gain(1100, 1000));
        Assert.Equal(0, e.Gain(1200, 1000));
        Assert.Equal(1200, e.TotalLength(1000));
    }

    [Fact]
    public void Hold_PlateausAtFullLevelBetweenAttackAndDecay()
    {
        IntegerEnvelope e = new(100, 100, 16384, 200, Hold: 50);
        Assert.Equal(32768, e.Gain(100, 1000));  // right at the end of attack
        Assert.Equal(32768, e.Gain(130, 1000));  // still in the hold plateau
        Assert.Equal(32768, e.Gain(149, 1000));  // last sample of the hold
        Assert.Equal(24576, e.Gain(200, 1000));  // 50 samples into decay (hold ends at 150): halfway to sustain
        Assert.Equal(16384, e.Gain(250, 1000));  // decay finished, at sustain
    }

    [Fact]
    public void Hold_defaults_to_zero_unchanged_from_before_it_existed()
    {
        IntegerEnvelope withoutHold = new(100, 100, 16384, 200);
        Assert.Equal(0, withoutHold.Hold);
        Assert.Equal(24576, withoutHold.Gain(150, 1000)); // same value the original Adsr_Shape_IsIntegerAndExact test asserts
    }

    [Fact]
    public void Release_StartsFromLevelReachedAtNoteOff()
    {
        IntegerEnvelope e = new(100, 100, 16384, 100);
        Assert.Equal(e.HeldGain(50), e.Gain(50, 50));
        Assert.Equal(e.HeldGain(50) / 2, e.Gain(100, 50));
        Assert.Equal(0, new IntegerEnvelope(0, 0, 32768, 0).Gain(10, 10));
        Assert.Equal(32768, new IntegerEnvelope(0, 0, 32768, 0).Gain(0, 10));
    }

    [Fact]
    public void Loop_FillsAnyLengthAndIsPeriodic()
    {
        short[] src = new short[100];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (short)(i * 10);
        }

        LoopPlayer p = new(src, 40, 80, 0);
        short[] buf = new short[1000];
        p.Fill(buf, 0);
        Assert.Equal(src[10], buf[10]);
        for (int i = 80; i < 960; i++)
        {
            Assert.Equal(buf[i], buf[i + 40]);
        }

        Assert.Equal(src[40], buf[80]);
    }

    [Fact]
    public void Loop_Crossfade_BlendsTailIntoLeadIn()
    {
        short[] src = new short[100];
        Array.Fill(src, (short)1000, 0, 40);
        LoopPlayer p = new(src, 40, 80, 20);
        Assert.Equal(20, p.Crossfade);
        Assert.Equal(0, p.At(59));
        Assert.Equal(0, p.At(60));
        Assert.Equal(1000 * 19 / 20, p.At(79));
        Assert.Equal(p.At(80), p.At(40));
    }

    [Fact]
    public void Loop_Crossfade_IsClampedToLeadIn()
    {
        Assert.Equal(10, new LoopPlayer(new short[100], 10, 90, 50).Crossfade);
        Assert.Equal(0, new LoopPlayer(new short[10], 0, 10, 5).Crossfade);
    }

    [Fact]
    public void PanTable_IsEqualPowerAndSymmetric()
    {
        Assert.Equal(32768, PanTable.LeftGain(-32));
        Assert.Equal(0, PanTable.RightGain(-32));
        Assert.Equal(PanTable.LeftGain(0), PanTable.RightGain(0));
        Assert.Equal(23170, PanTable.LeftGain(0));
        for (int p = -32; p <= 32; p++)
        {
            Assert.Equal(PanTable.LeftGain(p), PanTable.RightGain(-p));
            long power = ((long)PanTable.LeftGain(p) * PanTable.LeftGain(p)) + ((long)PanTable.RightGain(p) * PanTable.RightGain(p));
            Assert.InRange(power, (1L << 30) - 100000, (1L << 30) + 100000);
        }
    }

    [Fact]
    public void Stereo_WavRoundTrips_AndLeftRightDifferenceSurvives()
    {
        StereoBuffer b = StereoBuffer.Pan([1000, -2000, 3000], -32);
        Assert.All(b.Right, s => Assert.Equal(0, s));
        byte[] wav = StereoWav.Encode(b, 44100);
        Assert.Equal(44 + 12, wav.Length);
        (StereoBuffer d, int rate) = StereoWav.Decode(wav);
        Assert.Equal(44100, rate);
        Assert.Equal(b.Left, d.Left);
        Assert.Equal(b.Right, d.Right);
        Assert.Equal(wav, StereoWav.Encode(d, 44100));
    }

    [Fact]
    public void Stereo_MonoFoldAndMix()
    {
        short[] mono = [100, -100, 7];
        StereoBuffer s = StereoBuffer.FromMono(mono);
        Assert.Equal(mono, s.ToMono());
        StereoBuffer m = s.Mix(new StereoBuffer([32767], [0]));
        Assert.Equal(32767, m.Left[0]);
        Assert.Equal(100, m.Right[0]);
        Assert.Throws<InvalidDataException>(() => StereoWav.Decode(Wav.Encode(mono, 44100)));
    }
}
