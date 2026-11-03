using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>A pattern of a drums block that asks for a voice its kit has no sample for is a diagnostic before anything is rendered (it was AMP5350 at build time, and the build of everything behind it was skipped).</summary>
public sealed class DrumPreflightTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ample-drum-preflight-" + Guid.NewGuid().ToString("N"));

    public DrumPreflightTests()
    {
        foreach (string folder in new[] { "samples/drums-full", "samples/toms" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }

        foreach (string name in new[] { "kick", "kick_v2", "snare", "hat", "hat_v2", "crash" })
        {
            File.WriteAllBytes(Path.Combine(root, "samples/drums-full", name + ".wav"), []);
        }

        File.WriteAllBytes(Path.Combine(root, "samples/toms", "tom.wav"), []);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // a temp folder that stays is not a failure
        }
    }

    private AmpleDocument Parse(string drums)
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n" + drums + "\n";
        return Parser.Parse(song, EngineRig.Spec, new ParseOptions { Preflight = (blocks, lines) => DrumPreflight.Check(root, blocks, lines) });
    }

    [Fact]
    public void A_voice_the_kit_has_no_sample_for_is_found_on_its_line_with_the_voices_the_kit_has()
    {
        AmpleDocument doc = Parse("drums beat at 1 for 4 {\n  kit samples/drums-full\n  pattern kick:x...x... snare:....x... tom:......x.\n}");
        Diagnostic found = Assert.Single(doc.Diagnostics, d => d.Code == DrumPreflight.Code);
        Assert.Equal(Severity.Error, found.Severity);
        Assert.Equal(5, found.Line);   // project, empty line, the header, kit, pattern
        Assert.Contains("has no sample for \"tom\"", found.Message, StringComparison.Ordinal);
        Assert.Contains("crash, hat, kick, snare", found.Message, StringComparison.Ordinal);
        Assert.Equal("  pattern kick:x...x... snare:....x... tom:......x.".IndexOf("tom", StringComparison.Ordinal) + 1, found.Col);
    }

    [Fact]
    public void Voices_the_kit_has_and_variants_of_them_are_fine()
    {
        AmpleDocument doc = Parse("drums beat at 1 for 4 {\n  kit samples/drums-full\n  pattern kick:x...x... snare:....x... hat:x.x.x.x. crash:x.......\n  patterns groove=kick:x... snare:..x. / fill=kick:x.x. hat:xxxx\n}");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == DrumPreflight.Code);
    }

    [Fact]
    public void A_voice_in_a_named_pattern_is_found_too_and_a_map_or_a_fallback_kit_makes_it_fine()
    {
        string withTom = "drums beat at 1 for 4 {\n  kit samples/drums-full\n  pattern kick:x...\n  patterns groove=kick:x... / fill=tom:xxxx\n";
        Assert.Contains(Parse(withTom + "}").Diagnostics, d => d.Code == DrumPreflight.Code && d.Message.Contains("\"tom\"", StringComparison.Ordinal));
        Assert.DoesNotContain(Parse(withTom + "  map tom=samples/toms/tom\n}").Diagnostics, d => d.Code == DrumPreflight.Code);
        Assert.DoesNotContain(Parse(withTom + "  fallback samples/toms\n}").Diagnostics, d => d.Code == DrumPreflight.Code);
    }

    [Fact]
    public void Nothing_is_said_when_the_kit_cannot_be_read_and_a_comment_silences_nothing_that_is_an_error()
    {
        Assert.DoesNotContain(Parse("drums beat at 1 for 4 {\n  kit samples/nowhere\n  pattern tom:x...\n}").Diagnostics, d => d.Code == DrumPreflight.Code);
        Assert.Contains(Parse("drums beat at 1 for 4 {\n  kit samples/drums-full\n  pattern tom:x...   # ample-ignore AMP2088\n}").Diagnostics, d => d.Code == DrumPreflight.Code);   // an error stays
    }
}
