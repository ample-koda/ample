using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>Every block template carries a small tested example song (wish 3 of the docs feedback, 2026-11-01).</summary>
public sealed class TemplateExampleTests
{
    public static IEnumerable<object[]> Templates() =>
        Directory.GetFiles(Path.Combine(Corpus.Root, "spec", "templates"), "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    [Theory]
    [MemberData(nameof(Templates))]
    public void The_example_parses_without_errors_and_uses_the_block(string id)
    {
        using JsonDocument doc = JsonDocument.Parse(Corpus.Read($"spec/templates/{id}.json"));
        Assert.True(doc.RootElement.TryGetProperty("example", out JsonElement example), $"{id} has no example");
        string text = Corpus.Normalize(example.GetString()!);

        AmpleDocument parsed = Parser.Parse(text, Corpus.Spec);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(parsed.Blocks, b => b.Type == id);

        // no old port names: the same song with lint L010 switched on must not report any
        string[] lines = text.Split('\n');
        int song = Array.FindIndex(lines, l => l.StartsWith("composition ", StringComparison.Ordinal));
        string probed = string.Join('\n', lines.Take(song + 1).Append("lint { L010 warning }").Concat(lines.Skip(song + 1)));
        AmpleDocument linted = Parser.Parse(probed, Corpus.Spec);
        Assert.DoesNotContain(linted.Diagnostics, d => d.Code == "L010");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void The_description_says_what_the_block_does(string id)
    {
        using JsonDocument doc = JsonDocument.Parse(Corpus.Read($"spec/templates/{id}.json"));
        Assert.True(doc.RootElement.GetProperty("description").GetString()!.Length >= 30, $"{id}: description is too terse");
    }
}
