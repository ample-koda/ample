using System.Text;
using Ample.Audio.Midi;
using Ample.Core.Abstractions;

namespace Ample.Storage.Projects;

/// <summary>One kind of file or folder AMPLE can create in a project (the panel's Add menu, the CLI and the API share this table).</summary>
/// <param name="Id">Kind id (<c>lyrics</c>, <c>midi</c>...).</param>
/// <param name="Label">Name shown in the menu.</param>
/// <param name="Folder">Default project folder (empty for the project root).</param>
/// <param name="Extension">File extension with the dot, empty for a folder.</param>
/// <param name="IsFolder">True when the kind creates a folder.</param>
/// <param name="Open">How the web app opens the new item: <c>song</c>, <c>text</c>, <c>midi</c> or <c>none</c>.</param>
public sealed record NewFileKind(string Id, string Label, string Folder, string Extension, bool IsFolder, string Open);

/// <summary>A new file or folder, ready to be written through the command bus.</summary>
/// <param name="Path">Project-relative path.</param>
/// <param name="Bytes">The content of a file (null for a folder).</param>
/// <param name="IsFolder">True for a folder.</param>
public sealed record NewFile(string Path, byte[]? Bytes, bool IsFolder);

/// <summary>The kinds of file AMPLE can create, with a valid skeleton for each (plan project-panel PP01).</summary>
public static class NewFileKinds
{
    /// <summary>Gets the kinds, in menu order.</summary>
    public static IReadOnlyList<NewFileKind> All { get; } =
    [
        new("song", "Song", "", ".amplet", false, "song"),
        new("lyrics", "Lyrics (timed)", "lyrics", ".lrc", false, "text"),
        new("chords", "Chords (ChordPro)", "chords", ".chordpro", false, "text"),
        new("midi", "MIDI clip (empty)", "assets/midi", ".mid", false, "midi"),
        new("module", "Module", "modules", ".module", false, "text"),
        new("component", "Component (reusable typed block)", "components", ".amplec", false, "text"),
        new("samples", "Sample folder", "samples", string.Empty, true, "none"),
        new("note", "Text note", "notes", ".md", false, "text"),
        new("sequence", "Sequence (for a sequencer block)", "sequences", ".ampleseq", false, "text"),
        new("folder", "Folder", "", string.Empty, true, "none"),
    ];

    /// <summary>Finds a kind by id.</summary>
    public static NewFileKind? Find(string id) => All.FirstOrDefault(k => k.Id == id);

    /// <summary>Checks a name: letters, digits, <c>_</c>, <c>-</c> and spaces inside, 1 to 80 characters; with <paramref name="allowParentheses"/> also <c>(</c> and <c>)</c> (not for songs and modules, whose names are used as identifiers in the song text).</summary>
    public static bool ValidName(string name, bool allowParentheses = false) => name.Length is >= 1 and <= 80 && name == name.Trim() && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or ' ' || (allowParentheses && ch is '(' or ')')) && name.Any(char.IsAsciiLetterOrDigit);

    /// <summary>Works out the path and content of a new item. <paramref name="folder"/> overrides the default folder of the kind (project-relative, null for the default).</summary>
    /// <exception cref="AmpleException">AMP5285 when the kind or the name is not allowed.</exception>
    public static NewFile Build(string kindId, string name, string? folder, double tempo = 100, ulong masterSeed = 0)
    {
        NewFileKind kind = Find(kindId) ?? throw new AmpleException("AMP5285", $"There is no kind of file called '{kindId}'. The kinds are: {string.Join(", ", All.Select(k => k.Id))}.");
        name = name.Trim();
        bool isIdentifier = kind.Id is "song" or "module";
        if (!ValidName(name, !isIdentifier)) throw new AmpleException("AMP5285", isIdentifier ? "A name is letters, digits, spaces, _ and - only (at most 80 characters)." : "A name is letters, digits, spaces, _, - and ( ) only (at most 80 characters).");
        string directory = (folder ?? kind.Folder).Replace('\\', '/').Trim('/');
        string slug = name.Replace(' ', '-');
        // songs and modules are referred to by their file name, so it has no spaces
        string fileName = kind.Id is "song" or "module" ? slug : name;
        string path = (directory.Length == 0 ? fileName : directory + "/" + fileName) + kind.Extension;
        if (kind.IsFolder) return new NewFile(path, null, true);
        string text = kind.Id switch
        {
            "song" => $"composition \"{slug}\" {{ ample 1  tempo {tempo.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}  master-seed {masterSeed.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}\n",
            "lyrics" => $"[ti:{name}]\n",
            "chords" => $"{{title: {name}}}\n{{key: C}}\n{{tempo: 100}}\n{{start_of_verse}}\n[C]\n{{end_of_verse}}\n",
            "module" => $"module {slug} version 1.0\nparam kit kit/tape\noutput audio drums.audio\n\ndrums drums {{\n  kit $kit\n  pattern beat-A\n}}\n",
            "note" => $"# {name}\n\n",
            "sequence" => "sequencer 1\nchannel lead notes wave=square\nchannel kick drum sample=samples/kick.wav\npattern a bars=2\n  lead 1:0:1:60:96 1:1:1:64:96 1:2:1:67:96 1:3:1:64:96\n  kick x...x...x...x...\nplaylist\n  clip a track=1 at=1 for=2\n",
            "midi" => string.Empty,
            _ => string.Empty,
        };
        if (kind.Id == "midi")
        {
            SmfFile file = new(1, 480, [new SmfTrack([new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])])]);
            return new NewFile(path, SmfWriter.Write(file, SmfLimits.Standard), false);
        }

        return new NewFile(path, new UTF8Encoding(false).GetBytes(text), false);
    }
}
