using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>A drum channel (and a loop) can name a WAV file of an installed pack, like a sample set can name a folder of one: sample=pack:free/swirly-drums/kit/kick.wav.</summary>
[Collection("PackFolders")]
public sealed class SequencerPackSampleTests
{
    [Fact]
    public async Task A_drum_channel_plays_a_file_of_an_installed_pack()
    {
        string packs = Directory.CreateTempSubdirectory("ample-packs-").FullName;
        string previous = Environment.GetEnvironmentVariable("AMPLE_PACKS") ?? string.Empty;
        try
        {
            Directory.CreateDirectory(Path.Combine(packs, "free", "testpack", "kit"));
            File.WriteAllBytes(Path.Combine(packs, "free", "testpack", "kit", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000 }, 44100));
            Environment.SetEnvironmentVariable("AMPLE_PACKS", packs);
            using EngineRig rig = await EngineRig.CreateAsync();
            Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
            File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), "sequencer 1\nchannel kick drum sample=pack:free/testpack/kit/kick.wav vol=-3\npattern p bars=1\n  kick x...x...\nplaylist\n  clip p track=1 at=1 for=2\n");
            BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse("composition \"s\" { ample 1  tempo 120  master-seed 1 }\nsequencer sq {\n  file sequences/a.seq\n}\n"));
            string? takeId = report.Blocks.Single(b => b.Block == "sq").TakeId;
            Assert.NotNull(takeId);   // it built: the pack file was found
            TakeRecord take = rig.Bank.GetTake(takeId!);
            using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role == "mix").DataId);
            using MemoryStream ms = new();
            data.CopyTo(ms);
            Assert.True(ms.Length > 44 && ms.ToArray().Skip(44).Any(b => b != 0), "the kick is in the mix");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMPLE_PACKS", previous.Length == 0 ? null : previous);
            Directory.Delete(packs, true);
        }
    }
}
