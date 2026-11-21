namespace Ample.Speech;

/// <summary>Uncalibrated segment timing from the pinned Humanico phone rules.</summary>
public static class HumanicoReferenceSegments
{
    private static readonly Dictionary<string, (int Closure, int Aspir)> Stops = new()
    {
        ["P"] = (70, 35),
        ["T"] = (65, 35),
        ["K"] = (70, 35),
        ["B"] = (55, 12),
        ["D"] = (50, 12),
        ["G"] = (55, 12)
    };

    private static readonly Dictionary<string, int> Fricatives = new()
    {
        ["S"] = 115,
        ["Z"] = 90,
        ["SH"] = 110,
        ["ZH"] = 85,
        ["F"] = 95,
        ["V"] = 75,
        ["TH"] = 85,
        ["DH"] = 70,
        ["HH"] = 70
    };

    private static readonly Dictionary<string, (string Start, string End)> Diphthongs = new()
    {
        ["EY"] = ("EH", "IY"),
        ["AY"] = ("AA", "IY"),
        ["OY"] = ("AO", "IY"),
        ["OW"] = ("AX", "UH"),
        ["AW"] = ("AA", "UH")
    };

    private static readonly Dictionary<string, (string Kind, int Duration, int Transition)> Sonorants = new()
    {
        ["M"] = ("nasal", 95, 35),
        ["N"] = ("nasal", 90, 35),
        ["NG"] = ("nasal", 100, 35),
        ["L"] = ("liquid", 85, 45),
        ["R"] = ("liquid", 85, 45),
        ["W"] = ("glide", 65, 45),
        ["Y"] = ("glide", 65, 45)
    };

    public static IReadOnlyList<SpeechSegment> Plan(IReadOnlyList<SpeechPhone> phones, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(phones);
        token.ThrowIfCancellationRequested();
        List<SpeechSegment> segments = [];
        foreach (SpeechPhone phone in phones)
        {
            token.ThrowIfCancellationRequested();
            string symbol = phone.Symbol;
            if (Diphthongs.TryGetValue(symbol, out (string Start, string End) pair))
            {
                int duration = phone.Stress == 1 ? 165 : 115;
                segments.Add(new(pair.Start, "vowel", (int)(duration * 0.45), 55, phone.Stress));
                segments.Add(new(pair.End, "vowel", (int)(duration * 0.55), duration / 2, phone.Stress));
            }
            else if (HumanicoFrontEnd.IsVowel(symbol))
            {
                int duration = phone.Stress == 1 ? 150 : phone.Stress == 2 ? 100 : 85;
                segments.Add(new(symbol, "vowel", duration, 50, phone.Stress));
            }
            else if (Stops.TryGetValue(symbol, out (int Closure, int Aspir) stop))
            {
                AddStop(segments, symbol, stop, false);
            }
            else if (symbol is "CH" or "JH")
            {
                string closure = symbol == "CH" ? "T" : "D";
                string fricative = symbol == "CH" ? "SH" : "ZH";
                AddStop(segments, closure, Stops[closure], true);
                segments.Add(new(fricative, "fric", Fricatives[fricative], 20));
            }
            else if (Fricatives.TryGetValue(symbol, out int duration))
            {
                segments.Add(new(symbol, "fric", duration, 20));
            }
            else if (Sonorants.TryGetValue(symbol, out (string Kind, int Duration, int Transition) sonorant))
            {
                segments.Add(new(symbol, sonorant.Kind, sonorant.Duration, sonorant.Transition));
            }
            else
            {
                throw new ArgumentException($"Unsupported Humanico phone '{symbol}'.", nameof(phones));
            }
        }
        return segments;
    }

    private static void AddStop(List<SpeechSegment> segments, string symbol, (int Closure, int Aspir) stop, bool affricate)
    {
        int closure = affricate ? (int)(stop.Closure * 0.7) : stop.Closure;
        segments.Add(new(symbol, "sil", closure, 3));
        segments.Add(new(symbol, "burst", 14, 3));
        if (!affricate) segments.Add(new(symbol, "aspir", stop.Aspir, 12));
    }
}
