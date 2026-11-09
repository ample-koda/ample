using System.Diagnostics;
using System.IO.Compression;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;

namespace Ample.Server;

/// <summary>
/// Turns a PDF or image of printed music into MusicXML text with Audiveris (optical music recognition), an external, optionally installed tool - not part of
/// AMPLE itself. Used by <c>ample_import_score</c> when the source is not already MIDI or MusicXML. Audiveris's own recognition accuracy is not AMPLE's: a
/// scanned or low-quality source can come out wrong or empty, so the caller still sees the usual import warnings and note count to judge the result.
/// </summary>
internal static class ScoreOmr
{
    internal static readonly string[] RecognizableExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".webp", ".tif", ".tiff"];

    /// <summary>The Audiveris executable: AMPLE_AUDIVERIS, else the one that <c>ample packs install audiveris</c> put in the AMPLE folder, else "Audiveris" on the PATH.</summary>
    internal static string Exe => Environment.GetEnvironmentVariable("AMPLE_AUDIVERIS") is { Length: > 0 } configured ? configured : AudiverisLocator.FindExe() ?? "Audiveris";

    /// <summary>Runs Audiveris on <paramref name="source"/> and returns the MusicXML text it recognized. Scratch files are written under and removed from <paramref name="scratchRoot"/>.</summary>
    internal static string Recognize(string source, string scratchRoot, string projectRoot)
    {
        string scratch = Path.Combine(scratchRoot, "omr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            ProcessStartInfo info = new(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "-batch", "-export", "-output", scratch, source }) info.ArgumentList.Add(a);

            ExternalProcessResult result;
            try
            {
                result = ExternalProcess.Run(info, 180_000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw Missing(projectRoot);
            }

            if (!result.Exited)
            {
                throw new AmpleException("AMP5464", "Audiveris did not finish recognizing the score within 3 minutes. A long or very dense score may need more time; try a shorter excerpt.");
            }

            if (result.ExitCode != 0)
            {
                throw new AmpleException("AMP5464", "Audiveris could not recognize the score" + (result.Stderr.Length == 0 ? "." : ": " + result.Stderr[..Math.Min(result.Stderr.Length, 500)]));
            }

            string? mxl = Directory.EnumerateFiles(scratch, "*.mxl", SearchOption.AllDirectories).FirstOrDefault();
            if (mxl is null)
            {
                throw new AmpleException("AMP5464", "Audiveris did not produce a MusicXML file for this score; it may not have recognized any music in it.");
            }

            using ZipArchive zip = ZipFile.OpenRead(mxl);
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new AmpleException("AMP5464", "Audiveris's output did not contain a readable MusicXML part.");
            }

            using StreamReader reader = new(entry.Open());
            return reader.ReadToEnd();
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static AmpleException Missing(string projectRoot)
    {
        const string what = "Recognizing music notation in a PDF or image (Audiveris)";
        const string hint = "Audiveris (optical music recognition, audiveris.org) is not installed or not on the PATH. Install it (a per-user extraction of its Windows installer works without admin rights: run its .msi with msiexec /a) and set AMPLE_AUDIVERIS to Audiveris.exe, or put it on the PATH. A score that is already MIDI or MusicXML needs no OMR.";
        ProjectLog.Append(projectRoot, "missing", what, hint);
        return new AmpleException("AMP5464", hint);
    }
}
