#pragma warning disable VSSDK007
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace Ample.VisualStudio;

/// <summary>
/// The colours of the AMPLE editor for Visual Studio: one set for a dark and one for a light theme (the palette of the Studio's
/// editor), each kind of token a classification that the person can still change in Tools > Options > Environment > Fonts and Colors.
/// The person can switch the palette off: then Visual Studio's own colours (from the TextMate grammar) show.
/// </summary>
internal static class AmplePalette
{
    internal const string Comment = "ample.comment";
    internal const string Str = "ample.string";
    internal const string Number = "ample.number";
    internal const string Keyword = "ample.keyword";
    internal const string Directive = "ample.directive";
    internal const string BlockType = "ample.blocktype";
    internal const string Declaration = "ample.declaration";
    internal const string Reference = "ample.reference";
    internal const string Property = "ample.property";
    internal const string Operator = "ample.operator";
    internal const string Punctuation = "ample.punctuation";
    internal const string Version = "ample.version";

    internal static readonly string[] All =
    [
        Comment, Str, Number, Keyword, Directive, BlockType, Declaration, Reference, Property, Operator, Punctuation, Version,
    ];

    private static readonly string SettingFile = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Ample", "vs-palette.txt");

    /// <summary>Raised when the person switches the palette, so open editors paint again.</summary>
    internal static event System.EventHandler? Changed;

    /// <summary>Whether the AMPLE palette is on (the default); off means Visual Studio's own colours.</summary>
    internal static bool Enabled
    {
        get
        {
            try { return !System.IO.File.Exists(SettingFile) || System.IO.File.ReadAllText(SettingFile).Trim() != "vs"; }
            catch (System.IO.IOException) { return true; }
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingFile)!);
            System.IO.File.WriteAllText(SettingFile, enabled ? "ample" : "vs");
        }
        catch (System.IO.IOException)
        {
            return;
        }

        Changed?.Invoke(null, System.EventArgs.Empty);
    }

    /// <summary>Whether the theme of Visual Studio is dark (the background of tool windows is dark).</summary>
    internal static bool IsDarkTheme()
    {
        System.Drawing.Color back = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        return (back.R * 299 + back.G * 587 + back.B * 114) / 1000 < 128;
    }

    /// <summary>The colour of one kind of token in the current theme (the values of the Studio's editor theme: data, audio, studio, midi, dim, accent, warn).</summary>
    internal static Color ColorOf(string kind, bool dark) => kind switch
    {
        Keyword or Directive => dark ? Rgb(0xc0, 0x8c, 0xff) : Rgb(0x70, 0x40, 0xa0),                  // --data
        BlockType or Property => dark ? Rgb(0x5f, 0xb3, 0xff) : Rgb(0x1c, 0x75, 0xd6),                 // --audio
        Declaration or Number => dark ? Rgb(0xf5, 0xc5, 0x42) : Rgb(0xb8, 0x86, 0x0b),                 // --studio
        Str or Reference => dark ? Rgb(0x7e, 0xd5, 0x6f) : Rgb(0x1f, 0x9d, 0x4d),                      // --midi
        Operator => dark ? Rgb(0x5f, 0xb3, 0xff) : Rgb(0x1c, 0x75, 0xd6),                              // --acc
        Version => dark ? Rgb(0xf0, 0xc8, 0x69) : Rgb(0x9a, 0x5a, 0x00),                                // --warn
        _ => dark ? Rgb(0x8a, 0x97, 0xa4) : Rgb(0x59, 0x62, 0x6c),                                     // --dim: comments and punctuation
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

/// <summary>The base of the colour definitions: takes the colour of its kind for the current theme and follows theme changes.</summary>
internal abstract class AmpleFormat : ClassificationFormatDefinition
{
    private readonly string kind;

    protected AmpleFormat(string kind, string displayName, bool bold = false, bool italic = false)
    {
        this.kind = kind;
        DisplayName = displayName;
        IsBold = bold;
        IsItalic = italic;
        // never ask the theme while this part is being composed (that can run on a background thread while the UI thread waits for it):
        // start with the dark colours, then take the real theme on the UI thread
        ForegroundColor = AmplePalette.ColorOf(kind, true);
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Apply();
            VSColorTheme.ThemeChanged += _ => Apply();
        }).FileAndForget("ample/palette");
    }

    private void Apply()
    {
        ForegroundColor = AmplePalette.ColorOf(kind, AmplePalette.IsDarkTheme());
        AmpleLog.Write($"palette: {kind} = {ForegroundColor}");
    }
}

internal static class AmpleClassificationTypes
{
    [Export] [Name(AmplePalette.Comment)] internal static ClassificationTypeDefinition CommentType = null!;
    [Export] [Name(AmplePalette.Str)] internal static ClassificationTypeDefinition StringType = null!;
    [Export] [Name(AmplePalette.Number)] internal static ClassificationTypeDefinition NumberType = null!;
    [Export] [Name(AmplePalette.Keyword)] internal static ClassificationTypeDefinition KeywordType = null!;
    [Export] [Name(AmplePalette.Directive)] internal static ClassificationTypeDefinition DirectiveType = null!;
    [Export] [Name(AmplePalette.BlockType)] internal static ClassificationTypeDefinition BlockTypeType = null!;
    [Export] [Name(AmplePalette.Declaration)] internal static ClassificationTypeDefinition DeclarationType = null!;
    [Export] [Name(AmplePalette.Reference)] internal static ClassificationTypeDefinition ReferenceType = null!;
    [Export] [Name(AmplePalette.Property)] internal static ClassificationTypeDefinition PropertyType = null!;
    [Export] [Name(AmplePalette.Operator)] internal static ClassificationTypeDefinition OperatorType = null!;
    [Export] [Name(AmplePalette.Punctuation)] internal static ClassificationTypeDefinition PunctuationType = null!;
    [Export] [Name(AmplePalette.Version)] internal static ClassificationTypeDefinition VersionType = null!;
}

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Comment)] [Name("AMPLE comment")] [UserVisible(true)]
internal sealed class AmpleCommentFormat() : AmpleFormat(AmplePalette.Comment, "AMPLE comment", italic: true);

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Str)] [Name("AMPLE string")] [UserVisible(true)]
internal sealed class AmpleStringFormat() : AmpleFormat(AmplePalette.Str, "AMPLE string");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Number)] [Name("AMPLE number")] [UserVisible(true)]
internal sealed class AmpleNumberFormat() : AmpleFormat(AmplePalette.Number, "AMPLE number");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Keyword)] [Name("AMPLE keyword")] [UserVisible(true)]
internal sealed class AmpleKeywordFormat() : AmpleFormat(AmplePalette.Keyword, "AMPLE keyword");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Directive)] [Name("AMPLE directive")] [UserVisible(true)]
internal sealed class AmpleDirectiveFormat() : AmpleFormat(AmplePalette.Directive, "AMPLE directive", bold: true);

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.BlockType)] [Name("AMPLE block type")] [UserVisible(true)]
internal sealed class AmpleBlockTypeFormat() : AmpleFormat(AmplePalette.BlockType, "AMPLE block type", bold: true);

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Declaration)] [Name("AMPLE block name")] [UserVisible(true)]
internal sealed class AmpleDeclarationFormat() : AmpleFormat(AmplePalette.Declaration, "AMPLE block name");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Reference)] [Name("AMPLE reference")] [UserVisible(true)]
internal sealed class AmpleReferenceFormat() : AmpleFormat(AmplePalette.Reference, "AMPLE reference");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Property)] [Name("AMPLE parameter")] [UserVisible(true)]
internal sealed class AmplePropertyFormat() : AmpleFormat(AmplePalette.Property, "AMPLE parameter");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Operator)] [Name("AMPLE operator")] [UserVisible(true)]
internal sealed class AmpleOperatorFormat() : AmpleFormat(AmplePalette.Operator, "AMPLE operator");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Punctuation)] [Name("AMPLE punctuation")] [UserVisible(true)]
internal sealed class AmplePunctuationFormat() : AmpleFormat(AmplePalette.Punctuation, "AMPLE punctuation");

[Export(typeof(EditorFormatDefinition))] [ClassificationType(ClassificationTypeNames = AmplePalette.Version)] [Name("AMPLE version")] [UserVisible(true)]
internal sealed class AmpleVersionFormat() : AmpleFormat(AmplePalette.Version, "AMPLE version");
