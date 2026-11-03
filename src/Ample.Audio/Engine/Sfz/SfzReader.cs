using System.Globalization;
using System.Security.Cryptography;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine.Sfz;

/// <summary>
/// Builds an <see cref="Engine.Instrument"/> straight from an <c>.sfz</c> file's own opcodes, instead of AMPLE's
/// <c>name_v&lt;L&gt;_r&lt;R&gt;.wav</c> filename convention (<see cref="Engine.Instrument.FromFiles"/>). Phase 1 scope
/// (see <c>plan/sfz-import/README.md</c>, SFZ02): pitched regions only (<c>lokey</c>/<c>hikey</c>/<c>key</c> and
/// <c>pitch_keycenter</c>), played the same way a <c>set</c> folder of note-named WAVs is today, through the existing
/// <see cref="RegionResolver.Pick"/> — this type only ever produces another <c>Instrument</c>, never a second
/// playback path. Drum-kit (unpitched, voice-named) regions are not read yet; see <c>docs/samples.md</c> "SFZ sets".
/// </summary>
public static class SfzReader
{
    /// <summary>The result of reading an <c>.sfz</c> file: the instrument, the files that must be hashed into the take's plan, any warnings, and
    /// (SFZ03, drum kits only) the choke rules derived from <c>group</c>/<c>off_by</c>, in the same <c>voice:closer1,closer2</c> text form a
    /// <c>drums</c> block's own <c>choke</c> parameter uses, so both feed the one existing choke-and-fade mixing pass. <c>ChokeFadeMs</c> (SFZ
    /// <c>off_time</c>) overrides that pass's fixed ~7 ms fade for the whole kit when any closer region specifies one (the longest value found,
    /// since the mixing pass has one fade for the whole block, not per rule); null keeps the existing fixed fade.</summary>
    public sealed record Result(Instrument Instrument, IReadOnlyList<(string RelativePath, string Hash)> UsedFiles, IReadOnlyList<string> Warnings, IReadOnlyList<string> ChokeRules, int? ChokeFadeMs = null);

    // Opcodes SFZ02 gives meaning to. Anything else found on a region is kept (never dropped) and reported as a
    // warning instead, per plan/sfz-import/README.md SFZ05's "unknown opcode" principle, brought forward to SFZ02
    // so the very first cut is already honest about what it does not read.
    private static readonly HashSet<string> KnownOpcodes = new(StringComparer.Ordinal)
    {
        "sample", "default_path", "lokey", "hikey", "key", "pitch_keycenter", "pitch_keytrack",
        "lovel", "hivel", "tune", "transpose", "volume", "gain", "seq_position", "seq_length", "group", "off_by", "off_time", "off_mode",
        "cutoff", "fil_type", "resonance", "fil_veltrack",
        "loop_mode", "loop_start", "loop_end", "loopstart", "loopend", "loop_crossfade",
        "ampeg_attack", "ampeg_decay", "ampeg_sustain", "ampeg_release", "ampeg_hold",
        "lfo01_freq", "lfo01_pitch", "lfo01_delay", "lfo01_wave",
        "fileg_attack", "fileg_decay", "fileg_sustain", "fileg_release", "fileg_hold", "fileg_depth",
        "trigger",
        "xfin_lovel", "xfin_hivel", "xfout_lovel", "xfout_hivel",
        "polyphony", "note_polyphony",
    };

    // SFZ08's two reused one-pole stages, see docs/samples.md "SFZ sets".
    private static readonly HashSet<string> SupportedFilterTypes = new(StringComparer.OrdinalIgnoreCase) { "lpf_1p", "lpf_2p" };

    /// <summary>
    /// Reads <paramref name="sfzPath"/> (an absolute path). <c>#include</c> and <c>sample=</c> paths may not escape the
    /// file's own folder tree. <paramref name="asDrumKit"/> (SFZ05) is true for a <c>drums</c> block's <c>kit</c>: each
    /// region's key is mapped to a voice name through <see cref="GmDrumMap"/> instead of becoming a pitched region, and
    /// the whole file is a General-MIDI-keyed drum rack, not a melodic instrument. Default (false) keeps SFZ02's
    /// original pitched-region behaviour, for a guitar/bass/piano <c>set</c>. <paramref name="jailRoot"/> is the
    /// furthest-out folder a <c>#include</c>/<c>sample=</c> path may resolve into (default: the <c>.sfz</c> file's own
    /// folder); pass a pack or project root to allow a real-world pack layout where samples live in a sibling folder
    /// of the <c>.sfz</c> file (confirmed against the real Karoryfer "Frankensnare" pack, SFZ06: its programs live in
    /// <c>Programs/</c> and reach samples via <c>../Samples/...</c>) without opening the whole filesystem.
    /// </summary>
    /// <exception cref="AmpleException">AMP5395 when the file or one of its includes/samples cannot be read or used.</exception>
    public static Result Read(string sfzPath, int sampleRate, int channel = 0, bool asDrumKit = false, string? jailRoot = null)
    {
        ArgumentNullException.ThrowIfNull(sfzPath);
        string fullSfzPath = Path.GetFullPath(sfzPath);
        if (!File.Exists(fullSfzPath))
        {
            throw new AmpleException("AMP5395", $"The SFZ file '{sfzPath}' does not exist.");
        }

        string rootDir = Path.GetDirectoryName(fullSfzPath) ?? throw new AmpleException("AMP5395", $"The SFZ file '{sfzPath}' has no folder.");
        string boundary = jailRoot is null ? rootDir : Path.GetFullPath(jailRoot);
        Dictionary<string, string> usedFiles = []; // relative path -> hash, de-duplicated
        AddUsed(usedFiles, rootDir, fullSfzPath);

        IReadOnlyList<SfzDocument.SfzRegion> regions;
        try
        {
            regions = SfzDocument.Parse(File.ReadAllText(fullSfzPath), path => AsText(ReadUnder(rootDir, boundary, path, usedFiles)));
        }
        catch (IOException ex)
        {
            throw new AmpleException("AMP5395", $"The SFZ file '{sfzPath}' cannot be read: {ex.Message}");
        }

        List<string> warnings = [];
        HashSet<string> warnedOpcodes = [];
        List<Region> built = [];
        // SFZ03: group -> the voice names that belong to it; offBy -> the voice names whose hit should choke that group.
        Dictionary<int, HashSet<string>> groupVoices = [];
        Dictionary<int, HashSet<string>> offByClosers = [];
        int? maxOffTimeMs = null;
        foreach (SfzDocument.SfzRegion region in regions)
        {
            foreach (string opcode in region.Opcodes.Keys)
            {
                if (!KnownOpcodes.Contains(opcode) && VelCurveIndex(opcode) is null && warnedOpcodes.Add(opcode))
                {
                    warnings.Add($"AMP5394: the opcode '{opcode}' is not read yet; its value is kept in the SFZ file but has no effect.");
                }
            }

            if (!region.Opcodes.TryGetValue("sample", out string? sampleValue) || sampleValue.Length == 0)
            {
                warnings.Add("AMP5394: a <region> with no 'sample' opcode was skipped.");
                continue;
            }

            string samplePath = region.Opcodes.TryGetValue("default_path", out string? defaultPath) && defaultPath.Length > 0
                ? defaultPath.TrimEnd('/', '\\') + "/" + sampleValue
                : sampleValue;
            byte[] sampleBytes = ReadUnder(rootDir, boundary, samplePath, usedFiles);
            short[] samples;
            try
            {
                samples = SampleSet.ReadWav(sampleBytes, sampleRate, channel);
            }
            catch (InvalidDataException ex)
            {
                throw new AmpleException("AMP5395", $"The sample '{samplePath}' of '{sfzPath}' cannot be used: {ex.Message}");
            }

            string sampleName = Path.GetFileNameWithoutExtension(samplePath).ToLowerInvariant();
            string sampleHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(sampleBytes));
            SampleFile sampleFile = new(sampleName, sampleHash, samples);

            int? key = Int(region, "key");
            int lokey = Int(region, "lokey") ?? key ?? -1;
            int hikey = Int(region, "hikey") ?? key ?? lokey;
            int root = Int(region, "pitch_keycenter") ?? key ?? lokey;
            string regionName;
            if (asDrumKit)
            {
                int? midiKey = Int(region, "key") ?? Int(region, "pitch_keycenter") ?? Int(region, "lokey");
                if (midiKey is null)
                {
                    warnings.Add($"AMP5394: a <region> for sample '{sampleName}' has no key/pitch_keycenter/lokey and was skipped (a drum-kit SFZ region needs one to pick a voice name).");
                    continue;
                }

                regionName = GmDrumMap.NameOf(midiKey.Value) ?? $"key{midiKey.Value}";
                if (GmDrumMap.NameOf(midiKey.Value) is null)
                {
                    warnings.Add($"AMP5394: key {midiKey.Value} has no standard General MIDI drum-map voice name; using '{regionName}' (sample '{sampleName}').");
                }

                root = -1; // unpitched: PickVoice matches by name, not by key range
                lokey = midiKey.Value;
                hikey = midiKey.Value;
            }
            else
            {
                if (lokey < 0 || root < 0)
                {
                    warnings.Add($"AMP5394: a <region> for sample '{sampleName}' has no key/lokey/pitch_keycenter and was skipped.");
                    continue;
                }

                regionName = sampleName;
            }

            int lovel = Int(region, "lovel") ?? 0;
            int hivel = Int(region, "hivel") ?? 127;
            int velLo = Math.Clamp(lovel, 0, 127) * 32768 / 127;
            int velHi = Math.Clamp(hivel, 0, 127) * 32768 / 127;

            if (!asDrumKit && region.Opcodes.ContainsKey("pitch_keytrack") && Int(region, "pitch_keytrack") != 100)
            {
                // Only meaningful for a pitched region: a drum-kit region is already unpitched (Root -1, no shifting at all),
                // so pitch_keytrack=0 (confirmed in real packs, e.g. Karoryfer's Frankensnare) is already honoured as-is.
                warnings.Add($"AMP5394: 'pitch_keytrack' other than 100 is not read yet (sample '{sampleName}'); the region keeps nearest-note pitch-shifting.");
            }

            int tuneCents = Int(region, "tune") ?? 0;
            int transposeSemitones = Int(region, "transpose") ?? 0;
            int tune = Math.Clamp((int)Math.Round(tuneCents / 100.0, MidpointRounding.AwayFromZero) + transposeSemitones, -24, 24);

            int volumeDb = Int(region, "volume") ?? Int(region, "gain") ?? 0;
            int gainQ15 = FixedAudio.GainFromDbWide(Math.Clamp(volumeDb, -60, FixedAudio.WideMaxDb));

            int sequence = Int(region, "seq_position") ?? 1;
            int group = Int(region, "group") ?? 0;
            int[]? velCurve = BuildVelCurve(region);
            (int cutoffHz, int poles, int velTrackCents, int resonanceDb) = ReadFilter(region, sampleName, warnings);
            (string loopMode, int loopStart, int loopEnd, int loopCrossfade) = ReadLoop(region, sampleBytes, sampleRate, samples.Length);
            IntegerEnvelope? envelope = ReadEnvelope(region, sampleName, sampleRate, warnings);
            (int lfoFreqMilliHz, int lfoDepthCents, int lfoDelaySamples) = ReadLfo(region, sampleName, sampleRate, warnings);
            (IntegerEnvelope? filterEnvelope, int filterEnvelopeDepthCents) = ReadFilterEnvelope(region, sampleRate);
            // S08/SFZ12: trigger=release makes this a release-triggered companion, played at note-off instead of
            // note-on (RegionResolver.PickRelease, never Pick/PickVoice). first/legato are SFZ12 territory, not read yet.
            string triggerValue = region.Opcodes.GetValueOrDefault("trigger", "attack");
            bool isRelease = triggerValue == "release";
            // SFZ12: "first"/"legato" are honoured for a pitched piano ("Keys") set only - the only renderer whose notes carry a real
            // held duration (not a synthesized decay tail); passed through as-is, TriggerMatches in Instrument.cs does the filtering.
            string regionTrigger = triggerValue is "first" or "legato" ? triggerValue : "attack";
            // SFZ12: a one-sided gain taper (see XfadeGainQ15 in SamplerRenderers.cs for the real-vs-spec note).
            int Vel127(string opcode) => Int(region, opcode) is { } v ? Math.Clamp(v, 0, 127) * 32768 / 127 : -1;
            int xfinLoVel = Vel127("xfin_lovel");
            int xfinHiVel = Vel127("xfin_hivel");
            int xfoutLoVel = Vel127("xfout_lovel");
            int xfoutHiVel = Vel127("xfout_hivel");
            // SFZ12: polyphony/note_polyphony (first wins if both are set), 0 = unlimited.
            int polyphony = Math.Max(0, Int(region, "polyphony") ?? Int(region, "note_polyphony") ?? 0);

            int offBy = 0;
            if (asDrumKit)
            {
                if (group != 0)
                {
                    (groupVoices.TryGetValue(group, out HashSet<string>? gv) ? gv : groupVoices[group] = []).Add(regionName);
                }

                if (Int(region, "off_by") is { } parsedOffBy and not 0)
                {
                    offBy = parsedOffBy;
                    (offByClosers.TryGetValue(offBy, out HashSet<string>? oc) ? oc : offByClosers[offBy] = []).Add(regionName);
                    // off_mode=normal asks for a natural decay instead of a fast cut, which the existing fade-only choke
                    // mixing pass cannot model - kept-and-warned rather than silently always using the fast cut.
                    if (region.Opcodes.GetValueOrDefault("off_mode", "fast") == "normal")
                    {
                        warnings.Add($"AMP5394: 'off_mode=normal' is not read yet (sample '{sampleName}'); the choke still cuts with a short fade.");
                    }

                    if (region.Opcodes.TryGetValue("off_time", out string? otv) && double.TryParse(otv, NumberStyles.Float, CultureInfo.InvariantCulture, out double ot) && ot > 0)
                    {
                        int ms = (int)Math.Round(ot * 1000, MidpointRounding.AwayFromZero);
                        maxOffTimeMs = Math.Max(maxOffTimeMs ?? 0, ms);
                    }
                }
            }

            built.Add(new Region(regionName, root, lokey, hikey, velLo, velHi, tune, gainQ15, loopMode, loopStart, loopEnd, group, Math.Max(1, sequence), sampleFile, velCurve, cutoffHz, poles, loopCrossfade, envelope, velTrackCents, lfoFreqMilliHz, lfoDepthCents, lfoDelaySamples, filterEnvelope, filterEnvelopeDepthCents, isRelease, xfinLoVel, xfinHiVel, xfoutLoVel, xfoutHiVel, regionTrigger, resonanceDb, polyphony, offBy));
        }

        if (built.Count == 0)
        {
            throw new AmpleException("AMP5395", asDrumKit
                ? $"The SFZ file '{sfzPath}' defines no usable region (every region needs a key to map to a voice name)."
                : $"The SFZ file '{sfzPath}' defines no usable (pitched) region.");
        }

        List<string> chokeRules = [];
        foreach ((int group, HashSet<string> closers) in offByClosers)
        {
            if (groupVoices.TryGetValue(group, out HashSet<string>? closed))
            {
                foreach (string voice in closed.Except(closers, StringComparer.Ordinal))
                {
                    chokeRules.Add($"{voice}:{string.Join(',', closers.OrderBy(c => c, StringComparer.Ordinal))}");
                }
            }
        }

        return new Result(new Instrument(built), [.. usedFiles.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Item1, StringComparer.Ordinal)], warnings, chokeRules, maxOffTimeMs);
    }

    private static int? Int(SfzDocument.SfzRegion region, string opcode) =>
        region.Opcodes.TryGetValue(opcode, out string? v) && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>
    /// SFZ08: a static cutoff (no <c>fileg_*</c> envelope - see <see cref="ReadFilterEnvelope"/>). <c>fil_type</c> defaults to
    /// <c>lpf_2p</c> (the SFZ default) when <c>cutoff</c> is given without it; a type outside <c>lpf_1p</c>/<c>lpf_2p</c> is
    /// warned and the cutoff is not applied (reporting it as unsupported is more honest than silently low-passing a patch
    /// that asked for a high-pass or band-pass). <c>resonance</c> (dB) is read as an approximation, not a true resonant
    /// pole - see <c>SamplerRenderers.ApplyFilter</c>'s own doc comment.
    /// </summary>
    private static (int CutoffHz, int Poles, int VelTrackCents, int ResonanceDb) ReadFilter(SfzDocument.SfzRegion region, string sampleName, List<string> warnings)
    {
        int resonanceDb = region.Opcodes.TryGetValue("resonance", out string? rv) && double.TryParse(rv, NumberStyles.Float, CultureInfo.InvariantCulture, out double rd) ? Math.Clamp((int)Math.Round(rd, MidpointRounding.AwayFromZero), 0, 24) : 0;

        if (!region.Opcodes.TryGetValue("cutoff", out string? cutoffValue) || !double.TryParse(cutoffValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double cutoffHz) || cutoffHz <= 0)
        {
            return (0, 1, 0, 0);
        }

        string filType = region.Opcodes.GetValueOrDefault("fil_type", "lpf_2p");
        if (!SupportedFilterTypes.Contains(filType))
        {
            warnings.Add($"AMP5394: fil_type '{filType}' is not read yet (sample '{sampleName}'; only lpf_1p and lpf_2p are supported today); no filter was applied.");
            return (0, 1, 0, 0);
        }

        int velTrack = Math.Clamp(Int(region, "fil_veltrack") ?? 0, -9600, 9600);
        return ((int)Math.Round(cutoffHz, MidpointRounding.AwayFromZero), string.Equals(filType, "lpf_2p", StringComparison.OrdinalIgnoreCase) ? 2 : 1, velTrack, resonanceDb);
    }

    /// <summary>
    /// SFZ04: <c>loop_mode</c>/<c>loop_start</c>/<c>loop_end</c> (or the alias-without-underscore spelling some dialects
    /// use), now that <c>S08</c> plays a <c>Region</c> with <c>LoopMode == "sustain"</c>. AMPLE's loop model does not
    /// distinguish <c>loop_sustain</c> (loops only while the key is held) from <c>loop_continuous</c> (loops regardless)
    /// — both map to the same <c>"sustain"</c>, a documented simplification (see docs/samples.md). Loop points are given
    /// in the sample's own native frames; they are rescaled here to the build rate <see cref="SampleSet.ReadWav"/>
    /// already resampled the audio to, so a point from a 48 kHz file is still correct once AMPLE builds at 44.1 kHz.
    /// </summary>
    private static (string LoopMode, int LoopStart, int LoopEnd, int LoopCrossfade) ReadLoop(SfzDocument.SfzRegion region, byte[] sampleBytes, int targetRate, int decodedLength)
    {
        string loopMode = region.Opcodes.GetValueOrDefault("loop_mode", "no_loop");
        if (loopMode is not ("loop_continuous" or "loop_sustain"))
        {
            return ("none", 0, 0, 0);
        }

        int nativeRate = SampleSet.NativeRateOf(sampleBytes);
        double ratio = nativeRate > 0 ? (double)targetRate / nativeRate : 1.0;
        int Rescale(int nativeFrame) => Math.Clamp((int)Math.Round(nativeFrame * ratio, MidpointRounding.AwayFromZero), 0, decodedLength);

        int start = Rescale(Int(region, "loop_start") ?? Int(region, "loopstart") ?? 0);
        int? nativeEnd = Int(region, "loop_end") ?? Int(region, "loopend");
        int end = nativeEnd is { } frame ? Rescale(frame) : decodedLength;
        double crossfadeSeconds = region.Opcodes.TryGetValue("loop_crossfade", out string? cf) && double.TryParse(cf, NumberStyles.Float, CultureInfo.InvariantCulture, out double cfs) ? cfs : 0.0;
        int crossfade = Math.Max(0, (int)Math.Round(crossfadeSeconds * targetRate, MidpointRounding.AwayFromZero));
        return ("sustain", start, end, crossfade);
    }

    /// <summary>
    /// SFZ07: <c>ampeg_attack</c>/<c>decay</c>/<c>sustain</c>/<c>release</c>/<c>hold</c> (seconds/percent) become the
    /// same integer <see cref="Engine.IntegerEnvelope"/> <c>S08</c> wired into the renderer, so an SFZ-authored
    /// envelope plays exactly like a <c>set.json</c> <c>"envelope"</c> one does. Returns null (today's fixed fade,
    /// unchanged) when the region has none of these opcodes.
    /// </summary>
    private static IntegerEnvelope? ReadEnvelope(SfzDocument.SfzRegion region, string sampleName, int sampleRate, List<string> warnings)
    {
        bool any = region.Opcodes.ContainsKey("ampeg_attack") || region.Opcodes.ContainsKey("ampeg_decay") ||
                   region.Opcodes.ContainsKey("ampeg_sustain") || region.Opcodes.ContainsKey("ampeg_release") ||
                   region.Opcodes.ContainsKey("ampeg_hold");
        if (!any)
        {
            return null;
        }

        int Ticks(string opcode) => region.Opcodes.TryGetValue(opcode, out string? v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? Math.Max(0, (int)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero))
            : 0;

        double sustainPercent = region.Opcodes.TryGetValue("ampeg_sustain", out string? sv) && double.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out double sp) ? Math.Clamp(sp, 0, 100) : 100;
        return new IntegerEnvelope(Ticks("ampeg_attack"), Ticks("ampeg_decay"), (int)Math.Round(sustainPercent * IntegerEnvelope.One / 100.0), Ticks("ampeg_release"), Ticks("ampeg_hold"));
    }

    /// <summary>
    /// SFZ09: <c>lfo01_freq</c> (Hz)/<c>lfo01_pitch</c> (cents depth)/<c>lfo01_delay</c> (seconds) become a pitch LFO on the
    /// region (<c>PlayExpressiveNote</c> is the only path that can play a time-varying pitch, so a region with an LFO
    /// forces its whole block through that path — see <see cref="Instrument.NeedsPerFrameModulation"/>). Only LFO 1 (pitch) is read;
    /// further-numbered LFOs and pan/volume depth are not. <c>lfo01_wave</c> other than triangle (SFZ's own default) is
    /// kept-and-warned and still renders as a triangle rather than silently using the wrong shape.
    /// </summary>
    private static (int FreqMilliHz, int DepthCents, int DelaySamples) ReadLfo(SfzDocument.SfzRegion region, string sampleName, int sampleRate, List<string> warnings)
    {
        bool any = region.Opcodes.ContainsKey("lfo01_freq") || region.Opcodes.ContainsKey("lfo01_pitch");
        if (!any)
        {
            return (0, 0, 0);
        }

        if (region.Opcodes.TryGetValue("lfo01_wave", out string? wave) && wave != "0")
        {
            warnings.Add($"AMP5394: 'lfo01_wave' other than triangle is not read yet (sample '{sampleName}'); the LFO still renders as a triangle wave.");
        }

        double freqHz = region.Opcodes.TryGetValue("lfo01_freq", out string? fv) && double.TryParse(fv, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) ? Math.Max(0, f) : 0;
        int depthCents = region.Opcodes.TryGetValue("lfo01_pitch", out string? dv) && double.TryParse(dv, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)Math.Round(d, MidpointRounding.AwayFromZero) : 0;
        double delaySeconds = region.Opcodes.TryGetValue("lfo01_delay", out string? lv) && double.TryParse(lv, NumberStyles.Float, CultureInfo.InvariantCulture, out double l) ? Math.Max(0, l) : 0;
        if (freqHz <= 0 || depthCents == 0)
        {
            return (0, 0, 0);
        }

        return ((int)Math.Round(freqHz * 1000, MidpointRounding.AwayFromZero), depthCents, (int)Math.Round(delaySeconds * sampleRate, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// SFZ08: <c>fileg_attack</c>/<c>decay</c>/<c>sustain</c>/<c>release</c>/<c>hold</c> build the same <see cref="Engine.IntegerEnvelope"/>
    /// shape <c>ampeg_*</c> does, applied to the cutoff instead of the amplitude: <c>fileg_depth</c> (cents, may be negative to close the
    /// filter instead of opening it) is how far the envelope moves the cutoff at its peak. Returns a null envelope (today's static-or-veltracked
    /// cutoff, unchanged) when the region has none of the <c>fileg_*</c> opcodes or <c>fileg_depth</c> is 0 (an envelope with no depth does
    /// nothing, same principle as <c>fil_veltrack</c> doing nothing at 0).
    /// </summary>
    private static (IntegerEnvelope? Envelope, int DepthCents) ReadFilterEnvelope(SfzDocument.SfzRegion region, int sampleRate)
    {
        bool any = region.Opcodes.ContainsKey("fileg_attack") || region.Opcodes.ContainsKey("fileg_decay") ||
                   region.Opcodes.ContainsKey("fileg_sustain") || region.Opcodes.ContainsKey("fileg_release") ||
                   region.Opcodes.ContainsKey("fileg_hold");
        int depthCents = region.Opcodes.TryGetValue("fileg_depth", out string? dv) && double.TryParse(dv, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)Math.Round(d, MidpointRounding.AwayFromZero) : 0;
        if (!any || depthCents == 0)
        {
            return (null, 0);
        }

        int Ticks(string opcode) => region.Opcodes.TryGetValue(opcode, out string? v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? Math.Max(0, (int)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero))
            : 0;

        double sustainPercent = region.Opcodes.TryGetValue("fileg_sustain", out string? sv) && double.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out double sp) ? Math.Clamp(sp, 0, 100) : 100;
        return (new IntegerEnvelope(Ticks("fileg_attack"), Ticks("fileg_decay"), (int)Math.Round(sustainPercent * IntegerEnvelope.One / 100.0), Ticks("fileg_release"), Ticks("fileg_hold")), depthCents);
    }

    private const string VelCurvePrefix = "amp_velcurve_";

    /// <summary>The MIDI velocity index (0-127) of an <c>amp_velcurve_NNN</c> opcode name, or null when it is not one (including a malformed one, which should still warn as unknown).</summary>
    private static int? VelCurveIndex(string opcode) =>
        opcode.StartsWith(VelCurvePrefix, StringComparison.Ordinal) &&
        int.TryParse(opcode.AsSpan(VelCurvePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= 127
            ? n : null;

    /// <summary>
    /// SFZ10a: a static, per-region velocity-to-amplitude curve (<c>&lt;curve&gt;</c>-block <c>vNNN</c> points are Phase 2's other
    /// use of the same idea and are not read here, only the inline <c>amp_velcurve_NNN</c> region opcode is). Points not given are
    /// linearly interpolated between the nearest ones that are; velocities below the lowest or above the highest given point hold
    /// that point's level. Returns null (the existing, unchanged linear response) when the region has no such opcode at all.
    /// </summary>
    private static int[]? BuildVelCurve(SfzDocument.SfzRegion region)
    {
        SortedDictionary<int, double> points = [];
        foreach ((string opcode, string value) in region.Opcodes)
        {
            if (VelCurveIndex(opcode) is { } index && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double level))
            {
                points[index] = Math.Clamp(level, 0.0, 1.0);
            }
        }

        if (points.Count == 0)
        {
            return null;
        }

        int[] table = new int[128];
        (int Index, double Level)[] known = [.. points.Select(p => (p.Key, p.Value))];
        for (int v = 0; v < 128; v++)
        {
            (int Index, double Level) before = known.Where(p => p.Index <= v).DefaultIfEmpty(known[0]).Last();
            (int Index, double Level) after = known.Where(p => p.Index >= v).DefaultIfEmpty(known[^1]).First();
            double level = before.Index == after.Index ? before.Level : before.Level + ((after.Level - before.Level) * (v - before.Index) / (after.Index - before.Index));
            table[v] = (int)Math.Round(level * 32768.0, MidpointRounding.AwayFromZero);
        }

        return table;
    }

    /// <summary>Resolves <paramref name="relativeToRoot"/> under <paramref name="rootDir"/>, refusing any path that escapes it (no <c>..</c> trick, no rooted path), reads it and records its hash for the plan.</summary>
    /// <summary>Resolves <paramref name="relativeToRoot"/> against <paramref name="rootDir"/> (the including file's own folder, or the root <c>.sfz</c>'s
    /// folder for a <c>sample=</c> path — see <see cref="Read"/>), refusing a result outside <paramref name="boundary"/> (no escaping via <c>..</c> or a
    /// rooted path), reads it and records its hash for the plan.</summary>
    private static byte[] ReadUnder(string rootDir, string boundary, string relativeToRoot, Dictionary<string, string> usedFiles)
    {
        string normalized = relativeToRoot.Replace('\\', '/');
        string full = Path.GetFullPath(Path.Combine(rootDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string boundaryWithSep = boundary.EndsWith(Path.DirectorySeparatorChar) ? boundary : boundary + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundaryWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new AmpleException("AMP5395", $"'{relativeToRoot}' is outside the allowed folder; this is not allowed.");
        }

        if (!File.Exists(full))
        {
            throw new AmpleException("AMP5395", $"'{relativeToRoot}' (referenced from the SFZ file) does not exist.");
        }

        byte[] bytes = File.ReadAllBytes(full);
        AddUsed(usedFiles, rootDir, full, bytes);
        return bytes;
    }

    private static string AsText(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    private static void AddUsed(Dictionary<string, string> usedFiles, string rootDir, string fullPath, byte[]? bytes = null)
    {
        string rel = Path.GetRelativePath(rootDir, fullPath).Replace('\\', '/');
        bytes ??= File.ReadAllBytes(fullPath);
        usedFiles[rel] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
