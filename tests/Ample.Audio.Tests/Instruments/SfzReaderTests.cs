using Ample.Audio.Instruments;

namespace Ample.Core.Tests.Instruments;

[Trait("Category", "Instruments")]
public sealed class SfzReaderTests
{
    private static SfzInstrument P(string text, Func<string, string?>? inc = null) => SfzReader.Parse(text, "base", inc);

    [Fact]
    public void InheritanceOverridesDownTheHeaders()
    {
        var r = P("<global> volume=-6 lovel=10 <master> hivel=100 <group> lokey=c4 hikey=e4 pitch_keycenter=60 <region> sample=a.wav hivel=50 <region> sample=b.wav key=g4");
        Assert.Equal(2, r.Regions.Count);
        var a = r.Regions[0];
        Assert.Equal((60, 64, 60, 10, 50, -600L), (a.LoKey, a.HiKey, a.PitchKeycenter, a.LoVel, a.HiVel, a.VolumeCentiDb));
        var b = r.Regions[1];
        Assert.Equal((67, 67, 67, 100), (b.LoKey, b.HiKey, b.PitchKeycenter, b.HiVel));
    }

    [Fact]
    public void NewGroupResetsGroupOpcodes()
    {
        var r = P("<group> lovel=20 <region> sample=a.wav <group> <region> sample=b.wav");
        Assert.Equal(20, r.Regions[0].LoVel);
        Assert.Equal(0, r.Regions[1].LoVel);
    }

    [Fact]
    public void OpcodesLoopsSequenceRandomEnvelope()
    {
        var r = P("<region> sample=x y.wav loop_mode=loop_sustain loop_start=100 loop_end=2000 seq_length=3 seq_position=2 lorand=0.25 hirand=0.5 offset=7 end=900 tune=-12 transpose=2 volume=-3.5 ampeg_attack=0.015 ampeg_sustain=80.5 ampeg_release=1.25");
        var x = Assert.Single(r.Regions);
        Assert.Equal("x y.wav", x.Sample);
        Assert.Equal("loop_sustain", x.LoopMode);
        Assert.Equal((100L, 2000L, 3, 2), (x.LoopStart, x.LoopEnd, x.SeqLength, x.SeqPosition));
        Assert.Equal((250_000L, 500_000L, 7L, 900L), (x.LoRand, x.HiRand, x.Offset, x.End));
        Assert.Equal((-12, 2, -350L), (x.TuneCents, x.Transpose, x.VolumeCentiDb));
        Assert.Equal((15_000L, 80_500L, 1_250_000L), (x.AmpegAttackUs, x.AmpegSustainMilliPercent, x.AmpegReleaseUs));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void CommentsDefineIncludeAndDefaultPath()
    {
        var files = new Dictionary<string, string> { [Path.Combine("base", "inc.sfz")] = "<group> lovel=5 // c\n<region> sample=$S.wav" };
        var r = P("// header\n#define $S snare\n<control> default_path=kit\\\n/* block\ncomment */\n#include \"inc.sfz\"\n<region> sample=k.wav", f => files.GetValueOrDefault(f));
        Assert.Equal(["kit/snare.wav", "kit/k.wav"], r.Regions.Select(x => x.Sample));
        Assert.Equal(5, r.Regions[0].LoVel);
    }

    [Fact]
    public void UnknownOpcodesAreKeptAndReportedOnce()
    {
        var r = P("<region> sample=a.wav cutoff=500 <region> sample=b.wav cutoff=900 fil_type=lpf_2p");
        Assert.Equal("500", r.Regions[0].Unknown["cutoff"]);
        Assert.Equal(1, r.Warnings.Count(w => w.Message.Contains("cutoff", StringComparison.Ordinal)));
        Assert.Contains(r.Warnings, w => w.Message.Contains("fil_type", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSampleBadValuesAndMissingIncludeWarn()
    {
        var r = P("<region> lokey=zz <region> sample=a.wav lovel=abc\n#include \"nope.sfz\"");
        Assert.Single(r.Regions);
        Assert.Contains(r.Warnings, w => w.Code == "AMP5304");
        Assert.Contains(r.Warnings, w => w.Code == "AMP5306");
        Assert.Contains(r.Warnings, w => w.Code == "AMP5305");
    }

    [Fact]
    public void IncludeCannotEscapeOrRecurseForever()
    {
        var r = P("#include \"../x.sfz\"\n#include \"self.sfz\"", f => "#include \"self.sfz\"\n<region> sample=a.wav");
        Assert.True(r.Regions.Count <= 20);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void MissingFileThrowsDiagnostic()
    {
        var e = Assert.Throws<SfzFormatException>(() => SfzReader.Read(Path.Combine(Path.GetTempPath(), "ample-no-such.sfz")));
        Assert.StartsWith("AMP5301", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FuzzedTextNeverThrows()
    {
        var rnd = new Lcg(1234);
        const string alphabet = "<>=/*\n #$\"\\abcdefg_0123.-region sample= group";
        for (var i = 0; i < 500; i++)
        {
            var s = new string(Enumerable.Range(0, rnd.Next(0, 300)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
            _ = P(s, _ => s);
        }
    }
}
