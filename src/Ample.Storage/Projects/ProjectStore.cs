using Ample.Core.Abstractions;
using Ample.Audio.IO;

namespace Ample.Storage.Projects;

/// <summary>An open project. Disposing releases the writer lock.</summary>
public sealed class OpenProject : IDisposable
{
    private readonly InstanceLock? writerLock;

    internal OpenProject(string root, ProjectManifest manifest, InstanceLock? writerLock, bool migrated)
    {
        Root = root;
        Manifest = manifest;
        this.writerLock = writerLock;
        Migrated = migrated;
    }

    /// <summary>Gets the project folder.</summary>
    public string Root { get; }

    /// <summary>Gets the manifest.</summary>
    public ProjectManifest Manifest { get; }

    /// <summary>Gets a value indicating whether the project was opened without the writer lock.</summary>
    public bool IsReadOnly => writerLock is null;

    /// <summary>Gets a value indicating whether opening upgraded the project format (a backup copy was made first).</summary>
    public bool Migrated { get; }

    /// <summary>Gets the <c>.ample</c> folder.</summary>
    public string AmplePath => Path.Combine(Root, ProjectLayout.AmpleFolder);

    /// <summary>Gets a path inside <c>.ample</c>.</summary>
    public string InAmple(params string[] parts) => Path.Combine([AmplePath, .. parts]);

    /// <summary>Resolves a user path inside the project, refusing escapes.</summary>
    public string Resolve(string relative) => ProjectPath.Resolve(Root, relative);

    /// <summary>Writes a fresh heartbeat (writers only).</summary>
    public ValueTask TouchAsync(CancellationToken cancellationToken = default) => writerLock?.TouchAsync(cancellationToken) ?? ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => writerLock?.Dispose();
}

/// <summary>Creates, opens and validates projects.</summary>
public static class ProjectStore
{
    /// <summary>Creates a project folder with the mandatory <c>.ample</c> structure.</summary>
    /// <exception cref="AmpleException">AMP5208 when the folder already is a project.</exception>
    public static async ValueTask<OpenProject> CreateAsync(IFileSystem fs, IClock clock, IIdGenerator ids, string root, string name, ulong? masterSeed = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ids);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AmpleException("AMP5202", "A project needs a name.");
        }

        string ample = Path.Combine(root, ProjectLayout.AmpleFolder);
        if (fs.FileExists(Path.Combine(ample, ProjectLayout.ProjectFile)))
        {
            throw new AmpleException("AMP5208", $"'{root}' is already an AMPLE project.");
        }

        fs.CreateDirectory(ample);
        foreach (string folder in ProjectLayout.RequiredFolders)
        {
            fs.CreateDirectory(Path.Combine(ample, folder));
        }

        ProjectManifest manifest = ProjectFile.CreateNew(ids.NewId(), name.Trim(), clock.UtcNow, masterSeed);
        await AtomicFile.WriteTextAsync(fs, Path.Combine(ample, ProjectLayout.ProjectFile), ProjectFile.Serialize(manifest), cancellationToken).ConfigureAwait(false);
        InstanceLock writerLock = await InstanceLock.AcquireAsync(fs, clock, root, cancellationToken).ConfigureAwait(false);
        return new OpenProject(root, manifest, writerLock, migrated: false);
    }

    /// <summary>Opens a project. For writing it takes the instance lock (AMP5204 when held) and upgrades an older format after copying the old project file to <c>.ample/backups</c>; read-only opening refuses to upgrade (AMP5209).</summary>
    public static async ValueTask<OpenProject> OpenAsync(IFileSystem fs, IClock clock, string root, bool readOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(clock);
        string projectFile = Path.Combine(root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);
        if (!fs.FileExists(projectFile))
        {
            throw new AmpleException("AMP5210", $"'{root}' is not an AMPLE project (there is no {ProjectLayout.AmpleFolder}/{ProjectLayout.ProjectFile}).");
        }

        InstanceLock? writerLock = readOnly ? null : await InstanceLock.AcquireAsync(fs, clock, root, cancellationToken).ConfigureAwait(false);
        try
        {
            string json = await fs.ReadAllTextAsync(projectFile, cancellationToken).ConfigureAwait(false);
            int version = ProjectFile.ReadFormatVersion(json);
            bool migrated = false;
            if (version < ProjectLayout.CurrentFormatVersion)
            {
                if (readOnly)
                {
                    throw new AmpleException("AMP5209", $"This project uses the older format {version}. Open it for writing once to upgrade it (a backup copy is made first).");
                }

                string backups = Path.Combine(root, ProjectLayout.AmpleFolder, "backups");
                fs.CreateDirectory(backups);
                string backup = Path.Combine(backups, $"project.v{version}.{clock.UtcNow:yyyyMMddHHmmss}.json");
                await AtomicFile.WriteTextAsync(fs, backup, json, cancellationToken).ConfigureAwait(false);
                json = Migrations.UpgradeToCurrent(json);
                await AtomicFile.WriteTextAsync(fs, projectFile, json, cancellationToken).ConfigureAwait(false);
                migrated = true;
            }

            return new OpenProject(root, ProjectFile.Parse(json), writerLock, migrated);
        }
        catch
        {
            writerLock?.Dispose();
            throw;
        }
    }
}
