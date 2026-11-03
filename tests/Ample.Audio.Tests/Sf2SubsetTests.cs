using Ample.Audio.IO;
using Ample.Audio.Instruments;

namespace Ample.Core.Tests;

public sealed class Sf2SubsetTests
{
    private static string? Pack()
    {
        PackLookup lookup = AmpleHome.LookupPackFile(AmpleHome.DefaultPath(), null, AppContext.BaseDirectory, "licensed/musescore-general/MuseScore_General.sf2");
        return lookup.Path;
    }

    [Fact]
    public void A_subset_gives_the_same_regions_and_the_same_sample_data_as_the_original()
    {
        string? pack = Pack();
        if (pack is null)
        {
            return;   // the pack is not installed on this machine
        }

        string target = Path.Combine(Path.GetTempPath(), "ample-subset-" + Guid.NewGuid().ToString("N") + ".sf2");
        try
        {
            Sf2Subset.Result result = Sf2Subset.Write(pack, [(0, 52), (0, 53), (128, 0)], target, "test");
            Assert.True(result.SubsetBytes < result.OriginalBytes / 10);
            Sf2Bank original = Sf2Reader.Read(pack), subset = Sf2Reader.Read(target);
            foreach ((int bank, int program) in new[] { (0, 52), (0, 53), (128, 0) })
            {
                IReadOnlyList<Sf2PlayableRegion> a = Sf2Program.Resolve(original, bank, program), b = Sf2Program.Resolve(subset, bank, program);
                Assert.Equal(a.Count, b.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    Assert.Equal(a[i].KeyLo, b[i].KeyLo);
                    Assert.Equal(a[i].KeyHi, b[i].KeyHi);
                    Assert.Equal(a[i].Root, b[i].Root);
                    Assert.Equal(a[i].TuneCents, b[i].TuneCents);
                    Assert.Equal(a[i].GainQ15, b[i].GainQ15);
                    Assert.Equal(a[i].Loop, b[i].Loop);
                    Assert.Equal(a[i].End - a[i].Start, b[i].End - b[i].Start);
                    Assert.Equal(a[i].LoopEnd - a[i].LoopStart, b[i].LoopEnd - b[i].LoopStart);
                    short[] left = original.ReadSampleData(a[i].Sample), right = subset.ReadSampleData(b[i].Sample);
                    Assert.Equal(left, right);
                    Assert.Equal(original.Samples[a[i].Sample].Rate, subset.Samples[b[i].Sample].Rate);
                }
            }

            Assert.Throws<Sf2FormatException>(() => Sf2Program.Resolve(subset, 0, 1));
        }
        finally
        {
            File.Delete(target);
        }
    }
}
