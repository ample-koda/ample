using Ample.Audio.Engine;

namespace Ample.Audio.Instruments;

/// <summary>A resolved SF2 zone after preset and instrument global zones have been combined.</summary>
/// <remarks><paramref name="Generators"/> holds the value of every generator (instrument, with the preset added) and <paramref name="Modulators"/> the modulators of the zone; <see cref="Sf2Program.Note"/> turns them into the settings of one note.</remarks>
internal sealed record Sf2PlayableRegion(int Sample, int KeyLo, int KeyHi, int VelocityLo, int VelocityHi, int Root,
    int TuneCents, int GainQ15, int Start, int End, int LoopStart, int LoopEnd, bool Loop, bool InvalidLoop, int Attack, int Decay, int SustainQ15, int Release, int FilterCents = 13500, int PanPermille = 0,
    int[]? Generators = null, Sf2Mod[]? Modulators = null);

/// <summary>The settings of one note of a zone: its generators with the modulators of its key and velocity added.</summary>
/// <param name="AttenuationCb">Attenuation in centibels (the zone's attenuation and velocity together).</param>
/// <param name="TuneCents">Fine and coarse tuning in cents.</param>
/// <param name="FilterCents">The cutoff of the low pass in absolute cents (13500 and above is open).</param>
/// <param name="ModEnvToFilterCents">How far the modulation envelope moves the cutoff at its peak, in cents.</param>
/// <param name="ModEnv">The modulation envelope (delay, attack, hold, decay, release in timecents; sustain in 0.1 % below the peak).</param>
/// <param name="VolAttackTc">Attack of the volume envelope (timecents).</param>
/// <param name="VolDecayTc">Decay of the volume envelope (timecents).</param>
/// <param name="VolSustainCb">Sustain of the volume envelope (centibels below the peak).</param>
/// <param name="VolReleaseTc">Release of the volume envelope (timecents).</param>
/// <param name="PanPermille">Pan, -500 (left) to 500 (right).</param>
/// <param name="FilterQCb">The resonance of the low pass: the height of its peak above the level below the cutoff, in centibels (0 is no peak).</param>
/// <param name="VolDelayTc">Delay of the volume envelope (timecents).</param>
/// <param name="VolHoldTc">Hold of the volume envelope (timecents, with its key scaling).</param>
/// <param name="ScaleTuning">Cents per key away from the root (generator 56): 100 is normal, 0 plays every key at the pitch of the root (a woodblock, a sound effect).</param>
/// <param name="Lfos">The modulation LFO (delay, frequency; to pitch in cents, to filter in cents, to volume in centibels) and the vibrato LFO (delay, frequency; to pitch).</param>
/// <param name="ModEnvToPitchCents">How far the modulation envelope moves the pitch at its peak, in cents (generator 7).</param>
/// <param name="ReverbPermille">The reverb send (generator 16, 0 to 1000 = 0 to 100 %).</param>
/// <param name="ChorusPermille">The chorus send (generator 15, 0 to 1000 = 0 to 100 %).</param>
internal readonly record struct Sf2NoteSettings(int AttenuationCb, int TuneCents, int FilterCents, int ModEnvToFilterCents, (int Delay, int Attack, int Hold, int Decay, int Sustain, int Release) ModEnv,
    int VolAttackTc, int VolDecayTc, int VolSustainCb, int VolReleaseTc, int PanPermille, int FilterQCb = 0, int VolDelayTc = -12000, int VolHoldTc = -12000, int ScaleTuning = 100,
    (int ModDelay, int ModFreq, int ModToPitch, int ModToFilter, int ModToVolume, int VibDelay, int VibFreq, int VibToPitch) Lfos = default, int ModEnvToPitchCents = 0,
    int ReverbPermille = 0, int ChorusPermille = 0);

/// <summary>Resolves standard SF2 preset/instrument zones with bounded integer generator arithmetic.</summary>
internal static class Sf2Program
{
    private static readonly HashSet<ushort> SupportedOperators = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 16, 21, 22, 23, 24, 12, 17, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 43, 44, 45, 46, 47, 48, 50, 51, 52, 53, 54, 56, 57, 58];

    // generators a preset may not change (SF2 8.5): sample offsets, the key and velocity that replace the note's own, sample modes, exclusive class, root key, and the indexes
    private static readonly HashSet<ushort> InstrumentOnly = [0, 1, 2, 3, 4, 12, 45, 46, 47, 50, 54, 57, 58, 41, 53, 43, 44];

    /// <summary>The default value of a generator (SF2 8.1.3).</summary>
    private static int Default(int oper) => oper switch
    {
        8 => 13500,
        21 or 23 or 25 or 26 or 27 or 28 or 30 or 33 or 34 or 35 or 36 or 38 => -12000,
        46 or 47 or 58 => -1,
        56 => 100,
        _ => 0,
    };

    public static IReadOnlyList<Sf2PlayableRegion> Resolve(Sf2Bank bank, int bankNumber, int program)
    {
        Sf2Preset preset = bank.Presets.FirstOrDefault(p => p.Bank == bankNumber && p.Program == program)
            ?? throw new Sf2FormatException($"AMP5311: SoundFont preset bank {bankNumber}, program {program} is unavailable");
        UsageTrace.Preset(bank.SourcePath, bankNumber, program);
        Sf2Zone? presetGlobal = preset.Zones.FirstOrDefault(z => z.Find(41) is null);
        List<Sf2PlayableRegion> result = [];
        foreach (Sf2Zone presetZone in preset.Zones.Where(z => z.Find(41) is not null))
        {
            List<Sf2Zone> presetZones = presetGlobal is null ? [presetZone] : [presetGlobal, presetZone];
            int instrumentIndex = presetZone.Find(41)!.Value.Amount;
            if ((uint)instrumentIndex >= (uint)bank.Instruments.Count) throw new Sf2FormatException("AMP5311: SoundFont instrument index is invalid");
            Sf2Instrument instrument = bank.Instruments[instrumentIndex];
            Sf2Zone? instrumentGlobal = instrument.Zones.FirstOrDefault(z => z.Find(53) is null);
            foreach (Sf2Zone instrumentZone in instrument.Zones.Where(z => z.Find(53) is not null))
            {
                List<Sf2Zone> zones = [.. presetZones];
                if (instrumentGlobal is not null) zones.Add(instrumentGlobal);
                zones.Add(instrumentZone);
                Sf2Gen sampleGen = instrumentZone.Find(53)!.Value;
                int sampleIndex = sampleGen.Amount;
                if ((uint)sampleIndex >= (uint)bank.Samples.Count) throw new Sf2FormatException("AMP5311: SoundFont sample index is invalid");
                bank.LayOut(sampleIndex);   // a compressed (SF3) sample learns its length
                Sf2Sample sample = bank.Samples[sampleIndex];
                (int Lo, int Hi)? keyRange = Range(zones, 43), velocityRange = Range(zones, 44);
                if (keyRange is null || velocityRange is null) continue;
                (int keyLo, int keyHi) = keyRange.Value;
                (int velLo, int velHi) = velocityRange.Value;
                // each generator: the instrument's local zone, else its global zone, else the default; the preset adds its local zone, else its global zone (SF2 9.4)
                int[] gens = new int[61];
                for (ushort oper = 0; oper < gens.Length; oper++)
                {
                    int value = (instrumentZone.Find(oper) ?? instrumentGlobal?.Find(oper))?.Amount ?? Default(oper);
                    if (!InstrumentOnly.Contains(oper) && (presetZone.Find(oper) ?? presetGlobal?.Find(oper)) is { } offset) value = checked(value + offset.Amount);
                    gens[oper] = value;
                }

                int start = checked((int)sample.Start + gens[0] + (gens[4] * 32768));
                int end = checked((int)sample.End + gens[1] + (gens[12] * 32768));
                int loopStart = checked((int)sample.LoopStart + gens[2] + (gens[45] * 32768));
                int loopEnd = checked((int)sample.LoopEnd + gens[3] + (gens[50] * 32768));
                if (start < sample.Start || end <= start || end > sample.End)
                    throw new Sf2FormatException("AMP5311: SoundFont sample generator range is invalid");
                int root = gens[58] >= 0 ? gens[58] : sample.OriginalPitch;
                int tune = sample.PitchCorrection + gens[51] * 100 + gens[52];
                int attenuationDb = Math.Clamp(gens[48] / 10, 0, 96);
                int gain = FixedAudio.GainFromDb(-attenuationDb);
                if (sample.Rate is < 1000 or > 384000) throw new Sf2FormatException("AMP5311: SoundFont sample rate is invalid");
                int attack = Frames(gens[34], sample.Rate);
                int decay = Frames(gens[36], sample.Rate);
                int sustain = FixedAudio.GainFromDb(-Math.Clamp(gens[37] / 10, 0, 96));
                int release = Frames(gens[38], sample.Rate);
                int loopMode = gens[54];
                bool loop = loopMode is 1 or 3;
                bool invalidLoop = loop && (loopStart < start || loopEnd > end || loopEnd <= loopStart);
                if (invalidLoop) loop = false;
                if (!loop) { loopStart = start; loopEnd = end; }
                // the modulators: the defaults, replaced or joined by the instrument's global zone and then its local zone; the preset's (global, then local) are added on top
                List<Sf2Mod> instrumentMods = Sf2Modulators.Merge(Sf2Modulators.Defaults, instrumentGlobal?.Mods ?? [], instrumentZone.Mods);
                List<Sf2Mod> presetMods = Sf2Modulators.Merge(presetGlobal?.Mods ?? [], presetZone.Mods);
                Link(instrumentMods, 0, instrumentGlobal?.Mods, instrumentZone.Mods);
                Link(presetMods, 0, presetGlobal?.Mods, presetZone.Mods);
                for (int i = 0; i < presetMods.Count; i++) if (presetMods[i].LinkTarget >= 0) presetMods[i] = presetMods[i] with { LinkTarget = presetMods[i].LinkTarget + instrumentMods.Count };
                result.Add(new Sf2PlayableRegion(sampleIndex, keyLo, keyHi, velLo, velHi, Math.Clamp(root, 0, 127), tune,
                    gain, start, end, loopStart, loopEnd, loop, invalidLoop, attack, decay, sustain, release,
                    Math.Clamp(gens[8], 1500, 13500), Math.Clamp(gens[17], -500, 500), gens, [.. instrumentMods, .. presetMods]));
            }
        }
        return result.Count == 0 ? throw new Sf2FormatException("AMP5311: SoundFont preset has no playable sample zones") : result;
    }

    /// <summary>
    /// The settings of one note of a zone: every generator with the modulators of the note's key and velocity added (SF2 9.5), and the key scaling of the envelopes.
    /// With the <paramref name="controllers"/> of the channel the modulators of the controllers are added too (the renderer reads the settings again when a controller
    /// changes); the ones the renderer plays itself, pan (controller 10) and the pitch wheel, are left out. A zone without generators (made by hand) keeps its own fields
    /// and the velocity curve of the default modulator.
    /// </summary>
    public static Sf2NoteSettings Note(Sf2PlayableRegion region, int key, int velocity, Sf2Controllers? controllers = null)
    {
        if (region.Generators is not { } baseGens)
        {
            int velocityCb = (int)Sf2Modulators.Value(Sf2Modulators.Defaults[0], key, velocity);
            return new Sf2NoteSettings(velocityCb, region.TuneCents, region.FilterCents, 0, (-12000, -12000, -12000, -12000, 0, -12000), -12000, -12000, 0, -12000, region.PanPermille);
        }

        long[] g = new long[baseGens.Length];
        for (int i = 0; i < g.Length; i++) g[i] = baseGens[i];
        if (controllers is not null) for (int i = 0; i < g.Length; i++) g[i] += controllers.NrpnOffsets[i];   // offsets a song set with SF2 NRPNs (SF2 9.6)
        // the modulators: first those with a plain source, then (a few rounds, for chains) those whose source is the output of others (SF2 8.2.2, linked modulators)
        Sf2Mod[] mods = region.Modulators ?? [];
        long[] output = new long[mods.Length];
        for (int i = 0; i < mods.Length; i++) if (!Sf2Modulators.IsLink(mods[i].Source)) output[i] = Sf2Modulators.Value(mods[i], key, velocity, controllers);
        if (mods.Any(m => m.LinkTarget >= 0))
        {
            for (int round = 0; round < 4; round++)
            {
                long[] linked = new long[mods.Length];
                for (int i = 0; i < mods.Length; i++) if (mods[i].LinkTarget >= 0) linked[mods[i].LinkTarget] += output[i];
                for (int i = 0; i < mods.Length; i++) if (Sf2Modulators.IsLink(mods[i].Source)) output[i] = Sf2Modulators.Value(mods[i], key, velocity, controllers, (int)Math.Clamp(linked[i], -32768, 32768));
            }
        }

        for (int i = 0; i < mods.Length; i++)
        {
            if (mods[i].LinkTarget < 0 && mods[i].Destination < g.Length) g[mods[i].Destination] += output[i];
        }

        int Gen(int oper, long min, long max) => (int)Math.Clamp(g[oper], min, max);
        int keyOffset = 60 - key;   // the key scaling of hold and decay: per key below middle C, longer (SF2 8.1.3, generators 31, 32, 39, 40)
        // the attenuation generator counts at 0.4 of its value, as in FluidSynth and the E-mu players the soundfonts were voiced with; the modulators count in full
        long attenuation = (baseGens[48] * 4 / 10) + (g[48] - baseGens[48]);
        return new Sf2NoteSettings(
            (int)Math.Clamp(attenuation, 0, 1440),
            Gen(51, -120, 120) * 100 + Gen(52, -99, 99) + (region.TuneCents - (baseGens[51] * 100) - baseGens[52]) + Gen(59, -12700, 12700),   // 59: the pitch the pitch wheel adds (its default modulator)
            Gen(8, 1500, 13500),
            Gen(11, -12000, 12000),
            (Gen(25, -12000, 5000), Gen(26, -12000, 8000), Gen(27, -12000, 5000) + (Gen(31, -1200, 1200) * keyOffset), Gen(28, -12000, 8000) + (Gen(32, -1200, 1200) * keyOffset), Gen(29, 0, 1000), Gen(30, -12000, 8000)),
            Gen(34, -12000, 8000),
            Gen(36, -12000, 8000) + (Gen(40, -1200, 1200) * keyOffset),
            Gen(37, 0, 1440),
            Gen(38, -12000, 8000),
            Gen(17, -500, 500),
            Gen(9, 0, 960),
            Gen(33, -12000, 5000),
            Gen(35, -12000, 5000) + (Gen(39, -1200, 1200) * keyOffset),
            Gen(56, 0, 1200),
            (Gen(21, -12000, 5000), Gen(22, -16000, 4500), Gen(5, -12000, 12000), Gen(10, -12000, 12000), Gen(13, -960, 960), Gen(23, -12000, 5000), Gen(24, -16000, 4500), Gen(6, -12000, 12000)),
            Gen(7, -12000, 12000),
            Gen(16, 0, 1000),
            Gen(15, 0, 1000));
    }

    public static IReadOnlyList<string> UnsupportedFeatures(Sf2Bank bank, int bankNumber, int program)
    {
        Sf2Preset preset = bank.Presets.FirstOrDefault(p => p.Bank == bankNumber && p.Program == program)
            ?? throw new Sf2FormatException($"AMP5311: SoundFont preset bank {bankNumber}, program {program} is unavailable");
        List<Sf2Zone> zones = [.. preset.Zones];
        foreach (int index in preset.Zones.Select(z => z.Find(41)?.Amount ?? -1).Where(i => i >= 0).Distinct())
            if ((uint)index < (uint)bank.Instruments.Count) zones.AddRange(bank.Instruments[index].Zones);
        List<string> warnings = [];
        if (zones.SelectMany(z => z.Gens).Any(g => !SupportedOperators.Contains(g.Oper) && g.Amount != 0)) warnings.Add("The SoundFont preset uses generator operators that the integer renderer does not apply");
        return warnings;
    }

    // A modulator whose destination is another modulator (bit 15) names that one by its place in the zone's own list; the resolved list (defaults and global zone merged)
    // has other places, so the link is turned into the index in the resolved list (offset by start).
    private static void Link(List<Sf2Mod> resolved, int start, params IReadOnlyList<Sf2Mod>?[] zones)
    {
        foreach (IReadOnlyList<Sf2Mod>? zone in zones)
        {
            if (zone is null) continue;
            foreach (Sf2Mod mod in zone)
            {
                if ((mod.Destination & 0x8000) == 0) continue;
                int targetPlace = mod.Destination & 0x7FFF;
                if (targetPlace >= zone.Count) continue;
                int from = resolved.FindIndex(m => Sf2Modulators.Same(m, mod)), to = resolved.FindIndex(m => Sf2Modulators.Same(m, zone[targetPlace]));
                if (from >= 0 && to >= 0 && from != to) resolved[from] = resolved[from] with { LinkTarget = start + to };
            }
        }
    }

    /// <summary>Timecents to frames at a sample rate.</summary>
    internal static int Frames(int timecents, uint sampleRate) => checked((int)Math.Clamp(InstrumentMath.TimecentsToMicroseconds(timecents) * sampleRate / 1_000_000, 0, int.MaxValue));

    private static (int Lo, int Hi)? Range(IReadOnlyList<Sf2Zone> zones, ushort oper)
    {
        int lo = 0, hi = 127;
        foreach (Sf2Zone zone in zones)
        {
            if (zone.Find(oper) is not { } gen) continue;
            if (gen.RangeLo > gen.RangeHi || gen.RangeHi > 127) throw new Sf2FormatException("AMP5311: SoundFont key or velocity range is malformed");
            lo = Math.Max(lo, gen.RangeLo);
            hi = Math.Min(hi, gen.RangeHi);
        }
        return lo > hi ? null : (lo, hi);
    }
}
