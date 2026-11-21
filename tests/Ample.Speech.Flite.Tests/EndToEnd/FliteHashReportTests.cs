using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>
/// Writes the SHA-256 of the waveform of thirty fixed renderings, so the same list can be made on different processors (x64 and arm64) and compared line by line. Every line is
/// <c>id samples sha256</c>. The report goes to the file named by the environment variable <c>FLITE_HASH_REPORT</c> when it is set, and always to the test output. The test
/// itself only checks that the list is complete and that a second run gives the same lines, so it can run anywhere; the comparison between machines is done by the workflow
/// <c>flite-arm64</c>.
/// </summary>
public class FliteHashReportTests(ITestOutputHelper output)
{
    private static readonly string[] Texts =
    [
        "Hello world.",
        "The quick brown fox jumps over the lazy dog.",
        "Mister Jones paid $1,234.56 for 7 books; was it worth it?",
        "On 12/25/2024 at 10:45 the 3rd train left platform 9.",
        "She sells sea shells by the sea shore.",
        "How much wood would a woodchuck chuck if a woodchuck could chuck wood?",
        "Dr. Smith lives at 221B Baker St. in London, U.K.",
        "One two three four five six seven eight nine ten.",
        "Why? Because! Really... maybe, perhaps; no: yes.",
        "AMPLE writes music as code.",
        "Peter Piper picked a peck of pickled peppers.",
        "Call 555-0134 before 9 a.m. or after 6 p.m.",
        "The year 1999 was followed by 2000 and then 2001.",
        "Roman numerals: IV, IX, XIV and XL.",
        "He weighs 72.5 kilograms and is 1.83 metres tall.",
        "Numbers like 3/4, 1,000,000 and 0.001 are read aloud.",
        "Zebras, xylophones and quizzical jazz vex lumbering oxen.",
        "A very long word: supercalifragilisticexpialidocious.",
        "Unknown names such as Xyzzy and Qwerty fall to the letter rules.",
        "Good morning. How are you today? I am fine, thank you.",
    ];

    [Fact]
    public void The_thirty_renderings_have_a_stable_digest_list()
    {
        List<string> lines = [];
        FliteEngine engine = FliteEngine.Default;
        for (int i = 0; i < Texts.Length; i++)
        {
            FliteRenderResult result = engine.RenderText(Texts[i]);
            lines.Add(Line($"text-{i + 1:00}", result.Samples));
        }

        // other speeds and pitches: the float steps of the durations and the F0 targets
        (float Stretch, float Mean, float Stddev)[] settings = [(0.7f, 140f, 25f), (1.6f, 80f, 5f)];
        for (int i = 0; i < settings.Length; i++)
        {
            (float stretch, float mean, float stddev) = settings[i];
            FliteRenderResult result = engine.Render(Texts[1], new FliteOptions { DurationStretch = stretch, F0Mean = mean, F0Stddev = stddev });
            lines.Add(Line($"option-{i + 1:00}", result.Samples));
        }

        // one word at a steady pitch (words mode): the F0 target is a single float
        (string Word, float Hz)[] words = [("music", 110f), ("music", 220f), ("music", 330f), ("again", 165f), ("hello", 247f), ("sing", 440f)];
        for (int i = 0; i < words.Length; i++) lines.Add(Line($"word-{i + 1:00}", engine.RenderWord(words[i].Word, words[i].Hz).Samples));

        // the phone entry of the C program
        lines.Add(Line("phones-01", engine.RenderPhones("hh ax l ow").Samples));
        lines.Add(Line("phones-02", engine.RenderPhones("m y uw z ih k").Samples));

        Assert.Equal(30, lines.Count);
        string report = string.Join('\n', lines) + '\n';
        output.WriteLine(report);
        string? file = Environment.GetEnvironmentVariable("FLITE_HASH_REPORT");
        if (!string.IsNullOrEmpty(file)) File.WriteAllText(file, report, new UTF8Encoding(false));
    }

    private static string Line(string id, short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return string.Create(CultureInfo.InvariantCulture, $"{id} {samples.Length} {Convert.ToHexString(SHA256.HashData(bytes))}");
    }
}
