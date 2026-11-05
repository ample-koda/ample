using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>The take a stem was cut from (kept in the manifest so a render can be reproduced).</summary>
/// <param name="Block">The block name.</param>
/// <param name="TakeId">The take id.</param>
/// <param name="PlanId">The plan the take was made from.</param>
public sealed record GameScoreTake(string Block, string TakeId, string PlanId);

/// <summary>Where a game score came from: software, seed, song, definition and takes.</summary>
/// <param name="Ample">The AMPLE version.</param>
/// <param name="Spec">The spec version.</param>
/// <param name="MasterSeed">The master seed of the project (as text).</param>
/// <param name="Song">The song file.</param>
/// <param name="SongSha256">The hash of the song text.</param>
/// <param name="DefinitionSha256">The hash of the score definition text.</param>
/// <param name="Takes">The takes the stems were cut from.</param>
public sealed record GameScoreProvenance(string Ample, string Spec, string MasterSeed, string Song, string SongSha256, string DefinitionSha256, IReadOnlyList<GameScoreTake> Takes);

/// <summary>One file a score build makes: a stem or a stinger.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Role"><c>layer</c> or <c>stinger</c>.</param>
/// <param name="Id">The layer or stinger id.</param>
/// <param name="Data">The WAV bytes.</param>
/// <param name="Frames">The number of frames.</param>
/// <param name="Sha256">The hash of <paramref name="Data"/>.</param>
public sealed record GameScoreFile(string Name, string Role, string Id, byte[] Data, int Frames, string Sha256);

/// <summary>What a score build made.</summary>
/// <param name="ManifestFile">The file name of the manifest.</param>
/// <param name="ManifestJson">The manifest text.</param>
/// <param name="Files">The stems and stingers.</param>
/// <param name="Warnings">Things worth a look that do not stop the build (a silent stem, for example).</param>
/// <param name="Revision">A short identity of this exact build (changes when any stem or the definition changes).</param>
public sealed record GameScoreBuild(string ManifestFile, string ManifestJson, IReadOnlyList<GameScoreFile> Files, IReadOnlyList<string> Warnings, string Revision);

/// <summary>
/// Cuts the built takes of a song into the bar-aligned, loopable stems of a game score and writes the manifest the game reads
/// (see <c>docs/game-score.md</c>). Everything here is pure: the same takes and definition always give the same files and manifest.
/// </summary>
public static class GameScoreBuilder
{
    /// <summary>The name of the manifest format.</summary>
    public const string Format = "ample-game-score";

    /// <summary>The version of the manifest format.</summary>
    public const int Version = 1;

    /// <summary>Gets the file prefix of a score, with its variant when it has one.</summary>
    /// <param name="name">The score name.</param>
    /// <param name="variant">The variant, or null.</param>
    public static string Prefix(string name, string? variant)
    {
        ArgumentNullException.ThrowIfNull(name);
        return variant is { Length: > 0 } ? name + "." + variant : name;
    }

    /// <summary>Decodes the WAV audio of a block take (mono or stereo) to a stereo buffer.</summary>
    /// <param name="wav">The WAV bytes.</param>
    public static (StereoBuffer Buffer, int SampleRate) Decode(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        try
        {
            return ChannelAudio.Decode(wav);
        }
        catch (InvalidDataException ex)
        {
            throw new AmpleException("AMP5287", "A block of the score does not have WAV audio: " + ex.Message);
        }
    }

    /// <summary>Gets the blocks a score needs from the built song (layers and stingers, without duplicates).</summary>
    /// <param name="definition">The definition.</param>
    public static IReadOnlyList<string> Blocks(GameScoreDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return [.. definition.Layers.Select(l => l.Block).Concat(definition.Stingers.Select(s => s.Block)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Cuts the stems and stingers and writes the manifest.</summary>
    /// <param name="definition">The checked definition.</param>
    /// <param name="variant">The variant being built, or null for the main song.</param>
    /// <param name="blockAudio">The full-length audio of each block named in <see cref="Blocks"/>.</param>
    /// <param name="sampleRate">The sample rate of the audio.</param>
    /// <param name="tempo">The tempo of the song in beats per minute.</param>
    /// <param name="meterNumerator">The top of the meter (only 4 is supported).</param>
    /// <param name="meterDenominator">The bottom of the meter (only 4 is supported).</param>
    /// <param name="provenance">Where the takes came from.</param>
    /// <exception cref="AmpleException">AMP5287 when a block is missing, the meter is not 4/4 or the song is shorter than the loop.</exception>
    public static GameScoreBuild Build(GameScoreDefinition definition, string? variant, IReadOnlyDictionary<string, StereoBuffer> blockAudio, int sampleRate, double tempo, int meterNumerator, int meterDenominator, GameScoreProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(blockAudio);
        ArgumentNullException.ThrowIfNull(provenance);
        if (meterNumerator != 4 || meterDenominator != 4)
        {
            throw new AmpleException("AMP5287", "A game score needs a song in 4/4; the stems are cut on whole bars of four beats.");
        }

        if (tempo is < 20 or > 400 || sampleRate < 8000)
        {
            throw new AmpleException("AMP5287", "The song has a tempo or sample rate a game score cannot use.");
        }

        string prefix = Prefix(definition.Name, variant);
        double bar = sampleRate * 60.0 / tempo * 4;
        List<GameScoreFile> files = [];
        List<string> warnings = [];
        int loopFrames = -1;
        foreach (GameScoreLayer layer in definition.Layers)
        {
            StereoBuffer source = Source(blockAudio, layer.Block);
            int end = (int)Math.Round((definition.LoopFromBar - 1 + definition.LoopBars) * bar);
            if (source.Length < end)
            {
                throw new AmpleException("AMP5287", $"The block '{layer.Block}' is {Math.Round(source.Length / bar, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)} bars long but the loop ends at bar {definition.LoopFromBar + definition.LoopBars - 1}; make the song longer or the loop shorter.");
            }

            StereoBuffer cut = FinalsStore.Cut(source, sampleRate, tempo, new FinalSpec(layer.Id, definition.LoopFromBar, definition.LoopBars, 0, 0, "wav"));
            loopFrames = cut.Length;
            files.Add(MakeFile(prefix + "." + layer.Id + ".wav", "layer", layer.Id, cut, sampleRate, warnings));
        }

        foreach (GameScoreStinger stinger in definition.Stingers)
        {
            StereoBuffer source = Source(blockAudio, stinger.Block);
            int end = (int)Math.Round((stinger.FromBar - 1 + stinger.Bars) * bar);
            if (source.Length < end)
            {
                throw new AmpleException("AMP5287", $"The block '{stinger.Block}' is too short for the stinger '{stinger.Id}' (it needs {stinger.FromBar - 1 + stinger.Bars} bars).");
            }

            StereoBuffer cut = FinalsStore.Cut(source, sampleRate, tempo, new FinalSpec(stinger.Id, stinger.FromBar, stinger.Bars, 0, 0, "wav"));
            files.Add(MakeFile(prefix + "." + stinger.Id + ".wav", "stinger", stinger.Id, cut, sampleRate, warnings));
        }

        string revision = Revision(provenance.DefinitionSha256, files);
        string manifestFile = prefix + ".manifest.json";
        return new GameScoreBuild(manifestFile, Manifest(definition, variant, files, sampleRate, tempo, loopFrames, provenance, revision), files, warnings, revision);
    }

    private static StereoBuffer Source(IReadOnlyDictionary<string, StereoBuffer> blockAudio, string block) =>
        blockAudio.TryGetValue(block, out StereoBuffer? buffer) ? buffer : throw new AmpleException("AMP5287", $"The song has no built block '{block}'.");

    private static GameScoreFile MakeFile(string name, string role, string id, StereoBuffer cut, int sampleRate, List<string> warnings)
    {
        byte[] wav = StereoWav.Encode(cut, sampleRate);
        int peak = 0;
        for (int i = 0; i < cut.Length; i++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs((int)cut.Left[i]), Math.Abs((int)cut.Right[i])));
        }

        if (peak == 0)
        {
            warnings.Add($"The {role} '{id}' is silent in the cut region; check the block and the loop bars.");
        }

        return new GameScoreFile(name, role, id, wav, cut.Length, Convert.ToHexStringLower(SHA256.HashData(wav)));
    }

    private static string Revision(string definitionSha, IReadOnlyList<GameScoreFile> files)
    {
        StringBuilder text = new();
        text.Append(definitionSha);
        foreach (GameScoreFile file in files)
        {
            text.Append('\n').Append(file.Name).Append(':').Append(file.Sha256);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    private static string Manifest(GameScoreDefinition d, string? variant, IReadOnlyList<GameScoreFile> files, int sampleRate, double tempo, int loopFrames, GameScoreProvenance p, string revision)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", Version);
            w.WriteString("id", d.Name);
            if (variant is { Length: > 0 })
            {
                w.WriteString("variant", variant);
            }
            else
            {
                w.WriteNull("variant");
            }

            w.WriteString("revision", revision);
            w.WriteNumber("tempo", tempo);
            w.WriteStartArray("meter");
            w.WriteNumberValue(4);
            w.WriteNumberValue(4);
            w.WriteEndArray();
            w.WriteNumber("sampleRate", sampleRate);
            w.WriteNumber("channels", 2);
            w.WriteStartObject("loop");
            w.WriteNumber("fromBar", d.LoopFromBar);
            w.WriteNumber("bars", d.LoopBars);
            w.WriteNumber("frames", loopFrames);
            w.WriteEndObject();
            w.WriteString("initialState", d.InitialState);
            WriteFiles(w, "layers", files.Where(f => f.Role == "layer"), null);
            WriteFiles(w, "stingers", files.Where(f => f.Role == "stinger"), d);
            w.WriteStartArray("states");
            foreach (GameScoreState state in d.States)
            {
                w.WriteStartObject();
                w.WriteString("id", state.Id);
                w.WriteStartObject("levels");
                foreach (GameScoreLayer layer in d.Layers)
                {
                    w.WriteNumber(layer.Id, state.Levels.TryGetValue(layer.Id, out double level) ? level : 0.0);
                }

                w.WriteEndObject();
                WriteNullable(w, "stinger", state.Stinger);
                WriteNullable(w, "fallback", state.Fallback);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("transitions");
            WriteTransition(w, "default", d.Default);
            w.WriteStartArray("rules");
            foreach (GameScoreTransition rule in d.Rules)
            {
                w.WriteStartObject();
                w.WriteString("from", rule.From);
                w.WriteString("to", rule.To);
                w.WriteString("quantize", rule.Quantize);
                w.WriteNumber("fadeBars", rule.FadeBars);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("provenance");
            w.WriteString("ample", p.Ample);
            w.WriteString("spec", p.Spec);
            w.WriteString("masterSeed", p.MasterSeed);
            w.WriteString("song", p.Song);
            w.WriteString("songSha256", p.SongSha256);
            w.WriteString("definitionSha256", p.DefinitionSha256);
            w.WriteStartArray("takes");
            foreach (GameScoreTake take in p.Takes)
            {
                w.WriteStartObject();
                w.WriteString("block", take.Block);
                w.WriteString("takeId", take.TakeId);
                w.WriteString("planId", take.PlanId);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static void WriteFiles(Utf8JsonWriter w, string name, IEnumerable<GameScoreFile> files, GameScoreDefinition? stingerSource)
    {
        w.WriteStartArray(name);
        foreach (GameScoreFile file in files)
        {
            w.WriteStartObject();
            w.WriteString("id", file.Id);
            w.WriteString("file", file.Name);
            w.WriteString("url", "/api/game/stems/" + file.Name);
            w.WriteString("sha256", file.Sha256);
            w.WriteNumber("frames", file.Frames);
            w.WriteNumber("bytes", file.Data.Length);
            if (stingerSource is not null)
            {
                GameScoreStinger info = stingerSource.Stingers.First(s => s.Id == file.Id);
                w.WriteNumber("bars", info.Bars);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteTransition(Utf8JsonWriter w, string name, GameScoreTransition t)
    {
        w.WriteStartObject(name);
        w.WriteString("quantize", t.Quantize);
        w.WriteNumber("fadeBars", t.FadeBars);
        w.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }
}
