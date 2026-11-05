using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>
/// The browser of the assistant (Koda) is an optional pack, <c>koda-browser</c>, not a part of the program: the Playwright driver (a Node program) is large,
/// and Koda does not need a browser until a task needs interaction on the web. <c>ample packs install koda-browser</c> puts the driver in
/// <c>&lt;home&gt;/koda-browser/.playwright</c>; this class points Playwright at it, or says plainly that there is no browser.
/// </summary>
internal static class KodaBrowserPack
{
    internal const string FolderName = "koda-browser";
    internal const string DriverSearchPathVariable = "PLAYWRIGHT_DRIVER_SEARCH_PATH";

    /// <summary>The folder of the pack inside the user's AMPLE home.</summary>
    internal static string FolderOf(string home) => Path.Combine(home, FolderName);

    /// <summary>True when the pack is complete enough to start the driver (the Playwright package folder is there).</summary>
    internal static bool Installed(string home) => File.Exists(Path.Combine(FolderOf(home), ".playwright", "package", "package.json"));

    /// <summary>
    /// Decides where Playwright finds its driver. Returns null when nothing has to be set: a driver next to the program (a development build, the tests)
    /// or a folder that <c>PLAYWRIGHT_DRIVER_SEARCH_PATH</c> already names is left alone. Otherwise it returns the folder of the installed pack.
    /// Pure: it changes nothing, so it can be tested without touching the environment of the process.
    /// </summary>
    /// <exception cref="AmpleException">AMP5458 when there is no driver anywhere.</exception>
    internal static string? DriverSearchPathFor(string home, string programFolder, string? currentSearchPath)
    {
        if (Directory.Exists(Path.Combine(programFolder, ".playwright"))) return null;
        if (!string.IsNullOrEmpty(currentSearchPath)) return null;
        if (!Installed(home)) throw new AmpleException("AMP5458", "Koda has no browser on this computer. Install it with: ample packs install koda-browser");
        return FolderOf(home);
    }

    /// <summary>Points Playwright at the pack when it is needed, or throws that Koda has no browser.</summary>
    internal static void PrepareOrThrow(string home)
    {
        string? path = DriverSearchPathFor(home, AppContext.BaseDirectory, Environment.GetEnvironmentVariable(DriverSearchPathVariable));
        if (path is not null) Environment.SetEnvironmentVariable(DriverSearchPathVariable, path);
    }
}
