namespace Ample.Audio.IO;

/// <summary>
/// Plain file writes for the few places that are not project state: the marker file inside a catalog folder the user chose to
/// make writable, and the provenance file of an import (which already runs inside a journaled command). Everything that
/// changes a project goes through the command bus instead.
/// </summary>
internal static class RawFiles
{
    public static void WriteText(string path, string text) => File.WriteAllText(path, text);

    public static Task WriteTextAsync(string path, string text, CancellationToken cancellationToken) => File.WriteAllTextAsync(path, text, cancellationToken);
}
