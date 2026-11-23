using System.ComponentModel.Composition;
using System.Diagnostics;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Ample.VisualStudio;

internal static class AmpleContentType
{
    internal const string Name = "AMPLE";

    [Export]
    [Name(Name)]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition Definition = null!;

    [Export]
    [FileExtension(".amplet")]
    [ContentType(Name)]
    internal static FileExtensionToContentTypeDefinition Song = null!;

    [Export]
    [FileExtension(".amplec")]
    [ContentType(Name)]
    internal static FileExtensionToContentTypeDefinition Component = null!;
}

[ContentType(AmpleContentType.Name)]
[Export(typeof(ILanguageClient))]
internal sealed class AmpleLanguageClient : ILanguageClient
{
    public string Name => "AMPLE Language Server";
    public bool ShowNotificationOnInitializeFailed => true;
    public IEnumerable<string> ConfigurationSections => Array.Empty<string>();
    public object? InitializationOptions => null;
    public IEnumerable<string> FilesToWatch => Array.Empty<string>();
    public event AsyncEventHandler<EventArgs>? StartAsync;
    public event AsyncEventHandler<EventArgs>? StopAsync { add { } remove { } }

    public async Task OnLoadedAsync()
    {
        // without an AMPLE command line the framework would show a modal "connection could not be established" error: start nothing, say so quietly
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnvDTE.DTE? dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        string cli = AmpleCli.For(AmpleCli.ProjectFileIn(FindProjectRoot(dte?.ActiveDocument?.FullName)));
        if (!CliExists(cli))
        {
            AmpleLog.Write($"language server not started: {cli} not found");
            if (dte is not null) dte.StatusBar.Text = $"AMPLE: language features are off, the AMPLE command line was not found ({cli}). Set AMPLE_CLI or put ample.exe on PATH.";
            return;
        }

        await StartAsync!.InvokeAsync(this, EventArgs.Empty);
    }

    private static bool CliExists(string cli)
    {
        if (Path.IsPathRooted(cli)) return File.Exists(cli);
        string[] folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        return folders.Any(f => f.Length > 0 && File.Exists(Path.Combine(f.Trim('"'), cli)));
    }

    public async Task<Connection?> ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        EnvDTE.DTE? dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        string? projectRoot = FindProjectRoot(dte?.ActiveDocument?.FullName);
        string cli = AmpleCli.For(AmpleCli.ProjectFileIn(projectRoot));
        // an older AMPLE (the installed one) has no "lsp --project": start it without, instead of a server that stops at once
        bool withProject = projectRoot is not null && await Task.Run(() => AmpleCli.Supports(cli, "lsp", "--project"), cancellationToken);
        if (projectRoot is not null && !withProject) AmpleLog.Write($"{cli} has no 'lsp --project' (an older AMPLE): the language server runs without the project");
        ProcessStartInfo start = new(cli, withProject ? $"lsp --project {AmpleProjectWizard.Quote(projectRoot!)}" : "lsp")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Process server = new() { StartInfo = start, EnableRaisingEvents = true };
        // the server's error output goes to a log file, so a server that stops is never silent
        string log = Path.Combine(Path.GetTempPath(), "ample-lsp-vs.log");
        server.ErrorDataReceived += (_, e) => { if (e.Data is not null) try { File.AppendAllText(log, e.Data + Environment.NewLine); } catch (IOException) { } };
        server.Exited += (_, _) => server.Dispose();
        try
        {
            if (!server.Start()) throw new InvalidOperationException("Could not start the AMPLE language server.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // no AMPLE command line: say so quietly (status bar and log), without the modal "extension exception" dialog; no language features then
            AmpleLog.Write($"language server not started: {start.FileName}: {ex.Message}");
            if (dte is not null) dte.StatusBar.Text = $"AMPLE: language features are off, the AMPLE command line was not found ({start.FileName}). Set AMPLE_CLI or put ample.exe on PATH.";
            server.Dispose();
            return null;
        }

        server.BeginErrorReadLine();
        try { File.AppendAllText(log, $"{DateTime.Now:O} started {start.FileName} {start.Arguments}{Environment.NewLine}"); } catch (IOException) { }
        return new Connection(server.StandardOutput.BaseStream, server.StandardInput.BaseStream);
    }

    private static string? FindProjectRoot(string? documentPath)
    {
        DirectoryInfo? folder = string.IsNullOrWhiteSpace(documentPath) ? null : new DirectoryInfo(Path.GetDirectoryName(documentPath)!);
        while (folder is not null)
        {
            if (File.Exists(Path.Combine(folder.FullName, ".ample", "project.json"))) return folder.FullName;
            folder = folder.Parent;
        }
        return null;
    }

    public Task OnServerInitializedAsync() => Task.CompletedTask;
    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo failure) =>
        Task.FromResult<InitializationFailureContext?>(
            new InitializationFailureContext { FailureMessage = failure.InitializationException?.Message ?? failure.StatusMessage });

}
