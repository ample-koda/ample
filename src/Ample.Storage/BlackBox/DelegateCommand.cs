using System.Text.Json;
using Ample.Audio.IO;

namespace Ample.Storage.BlackBox;

/// <summary>A journaled change implemented by a delegate (for database operations that have no file pre-image).</summary>
public sealed class DelegateCommand(string kind, string payloadJson, Func<ValueTask> action) : ICommand
{
    /// <inheritdoc />
    public string Kind => kind;

    /// <inheritdoc />
    public string PayloadJson => payloadJson;

    /// <inheritdoc />
    public ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken) => action();
}

/// <summary>Deletes a file inside the project; the old content is the pre-image (so the journal can restore it).</summary>
public sealed class DeleteFileCommand(string relativePath) : ICommand
{
    /// <inheritdoc />
    public string Kind => "file.delete";

    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { path = relativePath });

    /// <inheritdoc />
    public ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken) => new WriteFileCommand(relativePath, string.Empty).PreImageAsync(context, cancellationToken);

    /// <inheritdoc />
    public ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (Ample.Storage.Projects.DemoSongRules.IsDemo(relativePath) && Ample.Storage.Projects.DemoSongRules.IsProtected(context.ProjectRoot)) throw new Ample.Core.Abstractions.AmpleException("AMP5285", Ample.Storage.Projects.DemoSongRules.Refusal);
        context.Fs.DeleteFile(ProjectPath.Resolve(context.ProjectRoot, relativePath));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Creates a project folder through the command journal.</summary>
public sealed class CreateFolderCommand(string relativePath) : ICommand
{
    /// <inheritdoc />
    public string Kind => "folder.create";
    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { path = relativePath });
    /// <inheritdoc />
    public ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(JsonSerializer.Serialize(new { operation = "folder.create", path = relativePath }));
    /// <inheritdoc />
    public ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Fs.CreateDirectory(ProjectPath.Resolve(context.ProjectRoot, relativePath));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Deletes an empty project folder through the command journal.</summary>
public sealed class DeleteFolderCommand(string relativePath) : ICommand
{
    /// <inheritdoc />
    public string Kind => "folder.delete";
    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { path = relativePath });
    /// <inheritdoc />
    public ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(JsonSerializer.Serialize(new { operation = "folder.delete", path = relativePath }));
    /// <inheritdoc />
    public ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(context.ProjectRoot, relativePath);
        if (context.Fs.GetFiles(full).Count != 0 || context.Fs.GetDirectories(full).Count != 0) throw new IOException("Only empty project folders can be deleted.");
        context.Fs.DeleteDirectory(full, recursive: false);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Moves a project file or folder through the command journal; the pre-image records the inverse move.</summary>
public sealed class MoveProjectPathCommand(string source, string destination, bool directory) : ICommand
{
    /// <inheritdoc />
    public string Kind => directory ? "folder.move" : "file.move";
    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { source, destination, directory });
    /// <inheritdoc />
    public ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(JsonSerializer.Serialize(new { operation = "path.move", source, destination, directory }));
    /// <inheritdoc />
    public ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!directory && Ample.Storage.Projects.DemoSongRules.IsDemo(source) && Ample.Storage.Projects.DemoSongRules.IsProtected(context.ProjectRoot)) throw new Ample.Core.Abstractions.AmpleException("AMP5285", Ample.Storage.Projects.DemoSongRules.Refusal);
        string from = ProjectPath.Resolve(context.ProjectRoot, source), to = ProjectPath.Resolve(context.ProjectRoot, destination);
        string parent = Path.GetDirectoryName(to)!;
        if (!context.Fs.DirectoryExists(parent)) throw new DirectoryNotFoundException(parent);
        if (directory) context.Fs.MoveDirectory(from, to); else context.Fs.Move(from, to, overwrite: false);
        context.Fs.FlushDirectory(Path.GetDirectoryName(from)!);
        if (!string.Equals(Path.GetDirectoryName(from), parent, StringComparison.Ordinal)) context.Fs.FlushDirectory(parent);
        return ValueTask.CompletedTask;
    }
}
