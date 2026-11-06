namespace Ample.Language;

/// <summary>
/// What an error means for the views of a song (plan/editor-tabs.md, part 4). Most errors sit in one place of a graph that exists (an invalid value, an unknown parameter,
/// a connection that does not fit): that block is marked, the rest of the song is still there. A few errors are about the shape of the text itself: the text cannot be
/// read into blocks and links with any trust (no composition line, a block that is not closed, text that is not a block). Only then is the Blocks view out of use and
/// the code view the place to fix it.
/// </summary>
public static class DiagnosticKinds
{
    private static readonly HashSet<string> GraphBreaking = new(StringComparer.Ordinal)
    {
        "AMP1001",   // the composition line is missing
        "AMP1003",   // the composition line is malformed
        "AMP1100",   // text that is not a block, a comment or a layout line
        "AMP1101",   // a block that is not closed
        "AMP1102",   // a block header that cannot be read
        "AMP1116",   // a block comment that never ends
    };

    /// <summary>Gets the codes whose errors make the text unreadable as a graph (for a test that every one of them exists in the spec).</summary>
    public static IReadOnlyCollection<string> GraphBreakingCodes => GraphBreaking;

    /// <summary>Gets whether an error with this code stops the text from being read into blocks and links.</summary>
    /// <param name="code">The diagnostic code, for example <c>AMP1101</c>.</param>
    /// <returns>True when the Blocks view cannot show the song.</returns>
    public static bool BreaksGraph(string code) => GraphBreaking.Contains(code);

    /// <summary>Gets whether the document has at least one error that stops it from being read into blocks and links.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>True when the Blocks view is out of use and the errors have to be fixed in the code view.</returns>
    public static bool GraphBroken(AmpleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Diagnostics.Any(d => d.Severity == Severity.Error && BreaksGraph(d.Code));
    }

    /// <summary>Finds the block a diagnostic belongs to: the block whose lines (header to closing brace) hold the line of the diagnostic.</summary>
    /// <param name="document">The parsed document.</param>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <returns>The block name, or null when the diagnostic is about the song (the composition line, a layout line, text between blocks).</returns>
    public static string? BlockOf(AmpleDocument document, Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.Code.StartsWith("AMP100", StringComparison.Ordinal)) return null;   // the composition line and the song settings (AMP1001 to AMP1009) belong to the song, not to a block
        int line = diagnostic.Line - 1;   // a diagnostic counts lines from 1, a block from 0
        List<BlockNode> blocks = [.. document.Blocks.OrderBy(b => b.Line)];
        for (int i = 0; i < blocks.Count; i++)
        {
            BlockNode block = blocks[i];
            int last = block.CloseLine >= 0 ? block.CloseLine : (i + 1 < blocks.Count ? blocks[i + 1].Line - 1 : int.MaxValue);
            if (line >= block.Line && line <= last) return block.Name.Length > 0 ? block.Name : null;
        }

        return null;
    }
}
