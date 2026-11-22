using System.Text.Json.Nodes;
using Ample.Language;

namespace Ample.Server.Tests;

/// <summary>What Koda is told about the local speech block (ample_lookup topic=howto).</summary>
public sealed class AiContextSpeechTests
{
    private static string SpeechHowTo(string query)
    {
        JsonArray found = AiContext.HowTo(query)["howtos"]!.AsArray();
        // only the local speech entry: a question about speech also finds the voice-model how-to, which is about something else
        return string.Join('\n', found.Where(item => item!["about"]!.ToString().StartsWith("Local speech", StringComparison.Ordinal))
            .Select(item => item!["about"] + ": " + item["howto"]));
    }

    [Theory]
    [InlineData("speech")]
    [InlineData("local speech")]
    [InlineData("text to speech")]
    [InlineData("phonemes")]
    [InlineData("words mode")]
    [InlineData("sing words")]
    public void The_speech_questions_find_the_local_speech_how_to(string query)
    {
        string text = SpeechHowTo(query);

        Assert.Contains("Local speech (the speech block)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_how_to_gives_the_rules_that_a_wrong_block_breaks_and_the_limits()
    {
        string text = SpeechHowTo("local speech");

        foreach (string expected in new[] { "text OR phonemes", "never empty", "AMP5351", "AMPL924", "mode \"words\"", "duration_policy", "at most 4", "16 speech blocks", "60 seconds", "4096 characters" })
            Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("xtts", text, StringComparison.OrdinalIgnoreCase);   // the non-commercial model is never suggested here
    }

    [Fact]
    public void The_example_in_the_how_to_is_a_song_that_parses_without_errors()
    {
        string text = SpeechHowTo("local speech");
        int start = text.IndexOf("speech voice at 1 for 1 {", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string block = text[start..(text.IndexOf('}', start) + 1)];
        string song = "composition \"t\" { ample 1 tempo 120 master-seed 1 }\n" + block + "\nmaster master {\n  from voice.audio -> audio\n}\n";
        SpecData spec = Ample.Docs.SpecRoot.At(FindRoot()).LoadSpecData();

        AmpleDocument doc = Parser.Parse(song, spec);

        Assert.DoesNotContain(doc.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        Assert.Contains(doc.Blocks, b => b.Type == "speech");
    }

    private static string FindRoot()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !Directory.Exists(Path.Combine(folder, "spec", "templates"))) folder = Path.GetDirectoryName(folder);
        return folder ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
