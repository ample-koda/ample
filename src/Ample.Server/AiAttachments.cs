using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>A file the user attached to a message to Koda or the assistant: kept by the server, handed to the working copy of a task.</summary>
/// <param name="Id">The id (32 hex digits) that the message carries.</param>
/// <param name="Name">The clean file name.</param>
/// <param name="Path">Where the server keeps it.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="IsImage">True for a picture the model can look at (png, jpeg, gif, webp).</param>
internal sealed record AiAttachment(string Id, string Name, string Path, long Size, bool IsImage);

/// <summary>
/// The files that are attached to messages (a screenshot of notes, a score, a text): one folder per file in <c>.ample/ai/attachments/&lt;id&gt;/</c>, private to the project, at most 40 MB each (a scanned score),
/// and cleaned up after a week. Pictures reach the model as pictures; every other file is copied into the working copy of the task and named in the message.
/// </summary>
internal sealed partial class AiAttachments(string folder)
{
    /// <summary>The largest file in bytes.</summary>
    public const long MaxBytes = 40 * 1024 * 1024;

    /// <summary>The most files with one message.</summary>
    public const int MaxPerMessage = 8;

    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh", ".msi", ".com", ".scr", ".vbs", ".js", ".jar", ".lnk", ".app" };

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdPattern();

    /// <summary>A file name that is safe to keep: no folders, no odd characters, at most 80 characters, and a name when there was none.</summary>
    public static string CleanName(string? name)
    {
        string leaf = System.IO.Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        char[] bad = [.. System.IO.Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '|', '?', '*'];
        string clean = new([.. leaf.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c)]);
        clean = clean.Trim().Trim('.');
        if (clean.Length == 0) clean = "attachment";
        if (clean.Length > 80) clean = clean[..60] + System.IO.Path.GetExtension(clean)[..Math.Min(System.IO.Path.GetExtension(clean).Length, 20)];
        return clean;
    }

    /// <summary>Whether a file name is a picture the model can look at.</summary>
    public static bool IsPicture(string name) => Pictures.Contains(System.IO.Path.GetExtension(name));

    /// <summary>Keeps a file and says how to refer to it.</summary>
    /// <exception cref="AmpleException">AMP5285 when it is empty, larger than 10 MB or a program.</exception>
    public AiAttachment Save(string? name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string clean = CleanName(name);
        if (data.Length == 0) throw new AmpleException("AMP5285", "The attached file is empty.");
        if (data.Length > MaxBytes) throw new AmpleException("AMP5285", $"The attached file is larger than {MaxBytes / 1024 / 1024} MB.");
        if (Refused.Contains(System.IO.Path.GetExtension(clean))) throw new AmpleException("AMP5285", $"A program or script ({System.IO.Path.GetExtension(clean)}) cannot be attached.");
        Prune();
        string id = Guid.NewGuid().ToString("N");
        string dir = System.IO.Path.Combine(folder, id);
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, clean);
        File.WriteAllBytes(path, data);
        return new AiAttachment(id, clean, path, data.Length, IsPicture(clean));
    }

    /// <summary>
    /// Turns the first pages of a PDF (sheet music, a scan) into pictures that the model can look at: <c>stem-page-1.png</c> ... in the folder, 110 dpi, at most <paramref name="maxPages"/>.
    /// Uses pdftoppm (poppler), else mutool, else Ghostscript, whichever the machine has; an empty list when none of them is there or the file cannot be drawn (never an error).
    /// </summary>
    public static IReadOnlyList<string> RenderPdf(string pdf, string folder, int maxPages = 120, int dpi = 110)
    {
        Directory.CreateDirectory(folder);
        string stem = System.IO.Path.GetFileNameWithoutExtension(pdf);
        string prefix = System.IO.Path.Combine(folder, stem + "-page");
        string last = maxPages.ToString(System.Globalization.CultureInfo.InvariantCulture), res = dpi.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (string Exe, string[] Args)[] tries =
        [
            ("pdftoppm", ["-r", res, "-png", "-f", "1", "-l", last, pdf, prefix]),
            ("mutool", ["draw", "-r", res, "-o", prefix + "-%d.png", pdf, "1-" + last]),
            ("gs", ["-q", "-dNOPAUSE", "-dBATCH", "-sDEVICE=png16m", "-r" + res, "-dFirstPage=1", "-dLastPage=" + last, "-sOutputFile=" + prefix + "-%d.png", pdf]),
        ];
        foreach ((string exe, string[] args) in tries)
        {
            Run(exe, args);
            string[] pages = [.. Directory.GetFiles(folder, stem + "-page-*.png").OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)];
            if (pages.Length > 0)
            {
                return pages.Take(maxPages).ToList();
            }
        }

        return [];
    }

    private static void Run(string exe, string[] args)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo start = new(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args)
            {
                start.ArgumentList.Add(a);
            }

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return;
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(true);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the program is not on this machine: the next one is tried
        }
        catch (InvalidOperationException)
        {
            // it ended or never started
        }
    }

    /// <summary>The attachment with this id, or null (an unknown or damaged id is never an error: the message just goes without it).</summary>
    public AiAttachment? Find(string? id)
    {
        if (id is null || !IdPattern().IsMatch(id)) return null;
        string dir = System.IO.Path.Combine(folder, id);
        if (!Directory.Exists(dir)) return null;
        string? file = Directory.EnumerateFiles(dir).FirstOrDefault();
        if (file is null) return null;
        string name = System.IO.Path.GetFileName(file);
        return new AiAttachment(id, name, file, new FileInfo(file).Length, IsPicture(name));
    }

    /// <summary>Removes what is older than a week (this folder only holds what the server itself made).</summary>
    private void Prune()
    {
        if (!Directory.Exists(folder)) return;
        DateTime limit = DateTime.UtcNow.AddDays(-7);
        foreach (string dir in Directory.EnumerateDirectories(folder))
        {
            try
            {
                if (IdPattern().IsMatch(System.IO.Path.GetFileName(dir)) && Directory.GetCreationTimeUtc(dir) < limit) Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // in use: the next time
            }
        }
    }
}
