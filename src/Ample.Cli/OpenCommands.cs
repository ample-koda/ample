using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Library;
using Ample.Storage.Projects;
using Ample.Docs;

namespace Ample.Cli;

/// <summary>The <c>ample open</c> command.</summary>
internal static class OpenCommands
{
    private static readonly SystemClock Clock = new();

    internal static Command Create(TextWriter output)
    {
        Argument<string?> target = new("target") { Description = "A project folder, .amplet song or .module file.", Arity = ArgumentArity.ZeroOrOne };
        Option<bool> last = new("--last") { Description = "Open the most recently used project." };
        Option<bool> visible = new("--visible") { Description = "Show a console window for the server." };
        Option<bool> noOpen = new("--no-open") { Description = "Start or attach without opening a browser." };
        Option<string?> song = new("--song") { Description = "Song path to open when song deep links are supported." };
        Option<string?> agent = new("--agent") { Description = "Self-reported agent name; requires --reason." };
        Option<string?> reason = new("--reason") { Description = "Why the agent is opening this project." };
        Option<string?> home = new("--home") { Description = "The AMPLE user data folder (default: AMPLE_HOME or the local application data folder)." };
        Command command = new("open", "Attaches to or starts the server for a project.") { target, last, visible, noOpen, song, agent, reason, home };
        command.SetAction(async (parse, cancellationToken) => await RunAsync(
            output,
            parse.GetValue(target),
            parse.GetValue(last),
            parse.GetValue(visible),
            parse.GetValue(noOpen),
            parse.GetValue(song),
            parse.GetValue(agent),
            parse.GetValue(reason),
            parse.GetValue(home),
            cancellationToken).ConfigureAwait(false));
        return command;
    }

    private static async Task<int> RunAsync(TextWriter output, string? target, bool last, bool visible, bool noOpen, string? song, string? agentName, string? reason, string? requestedHome, CancellationToken cancellationToken)
    {
        if (last && target is not null)
        {
            output.WriteLine("Choose a target or --last, not both.");
            return 2;
        }

        agentName ??= Environment.GetEnvironmentVariable("AMPLE_AGENT");
        reason ??= Environment.GetEnvironmentVariable("AMPLE_AGENT_REASON");
        if (agentName is not null && string.IsNullOrWhiteSpace(reason))
        {
            output.WriteLine("--reason is required when --agent or AMPLE_AGENT is set.");
            return 2;
        }

        if (agentName is not null && string.IsNullOrWhiteSpace(agentName))
        {
            output.WriteLine("A non-empty agent name is required when --agent or AMPLE_AGENT is set.");
            return 2;
        }

        if (agentName is null && reason is not null)
        {
            output.WriteLine("--reason and AMPLE_AGENT_REASON require an agent name.");
            return 2;
        }

        string home = requestedHome is null ? Library.DefaultHome() : Path.GetFullPath(requestedHome);
        IReadOnlyList<RecentProject> recents = RecentProjects.List(home);
        if (target is null && !last)
        {
            WriteRecents(output, recents);
            return 0;
        }

        string? requested = target;
        if (last) requested = recents.Count > 0 ? recents[0].Path : null;
        if (requested is null || !TryResolveProject(requested, out string projectRoot, out string fullTarget, out ProjectManifest? manifest) || manifest is null)
        {
            output.WriteLine("The target is not an AMPLE project folder, .amplet song or .module file.");
            return 2;
        }

        string? songTarget = song;
        if (Path.GetExtension(fullTarget).Equals(".amplet", StringComparison.OrdinalIgnoreCase)) songTarget = fullTarget;
        if (songTarget is not null && !IsProjectFile(projectRoot, songTarget, out _))
        {
            output.WriteLine("The song target must be an existing file inside the project.");
            return 2;
        }

        if (songTarget is not null) output.WriteLine("This web UI does not yet support opening a song from a deep link; the project will be opened.");
        string projectId = manifest.Id.ToString("D");
        InstanceRegistry registry = new(home, Clock, new SystemInstanceProbe());
        InstanceRecord? running = await registry.FindAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (agentName is not null && OperatingSystem.IsWindows())
        {
            HubPipeResponse? response;
            try
            {
                HubPipeRequest request = new(HubPipeProtocol.Version, Guid.NewGuid(), running is null ? "start" : "attach", projectId, projectRoot, new HubPipeAgent(agentName, reason!), home);
                response = await EnsureHubAndSendAsync(request, cancellationToken).ConfigureAwait(false);
                if (request.Operation == "attach" && response is { Success: false, ErrorCode: "not-running" })
                {
                    request = request with { RequestId = Guid.NewGuid(), Operation = "start" };
                    response = await EnsureHubAndSendAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                output.WriteLine($"The AMPLE hub request failed: {ex.Message}");
                return 1;
            }

            if (response is not null)
            {
                if (!response.Success)
                {
                    output.WriteLine($"The AMPLE hub rejected the request ({response.ErrorCode ?? "hub-error"}): {response.Message ?? "No details were returned."}");
                    return 1;
                }

                if (response.ProjectId != projectId || response.Status is not ("started" or "attached") || !IsPrivateLaunchUrl(response.LaunchUrl))
                {
                    output.WriteLine("The AMPLE hub returned an invalid project launch response.");
                    return 1;
                }

                Uri launch = new(response.LaunchUrl!);
                RecentProjects.Touch(home, projectRoot, manifest.Name, projectId, Clock, launch.Port);
                return OpenBrowser(output, response.LaunchUrl!, noOpen);
            }
        }

        if (running is not null)
        {
            RecentProjects.Touch(home, projectRoot, manifest.Name, projectId, Clock, running.Port);
            string? launchUrl = registry.ReadLaunchUrl(projectId);
            if (string.IsNullOrWhiteSpace(launchUrl))
            {
                output.WriteLine("The project server is running, but its private launch URL is missing.");
                return 1;
            }

            return OpenBrowser(output, launchUrl, noOpen);
        }

        string logPath = Path.Combine(home, "logs", projectId + ".log");
        try
        {
            EnsureHomeWritable(home);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            output.WriteLine($"AMPLE cannot write its instance registry under '{home}'. Use --home or AMPLE_HOME to select a writable folder.");
            return 1;
        }

        int preferredPort = recents.Where(r => string.Equals(r.Path, projectRoot, StringComparison.OrdinalIgnoreCase)).Select(r => r.LastPort).FirstOrDefault() ?? 0;
        string ampleRoot;
        try
        {
            try { ampleRoot = SpecRoot.Find(AppContext.BaseDirectory).Path; }
            catch (DirectoryNotFoundException) { ampleRoot = SpecRoot.Find(Directory.GetCurrentDirectory()).Path; }
        }
        catch (Exception ex) when (ex is AmpleException or DirectoryNotFoundException)
        {
            output.WriteLine(ex is AmpleException ample ? $"{ample.DiagnosticCode}: {ample.Message}" : ex.Message);
            return 1;
        }

        bool showWindow = visible || agentName is not null;
        for (int attempt = 0; attempt < (preferredPort > 0 ? 2 : 1); attempt++)
        {
            int port = attempt == 0 ? preferredPort : 0;
            Process? child = null;
            try
            {
                child = StartServer(projectRoot, ampleRoot, home, port, logPath, showWindow, agentName, reason);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                output.WriteLine($"Could not start AMPLE. Log: {logPath}");
                return 1;
            }

            using Process ownedChild = child ?? throw new InvalidOperationException("The AMPLE server process could not be created.");
            DateTimeOffset deadline = Clock.UtcNow.AddSeconds(30);
            while (Clock.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                running = await registry.FindAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (running is not null) break;
                if (ownedChild.HasExited) break;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            if (running is not null)
            {
                RecentProjects.Touch(home, projectRoot, manifest.Name, projectId, Clock, running.Port);
                string? launchUrl = registry.ReadLaunchUrl(projectId);
                if (string.IsNullOrWhiteSpace(launchUrl))
                {
                    output.WriteLine($"AMPLE started but its private launch URL is missing. Log: {logPath}");
                    return 1;
                }

                return OpenBrowser(output, launchUrl, noOpen);
            }

            if (!ownedChild.HasExited)
            {
                try { ownedChild.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }

            if (attempt == 0 && preferredPort > 0 && ownedChild.HasExited) continue;
            output.WriteLine($"AMPLE did not start within 30 seconds. Log: {logPath}");
            return 1;
        }

        output.WriteLine($"AMPLE could not start. Log: {logPath}");
        return 1;
    }

    private static bool TryResolveProject(string target, out string projectRoot, out string fullTarget, out ProjectManifest? manifest)
    {
        projectRoot = string.Empty;
        fullTarget = string.Empty;
        manifest = null;
        string full;
        try { full = Path.GetFullPath(target); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
        fullTarget = full;
        string? folder;
        if (Directory.Exists(full)) folder = full;
        else if (File.Exists(full) && (Path.GetExtension(full).Equals(".amplet", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(full).Equals(".module", StringComparison.OrdinalIgnoreCase))) folder = Path.GetDirectoryName(full);
        else return false;

        while (folder is not null)
        {
            string projectFile = Path.Combine(folder, ".ample", "project.json");
            if (File.Exists(projectFile))
            {
                try
                {
                    manifest = ProjectFile.Parse(File.ReadAllText(projectFile));
                    projectRoot = folder;
                    return true;
                }
                catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return false; }
            }

            folder = Directory.GetParent(folder)?.FullName;
        }

        return false;
    }

    private static bool IsProjectFile(string projectRoot, string target, out string full)
    {
        full = string.Empty;
        try
        {
            full = Path.GetFullPath(target, projectRoot);
            string relative = Path.GetRelativePath(projectRoot, full);
            return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != ".." && File.Exists(full);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static void EnsureHomeWritable(string home)
    {
        foreach (string folder in new[] { Path.Combine(home, "instances"), Path.Combine(home, "logs") })
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".open-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
    }

    private static bool IsPrivateLaunchUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.StartsWith("#token=", StringComparison.Ordinal) && uri.Fragment.Length > 7;

    private static async Task<HubPipeResponse?> EnsureHubAndSendAsync(HubPipeRequest request, CancellationToken cancellationToken)
    {
        string pipeName = HubPipeProtocol.PipeNameForCurrentUser();
        HubPipeResponse? response = await HubPipeClient.SendAsync(pipeName, request, TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        if (response is not null) return response;

        string executable = Path.Combine(AppContext.BaseDirectory, "Ample.Hub.exe");
        if (!File.Exists(executable)) return null;
        try
        {
            using Process? hubProcess = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory });
            if (hubProcess is null) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        DateTimeOffset deadline = Clock.UtcNow.AddSeconds(10);
        while (Clock.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            response = await HubPipeClient.SendAsync(pipeName, request, TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            if (response is not null) return response;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static Process StartServer(string projectRoot, string ampleRoot, string home, int port, string logPath, bool visible, string? agentName, string? reason)
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable.");
        ProcessStartInfo start = new() { UseShellExecute = false, CreateNoWindow = !visible, WorkingDirectory = projectRoot };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = processPath;
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("The CLI assembly path is unavailable."));
        }
        else start.FileName = processPath;

        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(projectRoot);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(ampleRoot);
        start.ArgumentList.Add("--home");
        start.ArgumentList.Add(home);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--no-browser");
        if (visible) start.ArgumentList.Add("--visible");
        start.ArgumentList.Add("--no-token-output");
        start.ArgumentList.Add("--log-file");
        start.ArgumentList.Add(logPath);
        if (agentName is not null)
        {
            start.ArgumentList.Add("--started-by");
            start.ArgumentList.Add("agent");
            start.ArgumentList.Add("--agent-name");
            start.ArgumentList.Add(agentName);
            start.ArgumentList.Add("--agent-reason");
            start.ArgumentList.Add(reason!);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The AMPLE server process could not be created.");
    }

    private static int OpenBrowser(TextWriter output, string url, bool noOpen)
    {
        if (noOpen)
        {
            output.WriteLine(url);
            return 0;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            output.WriteLine("Opened the project in the default browser.");
            return 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.WriteLine($"The browser could not be started. Open: {url}");
            return 0;
        }
    }

    private static void WriteRecents(TextWriter output, IReadOnlyList<RecentProject> recents)
    {
        if (recents.Count == 0)
        {
            output.WriteLine("No recent AMPLE projects.");
            return;
        }

        output.WriteLine("Recent AMPLE projects:");
        foreach (RecentProject recent in recents) output.WriteLine($"{recent.Name}  {recent.Path}  ({recent.LastOpened:u})");
    }
}
