using Ample.Audio.Instruments;

namespace Ample.Core.Tests.Instruments;

/// <summary>The SF2 modulators played at note-on: the curves of their sources, which modulator replaces which, and how they change the settings of a note.</summary>
public sealed class Sf2ModulatorTests
{
    private static Sf2PlayableRegion Region(int[] gens, params Sf2Mod[] mods) =>
        new(0, 0, 127, 0, 127, 60, 0, 32768, 0, 100, 0, 100, false, false, 0, 0, 32768, 0, 13500, 0, gens, mods);

    private static int[] Defaults()
    {
        int[] gens = new int[61];
        gens[8] = 13500;
        foreach (int oper in new[] { 25, 26, 27, 28, 30, 33, 34, 35, 36, 38 }) gens[oper] = -12000;
        gens[58] = -1;
        return gens;
    }

    [Fact]
    public void The_default_velocity_modulator_is_about_40_log10_of_the_velocity()
    {
        foreach (int velocity in new[] { 30, 64, 100 })
        {
            double expected = -400 * Math.Log10(velocity / 127.0);   // centibels
            long actual = Sf2Modulators.Value(Sf2Modulators.Defaults[0], 60, velocity);
            Assert.InRange(actual, expected - 3, expected + 3);
        }

        Assert.Equal(0, Sf2Modulators.Value(Sf2Modulators.Defaults[0], 60, 127));
    }

    [Fact]
    public void A_source_follows_its_direction_and_polarity()
    {
        Assert.Equal(32768, Sf2Modulators.Source(0, 60, 64));                       // no controller: 1
        Assert.Equal(64 * 32768 / 128, Sf2Modulators.Source(0x0002, 60, 64));       // velocity, linear, positive
        Assert.Equal(32768 - (64 * 32768 / 128), Sf2Modulators.Source(0x0102, 60, 64));   // negative
        Assert.Equal(0, Sf2Modulators.Source(0x0203, 64, 100));                     // key, bipolar: middle is 0
        Assert.True(Sf2Modulators.Source(0x0203, 100, 100) > 0 && Sf2Modulators.Source(0x0203, 30, 100) < 0);
        Assert.Equal(32768, Sf2Modulators.Source(0x0C02, 60, 64));                  // velocity switch: on from 64
        Assert.Equal(0, Sf2Modulators.Source(0x0C02, 60, 63));
    }

    [Fact]
    public void A_later_modulator_with_the_same_source_destination_and_amount_source_replaces_the_earlier_one()
    {
        List<Sf2Mod> merged = Sf2Modulators.Merge(Sf2Modulators.Defaults, [new Sf2Mod(0x0502, 48, 0, 0, 0)], [new Sf2Mod(0x0002, 11, 6000, 0, 0)]);
        Assert.Equal(Sf2Modulators.Defaults.Length + 1, merged.Count);
        Assert.Equal(0, merged.Single(m => m.Source == 0x0502 && m.Destination == 48).Amount);   // the soundfont switched the default off
    }

    [Fact]
    public void Controller_modulators_are_not_played_at_note_on()
    {
        Assert.Equal(0, Sf2Modulators.Value(new Sf2Mod(0x0081, 6, 50, 0, 0), 60, 100));   // CC1 to vibrato
    }

    [Fact]
    public void Volume_and_expression_lower_the_level_on_the_concave_curve()
    {
        Sf2Mod volume = Sf2Modulators.Defaults.Single(m => m.Source == 0x0587);
        foreach (int value in new[] { 32, 64, 100 })
        {
            Sf2Controllers controllers = new();
            controllers.Cc[7] = value;
            double expected = -400 * Math.Log10(value / 127.0);   // centibels, as FluidSynth
            Assert.InRange(Sf2Modulators.Value(volume, 60, 100, controllers), expected - 3, expected + 3);
        }

        Assert.Equal(0, Sf2Modulators.Value(volume, 60, 100, new Sf2Controllers()));   // 127 at the start: nothing
    }

    [Fact]
    public void With_the_controllers_a_note_plays_the_controller_modulators()
    {
        int[] gens = Defaults();
        gens[8] = 8000;
        Sf2Mod wheelOpensFilter = new(0x0081, 8, 2400, 0, 0);   // the soundfont's own: the modulation wheel opens the filter by two octaves
        Sf2Mod panFromController = new(0x028A, 17, 500, 0, 0);   // a zone's own pan modulator, next to the default one
        Sf2PlayableRegion region = Region(gens, [.. Sf2Modulators.Defaults, wheelOpensFilter, panFromController]);
        Sf2Controllers rest = new(), wheel = new();
        wheel.Cc[1] = 127;
        wheel.Cc[91] = 127;
        wheel.Cc[93] = 64;
        wheel.Cc[10] = 0;
        Sf2NoteSettings atRest = Sf2Program.Note(region, 60, 100, rest), moved = Sf2Program.Note(region, 60, 100, wheel);
        Assert.Equal(0, atRest.Lfos.VibToPitch);
        Assert.InRange(moved.Lfos.VibToPitch, 49, 50);   // the default: the wheel deepens the vibrato LFO to 50 cents
        Assert.InRange(moved.FilterCents - atRest.FilterCents, 2370, 2400);
        Assert.InRange(moved.ReverbPermille, 198, 200);   // the reverb controller sends up to 20 %
        Assert.InRange(moved.ChorusPermille, 99, 101);
        Assert.Equal(-500, moved.PanPermille);   // pan is a modulator too now: the default and the zone's own both turn it left (clamped)
        Assert.Equal(atRest, Sf2Program.Note(region, 60, 100));   // at note-on without controllers: the same as controllers at rest
    }

    [Fact]
    public void A_note_adds_the_modulators_of_its_velocity_and_key_to_the_generators()
    {
        int[] gens = Defaults();
        gens[8] = 6237;    // a dark resting filter
        gens[48] = 100;    // 10 dB attenuation in the zone
        Sf2PlayableRegion region = Region(gens, [.. Sf2Modulators.Defaults, new Sf2Mod(0x0002, 11, 6400, 0, 0), new Sf2Mod(0x0203, 8, 2000, 0, 0)]);
        Sf2NoteSettings soft = Sf2Program.Note(region, 64, 32), loud = Sf2Program.Note(region, 64, 127), high = Sf2Program.Note(region, 100, 64);
        Assert.True(loud.ModEnvToFilterCents > soft.ModEnvToFilterCents, "velocity opens the filter envelope");
        Assert.True(high.FilterCents > soft.FilterCents, "a higher key makes the resting filter brighter");
        Assert.True(soft.AttenuationCb > loud.AttenuationCb, "a soft note is quieter");
        Assert.Equal(40, loud.AttenuationCb);   // 100 cB in the zone counts at 0.4; velocity 127 adds nothing
    }

    [Fact]
    public void The_modulation_envelope_rises_holds_decays_to_sustain_and_releases()
    {
        Sf2ModEnvelope envelope = new((-12000, 0, -12000, 0, 500, 0), 1000);   // attack 1 s, decay 1 s to half, release 1 s at 1000 Hz
        Assert.Equal(0, envelope.Level(0, 10_000));
        Assert.InRange(envelope.Level(500, 10_000), 16000, 17000);
        Assert.Equal(16384, envelope.Level(3000, 10_000));
        Assert.Equal(0, envelope.Level(5000, 3000));
    }
}
