using System.Diagnostics;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;

namespace Ample.Server;

/// <summary>
/// Turns a notation file MuseScore can read, but AMPLE cannot, into MusicXML with MuseScore itself (an external, optionally installed tool - not part of
/// AMPLE), used by <c>ample_import_score</c>. Unlike <see cref="ScoreOmr"/> this is a machine-readable conversion (MuseScore reads the file's own
/// structured note data), not recognition of printed music, so its result is as reliable as the source file.
/// </summary>
internal static class ScoreConvert
{
    internal static readonly string[] ConvertibleExtensions = [".mscz", ".mscx", ".gp", ".gp3", ".gp4", ".gp5", ".gpx", ".ptb", ".ove", ".cap", ".capx", ".bww"];

    /// <summary>The MuseScore executable: AMPLE_MUSESCORE, or "MuseScore4" on the PATH.</summary>
    internal static string Exe => Environment.GetEnvironmentVariable("AMPLE_MUSESCORE") is { Length: > 0 } configured ? configured : "MuseScore4";

    /// <summary>Converts <paramref name="source"/> to MusicXML with MuseScore and returns the text. Scratch files are written under and removed from <paramref name="scratchRoot"/>.</summary>
    internal static string Convert(string source, string scratchRoot, string projectRoot)
    {
        string scratch = Path.Combine(scratchRoot, "convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string output = Path.Combine(scratch, Path.GetFileNameWithoutExtension(source) + ".musicxml");
            ProcessStartInfo info = new(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { source, "-o", output }) info.ArgumentList.Add(a);

            ExternalProcessResult result;
            try
            {
                result = ExternalProcess.Run(info, 120_000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw Missing(projectRoot);
            }

            if (!result.Exited)
            {
                throw new AmpleException("AMP5465", "MuseScore did not finish converting the score within 2 minutes.");
            }

            if (result.ExitCode != 0 || !File.Exists(output))
            {
                throw new AmpleException("AMP5465", "MuseScore could not convert this file to MusicXML" + (result.Stderr.Length == 0 ? "." : ": " + result.Stderr[..Math.Min(result.Stderr.Length, 500)]));
            }

            return File.ReadAllText(output);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static AmpleException Missing(string projectRoot)
    {
        const string what = "Converting a MuseScore/Guitar Pro/Capella/Overture file to MusicXML";
        const string hint = "MuseScore (musescore.org) is not installed or not on the PATH. Install it and set AMPLE_MUSESCORE to MuseScore4.exe (a portable extraction works: its .paf.exe accepts /S), or put it on the PATH. A score that is already MIDI or MusicXML needs no conversion.";
        ProjectLog.Append(projectRoot, "missing", what, hint);
        return new AmpleException("AMP5465", hint);
    }
}
