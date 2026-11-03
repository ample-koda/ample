using System.Text;

namespace Ample.Lsp.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Stdio_framing_round_trips_messages_including_non_ascii_text()
    {
        using MemoryStream wire = new();
        using StdioChannel writer = new(Stream.Null, wire);
        await writer.WriteAsync("{\"a\":\"héllo 😀\"}", CancellationToken.None);
        await writer.WriteAsync("{\"b\":2}", CancellationToken.None);
        wire.Position = 0;
        string header = Encoding.ASCII.GetString(wire.ToArray(), 0, 20);
        Assert.StartsWith("Content-Length: ", header, StringComparison.Ordinal);
        using StdioChannel reader = new(wire, Stream.Null);
        Assert.Equal("{\"a\":\"héllo 😀\"}", await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("{\"b\":2}", await reader.ReadAsync(CancellationToken.None));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_truncated_body_ends_the_stream()
    {
        using MemoryStream wire = new(Encoding.ASCII.GetBytes("Content-Length: 50\r\n\r\n{\"short\":1}"));
        using StdioChannel reader = new(wire, Stream.Null);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("a😀b", 1, 1)]
    [InlineData("a😀b", 2, 3)]
    [InlineData("a😀b", 3, 4)]
    [InlineData("abc", 5, 5)]
    public void Code_point_columns_become_utf16_columns(string line, int codePoints, int expected) => Assert.Equal(expected, Positions.CodePointsToUtf16(line, codePoints));

    [Fact]
    public void Incremental_changes_apply_in_the_middle_and_across_lines()
    {
        Assert.Equal("a1\nbXb", Positions.Apply("a\nb", 0, 1, 1, 0, "1\nbX"));
        Assert.Equal("ac", Positions.Apply("abc", 0, 1, 0, 2, string.Empty));
        Assert.Equal("new", Positions.Apply("old", null, 0, 0, 0, "new"));
        Assert.Equal("ab!", Positions.Apply("ab", 5, 0, 5, 0, "!"));
    }
}
