namespace Ample.Language.Tests;

/// <summary>
/// Text from outside (a Remix visitor, a project, a package) must never make the parser or a later pass recurse until the stack of the process is gone:
/// that ends the whole server and cannot be caught. A very deep or very long expression is refused with a normal error.
/// </summary>
public sealed class NestingDepthTests
{
    private static string Repeat(string piece, int times) => string.Concat(Enumerable.Repeat(piece, times));

    public static TheoryData<string> Shapes => new()
    {
        Repeat("(", 100_000) + "1" + Repeat(")", 100_000),
        Repeat("!", 200_000) + "true",
        Repeat("-", 100_000) + "1",
        string.Join("+", Enumerable.Repeat("1", 100_000)),
        Repeat("[", 100_000) + Repeat("]", 100_000),
        string.Join(" |> ", Enumerable.Repeat("theme.notes", 50_000)),
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_very_deep_or_very_long_expression_is_an_error_and_not_the_end_of_the_process(string expression)
    {
        string text = "helper deep() -> Int {\n  return " + expression + "\n}\n";
        AmpleDocument document = Parser.Parse(text, Corpus.Spec, new ParseOptions { RequireProjectLine = false });
        Assert.Contains(document.Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void A_normal_nesting_is_still_fine()
    {
        string text = "helper shallow() -> Int {\n  return " + Repeat("(", 30) + "1" + Repeat(")", 30) + " + " + string.Join(" + ", Enumerable.Repeat("2", 100)) + "\n}\n";
        AmpleDocument document = Parser.Parse(text, Corpus.Spec, new ParseOptions { RequireProjectLine = false });
        Assert.DoesNotContain(document.Diagnostics, d => d.Message.Contains("too deeply", StringComparison.Ordinal));
    }
}
