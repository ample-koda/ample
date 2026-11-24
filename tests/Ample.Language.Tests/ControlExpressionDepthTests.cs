namespace Ample.Language.Tests;

/// <summary>The value of a block parameter (<c>600 + 3000 * traffic.level</c>) comes from the text of a song: it must not be able to end the process by its depth.</summary>
public sealed class ControlExpressionDepthTests
{
    private static string Repeat(string piece, int times) => string.Concat(Enumerable.Repeat(piece, times));

    public static TheoryData<string> Shapes => new()
    {
        Repeat("(", 100_000) + "1" + Repeat(")", 100_000),
        Repeat("-", 200_000) + "1",
        string.Join("+", Enumerable.Repeat("1", 100_000)) + "+traffic.level",
        string.Join("*", Enumerable.Repeat("1", 100_000)) + "*traffic.level",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_very_deep_or_long_control_expression_is_refused_and_not_the_end_of_the_process(string text)
    {
        ControlExpression? expression = ControlExpression.TryParse(text, out string? error);
        Assert.True(expression is null && error is not null, "a deep expression must be refused");
    }

    [Fact]
    public void A_normal_control_expression_still_reads_and_evaluates()
    {
        ControlExpression? expression = ControlExpression.TryParse("600 + 3000 * (traffic.level)", out string? error);
        Assert.NotNull(expression);
        Assert.Null(error);
        Assert.Equal(3600_000, expression.Evaluate(_ => 1000));
    }
}
