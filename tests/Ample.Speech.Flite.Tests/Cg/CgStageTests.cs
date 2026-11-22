using System.Buffers.Binary;
using System.Globalization;
using Ample.Speech.Flite.Cg;
using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite.Tests.Cg;

/// <summary>Milestone C2 of the clustergen voices: the stages up to the parameter track equal the pinned Flite program line by line on the 341 line corpus
/// (plan/speech/flite/reference/cg/dumpcg.c writes the digests: the HMM states with their ends as float bits, the number of frames, the parameter track and the strengths as float bits).</summary>
public sealed class CgStageTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static uint Fnv(uint hash, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) hash = (hash ^ b) * 16777619u;
        return hash;
    }

    private static uint FnvFloats(uint hash, ReadOnlySpan<float> values)
    {
        Span<byte> four = stackalloc byte[4];
        foreach (float value in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(four, value);
            hash = Fnv(hash, four);
        }

        return hash;
    }

    private static byte[][] Sentences()
    {
        byte[] all = File.ReadAllBytes(Fixture("flite-cg-sentences.txt"));
        List<byte[]> lines = [];
        int start = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != (byte)'\n') continue;
            int end = i > start && all[i - 1] == (byte)'\r' ? i - 1 : i;
            lines.Add(all[start..end]);
            start = i + 1;
        }

        return [.. lines];
    }

    private static FliteCgVoiceData Load(string voice) => FliteCgVoiceData.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cg", $"flite-cg-{voice}.bin"));

    [Theory]
    [InlineData("slt")]
    [InlineData("rms")]
    [InlineData("awb")]
    public void The_states_the_frames_and_the_parameter_tracks_equal_the_pinned_program_on_every_line(string voice)
    {
        FliteCgEngine engine = new(Load(voice));
        byte[][] sentences = Sentences();
        string[] expected = File.ReadAllLines(Fixture($"flite-cg-digest-{voice}.tsv"));
        Assert.Equal(sentences.Length, expected.Length);
        List<string> differences = [];
        for (int k = 0; k < sentences.Length; k++)
        {
            string[] parts = expected[k].Split('\t');
            Assert.Equal(k + 1, int.Parse(parts[0], CultureInfo.InvariantCulture));
            FliteCgPlan plan = engine.Plan(sentences[k], null, CancellationToken.None);
            uint states = 2166136261u;
            int count = 0;
            for (Item? s = plan.Utterance.GetRelation("HMMstate").Head; s is not null; s = Hrg.Next(s))
            {
                states = Fnv(states, System.Text.Encoding.Latin1.GetBytes(Hrg.Name(s)));
                states = FnvFloats(states, [Hrg.FeatFloat(s, "end")]);
                count++;
            }

            uint wave = 2166136261u;
            if (plan.Samples.Length > 0)
            {
                byte[] bytes = new byte[plan.Samples.Length * 2];
                for (int s = 0; s < plan.Samples.Length; s++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(s * 2), plan.Samples[s]);
                wave = Fnv(wave, bytes);
            }

            uint param = 2166136261u, strengths = 2166136261u, smooth = 2166136261u, paramRest = 2166136261u, smoothRest = 2166136261u;
            foreach (float[] row in plan.Track.Frames)
            {
                param = FnvFloats(param, row);
                paramRest = FnvFloats(paramRest, row.AsSpan(1));
            }

            foreach (float[] row in plan.Track.Strengths) strengths = FnvFloats(strengths, row);
            foreach (float[] row in plan.Smoothed)
            {
                smooth = FnvFloats(smooth, row);
                smoothRest = FnvFloats(smoothRest, row.AsSpan(1));
            }
            string[] mine =
            [
                (k + 1).ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture), plan.Frames.ToString(CultureInfo.InvariantCulture),
                states.ToString("x8", CultureInfo.InvariantCulture), param.ToString("x8", CultureInfo.InvariantCulture), strengths.ToString("x8", CultureInfo.InvariantCulture),
                smooth.ToString("x8", CultureInfo.InvariantCulture), paramRest.ToString("x8", CultureInfo.InvariantCulture), smoothRest.ToString("x8", CultureInfo.InvariantCulture),
                plan.Samples.Length.ToString(CultureInfo.InvariantCulture), wave.ToString("x8", CultureInfo.InvariantCulture)
            ];
            // the digest file has the hashes in the order of dumpcg.c: states, parameter track, strengths, smoothed track, samples, wave, parameter track and smoothed track without F0
            string[] theirs = [parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], parts[9], parts[10], parts[7], parts[8]];
            if (!mine.SequenceEqual(theirs)) differences.Add($"line {k + 1}: expected [{string.Join(' ', theirs)}] got [{string.Join(' ', mine)}]");
        }

        Assert.True(differences.Count == 0, $"{differences.Count} of {sentences.Length} lines differ; first: {(differences.Count > 0 ? differences[0] : string.Empty)}");
    }
}
