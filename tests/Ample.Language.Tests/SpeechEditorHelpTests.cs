using Xunit.Abstractions;

namespace Ample.Language.Tests;

/// <summary>What the editor shows for a speech block: hover on the block and its parameters, and completion of parameters and values.</summary>
public sealed class SpeechEditorHelpTests(ITestOutputHelper output)
{
    private const string Song = "composition \"t\" { ample 1 tempo 120 master-seed 1 }\n\nspeech voice at 1 for 1 {\n  engine \"retro\"\n  mode \"words\"\n  text \"I am a robot.\"\n  gain 3 dB\n}\n";

    private static (int Line, int Col) At(string song, string needle)
    {
        string[] lines = song.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].IndexOf(needle, StringComparison.Ordinal) is var at and >= 0) return (i + 1, at + 2);
        throw new InvalidOperationException(needle);
    }

    private string Hover(string needle)
    {
        AmpleDocument doc = Parser.Parse(Song, Corpus.Spec);
        (int line, int col) = At(Song, needle);
        string? hover = Queries.HoverAt(doc, Corpus.Spec, line, col);
        output.WriteLine($"[{needle}] {hover}");
        return hover ?? string.Empty;
    }

    [Fact]
    public void Hover_on_the_block_says_what_it_is()
    {
        string hover = Hover("speech voice");

        Assert.Contains("speech", hover, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("midi", hover, StringComparison.OrdinalIgnoreCase);   // the notes port
    }

    [Theory]
    [InlineData("engine \"retro\"", "Choices: retro, humanico")]
    [InlineData("mode \"words\"", "words")]
    [InlineData("text \"I am", "must not be empty")]
    [InlineData("gain 3", "From -60 dB to 12 dB")]
    public void Hover_on_a_parameter_gives_its_documentation(string needle, string expected)
    {
        Assert.Contains(expected, Hover(needle), StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<CompletionItem> Complete(string linePrefix, string? extraLines = null)
    {
        string song = "composition \"t\" { ample 1 tempo 120 master-seed 1 }\n\nspeech voice at 1 for 1 {\n" + (extraLines ?? string.Empty) + linePrefix;
        AmpleDocument doc = Parser.Parse(song + "\n}\n", Corpus.Spec);
        string[] lines = song.Split('\n');
        IReadOnlyList<CompletionItem> items = Queries.CompletionAt(doc, Corpus.Spec, lines.Length, lines[^1].Length + 1);
        output.WriteLine($"[{linePrefix}] " + string.Join(", ", items.Select(item => item.Label)));
        return items;
    }

    [Fact]
    public void The_editor_snippets_have_a_sentence_and_a_words_snippet_and_no_empty_placeholder_anywhere()
    {
        using System.Text.Json.JsonDocument snippets = System.Text.Json.JsonDocument.Parse(Corpus.Read("editors/vscode/snippets/amplet.code-snippets"));
        string[] prefixes = [.. snippets.RootElement.EnumerateObject().SelectMany(entry => entry.Value.GetProperty("prefix").EnumerateArray().Select(p => p.GetString()!))];
        Assert.Contains("speech-text", prefixes);
        Assert.Contains("speech-words", prefixes);

        // a placeholder with nothing in it would write an empty line such as text or phonemes with no value
        string[] bodies = [.. snippets.RootElement.EnumerateObject().SelectMany(entry => entry.Value.GetProperty("body").EnumerateArray().Select(line => line.GetString()!))];
        Assert.DoesNotContain(bodies, line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\$\{\d+:\}"));
    }

    [Fact]
    public void Completion_in_a_speech_block_offers_its_parameters()
    {
        string[] labels = [.. Complete("  ").Select(item => item.Label)];

        foreach (string expected in new[] { "text", "phonemes", "engine", "mode", "rate", "pitch_hz", "gain", "duration_policy" })
            Assert.Contains(expected, labels);
    }

    [Theory]
    [InlineData("  engine ", "humanico", "retro")]
    [InlineData("  mode ", "text", "words")]
    [InlineData("  duration_policy ", "natural", "gate")]
    public void Completion_of_a_choice_offers_its_values(string prefix, string first, string second)
    {
        string[] labels = [.. Complete(prefix).Select(item => item.Label.Trim('"'))];

        Assert.Contains(first, labels);
        Assert.Contains(second, labels);
    }
}
