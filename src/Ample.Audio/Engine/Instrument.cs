namespace Ample.Audio.Engine;

/// <summary>One playable sample with the range of notes and velocities it answers (the unit SFZ and SF2 regions map onto).</summary>
/// <param name="Name">Voice or note name the region belongs to (<c>kick</c>, <c>a2</c>).</param>
/// <param name="Root">MIDI note the recording sounds at, or -1 for an unpitched voice.</param>
/// <param name="KeyLo">Lowest note played from this region without nearest-note fallback.</param>
/// <param name="KeyHi">Highest note played from this region without nearest-note fallback.</param>
/// <param name="VelLo">Lowest velocity (0..32768) of the region.</param>
/// <param name="VelHi">Highest velocity (0..32768) of the region.</param>
/// <param name="Tune">Extra tuning in semitones.</param>
/// <param name="GainQ15">Extra gain, 32768 is unity.</param>
/// <param name="LoopMode">Loop mode: <c>none</c> (the only one played so far).</param>
/// <param name="LoopStart">Loop start in samples.</param>
/// <param name="LoopEnd">Loop end in samples.</param>
/// <param name="Group">Choke group, 0 for none.</param>
/// <param name="Sequence">Position in the round robin (1 based).</param>
/// <param name="Sample">The audio.</param>
/// <param name="VelCurve">A 128-entry (MIDI velocity 0..127) Q15 gain lookup table (SFZ <c>amp_velcurve_NNN</c>), applied on top of <paramref name="GainQ15"/> at note time instead of the default linear velocity response; null for the default (no curve).</param>
/// <param name="FilterCutoffHz">A static low-pass cutoff (SFZ <c>cutoff</c>, Hz), 0 for no filter.</param>
/// <param name="FilterPoles">1 or 2 one-pole stages in series (SFZ <c>fil_type lpf_1p</c>/<c>lpf_2p</c>); meaningless when <paramref name="FilterCutoffHz"/> is 0.</param>
/// <param name="LoopCrossfade">Samples of linear crossfade at the loop wrap (<see cref="LoopPlayer"/>); meaningless when <paramref name="LoopMode"/> is <c>none</c>.</param>
/// <param name="Envelope">An integer ADSR (<c>S08</c>) applied per note instead of the renderer's fixed fade; null keeps today's fixed fade (unchanged bytes).</param>
/// <param name="FilterVelTrackCents">How far the cutoff moves (in cents, -9600 to 9600) at maximum velocity relative to velocity 0 (SFZ <c>fil_veltrack</c>); 0 (the default) is a static cutoff, meaningless when <paramref name="FilterCutoffHz"/> is 0.</param>
/// <param name="LfoFreqMilliHz">SFZ09: the pitch LFO's rate in milli-hertz (Hz * 1000, integer), 0 for no LFO.</param>
/// <param name="LfoDepthCents">SFZ09: the pitch LFO's peak swing in cents (<c>lfo01_pitch</c>); meaningless when <paramref name="LfoFreqMilliHz"/> is 0.</param>
/// <param name="LfoDelaySamples">SFZ09: samples before the LFO starts modulating (<c>lfo01_delay</c>), 0 for immediately.</param>
/// <param name="FilterEnvelope">SFZ08: an integer envelope (<c>fileg_*</c>) modulating the cutoff over the note's life; null for a static (or only velocity-tracked) cutoff.</param>
/// <param name="FilterEnvelopeDepthCents">SFZ08: how far the envelope moves the cutoff at its peak (cents, <c>fileg_depth</c>, may be negative); meaningless when <paramref name="FilterEnvelope"/> is null.</param>
/// <param name="IsRelease">S08/SFZ12: a release-triggered companion region (<c>&lt;name&gt;_rel.wav</c>, or SFZ <c>trigger=release</c>), played at note-off
/// instead of note-on; excluded from <see cref="RegionResolver.Pick"/>/<see cref="RegionResolver.PickVoice"/>'s normal note-on selection, only
/// found through <see cref="RegionResolver.PickReleaseVoice"/>/<see cref="RegionResolver.PickRelease"/>.</param>
/// <param name="XfinLoVel">SFZ12: velocity (0..32768) below which this region is silent, fading linearly to full gain at <paramref name="XfinHiVel"/>; -1 (the default) for no fade-in taper.</param>
/// <param name="XfinHiVel">SFZ12: velocity at which the fade-in taper reaches full gain; meaningless when <paramref name="XfinLoVel"/> is -1.</param>
/// <param name="XfoutLoVel">SFZ12: velocity above which this region starts fading out, reaching silence at <paramref name="XfoutHiVel"/>; -1 (the default) for no fade-out taper.</param>
/// <param name="XfoutHiVel">SFZ12: velocity at which the fade-out taper reaches silence; meaningless when <paramref name="XfoutLoVel"/> is -1.</param>
/// <param name="Trigger">SFZ12: <c>attack</c> (the default, always eligible on note-on), <c>first</c> (eligible only when no other note of the
/// same instrument is currently held - a non-legato attack) or <c>legato</c> (eligible only when another note is held). Release-triggered
/// regions use <see cref="IsRelease"/> instead, selected through a separate path, not this field.</param>
/// <param name="ResonanceDb">SFZ08: a resonance boost near the cutoff (SFZ <c>resonance</c>, dB, 0 for none/the default) - an approximation
/// (a gain bump in the band just below the cutoff, built from the existing integer <c>AudioEffects.LowPass</c>), not a true resonant filter
/// pole; meaningless when <see cref="FilterCutoffHz"/> is 0.</param>
/// <param name="Polyphony">SFZ12: the most hits of this voice allowed to sound at once (SFZ <c>polyphony</c>/<c>note_polyphony</c>), 0 (the
/// default) for unlimited. Honoured by the drums kit voice path only (a bounded voice-stealing cap, first-come-first-served); pitched
/// (<c>set</c>) regions ignore it, since AMPLE's offline renderer has no live voice budget to protect there.</param>
/// <param name="OffBy">SFZ03: the choke group (see <see cref="Group"/>) this region's own hit closes, 0 (the default) for none. Read at
/// region precision by the drums renderer's choke-and-fade pass (which region actually sounded, not just which voice name it came from),
/// alongside the coarser voice-name <c>choke</c> rules <see cref="Sfz.SfzReader"/> also derives from the same opcode for the block's own
/// <c>choke</c> parameter text form.</param>
public sealed record Region(string Name, int Root, int KeyLo, int KeyHi, int VelLo, int VelHi, int Tune, int GainQ15, string LoopMode, int LoopStart, int LoopEnd, int Group, int Sequence, SampleFile Sample, int[]? VelCurve = null, int FilterCutoffHz = 0, int FilterPoles = 1, int LoopCrossfade = 0, IntegerEnvelope? Envelope = null, int FilterVelTrackCents = 0, int LfoFreqMilliHz = 0, int LfoDepthCents = 0, int LfoDelaySamples = 0, IntegerEnvelope? FilterEnvelope = null, int FilterEnvelopeDepthCents = 0, bool IsRelease = false, int XfinLoVel = -1, int XfinHiVel = -1, int XfoutLoVel = -1, int XfoutHiVel = -1, string Trigger = "attack", int ResonanceDb = 0, int Polyphony = 0, int OffBy = 0);

/// <summary>An immutable set of regions.</summary>
/// <param name="Regions">The regions in file-name order.</param>
public sealed record Instrument(IReadOnlyList<Region> Regions)
{
    /// <summary>Gets a value indicating whether any region is pitched (named by note).</summary>
    public bool HasPitched => Regions.Any(r => r.Root >= 0);

    /// <summary>Gets a value indicating whether a voice (for example <c>kick</c>) has a region.</summary>
    public bool HasVoice(string name) => Regions.Any(r => r.Root < 0 && r.Name == name);

    /// <summary>SFZ09/SFZ08: gets a value indicating whether any region has a pitch LFO or a filter envelope configured - both need the
    /// per-frame expressive playback path (<c>PlayExpressiveNote</c>). Checked once per render (not per note) so a pitched renderer can
    /// force every note through that path instead of deciding per note before the region (and so what it has configured) is even picked.</summary>
    public bool NeedsPerFrameModulation => Regions.Any(r => r.LfoFreqMilliHz != 0 || r.FilterEnvelope is not null);

    /// <summary>SFZ12: gets a value indicating whether any region reads <c>trigger=first</c>/<c>legato</c> - only then is it worth a
    /// renderer computing, per note, whether another note was already held at its start (<see cref="RegionResolver.Pick"/>'s <c>legato</c>
    /// parameter); a plain instrument (everything <c>trigger=attack</c>, the default) never needs that computation.</summary>
    public bool HasTriggerVariants => Regions.Any(r => r.Trigger is "first" or "legato");

    /// <summary>
    /// Builds the instrument of a folder. <c>name.wav</c> is layer 1, round robin 1; <c>name_v&lt;L&gt;_r&lt;R&gt;.wav</c> is layer L, round robin R. The velocity
    /// range of a layer comes from the highest layer number N of its voice or note: theoretical layer <c>min(N-1, v*N/32769)</c>, and a layer that has no file
    /// is played by the nearest layer that has one (the lower on a tie).
    /// </summary>
    public static Instrument FromFiles(IEnumerable<SampleFile> files, int gainQ15 = 32768, int tune = 0,
        IReadOnlyDictionary<string, (int Start, int End, int Crossfade)>? loops = null,
        IReadOnlyDictionary<string, IntegerEnvelope>? envelopes = null)
    {
        List<(SampleFile File, string Name, int Layer, int Rr, bool IsRelease)> parsed = [.. files.Select(f =>
        {
            (string n, int l, int r) = ParseName(f.Name);
            // S08: a trailing "_rel" (after the usual _v<L>_r<R> suffix is already stripped) names a release-triggered
            // companion of the voice/note that remains once it is removed - a reserved suffix, like _v/_r themselves.
            bool isRelease = n.Length > 4 && n.EndsWith("_rel", StringComparison.Ordinal);
            if (isRelease) n = n[..^4];
            return (f, n, l, r, isRelease);
        })];
        List<Region> regions = [];
        foreach (IGrouping<(string Name, bool IsRelease), (SampleFile File, string Name, int Layer, int Rr, bool IsRelease)> group in parsed.GroupBy(p => (p.Name, p.IsRelease)))
        {
            int n = group.Max(p => p.Layer);
            int[] present = [.. group.Select(p => p.Layer).Distinct().Order()];
            int[] owner = [.. Enumerable.Range(0, n).Select(idx => present.OrderBy(l => Math.Abs(l - 1 - idx)).ThenBy(l => l).First())];
            int? root = SampleSet.NoteOf(group.Key.Name);
            // A release companion does not loop or follow the voice's own ADSR - it is a separate, usually one-shot articulation.
            (string loopMode, int loopStart, int loopEnd, int loopCrossfade) = !group.Key.IsRelease && loops is not null && loops.TryGetValue(group.Key.Name, out (int Start, int End, int Crossfade) lp)
                ? ("sustain", lp.Start, lp.End, lp.Crossfade)
                : ("none", 0, 0, 0);
            IntegerEnvelope? envelope = !group.Key.IsRelease && envelopes is not null && envelopes.TryGetValue(group.Key.Name, out IntegerEnvelope env) ? env : null;
            foreach ((SampleFile file, string name, int layer, int rr, bool _) in group)
            {
                int[] idxs = [.. Enumerable.Range(0, n).Where(i => owner[i] == layer)];
                int lo = idxs[0] == 0 ? 0 : (int)(((long)idxs[0] * 32769) + n - 1) / n;
                int hi = idxs[^1] == n - 1 ? 32768 : (int)((((long)(idxs[^1] + 1) * 32769) + n - 1) / n) - 1;
                int key = root ?? -1;
                regions.Add(new Region(name, key, key, key, lo, hi, tune, gainQ15, loopMode, loopStart, loopEnd, 0, rr, file, LoopCrossfade: loopCrossfade, Envelope: envelope, IsRelease: group.Key.IsRelease));
            }
        }

        return new Instrument(regions);
    }

    /// <summary>Splits <c>kick_v2_r3</c> into (<c>kick</c>, 2, 3); a name without the suffix is layer 1, round robin 1.</summary>
    public static (string Name, int Layer, int Rr) ParseName(string file)
    {
        int r = file.LastIndexOf("_r", StringComparison.Ordinal);
        if (r > 0 && int.TryParse(file.AsSpan(r + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int rr) && rr > 0)
        {
            int v = file.LastIndexOf("_v", r, StringComparison.Ordinal);
            if (v > 0 && int.TryParse(file.AsSpan(v + 2, r - v - 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int layer) && layer > 0)
            {
                return (file[..v], layer, rr);
            }
        }

        return (file, 1, 1);
    }

    /// <summary>The layer a velocity (0..32768) plays in a voice with <paramref name="layers"/> layers (0 based): <c>min(layers-1, v*layers/32769)</c>.</summary>
    public static int LayerOf(int velocity, int layers) => Math.Min(layers - 1, (int)((long)Math.Clamp(velocity, 0, 32768) * layers / 32769));
}

/// <summary>Chooses the region that plays for a note or voice and velocity: pure, no clock, no random numbers.</summary>
public static class RegionResolver
{
    /// <summary>The region for a note: the pitched region whose key range holds it, else the nearest root (the lower root on a tie); null when there is none.
    /// Never a release-triggered region (<see cref="Region.IsRelease"/>) - see <see cref="PickRelease"/>. <paramref name="legato"/> (SFZ12) excludes a
    /// <c>trigger=first</c> region when true and a <c>trigger=legato</c> region when false; <c>trigger=attack</c> (the default) is always eligible.</summary>
    public static Region? Pick(Instrument instrument, int note, int velocity, int counter, bool legato = false)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Region[] pitched = [.. instrument.Regions.Where(r => r.Root >= 0 && !r.IsRelease && TriggerMatches(r, legato))];
        Region? near = pitched.OrderBy(r => note < r.KeyLo ? r.KeyLo - note : note > r.KeyHi ? note - r.KeyHi : 0).ThenBy(r => r.Root).FirstOrDefault();
        return near is null ? null : Choose(pitched.Where(r => r.Root == near.Root), velocity, counter);
    }

    /// <summary>The region of an unpitched voice for a velocity; null when the voice has none. Never a release-triggered region
    /// (<see cref="Region.IsRelease"/>) - see <see cref="PickReleaseVoice"/>. <paramref name="legato"/>: see <see cref="Pick"/>.</summary>
    public static Region? PickVoice(Instrument instrument, string name, int velocity, int counter, bool legato = false)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return Choose(instrument.Regions.Where(r => r.Root < 0 && !r.IsRelease && r.Name == name && TriggerMatches(r, legato)), velocity, counter);
    }

    private static bool TriggerMatches(Region region, bool legato) => region.Trigger switch
    {
        "first" => !legato,
        "legato" => legato,
        _ => true,
    };

    /// <summary>S08/SFZ12: the release-triggered companion region (<see cref="Region.IsRelease"/>) for a note, by nearest root the same way
    /// <see cref="Pick"/> finds its main region; null when the note's region has none.</summary>
    public static Region? PickRelease(Instrument instrument, int note, int velocity, int counter)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Region[] releases = [.. instrument.Regions.Where(r => r.Root >= 0 && r.IsRelease)];
        Region? near = releases.OrderBy(r => note < r.KeyLo ? r.KeyLo - note : note > r.KeyHi ? note - r.KeyHi : 0).ThenBy(r => r.Root).FirstOrDefault();
        return near is null ? null : Choose(releases.Where(r => r.Root == near.Root), velocity, counter);
    }

    /// <summary>S08/SFZ12: the release-triggered companion region (<see cref="Region.IsRelease"/>) of an unpitched voice; null when it has none.</summary>
    public static Region? PickReleaseVoice(Instrument instrument, string name, int velocity, int counter)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return Choose(instrument.Regions.Where(r => r.Root < 0 && r.IsRelease && r.Name == name), velocity, counter);
    }

    private static Region? Choose(IEnumerable<Region> group, int velocity, int counter)
    {
        Region[] all = [.. group];
        if (all.Length == 0)
        {
            return null;
        }

        Region[] hit = [.. all.Where(r => velocity >= r.VelLo && velocity <= r.VelHi).OrderBy(r => r.Sequence)];
        Region chosen = hit.Length == 0 ? all[0] : hit[Math.Max(0, counter) % hit.Length];
        UsageTrace.Sample(chosen.Sample.Hash);
        return chosen;
    }
}
