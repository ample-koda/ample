using System.Collections.Concurrent;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>
/// The last step of the ladder (plan/speech/flite/2-verification.md, step 11): the whole engine, text to samples, gives the same number of samples and the same SHA-256 as the pinned C program
/// (one call of <c>flite_synth_text</c> per text) on 6,970 texts. A text on which the C program dies or hangs must fail with a
/// <see cref="FliteRenderException"/>, never with another error and never with a waveform.
/// </summary>
public class FliteEndToEndParityTests
{
    private static readonly Lazy<List<byte[]>> Texts = new(EndToEndFixtures.Corpus);
    private static readonly Lazy<List<EndToEndFixtures.Reference>> References = new(EndToEndFixtures.References);

    [Fact]
    public void The_corpus_and_the_reference_have_the_same_number_of_lines()
    {
        Assert.Equal(6970, Texts.Value.Count);
        Assert.Equal(Texts.Value.Count, References.Value.Count);
        Assert.True(References.Value.Count(r => r.Status == "OK") > 6600, "almost every text has a waveform");
    }

    [Fact]
    public void The_reference_of_this_corpus_contains_the_golden_hashes_of_the_first_milestone()
    {
        // the 341 lines of the first corpus are part of the M3 corpus: the new dumper must give the hashes of the dumper of milestone 0 (the golden data of M6) for them
        HashSet<string> reference = [.. References.Value.Where(r => r.Status == "OK").Select(r => r.Sha256)];
        string[] golden = File.ReadAllLines(EndToEndFixtures.Path("flite-m6-pcm.tsv")).Skip(1).ToArray();
        Assert.Equal(341, golden.Length);
        string[][] rows = [.. golden.Select(l => l.Split((char)9))];
        // the 18 lines that give no samples in the golden data are the texts the new dumper reports as EMPTY
        Assert.Equal(18, rows.Count(f => f[2] == "0"));
        Assert.True(References.Value.Count(r => r.Status == "EMPTY") >= 18);
        List<string> missing = [.. rows.Where(f => f[2] != "0" && !reference.Contains(f[4])).Select(f => f[0])];
        Assert.True(missing.Count == 0, $"{missing.Count} golden lines are not in the reference of this corpus: {string.Join(' ', missing.Take(10))}");
    }

    [Fact]
    public void Every_text_of_the_corpus_gives_the_samples_of_the_c_program()
    {
        List<byte[]> texts = Texts.Value;
        List<EndToEndFixtures.Reference> references = References.Value;
        ConcurrentBag<string> failures = [];
        int compared = 0, equal = 0, crashes = 0;
        Parallel.For(0, texts.Count, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
        {
            EndToEndFixtures.Reference reference = references[i];
            Interlocked.Increment(ref compared);
            try
            {
                FliteRenderResult result = FliteEngine.Default.Render(texts[i]);
                if (reference.Status == "CRASH")
                {
                    failures.Add($"{i + 1} [{EndToEndFixtures.Printable(texts[i])}]: the C program died (signal {reference.Signal}), the port gives {result.Samples.Length} samples");
                }
                else if (reference.Status == "EMPTY" ? result.Samples.Length != 0 : result.Samples.Length != reference.Samples || EndToEndFixtures.Sha256(result.Samples) != reference.Sha256)
                {
                    string got = EndToEndFixtures.Sha256(result.Samples)[..12];
                    string want = reference.Sha256.Length >= 12 ? reference.Sha256[..12] : string.Empty;
                    failures.Add($"{i + 1} [{EndToEndFixtures.Printable(texts[i])}]: {result.Samples.Length} samples {got}, reference {reference.Status} {reference.Samples} {want}");
                }
                else
                {
                    Interlocked.Increment(ref equal);
                }
            }
            catch (FliteRenderException) when (reference.Status == "CRASH")
            {
                Interlocked.Increment(ref crashes);
            }
            catch (Exception e)
            {
                failures.Add($"{i + 1} [{EndToEndFixtures.Printable(texts[i])}]: {e.GetType().Name}: {e.Message} (reference {reference.Status} {reference.Samples})");
            }
        });

        Assert.Equal(texts.Count, compared);
        string[] first = [.. failures.OrderBy(f => int.Parse(f.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).Take(15)];
        Assert.True(failures.IsEmpty, $"{failures.Count} differences ({equal} equal, {crashes} crash cases failed cleanly), first ones:\n{string.Join('\n', first)}");
    }
}
