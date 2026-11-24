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

    /// <summary>
    /// True for a recipe that looks like the one <c>ample serve</c> writes: an existing program called ample (or Ample.Cli), given as a full path, started with <c>serve</c>.
    /// A record file that names any other program is not started. (A copy of ample somewhere else still passes: the record file is as trusted as the person's own folder.)
    /// </summary>
    public static bool IsRelaunchRecipe(string? exe, IReadOnlyList<string>? args)
    {
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathRooted(exe) || args is null || args.Count == 0 || args[0] != "serve") return false;
        string leaf = Path.GetFileName(exe);
        bool named = leaf.Equals("ample", StringComparison.OrdinalIgnoreCase) || leaf.Equals("ample.exe", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Ample.Cli", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Ample.Cli.exe", StringComparison.OrdinalIgnoreCase);
        return named && File.Exists(exe);
    }

    /// <summary>True for an existing folder: opening a path that is a program or a document would run it.</summary>
    public static bool IsFolder(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Directory.Exists(path);
}
