using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// The import-audio block: plays a WAV file of the project (for example a take made with the microphone recorder) from the start of the block. The file is part of the plan of the
/// take (its hash), so replacing the file makes a new take. A 16-bit stereo file at the rate of the song keeps its stereo image; anything else is mixed to mono and resampled.
/// </summary>
internal sealed class ImportAudioRenderer : IBlockRenderer
{
    public string BlockType => "import-audio";

    public string Id => "builtin.import-audio";

    public string Version => "4";

    /// <summary>Resolves the project file a block names in its <c>file</c> parameter, or null when it names none.</summary>
    internal static string? FileOf(string? projectRoot, BlockNode block, string? ampleRoot = null)
    {
        string? value = block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"');
        // a convolution block can pick one of the impulse responses of the installed pack by name (ir room, hall, plate, church, street; real-stairwell, garage, car, basement, microverb come from the impulse-responses-real pack)
        if (block.Type == "convolution" && block.Parameters.FirstOrDefault(p => p.Name == "ir")?.Value.Trim().Trim('"') is { Length: > 0 } preset && preset != "file")
        {
            value = preset.StartsWith("real-", StringComparison.Ordinal) ? "pack:impulse-responses-real/" + preset[5..] : "pack:impulse-responses/" + preset;
        }

        if (projectRoot is null || string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.StartsWith("pack:", StringComparison.Ordinal))
        {
            // pack:impulse-responses/hall: a WAV of an installed audio pack (free/ or licensed/), see 'ample packs list'
            string name = value[5..].Replace('\\', '/');
            if (name.Contains("..", StringComparison.Ordinal) || !name.Contains('/', StringComparison.Ordinal)) return null;
            if (!name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) name += ".wav";
            foreach (string tier in new[] { "free", "licensed" })
            {
                string? found = Ample.Audio.IO.AmpleHome.LookupPackFile(Ample.Audio.IO.AmpleHome.DefaultPath(), ampleRoot, projectRoot, tier + "/" + name).Path;
                if (found is not null && File.Exists(found)) return found;
            }

            return null;
        }

        string full = ProjectPath.Resolve(projectRoot, value);
        return File.Exists(full) ? full : null;
    }

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        string? value = r.Block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"');
        if (string.IsNullOrEmpty(value))
        {
            throw new AmpleException("AMP5350", "The import-audio block has no file: choose a WAV file of the project.");
        }

        string? path = FileOf(r.ProjectRoot, r.Block) ?? throw new AmpleException("AMP5350", $"The audio file '{value}' is not in the project.");
        byte[] bytes = AudioFile.ReadWav(path);
        int length = r.BarCount * r.SamplesPerBar;
        int startBar = r.Block.Parts.Count > 0 ? r.Block.Parts[0].At : 1;
        long offset = (long)(r.FirstBar - startBar) * r.SamplesPerBar;
        // only a stretch of the file can be used (start and end in seconds), and it can repeat to fill the block
        string Param(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? string.Empty;
        bool loop = Param("loop") == "on";
        int fade = Math.Min(AudioCut.SamplesOf(Param("crossfade"), r.SampleRate, r.SamplesPerBar, r.BeatsPerBar) ?? 0, 10 * r.SampleRate);   // with loop on: the join of the repeat is crossfaded (seconds, beats or bars)
        AudioCut.FadeCurve curve = AudioCut.CurveOf(Param("crossfade_curve"));
        // loop_start and loop_end (seconds in the file): what lies before repeats never (an intro), what lies after plays once at the end of the block
        (int From, int To) LoopOf((int Start, int End) kept)
        {
            double? a = AudioCut.Seconds(Param("loop_start")), b = AudioCut.Seconds(Param("loop_end"));
            int from = a is double x ? Math.Clamp((int)Math.Round(x * r.SampleRate) - kept.Start, 0, kept.End - kept.Start) : 0;
            int to = b is double y && y > 0 ? Math.Clamp((int)Math.Round(y * r.SampleRate) - kept.Start, 0, kept.End - kept.Start) : -1;
            return (from, to > from ? to : -1);
        }

        // offset: where in the block the sound starts, in seconds ("1.5", "1.5s") or in beats ("2 beats"); a negative value cuts the start off
        string shiftText = Param("offset").Trim().Trim('"');
        if (shiftText.Length > 0)
        {
            bool beats = shiftText.EndsWith("beats", StringComparison.OrdinalIgnoreCase) || shiftText.EndsWith("beat", StringComparison.OrdinalIgnoreCase);
            string number = new([.. shiftText.TakeWhile(c => char.IsDigit(c) || c is '.' or '-' or '+')]);
            if (double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double shift))
            {
                offset -= (long)Math.Round(beats ? shift * r.SamplesPerBar / r.BeatsPerBar : shift * r.SampleRate);
            }
        }
        try
        {
            if (ChannelAudio.IsStereo(bytes) && BitConverter.ToInt32(bytes, 24) == r.SampleRate && BitConverter.ToInt16(bytes, 34) == 16)
            {
                StereoBuffer source = StereoWav.Decode(bytes).Buffer;
                (int Start, int End) both = AudioCut.Range(source.Length, r.SampleRate, Param("start"), Param("end"), null, null);
                bool[] joins = new bool[length];
                short[] left = AudioCut.Place(AudioCut.Take(source.Left, both), offset, length, loop, fade, curve, LoopOf(both).From, LoopOf(both).To, joins);
                short[] right = AudioCut.Place(AudioCut.Take(source.Right, both), offset, length, loop, fade, curve, LoopOf(both).From, LoopOf(both).To);
                return ValueTask.FromResult(Outputs(left, right, joins, r.SampleRate));
            }

            short[] mono = SampleSet.ReadWav(bytes, r.SampleRate);
            (int Start, int End) one = AudioCut.Range(mono.Length, r.SampleRate, Param("start"), Param("end"), null, null);
            bool[] seam = new bool[length];
            return ValueTask.FromResult(Outputs(AudioCut.Place(AudioCut.Take(mono, one), offset, length, loop, fade, curve, LoopOf(one).From, LoopOf(one).To, seam), null, seam, r.SampleRate));
        }
        catch (InvalidDataException ex)
        {
            throw new AmpleException("AMP5350", $"The audio file '{value}' cannot be used: {ex.Message}");
        }
    }

    /// <summary>The three outputs of a loop: audio (the whole loop), body (the loop with its joins silent) and loop-region (only the joins, silent elsewhere); body plus loop-region is audio, sample for sample.</summary>
    private static IReadOnlyList<RenderOutput> Outputs(short[] left, short[]? right, bool[] seam, int rate)
    {
        byte[] Encode(short[] l, short[]? r) => r is null ? Wav.Encode(l, rate) : StereoWav.Encode(new StereoBuffer(l, r), rate);
        short[] Mask(short[] x, bool joins) => [.. x.Select((v, i) => seam[i] == joins ? v : (short)0)];
        return [new RenderOutput("audio", Encode(left, right)), new RenderOutput("body", Encode(Mask(left, false), right is null ? null : Mask(right, false))), new RenderOutput("loop-region", Encode(Mask(left, true), right is null ? null : Mask(right, true)))];
    }
}
