namespace Ample.Speech;

public sealed record SpeechSegment(string Symbol, string Kind, int DurationMilliseconds, int TransitionMilliseconds, int Stress = -1);

public static class HumanicoSegmentPlanner
{
    private static readonly Dictionary<string, (int Duration, int Transition)> Vowels = new()
    {
        ["IY"] = (85, 50),
        ["IH"] = (85, 50),
        ["EH"] = (85, 50),
        ["AE"] = (85, 50),
        ["AA"] = (85, 50),
        ["AO"] = (85, 50),
        ["UH"] = (85, 50),
        ["UW"] = (85, 50),
        ["AH"] = (85, 50),
        ["AX"] = (85, 50),
        ["ER"] = (85, 50)
    };

    private static readonly Dictionary<string, int> Consonants = new()
    {
        ["P"] = 70,
        ["T"] = 65,
        ["K"] = 70,
        ["B"] = 55,
        ["D"] = 50,
        ["G"] = 55,
        ["S"] = 115,
        ["Z"] = 90,
        ["SH"] = 110,
        ["ZH"] = 85,
        ["F"] = 95,
        ["V"] = 75,
        ["TH"] = 85,
        ["DH"] = 70,
        ["HH"] = 70,
        ["M"] = 95,
        ["N"] = 90,
        ["NG"] = 100,
        ["L"] = 85,
        ["R"] = 85,
        ["W"] = 65,
        ["Y"] = 65
    };

    private static readonly Dictionary<string, (string Start, string End)> Diphthongs = new()
    {
        ["EY"] = ("EH", "IY"),
        ["AY"] = ("AA", "IY"),
        ["OY"] = ("AO", "IY"),
        ["OW"] = ("AX", "UH"),
        ["AW"] = ("AA", "UH")
    };

    private static readonly Dictionary<string, (string Closure, string Fricative)> Affricates = new()
    {
        ["CH"] = ("T", "SH"),
        ["JH"] = ("D", "ZH")
    };

    public static IReadOnlyList<SpeechSegment> PlanWord(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return PlanPhones(HumanicoFrontEnd.WordToPhones(word));
    }

    public static IReadOnlyList<SpeechSegment> PlanWord(string word, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(word);
        return PlanPhones(HumanicoFrontEnd.WordToPhones(word), cancellationToken);
    }

    public static IReadOnlyList<SpeechSegment> PlanPhones(IReadOnlyList<SpeechPhone> phones)
        => PlanPhones(phones, CancellationToken.None);

    public static IReadOnlyList<SpeechSegment> PlanPhones(IReadOnlyList<SpeechPhone> phones, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phones);
        cancellationToken.ThrowIfCancellationRequested();
        List<SpeechSegment> segments = [];
        foreach (SpeechPhone phone in phones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Diphthongs.TryGetValue(phone.Symbol, out (string Start, string End) diphthong))
            {
                int duration = phone.Stress == 1 ? 165 : 115;
                int first = (int)(duration * 0.45);
                segments.Add(new(diphthong.Start, "vowel", first, 55, phone.Stress));
                segments.Add(new(diphthong.End, "vowel", duration - first, duration / 2, phone.Stress));
                continue;
            }

            if (Affricates.TryGetValue(phone.Symbol, out (string Closure, string Fricative) affricate))
            {
                segments.Add(new(affricate.Closure, "consonant", Consonants[affricate.Closure], 3));
                segments.Add(new(affricate.Fricative, "consonant", Consonants[affricate.Fricative], 20));
                continue;
            }

            if (Vowels.TryGetValue(phone.Symbol, out (int Duration, int Transition) vowel))
            {
                int duration = phone.Stress == 1 ? 150 : phone.Stress == 2 ? 100 : vowel.Duration;
                segments.Add(new(phone.Symbol, "vowel", duration, vowel.Transition, phone.Stress));
            }
            else if (Consonants.TryGetValue(phone.Symbol, out int consonant))
            {
                segments.Add(new(phone.Symbol, "consonant", consonant, Math.Min(45, consonant / 2)));
            }
        }

        return segments;
    }
}
