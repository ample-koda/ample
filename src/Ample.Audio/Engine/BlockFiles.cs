using Ample.Core.Abstractions;
using Ample.Audio.Instruments;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// The files a block reads while it renders: the sample folders, the lyrics, MIDI and audio files it names, its SoundFont and the module it is expanded from.
/// A change to one of them makes the take of that block (and the blocks that read it) out of date, although the song text did not change.
/// This only finds the files; the build engine still hashes their content into the plan.
/// </summary>
public static class BlockFiles
{
    /// <summary>Resolves the WAV impulse response selected by a convolution block, including installed pack presets.</summary>
    public static string? ConvolutionImpulse(string projectRoot, string? ampleRoot, BlockNode block) => block.Type == "convolution" ? ImportAudioRenderer.FileOf(projectRoot, block, ampleRoot) : null;

    /// <summary>Gets the existing files a block reads, as full paths in a stable order.</summary>
    public static IReadOnlyList<string> Of(string projectRoot, string? ampleRoot, AmpleDocument doc, BlockNode block, ISet<string>? setFiles = null)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(block);
        SortedSet<string> files = new(StringComparer.Ordinal);
        void Add(string? path) { if (path is not null && File.Exists(path)) files.Add(path); }
        void Folder(string folder)
        {
            try
            {
                string full = SampleSet.ResolveFolder(projectRoot, ampleRoot, folder);
                if (!Directory.Exists(full)) return;
                foreach (string wav in Directory.EnumerateFiles(full, "*.wav")) { Add(wav); setFiles?.Add(wav); }
                Add(Path.Combine(full, SampleSet.SettingsFile));
            }
            catch (AmpleException)
            {
                // A folder that cannot be found is reported by the renderer.
            }
        }

        if (block.Type == "sequencer") foreach (string sequenceFile in SequencerFiles.Of(projectRoot, ampleRoot, block, setFiles)) files.Add(sequenceFile);

        if (SampleSet.FolderOf(projectRoot, ampleRoot, block) is { } sampleFolder)
        {
            Folder(sampleFolder);
            foreach (string extra in SampledInstruments.ExtraKitFolders(block)) Folder(extra);
        }

        string Value(string name) => block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? string.Empty;
        try
        {
            Add(BuiltInRenderers.LyricsFile(projectRoot, block));
            if (block.Type is "import-audio" or "sampler" or "convolution" or "ambience") Add(ImportAudioRenderer.FileOf(projectRoot, block));
            if (block.Type == "voice-clone") Add(Ample.Audio.IO.ProjectPath.Resolve(projectRoot, Value("file")));
            if (block.Type == "midi") Add(BuiltInRenderers.MidiFile(projectRoot, block));
            if (block.Type is "gm" or "midi-render" or "midi")
            {
                string reference = block.Type == "midi" ? SoundFontAsset.GeneralMidi : Value("file");
                if (reference.Length > 0) Add(SoundFontAsset.Resolve(projectRoot, ampleRoot, reference));
            }
        }
        catch (AmpleException)
        {
            // A reference that cannot be resolved has nothing to watch; the renderer says why.
        }

        // a module's MIDI performance: the blocks it makes are named after its instance
        if (block.Type == "gm")
        {
            foreach (UseNode use in doc.Uses.Where(u => block.Name.StartsWith(u.Instance + "/", StringComparison.Ordinal))) Add(Path.Combine(projectRoot, "modules", use.Module + ".module"));
        }

        return [.. files];
    }

    /// <summary>A cheap fingerprint of the files: name, size and time of last change (the content hash is the engine's business, this is for noticing).</summary>
    public static string Stamp(IEnumerable<string> files) => string.Join('\n', files.Select(f =>
    {
        FileInfo info = new(f);
        return $"{f}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }));
}
