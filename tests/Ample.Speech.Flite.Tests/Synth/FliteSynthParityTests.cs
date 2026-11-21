using System.Globalization;
using System.Security.Cryptography;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Synth;

namespace Ample.Speech.Flite.Tests.Synth;

/// <summary>Milestone M6 of the Flite port (plan/speech/flite/PLAN.md): the synthesis half of the voice, from segments and pitch targets to samples, equals the C program at the pinned commit
/// on the 341 lines of the corpus: the diphone units, the pitch marks with their frames, and the SHA-256 of the 16-bit samples. The input of every line is what the C program held after
/// the prosody (<c>reference/m6/dumpsynth.c</c>), so these tests do not depend on the text front end.</summary>
public sealed class FliteSynthParityTests
{
    internal static readonly Lazy<List<CorpusLine>> Corpus = new(LoadCorpus);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void The_corpus_has_its_341_lines_with_the_golden_hashes()
    {
        Assert.Equal(341, Corpus.Value.Count);
        Assert.Equal(18, Corpus.Value.Count(line => line.ExpectedSamples == 0));
        Assert.Equal(323, Corpus.Value.Count(line => line.Units.Count > 0));
    }

    [Fact]
    public void Stage_units_the_selected_diphones_equal_the_C_program_on_every_line()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        foreach (CorpusLine line in Corpus.Value)
        {
            List<FliteUnit> units = FliteDiphoneUnits.Select(voice, line.Segments);
            Assert.True(line.Units.SequenceEqual(units), $"line {line.Number}: the units differ from the reference");
        }
    }

    [Fact]
    public void Stage_pm_the_pitch_marks_and_their_frames_equal_the_C_program_on_every_line()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        foreach (CorpusLine line in Corpus.Value)
        {
            if (line.PitchMarks is null) continue;
            int[] times = FlitePitchMarks.Plan(line.Targets, voice.SampleRate);
            FliteLpcRes joined = FliteJoin.Concat(voice, line.Units, times);
            Assert.True(line.PitchMarks.Count <= times.Length, $"line {line.Number}: fewer planned marks than the reference joined");
            Assert.Equal(line.PitchMarks.Count, joined.NumFrames);
            for (int i = 0; i < line.PitchMarks.Count; i++)
            {
                Assert.True(line.PitchMarks[i].Time == times[i], $"line {line.Number}: mark {i} is at {times[i]}, the reference has {line.PitchMarks[i].Time}");
                Assert.True(line.PitchMarks[i].Frame == joined.Frames[i], $"line {line.Number}: mark {i} uses frame {joined.Frames[i]}, the reference has {line.PitchMarks[i].Frame}");
            }
        }
    }

    [Fact]
    public void Stage_pcm_the_samples_equal_the_C_program_on_every_line_by_count_and_sha256()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        List<string> missing = [];
        foreach (CorpusLine line in Corpus.Value)
        {
            FliteSynthResult result = FliteSynthesizer.Synthesize(voice, line.Segments, line.Targets);
            missing.AddRange(result.MissingDiphones);
            Assert.True(line.ExpectedSamples == result.Samples.Length, $"line {line.Number}: {result.Samples.Length} samples, the reference has {line.ExpectedSamples}");
            Assert.True(line.ExpectedSha256 == Sha256(result.Samples), $"line {line.Number}: the samples differ from the reference (same count)");
            Assert.Equal(16000, result.SampleRate);
        }

        // the diphones the voice does not have are named in the same order as the C program wrote them to its error output
        string[] expected = File.ReadAllLines(Fixture("flite-m6-missing.txt")).Select(m => m["flite: udb failed to find entry for: ".Length..]).ToArray();
        Assert.Equal(expected, missing);
    }

    [Fact]
    public void The_end_of_every_segment_is_a_sample_index_that_never_goes_back_and_the_last_ends_with_the_waveform()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        foreach (CorpusLine line in Corpus.Value.Where(l => l.Segments.Count > 1 && l.ExpectedSamples > 0))
        {
            FliteSynthResult result = FliteSynthesizer.Synthesize(voice, line.Segments, line.Targets);
            Assert.Equal(line.Segments.Count, result.SegmentEnds.Length);
            Assert.Equal(result.Samples.Length, result.SegmentEnds[^1]);
            for (int k = 1; k < result.SegmentEnds.Length; k++) Assert.True(result.SegmentEnds[k - 1] <= result.SegmentEnds[k], $"line {line.Number}: segment {k}");
            Assert.All(result.SegmentEnds, end => Assert.InRange(end, 0, result.Samples.Length));
        }
    }

    [Fact]
    public void Synthesis_is_deterministic_and_can_be_cancelled()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        CorpusLine line = Corpus.Value.First(l => l.ExpectedSamples > 10000);
        short[] first = FliteSynthesizer.Synthesize(voice, line.Segments, line.Targets).Samples;
        short[] second = FliteSynthesizer.Synthesize(voice, line.Segments, line.Targets).Samples;
        Assert.Equal(first, second);
        using CancellationTokenSource stop = new();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => FliteSynthesizer.Synthesize(voice, line.Segments, line.Targets, stop.Token));
    }

    [Fact]
    public void Targets_that_do_not_advance_the_time_stop_instead_of_running_for_ever()
    {
        // the C code has no guard here: a pitch of 1e30 Hz adds 1e-30 s per mark, so it would need 1e30 marks to reach one second
        FliteTarget[] stuck = [new(0f, 1e30f), new(1f, 1e30f)];
        Assert.Throws<InvalidOperationException>(() => FlitePitchMarks.Plan(stuck, 16000));
    }

    [Fact]
    public void No_segments_or_no_targets_give_an_empty_waveform_and_no_pitch_marks()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        FliteSynthResult none = FliteSynthesizer.Synthesize(voice, [], []);
        Assert.Empty(none.Samples);
        Assert.Empty(none.Units);
        CorpusLine line = Corpus.Value.First(l => l.ExpectedSamples > 0);
        Assert.Empty(FliteSynthesizer.Synthesize(voice, [], line.Targets).Samples);
    }

    private static string Sha256(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[2 * i] = (byte)(samples[i] & 0xff);
            bytes[(2 * i) + 1] = (byte)((samples[i] >> 8) & 0xff);
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal sealed record CorpusLine(
        int Number, List<FliteSegmentInput> Segments, List<FliteTarget> Targets, List<FliteUnit> Units, List<(int Time, int Frame)>? PitchMarks, int ExpectedSamples, string ExpectedSha256);

    private static float Bits(string hex) => BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

    private static List<CorpusLine> LoadCorpus()
    {
        Dictionary<int, (int Samples, string Sha)> golden = [];
        foreach (string row in File.ReadLines(Fixture("flite-m6-pcm.tsv")).Skip(1))
        {
            string[] c = row.Split('\t');
            golden[int.Parse(c[0], CultureInfo.InvariantCulture)] = (int.Parse(c[2], CultureInfo.InvariantCulture), c[4]);
        }

        List<CorpusLine> lines = [];
        int number = 0;
        List<FliteSegmentInput> segments = [];
        List<FliteTarget> targets = [];
        List<FliteUnit> units = [];
        List<(int, int)>? marks = null;

        void Flush()
        {
            if (number == 0) return;
            lines.Add(new CorpusLine(number, segments, targets, units, marks, golden[number].Samples, golden[number].Sha));
        }

        foreach (string row in File.ReadLines(Fixture("flite-m6-synth.txt")))
        {
            string[] c = row.Split('\t');
            switch (c[0])
            {
                case var s when s.StartsWith("#---", StringComparison.Ordinal):
                    Flush();
                    number = int.Parse(s[5..], CultureInfo.InvariantCulture);
                    segments = [];
                    targets = [];
                    units = [];
                    marks = null;
                    break;
                case "#DSEG":
                    segments.Add(new FliteSegmentInput(c[2], Bits(c[3]), c[4], c[5]));
                    break;
                case "#TARGET":
                    targets.Add(new FliteTarget(Bits(c[2]), Bits(c[3])));
                    break;
                case "#UNIT":
                    units.Add(new FliteUnit(c[2], int.Parse(c[3], CultureInfo.InvariantCulture), int.Parse(c[4], CultureInfo.InvariantCulture), int.Parse(c[5], CultureInfo.InvariantCulture), int.Parse(c[6], CultureInfo.InvariantCulture)));
                    break;
                case "#PM":
                    marks = [];
                    foreach (string pair in c.Skip(2))
                    {
                        string[] tf = pair.Split(':');
                        marks.Add((int.Parse(tf[0], CultureInfo.InvariantCulture), int.Parse(tf[1], CultureInfo.InvariantCulture)));
                    }

                    break;
            }
        }

        Flush();
        return lines;
    }
}
