namespace Ample.Hub.Core;

/// <summary>
/// What the tray hub may hand to the shell (<c>UseShellExecute</c> opens anything: a web address, a program, a document). The values come from files in the person's
/// AMPLE data folder (the instance records and launch files), which any program of this person can write, so only the expected shapes are opened.
/// </summary>
public static class LaunchPolicy
{
    /// <summary>True for an http address on this machine (127.0.0.1, ::1 or localhost): the studio of an instance. Anything else (file:, another host, a program) is not opened.</summary>
    public static bool IsStudioAddress(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.UserInfo.Length == 0;

    /// <summary>True for an existing folder: opening a path that is a program or a document would run it.</summary>
    public static bool IsFolder(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Directory.Exists(path);
}
