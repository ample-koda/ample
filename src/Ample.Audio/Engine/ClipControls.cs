using System.Globalization;
using System.Text.Json;
using Ample.Audio.Instruments;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// The MIDI controllers a midi-clip writes: <c>modwheel</c> (percent), <c>volume</c> and <c>expression</c> (dB, 0 down to -96), <c>pan</c> (-100..100), <c>sustain</c>
/// (on/off), <c>reverb_send</c> and <c>chorus_send</c> (percent), and any controller by number with <c>cc "74=auto(1:20, 4:100) 67=127"</c> (raw 0..127). A value is a
/// number or a curve (<c>auto(...)</c> and the other curve forms of the clip). The clip passes them on with its notes. The gm block (and midi-render) plays them all as
/// control changes; every other instrument follows volume, expression and pan (<see cref="Apply"/>) and says so when it is given the others.
/// </summary>
internal static class ClipControls
{
    /// <summary>The named controllers (the written form lives in Ample.Language, shared with the lint).</summary>
    public static IReadOnlyList<(string Name, int Number, string Unit)> Named => ClipControlSyntax.Named;

    /// <summary>The entries of a cc parameter, or null when it is malformed.</summary>
    public static IReadOnlyList<(int Number, string Value)>? ParseCc(string text) => ClipControlSyntax.ParseCc(text);

    /// <summary>Whether a value can be played.</summary>
    public static bool Valid(string text, string unit) => ClipControlSyntax.Valid(text, unit);

    private static string Plain(string text) => ClipControlSyntax.Plain(text);

    /// <summary>A value of the clip as a controller value 0..127: percent 0..100; dB 0 down to -96 (the controller that gives that attenuation on the SoundFont curve, so -6 dB is -6 dB in the gm block); pan -100..100 (0 is 64); on/off (a curve in percent: 50 and up is on); raw 0..127.</summary>
    public static int Raw(int value, string unit) => unit switch
    {
        "percent" => Math.Clamp(value, 0, 100) * 127 / 100,
        "db" => Sf2Modulators.ControllerForAttenuation(-Math.Clamp(value, -96, 0) * 10),
        "pan" => 64 + (Math.Clamp(value, -100, 100) * 63 / 100),
        "onoff" => value >= 50 ? 127 : 0,
        _ => Math.Clamp(value, 0, 127),
    };

    /// <summary>
    /// The control changes of one controller over the render window: a number (or on/off) is one event at its start; a curve gives an event at the start and one
    /// whenever its value changes (read every 32 samples).
    /// </summary>
    public static IReadOnlyList<(int Sample, int Value)> Events(string text, string unit, int firstBar, int samplesPerBar, int sampleRate, int length)
    {
        text = Plain(text);
        if (unit == "onoff" && text is "on" or "off") return [(0, text == "on" ? 127 : 0)];
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int constant)) return [(0, Raw(constant, unit))];
        if (!Automation.Is(text) || length <= 0) return [];
        int[] curve = Automation.CurveAt(Automation.ParseSamples(text, samplesPerBar, sampleRate), firstBar, samplesPerBar, length, value => Raw(value, unit));
        List<(int Sample, int Value)> events = [(0, curve[0])];
        for (int i = 32; i < length; i += 32)
        {
            if (curve[i] != events[^1].Value) events.Add((i, curve[i]));
        }

        return events;
    }

    /// <summary>
    /// Volume, expression and pan of a clip for an instrument that does not play controllers itself (everything but gm and midi-render): the gain of volume and
    /// expression in dB (added), and pan (a mono sound becomes stereo). The other controllers of the clip are named in the warnings. The same bytes when the clip has none.
    /// </summary>
    public static IReadOnlyList<RenderOutput> Apply(IReadOnlyList<RenderOutput> outputs, RenderRequest r, CancellationToken cancellationToken)
    {
        if (r.Block.Type is "gm" or "midi-render") return outputs;
        string? input = r.Template.Inputs.FirstOrDefault(p => p.Type == "midi" && r.Inputs.ContainsKey(p.Name))?.Name;
        if (input is null || r.Inputs[input].AsSpan().IndexOf("\"controls\""u8) < 0) return outputs;
        using JsonDocument doc = JsonDocument.Parse(r.Inputs[input]);
        if (!doc.RootElement.TryGetProperty("controls", out JsonElement controls) || controls.ValueKind != JsonValueKind.Array) return outputs;
        List<(string Value, string Unit)> gains = [];
        string? pan = null;
        List<int> ignored = [];
        foreach (JsonElement entry in controls.EnumerateArray())
        {
            int number = entry.GetProperty("cc").GetInt32();
            string value = entry.GetProperty("value").GetString() ?? string.Empty, unit = entry.GetProperty("unit").GetString() ?? "raw";
            if (unit == "db") gains.Add((value, unit));
            else if (unit == "pan") pan = value;
            else ignored.Add(number);
        }

        HashSet<string> audioRoles = [.. r.Template.Outputs.Where(p => p.Type == "audio").Select(p => p.Name)];
        List<RenderOutput> result = [];
        foreach (RenderOutput output in outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Role == "warnings" && ignored.Count > 0)
            {
                string note = $"This {r.Block.Type} block plays only volume, expression and pan of the clip; controller(s) {string.Join(", ", ignored.Distinct())} need a gm block.";
                string before = System.Text.Encoding.UTF8.GetString(output.Data);
                result.Add(output with { Data = System.Text.Encoding.UTF8.GetBytes(before.Length == 0 ? note : before + "\n" + note) });
                continue;
            }

            if (!audioRoles.Contains(output.Role) || (gains.Count == 0 && pan is null))
            {
                result.Add(output);
                continue;
            }

            bool stereo = ChannelAudio.IsStereo(output.Data);
            (StereoBuffer channels, int rate) = ChannelAudio.Decode(output.Data);
            int length = channels.Left.Length;
            int samplesPerBar = (int)((long)r.SamplesPerBar * rate / Math.Max(1, r.SampleRate));
            int[] db = new int[length];
            foreach ((string value, string unit) in gains)
            {
                string text = Plain(value);
                int[] curve = int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int constant) ? Enumerable.Repeat(Math.Clamp(constant, -96, 0), length).ToArray()
                    : Automation.Is(text) ? Automation.CurveAt(Automation.ParseSamples(text, samplesPerBar, rate), r.FirstBar, samplesPerBar, length, v => Math.Clamp(v, -96, 0)) : new int[length];
                for (int i = 0; i < length; i++) db[i] += curve[i];
            }

            int[]? panCurve = pan is null ? null
                : int.TryParse(Plain(pan), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int fixedPan) ? Enumerable.Repeat(Math.Clamp(fixedPan, -100, 100), length).ToArray()
                : Automation.Is(Plain(pan)) ? Automation.CurveAt(Automation.ParseSamples(Plain(pan), samplesPerBar, rate), r.FirstBar, samplesPerBar, length, v => Math.Clamp(v, -100, 100)) : null;
            short[] left = new short[length], right = new short[length];
            for (int i = 0; i < length; i++)
            {
                int gain = db[i] <= -60 ? 0 : FixedAudio.GainFromDb(db[i]);
                long l = (long)channels.Left[i] * gain >> 15, rr = (long)(stereo ? channels.Right[i] : channels.Left[i]) * gain >> 15;
                if (panCurve is not null)
                {
                    int position = panCurve[i] * PanTable.Range / 100;
                    if (stereo) { l = l * PanTable.LeftGain(position) >> 15; rr = rr * PanTable.RightGain(position) >> 15; }   // a balance on a stereo sound
                    else { long mono = l; l = mono * PanTable.LeftGain(position) >> 15; rr = mono * PanTable.RightGain(position) >> 15; }
                }

                left[i] = (short)Math.Clamp(l, short.MinValue, short.MaxValue);
                right[i] = (short)Math.Clamp(rr, short.MinValue, short.MaxValue);
            }

            result.Add(output with { Data = stereo || panCurve is not null ? StereoWav.Encode(new StereoBuffer(left, right), rate) : Wav.Encode(left, rate) });
        }

        return result;
    }
}
