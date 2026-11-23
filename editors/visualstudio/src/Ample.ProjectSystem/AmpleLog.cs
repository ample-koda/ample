namespace Ample.VisualStudio;

/// <summary>A small log of what the extension does (%TEMP%\ample-vs-ext.log), so a step that does nothing visible can still be traced.</summary>
internal static class AmpleLog
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ample-vs-ext.log");

    public static void Write(string message)
    {
        try { System.IO.File.AppendAllText(Path, $"{System.DateTime.Now:O} {message}{System.Environment.NewLine}"); }
        catch (System.IO.IOException) { }
        catch (System.UnauthorizedAccessException) { }
    }
}
