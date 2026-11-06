using Ample.Language;

namespace Ample.Server;

/// <summary>A block that a build left out because of an error in it.</summary>
/// <param name="Block">The block name.</param>
/// <param name="Code">The diagnostic code of the first error of the block.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Line">The 1-based line of that error.</param>
internal sealed record SkippedBlock(string Block, string Code, string Message, int Line);

/// <summary>
/// "A block with an error does not stop the build" (plan/editor-tabs.md, decision 8): a build of a song whose errors all sit in blocks leaves those blocks out (and the lines that read
/// from them) and builds the rest, saying loudly which blocks were skipped. A song whose text cannot be read into blocks (a graph-breaking error), or with an error that belongs to no
/// block (the composition line, a component that is not right), is still refused as before.
/// </summary>
internal static class BrokenBlocks
{
    /// <summary>Leaves the blocks with errors out of a song text, round after round (a block that read from a skipped one may become an error itself).</summary>
    /// <param name="text">The song text.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="skipped">The blocks that were left out, in the order they were found.</param>
    /// <returns>The cleaned text, or null when the song cannot be built even without its broken blocks.</returns>
    internal static string? Skip(string text, SpecData spec, out IReadOnlyList<SkippedBlock> skipped)
    {
        List<SkippedBlock> found = [];
        skipped = found;
        string current = text;
        for (int round = 0; round < 12; round++)
        {
            AmpleDocument document = Parser.Parse(current, spec);
            List<Diagnostic> errors = [.. document.Diagnostics.Where(d => d.Severity == Severity.Error)];
            if (errors.Count == 0) return current;
            if (DiagnosticKinds.GraphBroken(document)) return null;
            Dictionary<string, Diagnostic> broken = [];
            foreach (Diagnostic error in errors)
            {
                string? block = DiagnosticKinds.BlockOf(document, error);
                if (block is null) return null;   // an error of the song itself: nothing to leave out
                broken.TryAdd(block, error);
            }

            foreach ((string block, Diagnostic error) in broken) found.Add(new SkippedBlock(block, error.Code, error.Message, error.Line));
            current = Remove(current, document, [.. broken.Keys]);
        }

        return null;
    }

    private static string Remove(string text, AmpleDocument document, HashSet<string> names)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        HashSet<int> drop = [];
        foreach (BlockNode block in document.Blocks)
        {
            if (names.Contains(block.Name))
            {
                for (int line = block.Line; line <= block.CloseLine && line < lines.Length; line++) drop.Add(line);
                continue;
            }

            foreach (FromLine from in block.Froms.Where(f => names.Contains(f.Block))) drop.Add(from.Line);   // a line that reads from a skipped block goes too
        }

        return string.Join('\n', lines.Where((_, index) => !drop.Contains(index)));
    }
}
