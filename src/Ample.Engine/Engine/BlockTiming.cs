using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Engine.Engine;

/// <summary>Separates local block audio from its placement on the song timeline.</summary>
internal static class BlockTiming
{
    internal static bool IsTimeline(BlockNode block) => block.Type is "mixer" or "bus" or "master";

    internal static bool IsArranged(BlockNode block, AmpleDocument document, HashSet<string> visited)
    {
        if (IsTimeline(block)) return true;
        if (!visited.Add(block.Name)) return false;
        FromLine? input = block.Froms.Where(f => !f.Disabled).FirstOrDefault(f => f.Input is "in" or "render_in");
        BlockNode? parent = document.Blocks.FirstOrDefault(b => b.Name == input?.Block);
        return parent is not null && IsArranged(parent, document, visited);
    }

    internal static int Origin(BlockNode block, AmpleDocument document, HashSet<string> visited)
    {
        if (IsTimeline(block) || !visited.Add(block.Name)) return 1;
        if (block.Parts.Count > 0) return block.Parts.Min(p => p.At);
        FromLine? input = block.Froms.Where(f => !f.Disabled).FirstOrDefault(f => f.Input is "in" or "render_in" or "notes");
        BlockNode? parent = document.Blocks.FirstOrDefault(b => b.Name == input?.Block);
        return parent is null ? 1 : Origin(parent, document, visited);
    }

    /// <summary>
    /// The length in bars of a block: its placement, its <c>bars</c> parameter, what <paramref name="inferred"/> says about it (an unplaced sequencer is as long as its playlist), else the
    /// length of the block that feeds it, else 8.
    /// </summary>
    internal static int Bars(BlockNode block, AmpleDocument document, HashSet<string> visited, Func<BlockNode, int?>? inferred = null)
    {
        if (!visited.Add(block.Name)) return 8;
        if (block.Parts.Count > 0) return block.Parts.Max(p => p.At + p.Length) - block.Parts.Min(p => p.At);
        if (block.Parameters.FirstOrDefault(p => p.Name == "bars") is { Number: double bars }) return Math.Max(1, (int)bars);
        if (inferred?.Invoke(block) is int known) return Math.Max(1, known);
        // a curve or a follow that is not placed lasts as long as the song (a control that stops after 8 bars would leave what it steers behind); what reads it (map, fire) follows
        if (block.Type is "curve" or "follow") return Math.Max(8, document.Blocks.Where(b => b.Parts.Count > 0).Select(b => b.Parts.Max(p => p.At + p.Length) - 1).DefaultIfEmpty(8).Max());
        FromLine? input = block.Froms.Where(f => !f.Disabled).FirstOrDefault(f => f.Input is "in" or "render_in" or "notes");
        BlockNode? parent = document.Blocks.FirstOrDefault(b => b.Name == input?.Block);
        return parent is null || IsTimeline(parent) ? 8 : Bars(parent, document, visited, inferred);
    }

    internal static BlockNode Local(BlockNode block)
    {
        int offset = IsTimeline(block) || block.Parts.Count == 0 ? 0 : block.Parts.Min(p => p.At) - 1;
        BlockNode copy = new() { Type = block.Type, Name = block.Name, RawName = block.RawName, Class = block.Class, Version = block.Version, Freeze = block.Freeze, Line = block.Line, CloseLine = block.CloseLine, Skipped = block.Skipped };
        copy.Parts.AddRange(block.Parts.Select(p => p with { At = p.At - offset }));
        copy.Flags.AddRange(block.Flags);
        copy.Froms.AddRange(block.Froms.Where(f => !f.Disabled));
        copy.ControlRefs.AddRange(block.ControlRefs);   // the controls its parameters read go along (they are dependencies too)
        copy.Parameters.AddRange(block.Parameters);
        return copy;
    }

    internal static byte[] Place(byte[] audio, int origin, BarRange range, int samplesPerBar)
    {
        if (ChannelAudio.IsStereo(audio))
        {
            (StereoBuffer buffer, int sampleRate) = ChannelAudio.Decode(audio);
            short[] left = Wav.Decode(Place(Wav.Encode(buffer.Left, sampleRate), origin, range, samplesPerBar)).Samples;
            short[] right = Wav.Decode(Place(Wav.Encode(buffer.Right, sampleRate), origin, range, samplesPerBar)).Samples;
            return StereoWav.Encode(new StereoBuffer(left, right), sampleRate);
        }
        (short[] samples, int rate) = Wav.Decode(audio);
        short[] placed = new short[checked(range.Bars * samplesPerBar)];
        long offset = (long)(origin - range.FirstBar) * samplesPerBar;
        int source = (int)Math.Min(samples.Length, Math.Max(0, -offset));
        int target = (int)Math.Min(placed.Length, Math.Max(0, offset));
        int count = Math.Min(samples.Length - source, placed.Length - target);
        if (count > 0) samples.AsSpan(source, count).CopyTo(placed.AsSpan(target));
        return Wav.Encode(placed, rate);
    }
}
