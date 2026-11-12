using System.Globalization;
using Ample.Audio.Engine;
using Ample.Language;
using Ample.Storage.Takes;

namespace Ample.Engine.Engine;

/// <summary>One thing the sound check found. <see cref="Fix"/> says what can be done about it: <c>normalize</c>, <c>volume:-6</c> (a volume block with that gain), or null.</summary>
public sealed record SoundFinding(string Severity, string Code, string Block, string Message, string? Fix);

/// <summary>X03: the master's own peak, RMS and integrated loudness, always available (not only when a <see cref="SoundFinding"/> flags it as too loud or
/// too quiet) so a UI meter can show the master's level after every build.</summary>
public sealed record MasterLevels(double PeakDbfs, double RmsDbfs, double? Lufs);

/// <summary>
/// The sound of a built song: per block that makes audio, silent, very quiet, clipping, not heard (does not reach the master), plus the loudness of the master and
/// numeric values that the build cuts (AMP2021). Only the takes that exist are looked at; a block without a take is reported as not built.
/// </summary>
public static class SoundAnalysis
{
    /// <summary>Looks at the takes of a build report.</summary>
    public static async Task<(IReadOnlyList<SoundFinding> Findings, MasterLevels? Master)> AnalyseAsync(AmpleDocument doc, SpecData spec, Takebank bank, BuildReport report, CancellationToken token)
    {
        List<SoundFinding> findings = [];
        MasterLevels? masterLevels = null;
        foreach (BlockResult result in report.Blocks.Where(b => b.Status is "failed" or "skipped"))
        {
            findings.Add(new SoundFinding("error", "sound.not-built", result.Block, $"{result.Status}{(result.Message is null ? string.Empty : ": " + result.Message.Split('\n')[0])}", null));
        }

        BlockNode? master = doc.Blocks.FirstOrDefault(b => b.Type == "master");
        HashSet<string> heard = [];
        if (master is not null)
        {
            Stack<BlockNode> work = new([master]);
            while (work.Count > 0)
            {
                BlockNode current = work.Pop();
                if (!heard.Add(current.Name)) continue;
                foreach (FromLine from in current.Froms.Where(f => !f.Disabled))
                {
                    BlockNode? source = doc.Blocks.FirstOrDefault(b => b.Name == from.Block);
                    if (source is not null) work.Push(source);
                }
            }
        }

        foreach (BlockResult result in report.Blocks.Where(b => b.TakeId is not null))
        {
            BlockNode? block = doc.Blocks.FirstOrDefault(b => b.Name == result.Block);
            if (block is null || !spec.Templates.TryGetValue(block.Type, out TemplateSpec? template) || !template.Outputs.Any(p => p.Type == "audio")) continue;
            if (master is not null && !heard.Contains(block.Name) && block.Type != "master")
            {
                findings.Add(new SoundFinding("warning", "sound.not-heard", block.Name, "makes sound but does not reach the master", null));
                continue;
            }

            TakeRecord take = bank.GetTake(result.TakeId!);
            TakeDataRef? data = take.Data.FirstOrDefault(d => d.Role is "audio" or "out" or "mixed" or "mix" or "master");
            if (data is null) continue;
            await using Stream stream = bank.OpenData(data.DataId);
            using MemoryStream bytes = new();
            await stream.CopyToAsync(bytes, token);
            byte[] wav = bytes.ToArray();
            if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I') continue;
            short[] left, right;
            int rate = BitConverter.ToInt32(wav, 24);
            if (BitConverter.ToInt16(wav, 22) == 2)
            {
                StereoBuffer both = StereoWav.Decode(wav).Buffer;
                left = both.Left;
                right = both.Right;
            }
            else
            {
                left = Wav.Decode(wav).Samples;
                right = left;
            }

            short[] all = ReferenceEquals(left, right) ? left : [.. left, .. right];
            double peak = AudioMeter.PeakDbfs(all), rms = AudioMeter.RmsDbfs(all);
            int clipped = all.Count(v => v is short.MaxValue or short.MinValue);
            bool isMaster = block.Type == "master";
            if (double.IsNegativeInfinity(peak) || rms <= -60)
            {
                findings.Add(new SoundFinding(isMaster ? "error" : "warning", "sound.silent", block.Name, "is silent (RMS below -60 dBFS)", null));
            }
            else if (rms < -45)
            {
                double add = Math.Min(48, Math.Round(-20 - rms));
                findings.Add(new SoundFinding("warning", "sound.very-quiet", block.Name, $"is very quiet (RMS {rms.ToString("F1", CultureInfo.InvariantCulture)} dBFS); a normalize block (peak -3) or about +{add.ToString("F0", CultureInfo.InvariantCulture)} dB brings it to a usable level", isMaster ? null : "normalize"));
            }

            if (clipped > 0 || (!double.IsNegativeInfinity(peak) && peak >= -0.1))
            {
                double lower = Math.Max(1, Math.Ceiling(peak + 3));
                findings.Add(new SoundFinding(isMaster ? "error" : "warning", "sound.clips", block.Name, $"clips or touches full scale (peak {AudioMeter.Format(peak)}, {clipped.ToString(CultureInfo.InvariantCulture)} clipped samples); lower it by at least {lower.ToString("F0", CultureInfo.InvariantCulture)} dB", $"volume:{(-lower).ToString("F0", CultureInfo.InvariantCulture)}"));
            }

            if (isMaster)
            {
                double? lufs = Loudness.IntegratedLufs(ReferenceEquals(left, right) ? [left] : [left, right], rate);
                masterLevels = new MasterLevels(peak, rms, lufs);
                if (lufs is not null && lufs > -9) findings.Add(new SoundFinding("warning", "sound.master-loud", block.Name, $"the master is very loud ({lufs.Value.ToString("F1", CultureInfo.InvariantCulture)} LUFS)", null));
                if (lufs is not null && lufs < -30) findings.Add(new SoundFinding("warning", "sound.master-quiet", block.Name, $"the master is very quiet ({lufs.Value.ToString("F1", CultureInfo.InvariantCulture)} LUFS)", null));
            }
        }

        foreach (Diagnostic d in doc.Diagnostics.Where(d => d.Code == "AMP2021"))
        {
            findings.Add(new SoundFinding("warning", "sound.cut-value", "line " + d.Line.ToString(CultureInfo.InvariantCulture), d.Message, null));
        }

        return (findings, masterLevels);
    }
}
