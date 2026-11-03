using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Audio.IO;

namespace Ample.Storage.Projects;

/// <summary>One finding of the project health check.</summary>
/// <param name="Code">Stable AMP52xx code.</param>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>info</c>.</param>
/// <param name="Message">What was found.</param>
/// <param name="Repair">What would repair it, or null when nothing can be repaired automatically.</param>
public sealed record DoctorFinding(string Code, string Severity, string Message, string? Repair);

/// <summary>Checks a project folder without changing it: structure, lock state, format version, leftovers and free disk space.</summary>
public static class ProjectDoctor
{
    /// <summary>Runs the checks.</summary>
    public static async ValueTask<IReadOnlyList<DoctorFinding>> CheckAsync(IFileSystem fs, IClock clock, string root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(clock);
        List<DoctorFinding> findings = [];
        string ample = Path.Combine(root, ProjectLayout.AmpleFolder);
        if (!fs.DirectoryExists(ample))
        {
            return [new DoctorFinding("AMP5210", "error", $"'{root}' is not an AMPLE project (no {ProjectLayout.AmpleFolder} folder).", null)];
        }

        string projectFile = Path.Combine(ample, ProjectLayout.ProjectFile);
        ProjectManifest? manifest = null;
        if (!fs.FileExists(projectFile))
        {
            findings.Add(new DoctorFinding("AMP5210", "error", $"{ProjectLayout.AmpleFolder}/{ProjectLayout.ProjectFile} is missing.", null));
        }
        else
        {
            string json = await fs.ReadAllTextAsync(projectFile, cancellationToken).ConfigureAwait(false);
            try
            {
                int version = ProjectFile.ReadFormatVersion(json);
                if (version < ProjectLayout.CurrentFormatVersion)
                {
                    findings.Add(new DoctorFinding("AMP5209", "warning", $"The project uses the older format {version}; it is upgraded (with a backup copy) the next time it is opened for writing.", null));
                }
                else
                {
                    manifest = ProjectFile.Parse(json);
                }
            }
            catch (AmpleException ex)
            {
                findings.Add(new DoctorFinding(ex.DiagnosticCode, "error", ex.Message, null));
            }
        }

        foreach (string folder in ProjectLayout.RequiredFolders.Where(f => !fs.DirectoryExists(Path.Combine(ample, f))))
        {
            findings.Add(new DoctorFinding("AMP5211", "warning", $"The folder {ProjectLayout.AmpleFolder}/{folder} is missing.", $"create {ProjectLayout.AmpleFolder}/{folder}"));
        }

        foreach (string entry in fs.GetDirectories(ample).Concat(fs.GetFiles(ample)).Select(Path.GetFileName).OfType<string>().Where(n => !ProjectLayout.KnownEntries.Contains(n)).Order(StringComparer.Ordinal))
        {
            findings.Add(new DoctorFinding("AMP5212", "info", $"{ProjectLayout.AmpleFolder}/{entry} is not something AMPLE creates.", null));
        }

        foreach (string dir in new[] { root, ample }.Concat(ProjectLayout.RequiredFolders.Select(f => Path.Combine(ample, f))).Where(fs.DirectoryExists))
        {
            foreach (string file in fs.GetFiles(dir).Where(f => f.EndsWith(AtomicFile.TempSuffix, StringComparison.Ordinal)))
            {
                findings.Add(new DoctorFinding("AMP5213", "warning", $"Leftover temporary file {Path.GetRelativePath(root, file)} (an interrupted write).", $"delete {Path.GetRelativePath(root, file)}"));
            }
        }

        string lockPath = Path.Combine(ample, "locks", "instance.lock");
        if (fs.FileExists(lockPath))
        {
            LockInfo? info = await InstanceLock.ReadInfoAsync(fs, root, cancellationToken).ConfigureAwait(false);
            using IDisposable? probe = fs.TryLockFile(lockPath);
            if (probe is null)
            {
                findings.Add(new DoctorFinding("AMP5214", "info", info is null ? "The project is open in another AMPLE instance." : $"The project is open in process {info.ProcessId.ToString(CultureInfo.InvariantCulture)} on {info.Machine} (last heartbeat {info.Updated:u}).", null));
            }
            else if (info is not null)
            {
                findings.Add(new DoctorFinding("AMP5215", "info", $"Nobody holds the project lock; the last holder (process {info.ProcessId.ToString(CultureInfo.InvariantCulture)} on {info.Machine}) stopped at {info.Updated:u}. The lock is free to take.", null));
            }
        }

        if (manifest is not null)
        {
            long free = fs.GetFreeSpace(root);
            if (free < manifest.Settings.BlackBox.BudgetBytes)
            {
                findings.Add(new DoctorFinding("AMP5216", "warning", $"Only {free / (1024 * 1024 * 1024)} GiB are free on this volume, less than the black box budget of {manifest.Settings.BlackBox.BudgetBytes / (1024 * 1024 * 1024)} GiB.", null));
            }
        }

        return findings;
    }
}
