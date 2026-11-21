using Ample.Speech.Flite.Tests.EndToEnd;

namespace Ample.Speech.Flite.Tests.Markup;

/// <summary>
/// The inline markup sets the token features that the duration model and the F0 model of Flite read (<c>local_duration_stretch</c>, <c>local_f0_shift</c>, <c>local_f0_range</c>).
/// With the same features on the same tokens the C program (<c>reference/x5/dumpmarks.c</c>) and the port give the same samples: 320 generated sentences with one to three tagged regions.
/// </summary>
public class FliteMarkupParityTests
{
    private static readonly Lazy<string[][]> Lines = new(() => [.. File.ReadAllLines(EndToEndFixtures.Path("flite-x5-marks.tsv")).Select(l => l.Split('\t'))]);

    [Fact]
    public void Every_marked_sentence_gives_the_samples_of_the_c_program_with_the_same_token_features()
    {
        Assert.Equal(320, Lines.Value.Length);
        List<string> failures = [];
        foreach (string[] line in Lines.Value)
        {
            FliteMarkup markup = FliteMarkup.Parse(line[0], 95);
            FliteRenderResult made = FliteEngine.Default.Render(markup.Plain, new FliteOptions { Markup = markup });
            bool same = line[2] == "OK"
                ? made.Samples.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) == line[3] && EndToEndFixtures.Sha256(made.Samples) == line[4]
                : made.Samples.Length == 0;
            if (!same) failures.Add(line[0]);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} differ from C: {string.Join(" | ", failures.Take(5))}");
    }

    [Fact]
    public void A_text_without_tags_is_the_exact_render_and_a_tag_that_asks_for_nothing_changes_nothing()
    {
        const string plain = "This is music today again.";
        FliteMarkup none = FliteMarkup.Parse(plain, 95);
        FliteMarkup tagged = FliteMarkup.Parse("This is <pitch 95>music</pitch> today again.", 95);

        Assert.False(none.HasMarkup);
        Assert.Equal(plain, tagged.Plain);
        short[] exact = FliteEngine.Default.Render(plain).Samples;
        Assert.Equal(exact, FliteEngine.Default.Render(tagged.Plain, new FliteOptions { Markup = tagged }).Samples);
    }
}
