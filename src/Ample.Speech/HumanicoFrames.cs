namespace Ample.Speech;

public sealed record SpeechFrame(string Symbol, string Kind, double FundamentalHz, int FrameIndex, int TransitionFrames, SpeechFrameParameters Parameters, int SegmentIndex, int FrameInSegment, int SegmentFrameCount);

public sealed record SpeechFrameParameters(double F1Hz, double F2Hz, double F3Hz, double NoiseGain, bool Voiced, double SourceGain = 1);

public static class HumanicoFrameParameters
{
    private static readonly Dictionary<string, SpeechFrameParameters> Vowels = new()
    {
        ["IY"] = new(270, 2290, 3010, 0, true, 0.92),
        ["IH"] = new(390, 1990, 2550, 0, true, 0.92),
        ["EH"] = new(530, 1840, 2480, 0, true, 0.92),
        ["AE"] = new(660, 1720, 2410, 0, true, 0.92),
        ["AA"] = new(730, 1090, 2440, 0, true, 0.92),
        ["AO"] = new(570, 840, 2410, 0, true, 0.92),
        ["UH"] = new(440, 1020, 2240, 0, true, 0.92),
        ["UW"] = new(300, 870, 2240, 0, true, 0.92),
        ["AH"] = new(640, 1190, 2390, 0, true, 0.92),
        ["AX"] = new(500, 1500, 2500, 0, true, 0.92),
        ["ER"] = new(490, 1350, 1690, 0, true, 0.92)
    };

    private static readonly Dictionary<string, double> NoiseByConsonant = new()
    {
        ["S"] = 1.6,
        ["SH"] = 1.45,
        ["F"] = 1.3,
        ["TH"] = 1.15,
        ["HH"] = 1.1,
        ["Z"] = 0.8,
        ["ZH"] = 0.75,
        ["V"] = 0.65,
        ["P"] = 0.35,
        ["T"] = 0.3,
        ["K"] = 0.35,
        ["B"] = 0.2,
        ["D"] = 0.2,
        ["G"] = 0.2
    };

    private static readonly Dictionary<string, SpeechFrameParameters> VoicedConsonants = new()
    {
        ["B"] = new(200, 1000, 2200, 0.08, true, 0.2),
        ["D"] = new(200, 1700, 2500, 0.08, true, 0.2),
        ["G"] = new(200, 1400, 2400, 0.08, true, 0.2),
        ["M"] = new(270, 1000, 2400, 0.03, true, 0.75),
        ["N"] = new(270, 1700, 2400, 0.03, true, 0.75),
        ["NG"] = new(270, 2600, 2900, 0.03, true, 0.75),
        ["L"] = new(380, 1350, 2600, 0.02, true, 0.72),
        ["R"] = new(460, 1380, 1720, 0.02, true, 0.72),
        ["W"] = new(300, 610, 2200, 0.02, true, 0.8),
        ["Y"] = new(270, 2290, 3010, 0.02, true, 0.8)
    };

    public static SpeechFrameParameters For(string symbol, string kind)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(kind);
        return kind == "vowel" && Vowels.TryGetValue(symbol, out SpeechFrameParameters? value)
            ? value
            : VoicedConsonants.TryGetValue(symbol, out SpeechFrameParameters? voiced)
                ? voiced
            : new(0, 0, 0, NoiseByConsonant.GetValueOrDefault(symbol, 0.55), false);
    }
}

public static class HumanicoFramePlanner
{
    private const int FrameMilliseconds = 5;

    public static IReadOnlyList<SpeechFrame> Plan(IReadOnlyList<SpeechSegment> segments, double pitchHz, double rate)
        => Plan(segments, pitchHz, rate, CancellationToken.None);

    public static IReadOnlyList<SpeechFrame> Plan(IReadOnlyList<SpeechSegment> segments, double pitchHz, double rate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(pitchHz) || pitchHz is < 45 or > 700) throw new ArgumentOutOfRangeException(nameof(pitchHz));
        if (!double.IsFinite(rate) || rate is < 0.1 or > 2) throw new ArgumentOutOfRangeException(nameof(rate));
        List<SpeechFrame> frames = [];
        int totalFrames = segments.Sum(segment => Math.Max(1, (int)Math.Round(segment.DurationMilliseconds / rate / FrameMilliseconds, MidpointRounding.ToEven)));
        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            SpeechSegment segment = segments[segmentIndex];
            int count = Math.Max(1, (int)Math.Round(segment.DurationMilliseconds / rate / FrameMilliseconds, MidpointRounding.ToEven));
            int transition = Math.Clamp((int)Math.Round(segment.TransitionMilliseconds / rate / FrameMilliseconds, MidpointRounding.ToEven), 0, count);
            for (int i = 0; i < count; i++)
            {
                if ((frames.Count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                double progress = totalFrames <= 1 ? 0 : (double)frames.Count / (totalFrames - 1);
                double contour = 1.12 - 0.32 * progress;
                if (segment.Stress == 1 && segment.Kind == "vowel")
                    contour *= 1 + 0.14 * Math.Sin(Math.PI * Math.Min(1, (double)i / Math.Max(1, count - 1)));
                frames.Add(new(segment.Symbol, segment.Kind, pitchHz * contour, frames.Count, transition,
                    HumanicoFrameParameters.For(segment.Symbol, segment.Kind), segmentIndex, i, count));
            }
        }

        return frames;
    }
}

