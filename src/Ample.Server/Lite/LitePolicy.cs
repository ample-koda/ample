using System.Text.RegularExpressions;
using Ample.Audio.IO;
using Ample.Language;

namespace Ample.Server.Lite;

/// <summary>A Lite request that is refused, with the status and the code the page shows. The message never holds a path of this machine.</summary>
public sealed class LiteRejectedException(string code, int status, string message) : Exception(message)
{
    /// <summary>Gets the diagnostic code.</summary>
    public string Code { get; } = code;

    /// <summary>Gets the HTTP status.</summary>
    public int Status { get; } = status;

    /// <summary>Gets the seconds after which the same request may work (a limit that was passed), or 0.</summary>
    public int RetryAfterSeconds { get; init; }
}

/// <summary>One file of a Lite project as the page sent it.</summary>
/// <param name="Path">The relative path with <c>/</c>.</param>
/// <param name="Data">The bytes.</param>
public sealed record LiteFile(string Path, byte[] Data);

/// <summary>
/// The capability boundary of Lite (plan/lite/PLAN.md, D8): what a project may contain and what a song may use. The project is untrusted input, also behind the sign-in:
/// paths stay inside the workspace, only known file types, no plugin-backed blocks (AI voices, ACE-Step), no capability beyond the two harmless defaults.
/// </summary>
public static partial class LitePolicy
{
    /// <summary>True for a block type Lite never offers: one that needs a plugin (ACE-Step, voice cloning, timing, any external tool) or is one of the AI blocks of <see cref="ForbiddenBlocks"/>.</summary>
    /// <param name="template">The template.</param>
    /// <returns>True when Lite must not show or run it.</returns>
    public static bool IsForbidden(Ample.Language.TemplateSpec template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Plugin is not null || ForbiddenBlocks.Contains(template.Id);
    }

    /// <summary>The block types that are always forbidden, whatever their template says.</summary>
    public static IReadOnlySet<string> ForbiddenBlocks { get; } = new HashSet<string>(StringComparer.Ordinal) { "ace-music", "ai-voice", "choir", "scorer", "voice-clone", "lyrics", "mood-scene" };

    /// <summary>The file types a project may hold: sources, components, WAV and FLAC audio, SoundFonts, MIDI and a few text kinds.</summary>
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".amplet", ".amplec", ".ampleseq", ".wav", ".flac", ".sf2", ".sf3", ".sfz", ".mid", ".midi", ".txt", ".md", ".json" };

    private static readonly HashSet<string> SoundFonts = new(StringComparer.OrdinalIgnoreCase) { ".sf2", ".sf3", ".sfz" };

    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase) { ".amplet", ".amplec", ".ampleseq", ".txt", ".md", ".json" };

    /// <summary>Whether a file is a text file (a source, a component or a note).</summary>
    /// <param name="path">The path.</param>
    /// <returns>True for text.</returns>
    public static bool IsText(string path) => Sources.Contains(System.IO.Path.GetExtension(path ?? string.Empty));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ \-]*$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex SegmentName();

    [GeneratedRegex(@"capabilities\.\s*(environment|network|filesystem|external-process|external-plugin)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CapabilityGrant();

    [GeneratedRegex(@"[A-Za-z]:[\\/]|\\\\|file:|https?:|(^|[""'\s])/[A-Za-z]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OutsideReference();

    /// <summary>Checks the names, types, sizes and counts of the files before anything is written.</summary>
    /// <param name="entry">The song to build.</param>
    /// <param name="files">The files.</param>
    /// <param name="limits">The limits.</param>
    /// <exception cref="LiteRejectedException">The first thing that is wrong.</exception>
    public static void CheckFiles(string entry, IReadOnlyList<LiteFile> files, LiteLimits limits)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(limits);
        if (files.Count == 0) throw new LiteRejectedException("AMPL901", 400, "The project has no files.");
        if (files.Count > limits.FilesAccepted) throw new LiteRejectedException("AMPL902", 413, $"A Remix project has at most {limits.FilesAccepted} files.");
        long total = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (LiteFile file in files)
        {
            string path = CleanPath(file.Path, limits);
            if (!seen.Add(path)) throw new LiteRejectedException("AMPL903", 400, $"The file \"{path}\" is in the project twice.");
            string ext = System.IO.Path.GetExtension(path);
            if (!Extensions.Contains(ext)) throw new LiteRejectedException("AMPL904", 400, $"\"{path}\": a Remix project can hold {string.Join(", ", Extensions.Order(StringComparer.Ordinal))}.");
            long cap = SoundFonts.Contains(ext) ? limits.MaxSoundFontBytes : Sources.Contains(ext) ? limits.MaxTextBytes : limits.MaxAudioBytes;
            if (file.Data.LongLength > cap) throw new LiteRejectedException("AMPL905", 413, $"\"{path}\" is {file.Data.LongLength / 1024} KB; the most for this kind of file is {cap / 1024} KB.");
            total += file.Data.LongLength;
        }

        if (total > limits.BytesAccepted) throw new LiteRejectedException("AMPL906", 413, $"The project is {total / 1024 / 1024} MB; the most is {limits.BytesAccepted / 1024 / 1024} MB.");
        string wanted = CleanPath(entry, limits);
        if (!string.Equals(System.IO.Path.GetExtension(wanted), ".amplet", StringComparison.OrdinalIgnoreCase)) throw new LiteRejectedException("AMPL907", 400, "The song to build must be a .amplet file.");
        if (!seen.Contains(wanted)) throw new LiteRejectedException("AMPL908", 400, $"The song \"{wanted}\" is not in the project.");
    }

    /// <summary>A relative path in the one form Lite accepts: <c>/</c> as the separator, plain names, no dot folders (that includes <c>.ample</c>), within the depth and name limits.</summary>
    /// <param name="path">The path as sent.</param>
    /// <param name="limits">The limits.</param>
    /// <returns>The path.</returns>
    /// <exception cref="LiteRejectedException">The path is not acceptable.</exception>
    public static string CleanPath(string path, LiteLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        string shown = Shown(path);
        if (string.IsNullOrWhiteSpace(path)) throw new LiteRejectedException("AMPL909", 400, "A file needs a name.");
        if (path.Contains('\\', StringComparison.Ordinal) || path.StartsWith('/') || path.Contains("//", StringComparison.Ordinal)) throw new LiteRejectedException("AMPL909", 400, $"\"{shown}\" is not a plain relative path (use / between names).");
        string[] segments = path.Split('/');
        if (segments.Length > limits.MaxDepth) throw new LiteRejectedException("AMPL909", 400, $"\"{shown}\" is deeper than {limits.MaxDepth} folders.");
        foreach (string segment in segments)
        {
            if (segment is "." or ".." || segment.Length > limits.MaxNameLength || !SegmentName().IsMatch(segment) || segment.EndsWith('.') || segment.EndsWith(' '))
                throw new LiteRejectedException("AMPL909", 400, $"\"{shown}\": names are letters, digits, space, dot, dash and underscore, start with a letter or digit and are at most {limits.MaxNameLength} characters.");
        }

        try
        {
            // the same rule as every project path of AMPLE (reserved names, bad characters, leaving the folder), checked against a dummy root: nothing touches the disk
            _ = ProjectPath.Resolve(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lite-check"), path);
        }
        catch (Ample.Core.Abstractions.AmpleException ex)
        {
            throw new LiteRejectedException("AMPL909", 400, $"\"{shown}\": {ex.Message.Replace(System.IO.Path.GetTempPath(), string.Empty, StringComparison.OrdinalIgnoreCase)}");
        }

        return path;
    }

    /// <summary>Checks the texts and the parsed song: no forbidden block, no capability beyond the defaults, no file parameter that points outside the project.</summary>
    /// <param name="texts">The text files (path, text).</param>
    /// <param name="doc">The parsed song.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="limits">The limits.</param>
    /// <exception cref="LiteRejectedException">The first thing that is not allowed.</exception>
    public static void CheckSong(IReadOnlyDictionary<string, string> texts, AmpleDocument doc, SpecData spec, LiteLimits limits)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(spec);
        foreach ((string path, string text) in texts)
        {
            if (CapabilityGrant().IsMatch(text)) throw new LiteRejectedException("AMPL910", 400, $"\"{path}\" asks for a capability that Remix does not give (environment, network, filesystem, external-process, external-plugin).");
            if (path.EndsWith(".ampleseq", StringComparison.OrdinalIgnoreCase)) { CheckSequence(path, text, limits); continue; }
            if (!path.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".amplec", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("import ", StringComparison.Ordinal) && !trimmed.StartsWith("reexport ", StringComparison.Ordinal)) continue;
                Match quoted = Regex.Match(trimmed, "\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (quoted.Success) CheckReference(quoted.Groups[1].Value, path, limits);
            }
        }

        foreach (BlockNode block in doc.Blocks)
        {
            if (ForbiddenBlocks.Contains(block.Type) || (spec.Templates.TryGetValue(block.Type, out TemplateSpec? needing) && needing.Plugin is not null)) throw new LiteRejectedException("AMPL911", 400, $"The block type \"{block.Type}\" needs a plugin or an AI model: Remix does not have those.");
            if (!spec.Templates.TryGetValue(block.Type, out TemplateSpec? template)) continue;
            foreach (Parameter parameter in block.Parameters)
            {
                bool pathLike = template.ParameterSpecs.TryGetValue(parameter.Name, out TemplateParameterSpec? declared) && declared.Kind is "file" or "sampleset";
                if (pathLike || parameter.Name is "map" or "fallback") CheckReference(parameter.Value, block.Name, limits, allowEmpty: true);
            }
        }
    }

    /// <summary>The one sound bank outside the project that a sequence may name (it is on the server, MIT licensed).</summary>
    public const string SharedBank = "pack:musescore-general";

    /// <summary>A sequence (.ampleseq) names its sounds on its channel lines (<c>file=</c>, <c>sample=</c>, <c>set=</c>): each one is a file of the project, or the shared bank.</summary>
    private static void CheckSequence(string path, string text, LiteLimits limits)
    {
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!line.StartsWith("channel ", StringComparison.Ordinal)) continue;
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = token.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0 || token[..equals] is not ("file" or "sample" or "set")) continue;
                string value = token[(equals + 1)..];
                if (string.Equals(value, SharedBank, StringComparison.Ordinal)) continue;
                if (value.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)) throw new LiteRejectedException("AMPL912", 400, $"\"{Shown(path)}\" names a sound bank that Remix does not have (\"{Shown(value)}\"): the only one is {SharedBank}.");
                CheckReference(value, path, limits);
            }
        }
    }

    /// <summary>A path or reference in a song must stay inside the project.</summary>
    private static void CheckReference(string value, string where, LiteLimits limits, bool allowEmpty = false)
    {
        string text = value.Trim().Trim('"');
        if (text.Length == 0) { if (allowEmpty) return; throw new LiteRejectedException("AMPL912", 400, $"\"{where}\" has an empty file name."); }
        if (OutsideReference().IsMatch(text) || text.Contains("..", StringComparison.Ordinal)) throw new LiteRejectedException("AMPL912", 400, $"\"{Shown(where)}\" points outside the project (\"{Shown(text)}\"): Remix only reads the files of the project.");
        // map entries read voice:folder/sample;voice:folder/sample, others are one path: look at every piece
        foreach (string piece in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string path = piece.Contains(':', StringComparison.Ordinal) && !piece.StartsWith('/') ? piece[(piece.IndexOf(':', StringComparison.Ordinal) + 1)..] : piece;
            if (path.Length == 0) continue;
            try
            {
                _ = ProjectPath.Resolve(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lite-check"), path.Trim());
            }
            catch (Ample.Core.Abstractions.AmpleException)
            {
                throw new LiteRejectedException("AMPL912", 400, $"\"{Shown(where)}\" points outside the project (\"{Shown(piece)}\"): Remix only reads the files of the project.");
            }
        }
    }

    private static string Shown(string text) => text.Length > 80 ? text[..80] + "..." : text;
}
