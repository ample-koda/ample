using Ample.Audio.IO;
using Ample.Core.Abstractions;

namespace Ample.Audio.Instruments;

/// <summary>Resolves project-owned SF2 files and the named AMPLE GM sample pack.</summary>
public static class SoundFontAsset
{
    /// <summary>The application asset name for the full General MIDI soundfont.</summary>
    public const string GeneralMidi = "pack:musescore-general";

    /// <summary>The project-relative path that is the default `file` of the gm template; a project that does not hold a copy plays the installed pack instead.</summary>
    public const string TemplateDefault = "samples/musescore-general/MuseScore_General.sf2";

    /// <summary>Resolves a project file or the installed AMPLE General MIDI pack.</summary>
    public static string Resolve(string projectRoot, string reference) => Resolve(projectRoot, null, reference);

    /// <summary>Resolves a project file or installed pack, using the server's AMPLE root when one is known.</summary>
    public static string Resolve(string projectRoot, string? ampleRoot, string reference)
    {
        if (!string.Equals(reference, GeneralMidi, StringComparison.Ordinal)) return ProjectPath.Resolve(projectRoot, reference);
        PackLookup lookup = AmpleHome.LookupPackFile(AmpleHome.DefaultPath(), ampleRoot, projectRoot, "licensed/musescore-general/MuseScore_General.sf2");
        return lookup.Path ?? throw new AmpleException("AMP5311", "The General MIDI soundfont pack is not installed. Searched: " + string.Join("; ", lookup.Searched) + ". Run 'ample packs link <folder>' or 'node tools/fetch-gm-soundfont.mjs --download'.");
    }

    /// <summary>Checks that a GM bank contains all melodic programs and the standard percussion preset.</summary>
    public static (int Programs, int DrumKits) VerifyGeneralMidi(string path)
    {
        Sf2Bank bank;
        try { bank = Sf2Reader.Read(path); }
        catch (Sf2FormatException ex) { throw new AmpleException("AMP5311", ex.Message); }
        HashSet<int> programs = [.. bank.Presets.Where(p => p.Bank == 0).Select(p => p.Program)];
        if (Enumerable.Range(0, 128).Any(p => !programs.Contains(p)) || !bank.Presets.Any(p => p.Bank == 128 && p.Program == 0))
            throw new AmpleException("AMP5311", "The SoundFont does not provide all 128 GM programs and bank-128 percussion program 0.");
        foreach (int program in Enumerable.Range(0, 128)) _ = Sf2Program.Resolve(bank, 0, program);
        int drumKits = 0;
        foreach (Sf2Preset preset in bank.Presets.Where(p => p.Bank == 128).DistinctBy(p => p.Program))
        {
            _ = Sf2Program.Resolve(bank, 128, preset.Program);
            drumKits++;
        }
        return (programs.Count, drumKits);
    }
}
