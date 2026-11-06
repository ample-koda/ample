using System.Text;
using Ample.Audio.IO;
using Ample.Engine.Library;
using Ample.Storage.Library;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Engine.Modules;

/// <summary>Finds module files (project <c>modules/</c> folder first, then the library's linked folders and packs), expands the <c>use</c> statements of songs and keeps the versions and hashes in <c>ample.lock</c>.</summary>
/// <param name="projectRoot">The project folder.</param>
/// <param name="library">The user's library, or null when there is none.</param>
/// <param name="projectId">The project id (for project-scoped linked folders), or null.</param>
public sealed class ModuleService(string projectRoot, Func<Ample.Storage.Library.Library?> library, string? projectId = null) : IDisposable
{
    private Ample.Storage.Library.Library? opened;

    /// <summary>The readers of unsaved text, by full project folder: a host that keeps working copies of files (the studio server) puts one here, and a module or a component file that has unsaved changes is read from it instead of from the disk (plan/editor-tabs.md, part 1).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<string, string?>> WorkingTextReaders = new(StringComparer.OrdinalIgnoreCase);

    private string? Working(string relative) => WorkingTextReaders.TryGetValue(Path.GetFullPath(projectRoot), out Func<string, string?>? read) ? read(relative.Replace('\\', '/')) : null;

    /// <summary>Creates a service that opens the user's library (AMPLE_HOME or the default folder) only when a module is not in the project, and closes it with the service.</summary>
    public static ModuleService WithUserLibrary(string projectRoot, IClock clock)
    {
        ModuleService? self = null;
        self = new ModuleService(projectRoot, () => self!.opened ??= Ample.Storage.Library.Library.Open(Ample.Storage.Library.Library.DefaultHome(), clock));
        return self;
    }

    /// <inheritdoc />
    public void Dispose() => opened?.Dispose();

    /// <summary>Creates the service with a library that is already open (or none).</summary>
    public ModuleService(string projectRoot, Ample.Storage.Library.Library? library, string? projectId = null)
        : this(projectRoot, () => library, projectId)
    {
    }

    /// <summary>The extension of a module file.</summary>
    public const string Extension = ".module";

    /// <summary>Finds the file of a module; null when there is none.</summary>
    public ModuleSource? Find(string name)
    {
        string? path = FindPath(name, out string origin);
        return path is null ? null : new ModuleSource(name, Working(Path.GetRelativePath(projectRoot, path)) ?? File.ReadAllText(path), origin);
    }

    /// <summary>The extension of a file of components.</summary>
    public const string ComponentExtension = ".amplec";

    private static readonly System.Text.RegularExpressions.Regex MayHaveComponents = new(@"^(?:component|import|embedded\s+module)\s", System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    /// <summary>The text of a file of components that an <c>import</c> names (the project, then its <c>components/</c>, <c>modules/</c> and <c>templates/</c> folders, then the library), or null.</summary>
    /// <param name="target">The name as written in the import (with or without the extension).</param>
    public string? ReadComponentFile(string target)
    {
        string name = target.EndsWith(ComponentExtension, StringComparison.OrdinalIgnoreCase) ? target : target + ComponentExtension;
        foreach (string candidate in new[] { name, "components/" + name, "modules/" + name, "templates/" + name })
        {
            try
            {
                string full = ProjectPath.Resolve(projectRoot, candidate);
                if (Working(candidate) is { } working) return working;   // the unsaved text of an open file
                if (File.Exists(full)) return File.ReadAllText(full);
            }
            catch (AmpleException)
            {
                return null;
            }
        }

        Ample.Storage.Library.Library? lib = library();
        if (lib is null) return null;
        foreach (string candidate in new[] { name, "components/" + name, "modules/" + name, "templates/" + name })
        {
            Resolution r = lib.Resolve(new AssetRef(candidate), projectRoot, projectId);
            if (r.Status is "found" or "changed" && r.FullPath is not null && r.FullPath.EndsWith(ComponentExtension, StringComparison.OrdinalIgnoreCase)) return File.ReadAllText(r.FullPath);
        }

        return null;
    }

    /// <summary>The components a song can see: its own, and what its imports bring, with the problems of putting them together (conflicts, missing files, cycles).</summary>
    /// <param name="songText">The song text.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The visible components; empty when the song neither declares nor imports any.</returns>
    public ComponentRegistryResult Components(string songText, SpecData spec) =>
        MayHaveComponents.IsMatch(songText) ? ComponentRegistry.Resolve(songText, "song", ReadComponentFile, spec) : new ComponentRegistryResult([], [], []);

    /// <summary>Expands the modules of a song text and the instances of its components.</summary>
    public ExpandedSong Expand(string songText, SpecData spec)
    {
        ComponentRegistryResult registry = Components(songText, spec);
        ExpandedSong plain = ModuleExpander.Expand(songText, spec, Find, registry.Sources);
        ExpandedSong expanded = LatencyCompensation.Apply(plain, spec);
        if (!string.Equals(expanded.Text, plain.Text, StringComparison.Ordinal)) expanded = expanded with { Text = AmpleMigration.RenamePorts(expanded.Text, spec) };   // the compensation writes the internal port name too
        if (registry.Issues.Count == 0) return expanded;
        return expanded with { Diagnostics = [.. expanded.Diagnostics, .. registry.Issues.Select(i => new ModuleDiagnostic(i.Code, i.Message, i.Line, i.File, i.Severity))] };
    }

    /// <summary>The options that make the editors know the components of a song (imported ones included): the host adds them to the options of a parse.</summary>
    /// <param name="songText">The song text.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The components to give <see cref="ParseOptions.Components"/>, and the problems.</returns>
    public (IReadOnlyList<ComponentDefinition> Components, IReadOnlyList<Diagnostic> Issues) ComponentsForEditors(string songText, SpecData spec) => ComponentsForEditors(songText, spec, Components(songText, spec));

    /// <summary>The same, for a project that is not on disk: the files are given by a reader (a Lite project that the browser keeps).</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="read">Reads a project file by its relative path, or gives null.</param>
    /// <returns>The components to give <see cref="ParseOptions.Components"/>, and the problems.</returns>
    public static (IReadOnlyList<ComponentDefinition> Components, IReadOnlyList<Diagnostic> Issues) ComponentsForEditors(string songText, SpecData spec, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(read);
        return ComponentsForEditors(songText, spec, MayHaveComponents.IsMatch(songText) ? ComponentRegistry.Resolve(songText, "song", t => read(t), spec) : new ComponentRegistryResult([], [], []));
    }

    private static (IReadOnlyList<ComponentDefinition> Components, IReadOnlyList<Diagnostic> Issues) ComponentsForEditors(string songText, SpecData spec, ComponentRegistryResult registry)
    {
        string[] lines = songText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int ImportLine(string file) => Math.Max(0, Array.FindIndex(lines, l => l.TrimStart().StartsWith("import", StringComparison.Ordinal) && l.Contains(file, StringComparison.Ordinal)));
        List<Diagnostic> issues = [];
        foreach (RegistryIssue i in registry.Issues)
        {
            // a problem inside an imported file is shown at the import line of the song that brought it
            int line = i.File is null ? i.Line : ImportLine(i.File);
            string message = i.File is null ? i.Message : $"In {i.File}: {i.Message}";
            issues.Add(new Diagnostic(i.Code, i.Severity, line + 1, 1, Math.Max(2, lines.ElementAtOrDefault(line)?.TrimEnd().Length ?? 1) + 1, message, string.Empty, null, null));
        }

        return (registry.Visible, issues);
    }

    /// <summary>
    /// Parses a song for building: every <c>use</c> is expanded into its blocks (see <see cref="ModuleExpander"/>). The diagnostics of the
    /// expanded text are moved to the line of the <c>use</c> they come from, module problems are added, and a module that differs from
    /// the one pinned in <c>ample.lock</c> is a warning (AMP5328). A song without <c>use</c> is parsed as it is.
    /// </summary>
    public AmpleDocument ParseExpanded(string text, SpecData spec)
    {
        // the notes of a note pipeline replace the expression in the blocks the build sees (the text of the song is not touched)
        ExpandedSong expanded = Expand(text, spec);
        if (expanded.Used.Count == 0 && expanded.Diagnostics.Count == 0 && expanded.Components is not { Count: > 0 } && ReferenceEquals(expanded.Text, text))
        {
            return Parser.Parse(text, spec, BuildOptions());
        }

        AmpleDocument x = Parser.Parse(expanded.Text, spec, BuildOptions());
        List<Diagnostic> all = [];
        foreach (Diagnostic d in x.Diagnostics)
        {
            int index = Math.Clamp(d.Line - 1, 0, Math.Max(0, expanded.LineOrigin.Count - 1));
            int origin = expanded.LineOrigin.Count == 0 ? 0 : expanded.LineOrigin[index];
            bool generated = origin != index;
            all.Add(generated ? d with { Line = origin + 1, Col = 1, EndCol = 2, Message = (expanded.Components is { Count: > 0 } ? "In a module or component: " : "In a module: ") + d.Message, Fix = null, Related = null } : d with { Fix = null, Related = null });
        }

        all.AddRange(expanded.Diagnostics.Select(m => new Diagnostic(m.Code, m.Severity, m.Line + 1, 1, 2, m.Message, string.Empty, null, null)));
        all.AddRange(ComponentNotes(expanded));
        string lockFile = System.IO.Path.Combine(projectRoot, Ample.Engine.Library.LockFile.FileName);
        if (File.Exists(lockFile))
        {
            try
            {
                IReadOnlyList<LockEntry> pinned = Ample.Engine.Library.LockFile.Parse(File.ReadAllText(lockFile)).Where(e => e.Section == "module").ToList();
                foreach (ModuleUsage u in expanded.Used)
                {
                    LockEntry? pin = pinned.FirstOrDefault(p => p.Name == u.Name);
                    if (pin is not null && pin.Hash != u.Hash)
                    {
                        all.Add(new Diagnostic("AMP5328", Severity.Warning, 1, 1, 2, $"Module \"{u.Name}\" has changed since it was pinned in ample.lock. Review it, then run 'ample lock update'.", string.Empty, null, null));
                    }
                }
            }
            catch (AmpleException)
            {
                // A damaged lock file is reported by the lock commands.
            }
        }

        return new AmpleDocument
        {
            Text = text,
            Lines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')],
            Project = x.Project,
            Features = x.Features,
            Assist = x.Assist,
            Lint = x.Lint,
            Blocks = x.Blocks,
            Layout = x.Layout,
            Settings = x.Settings,
            SettingsLine = x.SettingsLine,
            Diagnostics = [.. all.Select((d, i) => (d, i)).OrderBy(p => p.d.Line).ThenBy(p => p.d.Col).ThenBy(p => p.i).Select(p => p.d)],
        };
    }

    /// <summary>
    /// The pins a song should get the first time it uses components (requirement R-A: the first use pins, a later change is AMP2203): the whole lock with the missing component entries added, or null when every component is pinned already.
    /// The host writes it (through the command bus, so that it is journaled).
    /// </summary>
    /// <param name="expanded">The expanded song.</param>
    /// <returns>The text of the new lock file, or null when there is nothing to add.</returns>
    public string? LockWithNewComponents(ExpandedSong expanded)
    {
        ArgumentNullException.ThrowIfNull(expanded);
        if (expanded.Components is not { Count: > 0 } used) return null;
        string lockFile = System.IO.Path.Combine(projectRoot, Ample.Engine.Library.LockFile.FileName);
        List<LockEntry> entries = File.Exists(lockFile) ? [.. Ample.Engine.Library.LockFile.Parse(File.ReadAllText(lockFile))] : [];
        List<LockEntry> added = [.. Pins(used.Select(c => new ModuleUsage(ExpandedSong.ComponentPrefix + c.Id, c.Version, c.Semantic, c.Origin, 0))).Where(p => !entries.Any(e => e.Section == "component" && e.Id == p.Id))];
        return added.Count == 0 ? null : Ample.Engine.Library.LockFile.Serialize([.. entries, .. added]);
    }

    /// <summary>What the song should know about the components it used (requirement R-A): a component whose code changed since it was pinned (AMP2203), a seal that no longer matches (AMP2210), an unsealed one (AMP2211) and a deprecated version (AMP2220).</summary>
    private List<Diagnostic> ComponentNotes(ExpandedSong expanded)
    {
        List<Diagnostic> notes = [];
        if (expanded.Components is not { Count: > 0 } used) return notes;
        IReadOnlyList<LockEntry> pinned = [];
        string lockFile = System.IO.Path.Combine(projectRoot, Ample.Engine.Library.LockFile.FileName);
        if (File.Exists(lockFile))
        {
            try
            {
                pinned = [.. Ample.Engine.Library.LockFile.Parse(File.ReadAllText(lockFile)).Where(e => e.Section == "component")];
            }
            catch (AmpleException)
            {
                // A damaged lock file is reported by the lock commands.
            }
        }

        foreach (ComponentUsage u in used)
        {
            int line = u.Line + 1;
            LockEntry? pin = pinned.FirstOrDefault(p => p.Id == $"{u.Id}@{u.Version}");
            if (pin is not null && pin.Hash != u.Semantic)
                notes.Add(new Diagnostic("AMP2203", Severity.Warning, line, 1, 2, $"The component {u.Name} {u.Version} has changed since it was pinned in ample.lock (semantic hash {ComponentHashing.Abbreviate(pin.Hash)} is now {ComponentHashing.Abbreviate(u.Semantic)}): the song may sound different. Review it, then accept the change (ample lock update).", "A sealed version cannot change; this component is not sealed, or its seal is void.", null, null));
            if (u.Signature is not null && u.PublicKey is not null && u.Seal == SealState.Valid && !Ample.Storage.Security.SigningKeys.Verify(u.PublicKey, Encoding.UTF8.GetBytes(u.Payload), u.Signature))
                notes.Add(new Diagnostic("AMP2210", Severity.Warning, line, 1, 2, $"The signature of {u.Name} {u.Version} does not match its content: it was not made by the key it names, or the content was changed.", "Do not trust it; ask the author for a fresh version.", null, null));
            if (u.Seal == SealState.Invalid) notes.Add(new Diagnostic("AMP2210", Severity.Warning, line, 1, 2, $"The seal of {u.Name} {u.Version} does not match: {u.SealReason}", "It counts as unsealed until it is sealed again.", null, null));
            else if (u.Seal == SealState.Unsealed) notes.Add(new Diagnostic("AMP2211", Severity.Info, line, 1, 2, $"The component {u.Name} {u.Version} is not sealed: it builds, but it is a development version and nothing about it is trusted.", "Seal a version to make it immutable.", null, null));
            if (u.Deprecated is not null) notes.Add(new Diagnostic("AMP2220", Severity.Warning, line, 1, 2, $"The component {u.Name} {u.Version} is deprecated" + (u.Deprecated.Length > 0 ? ": " + u.Deprecated : "."), string.Empty, null, null));
        }

        return notes;
    }

    /// <summary>The notes of a midi block (a project MIDI file, one track or all) for a note pipeline: start and length in micro-beats, rounded half up; the tempo map does not matter because beats are quarter notes.</summary>
    public PipeValue? ReadMidiNotes(BlockNode block)
    {
        try
        {
            string? full = Ample.Audio.Engine.BuiltInRenderers.MidiFile(projectRoot, block);
            if (full is null) return null;
            Ample.Audio.Midi.SmfFile file = Ample.Audio.Midi.SmfReader.Parse(Ample.Audio.Engine.BuiltInRenderers.ReadMidiFile(full), Ample.Audio.Midi.SmfLimits.Standard);
            string selected = block.Parameters.FirstOrDefault(p => p.Name == "track")?.Value.Trim('"') ?? "all";
            int[] tracks = selected == "all" || selected.Length == 0 ? [.. Enumerable.Range(0, file.Tracks.Count)] : int.TryParse(selected, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int one) && one < file.Tracks.Count ? [one] : [];
            long ppq = file.TicksPerQuarter;
            List<PipeNote> events = [];
            foreach (int index in tracks)
            {
                foreach (Ample.Audio.Midi.MidiNoteSpan n in Ample.Audio.Midi.MidiNotePairer.Pair(file.Tracks[index], index).Notes)
                {
                    if (events.Count >= 100_000) break;
                    long at = ((n.StartTick * 2_000_000L) + ppq) / (2 * ppq), end = ((n.EndTick * 2_000_000L) + ppq) / (2 * ppq);
                    events.Add(new PipeNote(at, Math.Max(1, end - at), n.Pitch, Math.Clamp(n.Velocity, 1, 127), string.Empty, string.Empty));
                }
            }

            long last = events.Count == 0 ? 0 : events.Max(e => e.At + e.Len);
            return new PipeValue { Events = events, Span = Math.Max(1, (last + NotePipeline.Bar - 1) / NotePipeline.Bar) * NotePipeline.Bar };
        }
        catch (Exception e) when (e is AmpleException or IOException or Ample.Audio.Midi.SmfFormatException)
        {
            return null;
        }
    }

    private ParseOptions BuildOptions() => new()
    {
        Preflight = (blocks, lines) => Ample.Audio.Engine.DrumPreflight.Check(projectRoot, blocks, lines),
        ApplyNoteFlow = true,
        NoteLimit = NotePipeline.DefaultBuildLimit,
        MidiSource = ReadMidiNotes,
        ReadFile = relative =>
        {
            try
            {
                string full = ProjectPath.Resolve(projectRoot, relative);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }
            catch (AmpleException)
            {
                return null;
            }
        },
    };

    /// <summary>The module files of a project's own <c>modules/</c> folder (name and path).</summary>
    public IReadOnlyList<(string Name, string Path)> ProjectModules()
    {
        string folder = System.IO.Path.Combine(projectRoot, "modules");
        return Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => (System.IO.Path.GetRelativePath(folder, f).Replace('\\', '/')[..^Extension.Length], f))]
            : [];
    }

    /// <summary>The modules that songs use, as lock entries (hashed by content).</summary>
    public static IReadOnlyList<LockEntry> Pins(IEnumerable<ModuleUsage> used) =>
        [.. used.GroupBy(u => u.Name).Select(g => g.First()).Select(u => u.Name.StartsWith(ExpandedSong.ComponentPrefix, StringComparison.Ordinal) ? new LockEntry("component", $"{u.Name[ExpandedSong.ComponentPrefix.Length..]}@{u.Version}", u.Name[ExpandedSong.ComponentPrefix.Length..], u.Hash, 0, string.Empty) : new LockEntry("module", $"{u.Name}@{u.Version}", u.Name, u.Hash, u.Size, string.Empty))];

    /// <summary>Checks the pinned modules against the files now: ok, changed (AMP5328) or missing (AMP5320).</summary>
    public IReadOnlyList<LockCheck> VerifyPins(IEnumerable<LockEntry> pins)
    {
        List<LockCheck> checks = [];
        foreach (LockEntry e in pins.Where(p => p.Section == "module"))
        {
            ModuleSource? source = Find(e.Name);
            if (source is null)
            {
                checks.Add(new LockCheck(e, "missing", $"AMP5320: module '{e.Name}' is not in the project, its linked folders or the packs."));
                continue;
            }

            string hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source.Text)));
            checks.Add(hash == e.Hash ? new LockCheck(e, "ok", string.Empty) : new LockCheck(e, "changed", $"AMP5328: module '{e.Name}' has changed since it was pinned. Review it, then run 'ample lock update'."));
        }

        return checks;
    }

    private string? FindPath(string name, out string origin)
    {
        origin = "project";
        foreach (string candidate in new[] { "modules/" + name + Extension, name + Extension })
        {
            try
            {
                string full = ProjectPath.Resolve(projectRoot, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (AmpleException)
            {
                return null;
            }
        }

        Ample.Storage.Library.Library? lib = library();
        if (lib is null)
        {
            return null;
        }

        foreach (string candidate in new[] { "modules/" + name, name })
        {
            Resolution r = lib.Resolve(new AssetRef(candidate), projectRoot, projectId);
            if (r.Status is "found" or "changed" && r.FullPath is not null && r.FullPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                origin = r.Where ?? "library";
                return r.FullPath;
            }
        }

        return null;
    }
}
