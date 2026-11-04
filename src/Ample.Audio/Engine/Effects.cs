using System.Globalization;

namespace Ample.Audio.Engine;

/// <summary>The audio effect blocks (eq, distortion, delay, reverb, compressor, filter). All use integer arithmetic only, so the same input and parameters give the same bytes everywhere.</summary>
public static class AudioEffects
{
    /// <summary>Gets the effect renderers.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } = new IBlockRenderer[]
    {
        new VolumeRenderer(),
        new CutRenderer(),
        new ShiftRenderer(),
        new JoinRenderer(),
        new Effect("eq", Eq, null, "2"),
        new Effect("distortion", Distortion, null, "2"),
        new Effect("delay", Delay),
        new Effect("reverb", (p, x, rate) => Reverb(p, x, rate, false), (p, rate, l, rt) => (Reverb(p, l, rate, false), Reverb(p, rt, rate, true)), "5"),
        new Effect("compressor", (p, x, rate) => Compress(p, rate, null, null, x)[0], (p, rate, l, rt) => { int[][] both = Compress(p, rate, null, null, l, rt); return (both[0], both[1]); }, "6", (p, rate, key, thresholds, channels) => Compress(p, rate, key, thresholds, channels)),
        new Effect("filter", Filter, null, "4"),
        new Effect("limiter", (p, x, rate) => Limit(p, rate, x)[0], (p, rate, l, rt) => { int[][] both = Limit(p, rate, l, rt); return (both[0], both[1]); }),
        new Effect("gate", (p, x, rate) => Gate(p, rate, x)[0], (p, rate, l, rt) => { int[][] both = Gate(p, rate, l, rt); return (both[0], both[1]); }),
        new Effect("deesser", (p, x, rate) => DeEss(p, rate, x)[0], (p, rate, l, rt) => { int[][] both = DeEss(p, rate, l, rt); return (both[0], both[1]); }, "2"),
        new Effect("chorus", (p, x, rate) => Chorus(p, x, rate, 0), (p, rate, l, rt) => (Chorus(p, l, rate, 0), Chorus(p, rt, rate, 1)), "2"),
        new Effect("flanger", (p, x, rate) => ModDelay(x, rate, 3, Math.Clamp(p("depth", 2), 1, 3), Math.Clamp(p("period", 2000), 100, 10000), Math.Clamp(p("feedback", 50), 0, 90), Math.Clamp(p("mix", 50), 0, 100))),
        new Effect("phaser", Phase),
        new Effect("bitcrusher", Crush),
        new Effect("pitch", (p, x, rate) => TimeStretch.PitchShift(x, p("semitones", 0), rate, p("method", TimeStretch.MethodWsola)), null, "2"),
        new Effect("stretch", (p, x, rate) => TimeStretch.Speed(x, p("speed", 100), rate)),
        new Effect("width", (p, x, rate) => x, (p, rate, l, rt) => Width(p, l, rt)),
        new Panner(),
        new Duck(),
        new Scratch(),
    }.Select(renderer => (IBlockRenderer)new AudioRateRenderer(renderer)).ToArray();

    private sealed class Effect(string blockType, Func<Func<string, int, int>, int[], int, int[]> process, Func<Func<string, int, int>, int, int[], int[], (int[] Left, int[] Right)>? stereoProcess = null, string version = "1", Func<Func<string, int, int>, int, int[]?, int[]?, int[][], int[][]>? keyedProcess = null) : IBlockRenderer
    {
        public string BlockType => blockType;

        public string Id => "builtin." + blockType;

        public string Version => version;

        private static int[] MixCurve(RenderRequest r, string text, int length) => Fades.Is(text) ? [.. Fades.Amount(Fades.Parse(text), r.SampleRate, r.SamplesPerBar, r.FirstBar, length).Select(a => (int)((long)a * 100 / 32768))] : ControlBinding.Is(text) ? Automation.CurveAt(Automation.ParseSamples(text, r.SamplesPerBar, r.SampleRate), r.FirstBar, r.SamplesPerBar, length, v => Math.Clamp(v, 0, 100)) : Automation.Curve(Automation.Parse(text), r.FirstBar, r.SamplesPerBar, length, v => Math.Clamp(v, 0, 100));

        private static int[] Blend(int[] dry, int[] wet, int[] percent)
        {
            int[] y = new int[dry.Length];
            for (int i = 0; i < y.Length; i++)
            {
                y[i] = (int)(((long)dry[i] * (100 - percent[i]) + ((long)wet[i] * percent[i])) / 100);
            }

            return y;
        }

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            byte[]? input = r.Inputs.GetValueOrDefault("in");
            bool stereo = input is not null && ChannelAudio.IsStereo(input);
            string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
            if (blockType == "stretch" && Automation.Is(Text("speed")) && Text("enabled") != "off")
            {
                int length = input is null ? r.BarCount * r.SamplesPerBar : stereo ? ChannelAudio.Decode(input).Buffer.Left.Length : Wav.Decode(input).Samples.Length;
                int[] speeds = Automation.CurveAt(Automation.ParseSamples(Text("speed"), r.SamplesPerBar, r.SampleRate), r.FirstBar, r.SamplesPerBar, length, value => Math.Clamp(value, 25, 400));
                if (stereo)
                {
                    (StereoBuffer channels, int rate) = ChannelAudio.Decode(input!);
                    int[] left = TimeStretch.Speed([.. channels.Left.Select(s => (int)s)], speeds, rate);
                    int[] right = TimeStretch.Speed([.. channels.Right.Select(s => (int)s)], speeds, rate);
                    return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer([.. left.Select(v => FixedAudio.Clip(v))], [.. right.Select(v => FixedAudio.Clip(v))]), rate))]);
                }

                short[] rampSamples = input is null ? new short[length] : Wav.Decode(input).Samples;
                int[] swept = TimeStretch.Speed([.. rampSamples.Select(s => (int)s)], speeds, r.SampleRate);
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", Wav.Encode([.. swept.Select(v => FixedAudio.Clip(v))], r.SampleRate))]);
            }

            if (blockType == "pitch" && Automation.Is(Text("semitones")) && Text("enabled") != "off")
            {
                // a pitch curve, semitones auto(1:0, 4:12): the move changes from sample to sample (a riser, a tape stop, a bend) and the length stays. With range the values are
                // percent of that many semitones (auto(1:0, 4:100) with range 12 is an octave), like the pitch knob and range of a sampler. The spectral method moves it; the voice
                // method keeps the colour of a voice (the formants), the others move the colour along like a tape.
                (StereoBuffer bendChannels, int bendRate) = input is not null ? ChannelAudio.Decode(input) : (new StereoBuffer(new short[r.BarCount * r.SamplesPerBar], new short[r.BarCount * r.SamplesPerBar]), r.SampleRate);
                int range = Math.Clamp(Read(Text("range"), 0), 0, 48);
                int[] cents = Automation.CurveAt(Automation.ParseSamples(Text("semitones"), r.SamplesPerBar, bendRate), r.FirstBar, r.SamplesPerBar, bendChannels.Left.Length, value => range > 0 ? Math.Clamp(value, -100, 100) * range : Math.Clamp(value, -48, 48) * 100);
                short[][] source = stereo ? [bendChannels.Left, bendChannels.Right] : [bendChannels.Left];
                double[][] bent = Autotune.Spectral(source, bendRate, position => cents[(int)Math.Clamp(position, 0, cents.Length - 1)] / 100.0, Text("method") == "voice", cancellationToken);
                short[][] clipped = [.. bent.Select(channel => channel.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)).ToArray())];
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", stereo ? StereoWav.Encode(new StereoBuffer(clipped[0], clipped[1]), bendRate) : Wav.Encode(clipped[0], bendRate))]);
            }

            // an automated mix, mix auto(bar:percent, ...): the effect runs fully wet and the dry and wet sound are blended per sample afterwards
            bool mixAutomated = blockType is "delay" or "reverb" or "chorus" or "flanger" or "phaser" or "bitcrusher" && Automation.Is(Text("mix"));
            bool thresholdAutomated = blockType == "compressor" && Automation.Is(Text("threshold"));
            // the pitch is given in semitones with a decimal point (1.5 is a semitone and a half) and used in hundredths
            int Number(string name, int fallback) => name == "semitones" && blockType == "pitch" ? (double.TryParse(Text(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double semitones) ? (int)Math.Round(semitones * 100) : 0) : name == "method" && blockType == "pitch" ? Text(name) switch { "granular" => TimeStretch.MethodGranular, "voice" => TimeStretch.MethodVoice, _ => TimeStretch.MethodWsola } : name == "threshold" && thresholdAutomated ? -18 : name == "mix" && mixAutomated ? 100 : Text(name) is "hard" or "highpass" ? 1 : Text(name) is "soft" or "lowpass" ? 0 : Read(Text(name), fallback);
            double lfoCycles = FilterLfoCycles(Text("lfo_rate"));
            int lfoDepth = Math.Clamp(Read(Text("lfo_depth"), 0), 0, 8);
            bool lfoOn = blockType == "filter" && lfoCycles > 0 && lfoDepth > 0;
            bool resonanceAutomated = blockType == "filter" && Automation.Is(Text("resonance"));
            double resonance = blockType == "filter" && !resonanceAutomated ? Math.Clamp(double.TryParse(Text("resonance").Replace("%", string.Empty, StringComparison.Ordinal).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double resonanceValue) ? resonanceValue : 0, 0, 100) : 0;
            if (blockType == "filter" && input is not null && (Automation.Is(Text("cutoff")) || lfoOn || resonance > 0 || resonanceAutomated || Text("type") is "bandpass" or "notch" || ResonantFilter.PassesOf(Text("slope")) > 1) && Text("enabled") != "off")
            {
                // an automated cutoff, auto(bar:Hz, ...), and/or a tempo-synced low-frequency oscillator on the cutoff (the wobble): the two one-pole stages follow a cutoff per sample
                bool high = Text("type") == "highpass";
                int filterShape = ResonantFilter.ShapeOf(Text("type"));
                int passes = ResonantFilter.PassesOf(Text("slope"));
                (StereoBuffer both, int filterRate) = ChannelAudio.Decode(input);
                int[] cutoffs = Automation.Is(Text("cutoff"))
                    ? Automation.CurveAt(Automation.ParseSamples(Text("cutoff"), r.SamplesPerBar, r.SampleRate), r.FirstBar, r.SamplesPerBar, both.Left.Length, value => Math.Clamp(value, 20, 20000))
                    : [.. Enumerable.Repeat(Math.Clamp(Read(Text("cutoff"), 1000), 20, 20000), both.Left.Length)];
                if (lfoOn)
                {
                    // the cutoff is the lowest point of the swing; the oscillator lifts it by up to lfo_depth octaves; the phase follows the song bar, so every slice is the same
                    long barOffset = (long)(r.FirstBar - 1) * r.SamplesPerBar;
                    string shape = Text("lfo_shape");
                    for (int i = 0; i < cutoffs.Length; i++)
                    {
                        double cycle = ((barOffset + i) / (double)r.SamplesPerBar * lfoCycles) % 1.0;
                        double wave = shape switch
                        {
                            "triangle" => cycle < 0.5 ? cycle * 2 : 2 - (cycle * 2),
                            "saw" => cycle,
                            "square" => cycle < 0.5 ? 0 : 1,
                            _ => 0.5 - (0.5 * Math.Cos(2 * Math.PI * cycle)),
                        };
                        cutoffs[i] = (int)Math.Clamp(cutoffs[i] * Math.Pow(2, lfoDepth * wave), 20, 20000);
                    }
                }

                // resonance 0 keeps the two one-pole stages (byte-identical to older builds); above 0 a two-pole filter with a peak at the cutoff
                int[]? resonances = resonanceAutomated ? Automation.CurveAt(Automation.ParseSamples(Text("resonance"), r.SamplesPerBar, r.SampleRate), r.FirstBar, r.SamplesPerBar, both.Left.Length, value => Math.Clamp(value, 0, 100)) : null;
                int[] Sweep(int[] channel) => resonance > 0 || resonanceAutomated || filterShape >= ResonantFilter.BandPass || passes > 1 ? ResonantFilter.Process(channel, cutoffs, filterRate, filterShape, resonance, resonances, passes) : FilterSweep(channel, cutoffs, filterRate, high);
                short[] sweptLeft = [.. Sweep([.. both.Left.Select(v => (int)v)]).Select(v => FixedAudio.Clip(v))];
                if (!stereo)
                {
                    return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", Wav.Encode(sweptLeft, filterRate))]);
                }

                short[] sweptRight = [.. Sweep([.. both.Right.Select(v => (int)v)]).Select(v => FixedAudio.Clip(v))];
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer(sweptLeft, sweptRight), filterRate))]);
            }

            bool hasKey = r.Inputs.TryGetValue("key", out byte[]? keyAudio);
            if (keyedProcess is not null && input is not null && (hasKey || thresholdAutomated) && Text("enabled") != "off")
            {
                // sidechain: the level of the key input (the louder channel) drives the detector, the gain goes to the input;
                // an automated threshold, threshold auto(bar:dB, ...), is a threshold per sample
                int frames = stereo ? ChannelAudio.Decode(input).Buffer.Left.Length : Wav.Decode(input).Samples.Length;
                int[]? key = null;
                if (hasKey)
                {
                    (StereoBuffer keyChannels, int keyRate) = ChannelAudio.Decode(keyAudio!);
                    int[] keyLeft = [.. keyChannels.Left.Select(v => (int)v)], keyRight = [.. keyChannels.Right.Select(v => (int)v)];
                    int keyFilter = Number("keyFilter", 0);
                    if (keyFilter > 0)
                    {
                        // only the part of the key above the frequency drives the detector (a high-pass, so a bass in the key does not trigger it)
                        int[] lowLeft = LowPassAt(keyLeft, keyFilter, keyRate), lowRight = LowPassAt(keyRight, keyFilter, keyRate);
                        keyLeft = [.. keyLeft.Select((v, i) => v - lowLeft[i])];
                        keyRight = [.. keyRight.Select((v, i) => v - lowRight[i])];
                    }

                    key = new int[frames];
                    for (int i = 0; i < frames && i < keyLeft.Length; i++)
                    {
                        key[i] = Math.Max(Math.Abs(keyLeft[i]), Math.Abs(keyRight[i]));
                    }
                }

                int[]? thresholds = thresholdAutomated ? Automation.CurveAt(Automation.ParseSamples(Text("threshold"), r.SamplesPerBar, r.SampleRate), r.FirstBar, r.SamplesPerBar, frames, v => (int)((long)FixedAudio.GainFromDb(Math.Clamp(v, -60, 0)) * 32767 >> 15)) : null;

                if (stereo)
                {
                    (StereoBuffer both, int stereoRate) = ChannelAudio.Decode(input);
                    int[][] keyedStereo = keyedProcess(Number, stereoRate, key, thresholds, [[.. both.Left.Select(v => (int)v)], [.. both.Right.Select(v => (int)v)]]);
                    return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer([.. keyedStereo[0].Select(v => FixedAudio.Clip(v))], [.. keyedStereo[1].Select(v => FixedAudio.Clip(v))]), stereoRate))]);
                }

                int[][] keyedMono = keyedProcess(Number, r.SampleRate, key, thresholds, [[.. Wav.Decode(input).Samples.Select(v => (int)v)]]);
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", Wav.Encode([.. keyedMono[0].Select(v => FixedAudio.Clip(v))], r.SampleRate))]);
            }

            if (stereo)
            {
                // bypass: the very same bytes go through
                if (Text("enabled") == "off")
                {
                    return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", input!)]);
                }

                (StereoBuffer channels, int rate) = ChannelAudio.Decode(input!);
                int[] left = [.. channels.Left.Select(s => (int)s)];
                int[] right = [.. channels.Right.Select(s => (int)s)];
                (int[] wideLeft, int[] wideRight) = stereoProcess is not null ? stereoProcess(Number, rate, left, right) : (process(Number, left, rate), process(Number, right, rate));
                if (mixAutomated)
                {
                    int[] curve = MixCurve(r, Text("mix"), left.Length);
                    wideLeft = Blend(left, wideLeft, curve);
                    wideRight = Blend(right, wideRight, curve);
                }

                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer([.. wideLeft.Select(v => FixedAudio.Clip(v))], [.. wideRight.Select(v => FixedAudio.Clip(v))]), rate))]);
            }

            short[] samples = input is not null ? Wav.Decode(input).Samples : new short[r.BarCount * r.SamplesPerBar];
            short[] result;
            if (Text("enabled") == "off")
            {
                result = samples;
            }
            else
            {
                int[] wide = process(Number, [.. samples.Select(s => (int)s)], r.SampleRate);
                if (mixAutomated)
                {
                    wide = Blend([.. samples.Select(s => (int)s)], wide, MixCurve(r, Text("mix"), samples.Length));
                }

                result = [.. wide.Select(v => FixedAudio.Clip(v))];
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", Wav.Encode(result, r.SampleRate))]);
        }
    }

    /// <summary>
    /// The DJ scratch: in the marked steps the sound is read from a moving point, as if the record were pushed and pulled by hand.
    /// The record holds the sound at the start of the step; x moves it forward and back (a triangle), b only back, f only forward, c forward and back
    /// with the fader shut at the turn, r spins it back faster and faster while it fades, - is silence. Integer arithmetic with a linear read between samples.
    /// </summary>
    private sealed class Scratch : IBlockRenderer
    {
        public string BlockType => "scratch";

        public string Id => "builtin.scratch";

        public string Version => "1";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            byte[]? input = r.Inputs.GetValueOrDefault("in");
            bool stereo = input is not null && ChannelAudio.IsStereo(input);
            string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
            StereoBuffer channels = default!;
            int rate = r.SampleRate;
            short[] mono = [];
            if (stereo)
            {
                (channels, rate) = ChannelAudio.Decode(input!);
            }
            else
            {
                mono = input is not null ? Wav.Decode(input).Samples : new short[r.BarCount * r.SamplesPerBar];
            }

            if (Text("enabled") == "off")
            {
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", stereo ? input! : Wav.Encode(mono, r.SampleRate))]);
            }

            string[] groups = [.. Text("pattern").Split('|').Select(g => g.Replace(" ", string.Empty, StringComparison.Ordinal)).Where(g => g.Length > 0)];
            if (groups.Length == 0)
            {
                groups = ["x.x.b...x.x.r..."];
            }

            HashSet<int> bars = [.. Text("bars").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(b => Read(b, 0)).Where(b => b > 0)];
            int depth = Math.Clamp(Read(Text("depth"), 120), 10, 600) * rate / 1000;
            int mix = Math.Clamp(Read(Text("mix"), 100), 0, 100);
            if (stereo)
            {
                short[] left = Apply(channels.Left, r, groups, bars, depth, mix);
                short[] right = Apply(channels.Right, r, groups, bars, depth, mix);
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer(left, right), rate))]);
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", Wav.Encode(Apply(mono, r, groups, bars, depth, mix), r.SampleRate))]);
        }

        private static short[] Apply(short[] x, RenderRequest r, string[] groups, HashSet<int> bars, int depth, int mix)
        {
            short[] y = (short[])x.Clone();
            int spb = r.SamplesPerBar;
            int edge = Math.Max(1, r.SampleRate / 500);
            for (int ob = 0; (long)ob * spb < x.Length; ob++)
            {
                int songBar = r.FirstBar + ob;
                if (bars.Count > 0 && !bars.Contains(songBar))
                {
                    continue;
                }

                string grid = groups[(songBar - 1) % groups.Length];
                int steps = grid.Length is 12 or 24 or 32 ? grid.Length : 16;
                for (int st = 0; st < steps; st++)
                {
                    char move = grid[st % grid.Length];
                    if (move is not ('x' or 'c' or 'b' or 'f' or 'r' or '-'))
                    {
                        continue;
                    }

                    long start = ((long)ob * spb) + ((long)st * spb / steps);
                    int length = (int)(((long)ob * spb) + ((long)(st + 1) * spb / steps) - start);
                    int half = Math.Max(1, length / 2);
                    // only an edge next to an untouched step is ramped, so two scratched or cut steps in a row join without a gap
                    bool rampIn = st == 0 || grid[(st - 1) % grid.Length] is not ('x' or 'c' or 'b' or 'f' or 'r' or '-');
                    bool rampOut = st == steps - 1 || grid[(st + 1) % grid.Length] is not ('x' or 'c' or 'b' or 'f' or 'r' or '-');
                    for (int i = 0; i < length && start + i < x.Length; i++)
                    {
                        // where the record is, in 1/256 samples ahead of the start of the step, how loud, and whether the fader is open
                        long triangle = i < half ? (long)depth * 256 * i / half : (long)depth * 256 * (length - i) / Math.Max(1, length - half);
                        long position = move switch
                        {
                            'x' or 'c' => triangle,
                            'b' => (long)depth * 256 * (length - i) / Math.Max(1, length),
                            'f' => (long)depth * 256 * i / Math.Max(1, length),
                            'r' => (long)depth * 4 * 256 * ((long)(length - i) * (length - i)) / Math.Max(1L, (long)length * length),
                            _ => 0,
                        };
                        bool open = move != '-' && (move != 'c' || Math.Abs(i - half) > length / 8);
                        int amount = move == 'r' ? 256 * (length - i) / Math.Max(1, length) : 256;
                        long read = (start * 256) + position;
                        long whole = read >> 8;
                        int fraction = (int)(read & 255);
                        int a = whole < x.Length ? x[whole] : 0;
                        int b = whole + 1 < x.Length ? x[whole + 1] : 0;
                        int scratched = open ? ((((a * (256 - fraction)) + (b * fraction)) >> 8) * amount) >> 8 : 0;
                        // short ramps at the edges of a step (about 2 ms), so a step never clicks
                        int ramp = Math.Min(256, Math.Min(rampIn ? i : edge, rampOut ? length - 1 - i : edge) * 256 / edge);
                        int wet = ((scratched * ramp) + (x[start + i] * (256 - ramp))) >> 8;
                        y[start + i] = FixedAudio.Clip(((wet * mix) + (x[start + i] * (100 - mix))) / 100);
                    }
                }
            }

            return y;
        }
    }

    /// <summary>The sidechain-like duck: the sound dips at every hit of a step pattern and comes back up over the release (linear, integer gain), like a pumping compressor keyed by the kick.</summary>
    private sealed class Duck : IBlockRenderer
    {
        public string BlockType => "duck";

        public string Id => "builtin.duck";

        public string Version => "3";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            byte[]? input = r.Inputs.GetValueOrDefault("in");
            bool stereo = input is not null && ChannelAudio.IsStereo(input);
            StereoBuffer channels = default!;
            int rate = r.SampleRate;
            short[] samples;
            if (stereo)
            {
                (channels, rate) = ChannelAudio.Decode(input!);
                samples = channels.Left;
            }
            else
            {
                samples = input is not null ? Wav.Decode(input).Samples : new short[r.BarCount * r.SamplesPerBar];
            }

            string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
            if (Text("enabled") == "off")
            {
                // bypass: the sound goes through untouched, stereo stays the very same bytes
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", stereo ? input! : Wav.Encode(samples, r.SampleRate))]);
            }

            int[] gain = Curve(r, Text, samples.Length);
            if (stereo)
            {
                // one gain curve for both channels, so left and right can never pump differently
                short[] left = Apply(channels.Left, gain);
                short[] right = Apply(channels.Right, gain);
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer(left, right), rate))]);
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", Wav.Encode(Apply(samples, gain), r.SampleRate))]);
        }

        private static short[] Apply(short[] samples, int[] gain)
        {
            short[] result = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                result[i] = FixedAudio.Clip((long)samples[i] * gain[i] >> 15);
            }

            return result;
        }

        private static int[] Curve(RenderRequest r, Func<string, string> text, int length)
        {
            int depth = Math.Clamp(Read(text("depth"), 10), 0, 40);
            int release = Math.Max(1, Math.Clamp(Read(text("release"), 160), 10, 2000) * r.SampleRate / 1000);
            string pattern = text("pattern").Replace("|", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            if (pattern.Length == 0)
            {
                pattern = "x...x...x...x...";
            }

            int attack = Math.Max(1, r.SampleRate * 4 / 1000);
            release = Math.Max(release, attack + 1);
            int stepLength = Math.Max(1, (int)(r.SamplesPerBar / (4.0 * r.BeatsPerBar)));
            int floor = FixedAudio.GainFromDb(-Math.Min(depth, 60));
            int[] gain = new int[length];
            Array.Fill(gain, 32768);
            for (long step = 0; step * stepLength < length; step++)
            {
                if (pattern[(int)(step % pattern.Length)] != 'x')
                {
                    continue;
                }

                long start = step * stepLength;
                for (int i = 0; i < release && start + i < length; i++)
                {
                    // the dip starts with a short ramp (about 4 ms) so the gain never jumps: a step makes a click
                    int g = i < attack
                        ? 32768 - (int)((long)(32768 - floor) * i / attack)
                        : floor + (int)((long)(32768 - floor) * (i - attack) / Math.Max(1, release - attack));
                    if (g < gain[start + i])
                    {
                        gain[start + i] = g;
                    }
                }
            }

            return gain;
        }
    }

    /// <summary>
    /// Reads a level in decibels the way it is written in a song: with its unit (<c>-12 dB</c>), without it (<c>6</c>), or in quotes. A decimal part is dropped.
    /// The value is cut to the range of the parameter. A missing or unreadable value gives <paramref name="fallback"/>.
    /// </summary>
    internal static int ReadDb(string? text, int fallback, int min, int max) => Math.Clamp(Read((text ?? string.Empty).Trim().Trim('"').Trim(), fallback), min, max);

    /// <summary>Reads the leading whole number of a value like <c>-3 dB</c> or <c>40%</c>.</summary>
    internal static int Read(string text, int fallback)
    {
        int end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || end == 0 && text[end] is '-' or '+'))
        {
            end++;
        }

        return int.TryParse(text[..end], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }

    /// <summary>Coefficient (Q15) of a one-pole filter at <paramref name="hz"/>: x/(1+x) with x = 2*pi*fc/fs.</summary>
    internal static long Coefficient(int hz, int sampleRate)
    {
        long x = 205887L * Math.Clamp(hz, 1, sampleRate / 2 - 1) / sampleRate;
        return x * 32768 / (32768 + x);
    }

    /// <summary>
    /// Coefficient (Q15) of a one-pole low pass whose -3 dB point is close to <paramref name="hz"/>: 1 - e^(-2 pi fc / fs), with e^x summed as a series in integers (Q24), so it is
    /// the same everywhere. <see cref="Coefficient"/> is the older guess x/(1+x), which lands too low at high frequencies (6 kHz asked gave about 4.3 kHz); the instruments keep it.
    /// </summary>
    internal static long CutoffCoefficient(int hz, int sampleRate)
    {
        long x = 105414357L * Math.Clamp(hz, 1, sampleRate / 2 - 1) / sampleRate;   // 2 pi fc / fs in Q24 (2 pi is 105414357 / 2^24)
        long sum = 1L << 24, term = 1L << 24;
        for (int k = 1; k < 40 && term > 0; k++)
        {
            term = term * x / k >> 24;
            sum += term;
        }

        return 32768 - ((32768L << 24) / sum);
    }

    /// <summary>A one-pole low pass with its -3 dB point at <paramref name="hz"/> (see <see cref="CutoffCoefficient"/>); the effects use it.</summary>
    internal static int[] LowPassAt(int[] x, int hz, int sampleRate) => OnePole(x, CutoffCoefficient(hz, sampleRate));

    /// <summary>Two one-pole low passes in a row with the -3 dB point of the pair at <paramref name="hz"/> (each stage sits higher, at 1.554 times: one stage is -1.5 dB there).</summary>
    private static int[] LowPass2At(int[] x, int hz, int sampleRate)
    {
        long a = CutoffCoefficient(PairCutoff(hz, sampleRate), sampleRate);
        return OnePole(OnePole(x, a), a);
    }

    private static int PairCutoff(int hz, int sampleRate) => (int)Math.Min(sampleRate / 2 - 1, (long)hz * 1554 / 1000);

    private static int[] OnePole(int[] x, long a)
    {
        int[] y = new int[x.Length];
        long state = 0;
        for (int i = 0; i < x.Length; i++)
        {
            state += a * (x[i] - state) >> 15;
            y[i] = (int)state;
        }

        return y;
    }

    internal static int[] LowPass(int[] x, int hz, int sampleRate)
    {
        long a = Coefficient(hz, sampleRate);
        int[] y = new int[x.Length];
        long state = 0;
        for (int i = 0; i < x.Length; i++)
        {
            state += a * (x[i] - state) >> 15;
            y[i] = (int)state;
        }

        return y;
    }

    private static int Db(Func<string, int, int> p, string name, int fallback, int min, int max) => FixedAudio.GainFromDb(Math.Clamp(p(name, fallback), min, max));

    private static int Percent(Func<string, int, int> p, string name, int fallback) => Math.Clamp(p(name, fallback), 0, 100);

    private static int[] Mix(int[] dry, int[] wet, int percent) => [.. dry.Select((d, i) => (int)(((long)d * (100 - percent) + (long)wet[i] * percent) / 100))];

    private static int[] Eq(Func<string, int, int> p, int[] x, int rate)
    {
        int low = Db(p, "low", 0, -12, 12);
        int mid = Db(p, "mid", 0, -12, 12);
        int high = Db(p, "high", 0, -12, 12);
        int[] a = LowPassAt(x, Math.Clamp(p("low_hz", 250), 40, 2000), rate);
        int[] b = LowPassAt(x, Math.Clamp(p("high_hz", 3000), 1000, 12000), rate);
        int[] y = new int[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            long bandLow = a[i];
            long bandMid = b[i] - a[i];
            long bandHigh = x[i] - b[i];
            y[i] = (int)((bandLow * low + bandMid * mid + bandHigh * high) >> 15);
        }

        return y;
    }

    private static int[] Distortion(Func<string, int, int> p, int[] x, int rate)
    {
        int drive = Math.Clamp(p("drive", 12), 0, 36);
        long gain = 32768;
        for (int left = drive; left > 0; left -= 12)
        {
            gain = gain * FixedAudio.GainFromDb(Math.Min(left, 12)) >> 15;
        }

        bool hard = p("mode", 0) == 1;
        int level = Db(p, "level", 0, -24, 12);
        int[] driven = new int[x.Length];
        // four times over: four points on the line from the last sample to this one, each shaped, and their mean. The shaping makes harmonics above half the sample rate;
        // without this they fold back as fizz (aliasing), with it far less of them do.
        for (int i = 0; i < x.Length; i++)
        {
            long previous = i > 0 ? x[i - 1] : 0, sum = 0;
            for (int k = 1; k <= 4; k++)
            {
                long v = (previous + ((x[i] - previous) * k >> 2)) * gain >> 15;
                sum += hard ? Math.Clamp(v, -32767, 32767) : v * 32767 / (32768 + Math.Abs(v));
            }

            driven[i] = (int)(sum >> 2);
        }

        int[] tone = LowPassAt(driven, Math.Clamp(p("tone", 8000), 500, 16000), rate);
        int[] wet = [.. tone.Select(v => (int)((long)v * level >> 15))];
        return Mix(x, wet, Percent(p, "mix", 100));
    }

    private static int[] Delay(Func<string, int, int> p, int[] x, int rate)
    {
        int length = Math.Max(1, Math.Clamp(p("time", 250), 1, 2000) * rate / 1000);
        int feedback = Math.Clamp(p("feedback", 35), 0, 90);
        int mix = Percent(p, "mix", 30);
        int[] echo = new int[x.Length];
        for (int i = length; i < x.Length; i++)
        {
            echo[i] = x[i - length] + (int)((long)echo[i - length] * feedback / 100);
        }

        return [.. x.Select((v, i) => v + (int)((long)echo[i] * mix / 100))];
    }

    // the comb and all-pass lengths of the classic Freeverb (samples at 44.1 kHz); the right channel is a little longer, so a mono source gets a stereo tail
    private static readonly int[] CombLengths = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllPassLengths = [556, 441, 341, 225];
    private const int StereoSpread = 23;

    /// <summary>
    /// A room in the way of Freeverb, integer only. The sound that goes into the room loses its lows first (a tail full of bass is mud), then eight comb filters ring in
    /// parallel with a damped feedback (the highs die sooner than the lows, as in a real room) and four all-pass filters smear the echoes into one smooth tail.
    /// The room is about as loud as the dry sound whatever the decay; the dry sound stays as it is and mix sets the level of the room.
    /// </summary>
    private static int[] Reverb(Func<string, int, int> p, int[] x, int rate, bool right)
    {
        int size = Math.Clamp(p("size", 50), 1, 100);
        int decay = Math.Clamp(p("decay", 60), 0, 95);
        int mix = Percent(p, "mix", 25);
        long feedback = 22938 + (9175L * decay / 95);   // Q15, 0.70 to 0.98
        const long damping = 9830;                       // Q15 0.30: how much of its last value the filter in each comb keeps
        double g = feedback / 32768.0;
        long gain = (long)Math.Round(32768 * Math.Sqrt(0.5 * (1 - (g * g)) / CombLengths.Length));   // the input of each comb (Q15); Math.Sqrt is exact in IEEE, so this is the same everywhere
        int[] low = LowPassAt(x, 160, rate);
        long[] feed = new long[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            feed[i] = (x[i] - low[i]) * gain >> 15;
        }

        long[] wet = new long[x.Length];
        int spread = right ? StereoSpread : 0;
        foreach (int baseLength in CombLengths)
        {
            int length = (int)Math.Max(1, (long)(baseLength + spread) * rate * (60 + size) / (44100L * 110));   // size 50 is the classic room, 1 about half of it, 100 half again as large
            long[] ring = new long[length];
            long store = 0;
            for (int i = 0, at = 0; i < x.Length; i++, at = at + 1 == length ? 0 : at + 1)
            {
                long delayed = ring[at];
                store = ((delayed * (32768 - damping)) + (store * damping)) >> 15;
                ring[at] = feed[i] + (store * feedback >> 15);
                wet[i] += delayed;
            }
        }

        foreach (int baseLength in AllPassLengths)
        {
            int length = (int)Math.Max(1, (long)(baseLength + spread) * rate / 44100);
            long[] ring = new long[length];
            for (int i = 0, at = 0; i < x.Length; i++, at = at + 1 == length ? 0 : at + 1)
            {
                long delayed = ring[at];
                long input = wet[i];
                wet[i] = delayed - input;
                ring[at] = input + (delayed >> 1);
            }
        }

        int[] y = new int[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            y[i] = (int)Math.Clamp(x[i] + (wet[i] * mix / 100), int.MinValue, int.MaxValue);
        }

        return y;
    }

    /// <summary>One compressor over one or two channels (the detector listens to the key when one is given: sidechain). With two channels the level is the louder of both and one gain goes to both (linked), so the stereo image does not shift.</summary>
    private static int[][] Compress(Func<string, int, int> p, int rate, int[]? key, int[]? thresholds, params int[][] channels)
    {
        long threshold = FixedAudio.GainFromDb(Math.Clamp(p("threshold", -18), -60, 0));
        threshold = threshold * 32767 >> 15;
        int ratio = Math.Clamp(p("ratio", 4), 1, 20);
        int attack = Math.Max(1, Math.Clamp(p("attack", 10), 1, 500) * rate / 1000);
        int release = Math.Max(1, Math.Clamp(p("release", 120), 5, 2000) * rate / 1000);
        int makeup = Db(p, "makeup", 0, 0, 12);
        int length = channels[0].Length;
        int[][] y = [.. channels.Select(_ => new int[length])];
        long env = 0;
        for (int i = 0; i < length; i++)
        {
            long level = 0;
            if (key is not null)
            {
                level = i < key.Length ? key[i] : 0;
            }
            else
            {
                foreach (int[] channel in channels)
                {
                    level = Math.Max(level, Math.Abs((long)channel[i]));
                }
            }

            env += (level - env) / (level > env ? attack : release);
            long gain = 32768;
            long limit = thresholds is not null ? thresholds[i] : threshold;
            if (env > limit && env > 0)
            {
                long reduced = limit + ((env - limit) / ratio);
                gain = reduced * 32768 / env;
            }

            for (int c = 0; c < channels.Length; c++)
            {
                y[c][i] = (int)((((long)channels[c][i] * gain) >> 15) * makeup >> 15);   // in 64 bits: with make-up above 6 dB a loud sample overflowed 32 bits (a click)
            }
        }

        return y;
    }

    /// <summary>
    /// A look-ahead peak limiter over one or two channels (linked: one gain for both). The gain needed to keep each sample under the ceiling is released slowly, the minimum over the
    /// look-ahead window is taken and averaged over the window, and the signal is delayed by that window, so no sample passes the ceiling and the gain never jumps.
    /// </summary>
    private static int[][] Limit(Func<string, int, int> p, int rate, params int[][] channels)
    {
        long ceiling = (long)FixedAudio.GainFromDb(Math.Clamp(p("ceiling", -1), -24, 0)) * 32767 >> 15;
        int look = Math.Max(1, Math.Clamp(p("lookahead", 5), 1, 20) * rate / 1000);
        int release = Math.Max(1, Math.Clamp(p("release", 100), 10, 1000) * rate / 1000);
        int length = channels[0].Length;
        int total = length + look;
        long[] need = new long[total];
        long previous = 32768;
        for (int i = 0; i < total; i++)
        {
            long level = 0;
            if (i < length)
            {
                foreach (int[] channel in channels)
                {
                    level = Math.Max(level, Math.Abs((long)channel[i]));
                }
            }

            long wanted = level > ceiling ? ceiling * 32768 / level : 32768;
            previous = wanted < previous ? wanted : Math.Min(wanted, previous + Math.Max(1, (32768 - previous) / release));
            need[i] = previous;
        }

        // the minimum over the last look + 1 values (monotonic queue), then a running mean over the last look values
        long[] floor = new long[total];
        int[] queue = new int[total];
        int head = 0, tail = 0;
        for (int i = 0; i < total; i++)
        {
            while (tail > head && need[queue[tail - 1]] >= need[i])
            {
                tail--;
            }

            queue[tail++] = i;
            while (queue[head] < i - look)
            {
                head++;
            }

            floor[i] = need[queue[head]];
        }

        long[] prefix = new long[total + 1];
        for (int i = 0; i < total; i++)
        {
            prefix[i + 1] = prefix[i] + floor[i];
        }

        int[][] y = [.. channels.Select(_ => new int[length])];
        for (int k = 0; k < length; k++)
        {
            long gain = (prefix[k + look + 1] - prefix[k + 1]) / look;
            for (int c = 0; c < channels.Length; c++)
            {
                y[c][k] = (int)((long)channels[c][k] * gain >> 15);
            }
        }

        return y;
    }

    /// <summary>Limits a whole audio file (mono or stereo) to a ceiling in dB with the look-ahead limiter; used by the master block.</summary>
    internal static byte[] LimitAudio(byte[] audio, int ceilingDb, int releaseMs = 100, int lookaheadMs = 5)
    {
        bool stereo = ChannelAudio.IsStereo(audio);
        (StereoBuffer channels, int rate) = ChannelAudio.Decode(audio);
        int Settings(string name, int fallback) => name switch { "ceiling" => ceilingDb, "release" => releaseMs, "lookahead" => lookaheadMs, _ => fallback };
        int[] left = [.. channels.Left.Select(v => (int)v)];
        if (!stereo)
        {
            return Wav.Encode([.. Limit(Settings, rate, left)[0].Select(v => FixedAudio.Clip(v))], rate);
        }

        int[][] limited = Limit(Settings, rate, left, [.. channels.Right.Select(v => (int)v)]);
        return StereoWav.Encode(new StereoBuffer([.. limited[0].Select(v => FixedAudio.Clip(v))], [.. limited[1].Select(v => FixedAudio.Clip(v))]), rate);
    }

    /// <summary>Mid/side width: 100 percent leaves the image, 0 folds to mono, 200 doubles the side signal.</summary>
    private static (int[] Left, int[] Right) Width(Func<string, int, int> p, int[] left, int[] right)
    {
        int width = Math.Clamp(p("width", 100), 0, 200);
        int[] l = new int[left.Length];
        int[] r = new int[left.Length];
        for (int i = 0; i < left.Length; i++)
        {
            long mid = (left[i] + (long)right[i]) >> 1;
            long side = (left[i] - (long)right[i]) * width / 200;
            l[i] = (int)(mid + side);
            r[i] = (int)(mid - side);
        }

        return (l, r);
    }

    /// <summary>The pan effect: a mono input becomes stereo with the equal-power table, a stereo input is balanced (the far channel gets quieter, the near one is left alone).</summary>
    private sealed class Panner : IBlockRenderer
    {
        public string BlockType => "pan";

        public string Id => "builtin.pan";

        public string Version => "2";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            byte[]? input = r.Inputs.GetValueOrDefault("in");
            string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
            bool stereo = input is not null && ChannelAudio.IsStereo(input);
            if (Text("enabled") == "off" && input is not null)
            {
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", input)]);
            }

            // the pan can be automated: auto(bar:pan, ...) gives one gain per sample for each side
            if (Automation.Is(Text("pan")))
            {
                IReadOnlyList<(long Sample, int Value)> points = Automation.ParseSamples(Text("pan"), r.SamplesPerBar, r.SampleRate);
                int rate2 = r.SampleRate;
                StereoBuffer source;
                if (stereo)
                {
                    (source, rate2) = ChannelAudio.Decode(input!);
                }
                else
                {
                    short[] mono = input is not null ? Wav.Decode(input).Samples : new short[r.BarCount * r.SamplesPerBar];
                    source = new StereoBuffer(mono, mono);
                }

                int length = source.Left.Length;
                int[] leftCurve = Automation.CurveAt(points, r.FirstBar, r.SamplesPerBar, length, v => stereo ? (int)Math.Min(32768, (long)PanTable.LeftGain(Math.Clamp(v, -PanTable.Range, PanTable.Range)) * 32768 / PanTable.LeftGain(0)) : PanTable.LeftGain(Math.Clamp(v, -PanTable.Range, PanTable.Range)));
                int[] rightCurve = Automation.CurveAt(points, r.FirstBar, r.SamplesPerBar, length, v => stereo ? (int)Math.Min(32768, (long)PanTable.RightGain(Math.Clamp(v, -PanTable.Range, PanTable.Range)) * 32768 / PanTable.RightGain(0)) : PanTable.RightGain(Math.Clamp(v, -PanTable.Range, PanTable.Range)));
                short[] left = new short[length], rightChannel = new short[length];
                for (int i = 0; i < length; i++)
                {
                    left[i] = FixedAudio.Clip(PanTable.Apply(source.Left[i], leftCurve[i]));
                    rightChannel[i] = FixedAudio.Clip(PanTable.Apply(source.Right[i], rightCurve[i]));
                }

                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(new StereoBuffer(left, rightChannel), rate2))]);
            }

            int pan = Math.Clamp(Read(Text("pan"), 0), -PanTable.Range, PanTable.Range);
            StereoBuffer result;
            int rate = r.SampleRate;
            if (stereo)
            {
                (StereoBuffer channels, rate) = ChannelAudio.Decode(input!);
                int leftScale = (int)Math.Min(32768, (long)PanTable.LeftGain(pan) * 32768 / PanTable.LeftGain(0));
                int rightScale = (int)Math.Min(32768, (long)PanTable.RightGain(pan) * 32768 / PanTable.RightGain(0));
                result = new StereoBuffer([.. channels.Left.Select(v => FixedAudio.Clip(PanTable.Apply(v, leftScale)))], [.. channels.Right.Select(v => FixedAudio.Clip(PanTable.Apply(v, rightScale)))]);
            }
            else
            {
                short[] samples = input is not null ? Wav.Decode(input).Samples : new short[r.BarCount * r.SamplesPerBar];
                result = StereoBuffer.Pan(samples, pan);
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", StereoWav.Encode(result, rate))]);
        }
    }

    /// <summary>A noise gate over one or two channels (linked): below the threshold the sound is faded out after the hold time, above it faded in.</summary>
    private static int[][] Gate(Func<string, int, int> p, int rate, params int[][] channels)
    {
        long threshold = (long)FixedAudio.GainFromDb(Math.Clamp(p("threshold", -50), -80, 0)) * 32767 >> 15;
        int attack = Math.Max(1, Math.Clamp(p("attack", 5), 1, 200) * rate / 1000);
        int hold = Math.Clamp(p("hold", 80), 0, 1000) * rate / 1000;
        int release = Math.Max(1, Math.Clamp(p("release", 150), 5, 2000) * rate / 1000);
        int length = channels[0].Length;
        int[][] y = [.. channels.Select(_ => new int[length])];
        long gain = 0;
        long env = 0;
        int open = 0;
        for (int i = 0; i < length; i++)
        {
            long level = 0;
            foreach (int[] channel in channels)
            {
                level = Math.Max(level, Math.Abs((long)channel[i]));
            }

            env = level > env ? level : env - (env / Math.Max(1, release / 4));
            if (env > threshold)
            {
                open = hold;
            }
            else if (open > 0)
            {
                open--;
            }

            long target = open > 0 || env > threshold ? 32768 : 0;
            gain += target > gain ? Math.Max(1, (target - gain) / attack) : (target - gain) / release;
            gain = Math.Clamp(gain, 0, 32768);
            for (int c = 0; c < channels.Length; c++)
            {
                y[c][i] = (int)((long)channels[c][i] * gain >> 15);
            }
        }

        return y;
    }

    /// <summary>A de-esser: the part of the sound above the frequency is compressed on its own (linked for stereo) and added back to the rest.</summary>
    private static int[][] DeEss(Func<string, int, int> p, int rate, params int[][] channels)
    {
        int hz = Math.Clamp(p("frequency", 6000), 2000, Math.Max(2001, rate / 2 - 100));
        long threshold = (long)FixedAudio.GainFromDb(Math.Clamp(p("threshold", -30), -60, 0)) * 32767 >> 15;
        int ratio = Math.Clamp(p("ratio", 4), 1, 20);
        int length = channels[0].Length;
        int[][] low = [.. channels.Select(c => LowPassAt(c, hz, rate))];
        int[][] y = [.. channels.Select(_ => new int[length])];
        long env = 0;
        int attack = Math.Max(1, rate / 2000);
        int release = Math.Max(1, rate / 50);
        for (int i = 0; i < length; i++)
        {
            long level = 0;
            for (int c = 0; c < channels.Length; c++)
            {
                level = Math.Max(level, Math.Abs((long)channels[c][i] - low[c][i]));
            }

            env += (level - env) / (level > env ? attack : release);
            long gain = 32768;
            if (env > threshold && env > 0)
            {
                gain = (threshold + ((env - threshold) / ratio)) * 32768 / env;
            }

            for (int c = 0; c < channels.Length; c++)
            {
                long high = channels[c][i] - low[c][i];
                y[c][i] = (int)(low[c][i] + (high * gain >> 15));
            }
        }

        return y;
    }

    /// <summary>The triangle LFO (0 to 32767) at a sample, period in samples.</summary>
    private static int Triangle(long i, int period)
    {
        long phase = i % period;
        long half = Math.Max(1, period / 2);
        return (int)(phase < half ? phase * 32767 / half : (period - phase) * 32767 / half);
    }

    /// <summary>A modulated delay for chorus (no feedback) and flanger (feedback): the delay moves up and down with an integer triangle LFO, read with linear interpolation.</summary>
    private static int[] ModDelay(int[] x, int rate, int baseMs, int depthMs, int periodMs, int feedback, int mix, int quarter = 0)
    {
        int length = x.Length;
        int period = Math.Max(2, periodMs * rate / 1000);
        long offset = (long)quarter * period / 4;
        long baseSamples = (long)baseMs * rate / 1000;
        long depth = (long)depthMs * rate / 1000;
        int[] w = new int[length];
        int[] y = new int[length];
        for (int i = 0; i < length; i++)
        {
            long modulation = ((Triangle(i + offset, period) - 16384L) * depth * 256) >> 14;
            long delayQ8 = Math.Max(2 * 256, (baseSamples * 256) + modulation);
            long position = ((long)i * 256) - delayQ8;
            long delayed = 0;
            if (position >= 0)
            {
                int index = (int)(position >> 8);
                int frac = (int)(position & 255);
                int[] source = feedback > 0 ? w : x;
                delayed = ((source[index] * (long)(256 - frac)) + (source[Math.Min(index + 1, i)] * (long)frac)) >> 8;
            }

            w[i] = (int)(x[i] + (delayed * feedback / 100));
            y[i] = (int)(((long)x[i] * (100 - mix) / 100) + (delayed * mix / 100));
        }

        return y;
    }

    /// <summary>A phaser: a chain of first-order all-pass filters whose coefficient moves with the triangle LFO, mixed with the dry sound.</summary>
    private static int[] Phase(Func<string, int, int> p, int[] x, int rate)
    {
        int stages = Math.Clamp(p("stages", 4), 2, 8);
        int period = Math.Max(2, Math.Clamp(p("period", 2500), 100, 10000) * rate / 1000);
        int mix = Math.Clamp(p("mix", 50), 0, 100);
        long[] x1 = new long[stages];
        long[] y1 = new long[stages];
        int[] y = new int[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            long a = 3277 + (Triangle(i, period) * 26000L >> 15);
            long v = x[i];
            for (int s = 0; s < stages; s++)
            {
                long o = (-a * v >> 15) + x1[s] + (a * y1[s] >> 15);
                x1[s] = v;
                y1[s] = o;
                v = o;
            }

            y[i] = (int)(((long)x[i] * (100 - mix) / 100) + (v * mix / 100));
        }

        return y;
    }

    /// <summary>A bit crusher: fewer bits and a held sample for several samples (sample-rate reduction).</summary>
    private static int[] Crush(Func<string, int, int> p, int[] x, int rate)
    {
        int bits = Math.Clamp(p("bits", 8), 2, 16);
        int hold = Math.Clamp(p("downsample", 1), 1, 64);
        int mix = Math.Clamp(p("mix", 100), 0, 100);
        int shift = 16 - bits;
        int[] y = new int[x.Length];
        int held = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (i % hold == 0)
            {
                held = (x[i] >> shift) << shift;
            }

            y[i] = (int)(((long)x[i] * (100 - mix) / 100) + ((long)held * mix / 100));
        }

        return y;
    }

    /// <summary>The filter with a cutoff (Hz) per sample: two cascaded one-pole low-pass stages, or the input minus them for a high-pass.</summary>
    /// <summary>Cycles of the filter oscillator in one bar (4/4): 1/4 is four a bar, 1/8 eight, a t after it is a triplet; 0 is off.</summary>
    private static double FilterLfoCycles(string rate) => rate switch
    {
        "1/1" => 1,
        "1/2" => 2,
        "1/4" => 4,
        "1/8" => 8,
        "1/16" => 16,
        "1/32" => 32,
        "1/4t" => 6,
        "1/8t" => 12,
        "1/16t" => 24,
        _ => 0,
    };

    private static int[] FilterSweep(int[] x, int[] cutoffs, int rate, bool highpass)
    {
        int[] y = new int[x.Length];
        long first = 0, second = 0, inFirst = 0, inSecond = 0, a = 0;
        int last = -1;
        for (int i = 0; i < x.Length; i++)
        {
            if (cutoffs[i] != last)
            {
                last = cutoffs[i];
                a = CutoffCoefficient(highpass ? HighPairCutoff(last) : PairCutoff(last, rate), rate);
            }

            if (highpass)
            {
                long stage = HighPassStep(x[i], a, ref inFirst, ref first);
                y[i] = (int)HighPassStep(stage, a, ref inSecond, ref second);
            }
            else
            {
                first += a * (x[i] - first) >> 15;
                second += a * (first - second) >> 15;
                y[i] = (int)second;
            }
        }

        return y;
    }

    private static int[] Filter(Func<string, int, int> p, int[] x, int rate)
    {
        int hz = Math.Clamp(p("cutoff", 1000), 20, 20000);
        if (p("type", 0) != 1)
        {
            return LowPass2At(x, hz, rate);
        }

        long a = CutoffCoefficient(HighPairCutoff(hz), rate);
        int[] y = new int[x.Length];
        long inFirst = 0, first = 0, inSecond = 0, second = 0;
        for (int i = 0; i < x.Length; i++)
        {
            long stage = HighPassStep(x[i], a, ref inFirst, ref first);
            y[i] = (int)HighPassStep(stage, a, ref inSecond, ref second);
        }

        return y;
    }

    /// <summary>
    /// One step of a one-pole high pass, y = (1 + b) / 2 * (x - x1) + b * y1 with b = 1 - a (Q15): exactly 1 at the Nyquist, so the highs are not dulled
    /// (the input minus a low pass lost 1.5 dB near the top), and -3 dB at the frequency of <paramref name="a"/>.
    /// </summary>
    private static long HighPassStep(long x, long a, ref long previousIn, ref long previousOut)
    {
        long b = 32768 - a, g = (32768 + b) >> 1;
        long y = ((g * (x - previousIn)) + (b * previousOut)) >> 15;
        previousIn = x;
        previousOut = y;
        return y;
    }

    /// <summary>The cutoff of each of two one-pole high passes in a row, so the pair is about -3 dB at <paramref name="hz"/> (each stage sits lower, at 0.644 times).</summary>
    private static int HighPairCutoff(int hz) => (int)Math.Max(1, (long)hz * 644 / 1000);

    /// <summary>The chorus over one channel; the right channel (quarter 1) sweeps a quarter of a period ahead of the left, so a stereo sound gets width.</summary>
    private static int[] Chorus(Func<string, int, int> p, int[] x, int rate, int quarter) => ModDelay(x, rate, 20, Math.Clamp(p("depth", 5), 1, 15), Math.Clamp(p("period", 1500), 100, 10000), 0, Math.Clamp(p("mix", 50), 0, 100), quarter);
}
