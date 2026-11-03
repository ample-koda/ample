using System.Text.Json.Nodes;
using Ample.Audio.Instruments;

namespace Ample.Audio.Engine;

/// <summary>
/// A small subtractive synthesizer: one oscillator per note (sine, triangle, saw, square or a chip-style noise channel), an ADSR envelope, an optional second detuned oscillator and a low-pass filter.
/// Everything is integer arithmetic (a 32 bit phase accumulator, a table of the twelve semitone ratios, Q15 gains), so the same notes give the same bytes everywhere.
/// </summary>
internal sealed class SynthRenderer : IBlockRenderer
{
    private sealed record SynthNote(long Start, int Held, int Pitch, int Velocity, int Channel, int Pan, int Fine, int Release, int Cut, bool Slide, bool Tied, int Port);
    // 2^(k/12) in Q16 for k = 0 to 11
    private static readonly int[] SemitoneQ16 = [65536, 69433, 73562, 77936, 82570, 87480, 92682, 98193, 104032, 110218, 116772, 123715];

    public string BlockType => "synth";

    public string Id => "builtin.synth";

    public string Version => "4";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        int Number(string name, int fallback) => AudioEffects.Read(Text(name), fallback);
        int rate = r.SampleRate;
        int[] mix = new int[r.BarCount * r.SamplesPerBar];
        if (Text("enabled") == "off")
        {
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(new short[mix.Length], rate))]);
        }

        string wave = Text("wave");
        int attack = Math.Max(1, Math.Clamp(Number("attack", 10), 1, 5000) * rate / 1000), decay = Math.Max(1, Math.Clamp(Number("decay", 200), 1, 5000) * rate / 1000);
        int sustain = Math.Clamp(Number("sustain", 70), 0, 100) * 32768 / 100;
        int release = Math.Max(1, Math.Clamp(Number("release", 250), 1, 8000) * rate / 1000);
        int detuneCents = Math.Clamp(Number("detune", 0), 0, 50);
        int gain = FixedAudio.GainFromDbWide(Math.Clamp(Number("gain", -6), -36, FixedAudio.WideMaxDb));
        JsonObject root = r.Inputs.TryGetValue("notes", out byte[]? bytes) && JsonNode.Parse(bytes) is JsonObject parsed ? parsed : [];
        List<SynthNote> notes = [];
        foreach (JsonObject note in (root["notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            int bar = note["bar"]?.GetValue<int>() ?? 1;
            if (bar < r.FirstBar || bar >= r.FirstBar + r.BarCount)
            {
                continue;
            }

            int pitch = Math.Clamp(note["pitch"]?.GetValue<int>() ?? 60, 0, 127);
            int velocity = Math.Clamp(note["velocity"]?.GetValue<int>() ?? 96, 1, 127);
            decimal beat = note["beat"] is JsonValue b && b.TryGetValue(out decimal bd) ? bd : 0m;
            decimal beats = note["beats"] is JsonValue l && l.TryGetValue(out decimal ld) ? ld : 1m;
            long start = ((long)(bar - r.FirstBar) * r.SamplesPerBar) + (long)(beat * r.SamplesPerBar / r.BeatsPerBarDecimal);
            int held = Math.Max(1, (int)(beats * r.SamplesPerBar / r.BeatsPerBarDecimal));
            notes.Add(new SynthNote(start, held, pitch, velocity, note["channel"]?.GetValue<int>() ?? 0, note["pan"]?.GetValue<int>() ?? 0, note["fine"]?.GetValue<int>() ?? 0, note["rel"]?.GetValue<int>() ?? -1, note["cut"]?.GetValue<int>() ?? 100, note["slide"]?.GetValue<bool>() ?? false, note["tie"]?.GetValue<bool>() ?? false, note["port"]?.GetValue<int>() ?? 0));
        }

        // a note that was cut at a bar line (a long note, or one made longer by stretch) comes as pieces; a piece with the tie flag goes on with the voice that sounds up to its start, it is not struck again
        List<SynthNote> voices = [];
        foreach (SynthNote piece in notes.Where(n => !n.Slide && n.Port == 0))
        {
            int sounding = piece.Tied ? voices.FindLastIndex(v => v.Channel == piece.Channel && v.Pitch == piece.Pitch && v.Start + v.Held == piece.Start) : -1;
            if (sounding >= 0) voices[sounding] = voices[sounding] with { Held = voices[sounding].Held + piece.Held };
            else voices.Add(piece);
        }

        Dictionary<int, List<(int Start, int End, int Pitch)>> slidePaths = [];
        foreach (SynthNote slide in notes.Where(n => n.Slide).OrderBy(n => n.Start))
        {
            int previous = Enumerable.Range(0, voices.Count).Where(i => voices[i].Channel == slide.Channel && voices[i].Start <= slide.Start && voices[i].Start + voices[i].Held >= slide.Start).OrderByDescending(i => voices[i].Start).DefaultIfEmpty(-1).First();
            if (previous < 0) continue;
            if (!slidePaths.TryGetValue(previous, out List<(int Start, int End, int Pitch)>? path)) slidePaths[previous] = path = [];
            int slideStart = checked((int)slide.Start), slideEnd = checked((int)(slide.Start + slide.Held));
            if (slide.Tied && path.Count > 0 && path[^1].End == slideStart && path[^1].Pitch == slide.Pitch) path[^1] = (path[^1].Start, slideEnd, slide.Pitch);
            else path.Add((slideStart, slideEnd, slide.Pitch));
            voices[previous] = voices[previous] with { Held = Math.Max(voices[previous].Held, slideEnd - checked((int)voices[previous].Start)) };
        }
        foreach (SynthNote port in notes.Where(n => !n.Slide && n.Port > 0).OrderBy(n => n.Start))
        {
            int previous = Enumerable.Range(0, voices.Count).Where(i => voices[i].Channel == port.Channel && voices[i].Start < port.Start).OrderByDescending(i => voices[i].Start).DefaultIfEmpty(-1).First();
            if (previous < 0) { voices.Add(port with { Port = 0 }); continue; }
            int start = checked((int)port.Start), end = checked(start + Math.Max(1, port.Port * rate / 1000));
            if (!slidePaths.TryGetValue(previous, out List<(int Start, int End, int Pitch)>? path)) slidePaths[previous] = path = [];
            path.Add((start, end, port.Pitch));
            voices[previous] = voices[previous] with { Held = Math.Max(voices[previous].Held, end - checked((int)voices[previous].Start)) };
        }
        foreach (List<(int Start, int End, int Pitch)> path in slidePaths.Values) path.Sort((a, b) => a.Start.CompareTo(b.Start));
        int[]? right = voices.Any(n => n.Pan != 0) ? new int[mix.Length] : null;
        for (int i = 0; i < voices.Count; i++)
        {
            SynthNote note = voices[i];
            slidePaths.TryGetValue(i, out List<(int Start, int End, int Pitch)>? path);
            int noteRelease = note.Release < 0 ? release : Math.Max(1, note.Release * rate / 1000);
            RenderNote(mix, right, note.Start, note.Held, note.Pitch, note.Velocity, note.Pan, note.Fine, note.Cut, path, wave, attack, decay, sustain, noteRelease, detuneCents, gain, rate);
        }

        int cutoff = Math.Clamp(Number("cutoff", 8000), 40, 20000);
        if (cutoff < rate / 2 - 1 && cutoff < 19000)
        {
            mix = AudioEffects.LowPass(mix, cutoff, rate);
            if (right is not null) right = AudioEffects.LowPass(right, cutoff, rate);
        }

        byte[] audio = right is null ? Wav.Encode([.. mix.Select(v => FixedAudio.Clip(v))], rate) : StereoWav.Encode(new StereoBuffer([.. mix.Select(v => FixedAudio.Clip(v))], [.. right.Select(v => FixedAudio.Clip(v))]), rate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", audio)]);
    }

    private static uint Increment(int pitch, int cents, int rate)
    {
        long milliHz = 27500L;
        int octaves = Math.DivRem(pitch - 21, 12, out int semitone);
        if (semitone < 0)
        {
            semitone += 12;
            octaves--;
        }

        milliHz = octaves >= 0 ? milliHz << octaves : milliHz >> -octaves;
        milliHz = milliHz * SemitoneQ16[semitone] >> 16;
        if (cents != 0)
        {
            milliHz += milliHz * cents * 578 / 1_000_000;      // one cent is about 0.0578 percent
        }

        return (uint)(milliHz * 4294967296L / (rate * 1000L));
    }

    private static int Oscillator(string wave, uint phase)
    {
        int ramp = (int)(phase >> 16) - 32768;      // -32768 .. 32767
        return wave switch
        {
            "square" => phase < 0x80000000u ? 20000 : -20000,
            "triangle" => (Math.Abs(ramp) * 2) - 32768,
            "sine" => Sine(ramp),
            _ => ramp,
        };
    }

    // a parabola refined once: close to a sine, integer only
    private static int Sine(int ramp)
    {
        long x = ramp;                                                  // -32768 .. 32767 is one turn
        long half = x < 0 ? x + 32768 : x;                              // 0 .. 32767 is half a turn
        long parabola = half * (32768 - half) * 4 / 32768;              // 0 .. 32768 at the top
        long value = (parabola * 7 / 10) + (parabola * parabola / 32768 * 3 / 10);
        return (int)(x < 0 ? -value : value);
    }

    private static long Envelope(int i, int held, int attack, int decay, int sustain, int release)
    {
        static long Held(int n, int attack, int decay, int sustain) => n < attack ? 32768L * n / attack : n < attack + decay ? 32768 - ((32768L - sustain) * (n - attack) / decay) : sustain;
        return i < held ? Held(i, attack, decay, sustain) : Held(held, attack, decay, sustain) * (release - (i - held)) / release;
    }

    private static void RenderNote(int[] mix, int[]? right, long start, int held, int pitch, int velocity, int pan, int fine, int cut, IReadOnlyList<(int Start, int End, int Pitch)>? slidePath, string wave, int attack, int decay, int sustain, int release, int detuneCents, int gain, int rate)
    {
        uint baseIncrement = Increment(pitch, 0, rate), baseIncrement2 = detuneCents > 0 ? Increment(pitch, detuneCents, rate) : 0;
        uint increment = baseIncrement, increment2 = baseIncrement2;
        int total = held + release;
        uint phase = 0, phase2 = 0, previousPhase = 0, lfsr = 0x7FFF;
        bool noise = wave == "noise";
        long level = (long)velocity * gain / 127;      // Q15 gain scaled by the velocity
        int appliedCents = 0, position = Math.Clamp(pan, -100, 100) * PanTable.Range / 100, open = Math.Clamp(cut, 0, 100);
        long filterState = 0;
        int filterAlpha = 512 + open * open * (32768 - 512) / 10000;
        for (int i = 0; i < total; i++)
        {
            long at = start + i;
            if (at >= mix.Length)
            {
                break;
            }

            int cents = Math.Clamp(fine, -100, 100) + (slidePath is null ? 0 : NoteGlide.Cents(pitch, checked((int)at), slidePath));
            if (cents != appliedCents)
            {
                long ratio = InstrumentMath.CentsToRatioQ16(cents);
                increment = (uint)Math.Max(1, (long)baseIncrement * ratio >> 16);
                increment2 = baseIncrement2 == 0 ? 0 : (uint)Math.Max(1, (long)baseIncrement2 * ratio >> 16);
                appliedCents = cents;
            }
            if (at >= 0)
            {
                long sample;
                if (noise)
                {
                    // NES-style noise: a 15-bit shift register clocked once per cycle of the note's pitch, so a high note is bright hiss and a low note a dull rumble
                    if (phase < previousPhase)
                    {
                        lfsr = (lfsr >> 1) | (((lfsr ^ (lfsr >> 1)) & 1u) << 14);
                    }

                    previousPhase = phase;
                    sample = (lfsr & 1u) != 0 ? 20000 : -20000;
                }
                else
                {
                    sample = Oscillator(wave, phase);
                }

                if (increment2 != 0 && !noise)
                {
                    sample = (sample + Oscillator(wave, phase2)) / 2;
                }

                if (cut < 100) { filterState += (sample - filterState) * filterAlpha >> 15; sample = filterState; }
                int voiced = (int)((sample * Envelope(i, held, attack, decay, sustain, release) >> 15) * level >> 15);
                if (right is null) mix[at] += voiced;
                else
                {
                    mix[at] += (int)((long)voiced * PanTable.LeftGain(position) >> 15);
                    right[at] += (int)((long)voiced * PanTable.RightGain(position) >> 15);
                }
            }

            phase += increment;
            phase2 += increment2;
        }
    }
}
