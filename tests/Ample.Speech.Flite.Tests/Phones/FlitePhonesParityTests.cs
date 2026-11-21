using Ample.Speech.Flite.Tests.EndToEnd;

namespace Ample.Speech.Flite.Tests.Phones;

/// <summary>
/// The explicit-phones path (<c>flite -p</c>, <c>flite_synth_phones</c>, module <c>tokentosegs</c>): the whole engine from a phone string to samples gives the
/// same number of samples and the same SHA-256 as the pinned C program on 501 phone strings (every phone alone, with stress digits, words, phrases with pauses,
/// syllable breaks, punctuation, unknown phones, upper case, empty input and 300 generated sequences). Where the C program dies (an invalid input) the port
/// must fail with a <see cref="FliteRenderException"/>, never with another error and never with a waveform.
/// The reference is <c>plan/speech/flite/reference/phones</c> (<c>dumpphones.c</c>, one process per line).
/// </summary>
public class FlitePhonesParityTests
{
    private static readonly Lazy<string[]> Lines = new(() => File.ReadAllText(EndToEndFixtures.Path("flite-phones-corpus.txt")).Split('\n')[..^1]);
    private static readonly Lazy<string[][]> Reference = new(() => [.. File.ReadAllLines(EndToEndFixtures.Path("flite-phones-ref.tsv")).Select(l => l.Split('\t'))]);

    [Fact]
    public void The_corpus_and_the_reference_have_the_same_number_of_lines()
    {
        Assert.Equal(501, Lines.Value.Length);
        Assert.Equal(Lines.Value.Length, Reference.Value.Length);
        Assert.True(Reference.Value.Count(r => r[1] == "OK") > 250, "enough phone strings have a waveform");
        Assert.True(Reference.Value.Any(r => r[1] == "CRASH"), "the corpus has inputs on which C dies");
    }

    [Fact]
    public void Every_phone_string_gives_the_samples_of_the_c_program()
    {
        List<string> failures = [];
        int equal = 0, empty = 0, died = 0;
        for (int i = 0; i < Lines.Value.Length; i++)
        {
            string[] reference = Reference.Value[i];
            string label = $"{i + 1} [{Lines.Value[i].Replace("\t", "\\t", StringComparison.Ordinal)}]";
            try
            {
                FliteRenderResult result = FliteEngine.Default.RenderPhones(Lines.Value[i]);
                if (reference[1] == "CRASH")
                {
                    failures.Add($"{label}: the C program died (signal {reference[2]}), the port gives {result.Samples.Length} samples");
                }
                else if (reference[1] == "EMPTY")
                {
                    if (result.Samples.Length != 0) failures.Add($"{label}: C gives no waveform, the port {result.Samples.Length} samples");
                    else empty++;
                }
                else if (result.Samples.Length != int.Parse(reference[2], System.Globalization.CultureInfo.InvariantCulture) || EndToEndFixtures.Sha256(result.Samples) != reference[4])
                {
                    failures.Add($"{label}: {result.Samples.Length} samples {EndToEndFixtures.Sha256(result.Samples)[..12]}, reference {reference[2]} {reference[4][..12]}");
                }
                else
                {
                    equal++;
                }
            }
            catch (FliteRenderException) when (reference[1] == "CRASH")
            {
                died++;
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                failures.Add($"{label}: {e.GetType().Name}: {e.Message} (reference {reference[1]})");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {Lines.Value.Length} differ ({equal} equal, {empty} empty, {died} refused as C dies):\n" + string.Join('\n', failures.Take(25)));
        Assert.True(equal > 250);
    }
}
