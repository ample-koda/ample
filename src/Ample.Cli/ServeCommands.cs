using System.CommandLine;
using System.Diagnostics;
using Ample.Core.Abstractions;
using Ample.Storage.Library;
using Ample.Storage.Projects;
using Ample.Docs;
using Ample.Language;
using Ample.Server;

namespace Ample.Cli;

/// <summary>The <c>ample serve</c> command.</summary>
internal static class ServeCommands
{
    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Option<string?> project = new("--project") { Description = "The project folder (default: the current folder)." };
        Option<string?> root = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Option<string?> home = new("--home") { Description = "The AMPLE user data folder (default: AMPLE_HOME or the local application data folder)." };
        Option<int> port = new("--port") { Description = "Port on 127.0.0.1 (default: a free one).", DefaultValueFactory = _ => 0 };
        Option<int> maxUploadMb = new("--max-upload-mb") { Description = "Maximum project file upload size in MiB (1 to 512).", DefaultValueFactory = _ => 512 };
        Option<string?> envAllow = new("--environment-allow") { Description = "Comma-separated names (patterns like TZ, LANG*, *_HOME) that environment() may read on this server; every other name is refused." };
        Option<string?> forbidCapabilities = new("--forbid-capabilities") { Description = "Comma-separated capabilities that no project, song or proposal can ever use on this server (clock, os-random, environment, network, filesystem, external-process, external-plugin). A public playground forbids the sensitive ones." };
        Option<bool> noBrowser = new("--no-browser") { Description = "Do not open the browser." };
        Option<bool> visible = new("--visible") { Description = "Keep the server in a visible console window." };
        Option<bool> noTokenOutput = new("--no-token-output") { Description = "Do not print the private token launch URL." };
        Option<bool> allowLan = new("--allow-lan") { Description = "Accept other host names. Only on a trusted network: the session token is the only protection." };
        Option<bool> login = new("--login") { Description = "Also accept a sign-in with the user and password set by `ample auth set-password` (page: /login). The launch token keeps working." };
        Option<bool> allowAll = new("--allow-all") { Description = "Listen on every address and accept any host name (for a tunnel). Only together with --login." };
        Option<string?> bind = new("--bind") { Description = "The IP address to listen on (default: 0.0.0.0 with --allow-lan or --allow-all, else 127.0.0.1). Behind a reverse proxy on the same machine use 127.0.0.1 together with --allow-all." };
        Option<int?> parallelism = new("--parallelism") { Description = "How many blocks a build may render at the same time on this server: at most the number of cores of the machine, which is also the default (one per core). A project can ask for fewer, never for more. The environment variable AMPLE_PARALLELISM does the same." };
        Option<bool> liteOnly = new("--lite-only") { Description = "The public Remix: only the Remix page, its API and its language server exist (everything else is a 404), no sign-in or token is asked, the limits of the public profile hold and every build runs in a process of its own. Listens on 127.0.0.1 only: put a reverse proxy in front." };
        Option<string?> logFile = new("--log-file") { Description = "Write server output to this rolling log file." };
        Option<string> startedBy = new("--started-by") { Description = "Start source for launcher integration.", DefaultValueFactory = _ => "cli" };
        Option<string?> agentName = new("--agent-name") { Description = "Self-reported agent name for launcher integration." };
        Option<string?> agentReason = new("--agent-reason") { Description = "Reason for an agent-started server." };
        Option<string?> openapi = new("--openapi") { Description = "Write the OpenAPI document to this file and exit (no project needed)." };
        Command serve = new("serve", "Starts the local server with the web UI, the API and the help (127.0.0.1 only).") { project, root, home, port, maxUploadMb, forbidCapabilities, envAllow, noBrowser, visible, noTokenOutput, allowLan, login, allowAll, bind, parallelism, liteOnly, logFile, startedBy, agentName, agentReason, openapi };
        serve.SetAction(async (p, t) =>
        {
            try
            {
                if (p.GetValue(openapi) is string file)
                {
                    await File.WriteAllTextAsync(file, OpenApi.Build(), t);
                    output.WriteLine($"Wrote {file}");
                    return 0;
                }

                if (p.GetValue(forbidCapabilities) is { Length: > 0 } forbidden)
                {
                    string[] names = forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string? unknown = names.FirstOrDefault(n => !CapabilityPolicy.Names.Contains(n));
                    if (unknown is not null)
                    {
                        output.WriteLine($"--forbid-capabilities: there is no capability \"{unknown}\" (capabilities: {string.Join(", ", CapabilityPolicy.Names)}).");
                        return 1;
                    }

                    ServerCapabilities.Current = new ServerCapabilities(new HashSet<string>(names, StringComparer.Ordinal), ServerCapabilities.Local.DefaultOn);
                }

                if (p.GetValue(envAllow) is { Length: > 0 } allowed)
                {
                    ServerCapabilities.Current = ServerCapabilities.Current with { EnvironmentAllow = allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
                }

                int maxUploadMegabytes = p.GetValue(maxUploadMb);
                if (maxUploadMegabytes is < 1 or > 512)
                {
                    output.WriteLine("--max-upload-mb must be between 1 and 512.");
                    return 1;
                }

                string source = p.GetValue(startedBy) ?? "cli";
                string? name = p.GetValue(agentName);
                string? reason = p.GetValue(agentReason);
                if (source is not ("hub" or "cli" or "agent" or "other") || (source == "agent" ? string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(reason) : name is not null || reason is not null))
                {
                    output.WriteLine("Agent attribution requires --started-by agent, --agent-name and --agent-reason.");
                    return 2;
                }

                string ample = (p.GetValue(root) is string r ? SpecRoot.At(r) : SpecRoot.Find(Directory.GetCurrentDirectory())).Path;
                InstanceAgent? agent = source == "agent" ? new InstanceAgent(name!, reason!, null) : null;
                Ample.Engine.Engine.BuildParallelism.ConfigureServer(p.GetValue(parallelism) ?? (int.TryParse(Environment.GetEnvironmentVariable("AMPLE_PARALLELISM"), out int fromEnvironment) ? fromEnvironment : null));
                ServerOptions options = new(Path.GetFullPath(p.GetValue(project) ?? Directory.GetCurrentDirectory()), ample, p.GetValue(port), null, p.GetValue(allowLan), p.GetValue(home) ?? Library.DefaultHome(), maxUploadMegabytes * 1024L * 1024) { StartedBy = source, Agent = agent, Login = p.GetValue(login), AllowAll = p.GetValue(allowAll), BindAddress = p.GetValue(bind), LiteOnly = p.GetValue(liteOnly) };
                await using RunningServer server = await AmpleServer.StartAsync(options, t);
                output.WriteLine($"AMPLE is running at {server.Url}");
                output.WriteLine(p.GetValue(noTokenOutput) ? "Open: private launch URL is in the instance registry." : $"Open: {server.LaunchUrl}");
                if (p.GetValue(login)) output.WriteLine($"Sign in: {server.Url}/login (user and password from `ample auth set-password`).");
                if (!server.IsRegistered) output.WriteLine("Warning: the instance registry is unavailable; classic serving remains active, but `ample open` cannot attach to this server.");
                output.WriteLine("Press Ctrl+C to stop.");
                if (!p.GetValue(noBrowser))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(server.LaunchUrl) { UseShellExecute = true });
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        output.WriteLine("(The browser could not be started; open the address above.)");
                    }
                }

                await Task.WhenAny(server.Completion, Task.Delay(Timeout.Infinite, t));
                return 0;
            }
            catch (AmpleException ex)
            {
                output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
                return 1;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        });
        Option<string> authUser = new("--user") { Description = "The user name.", Required = true };
        Option<string?> authHome = new("--home") { Description = "The AMPLE user data folder (default: AMPLE_HOME or the local application data folder)." };
        Command setPassword = new("set-password", "Stores the user and password for `ample serve --login` (only a salted hash is kept; the password is asked for without echo).") { authUser, authHome };
        setPassword.SetAction(p =>
        {
            string folder = p.GetValue(authHome) ?? Library.DefaultHome();
            output.Write("Password (at least 8 characters): ");
            string first = ReadSecret();
            output.Write("Again: ");
            string second = ReadSecret();
            if (first != second)
            {
                output.WriteLine("The two passwords differ; nothing was stored.");
                return 1;
            }

            try
            {
                AuthService.SetPassword(folder, p.GetValue(authUser)!, first);
            }
            catch (ArgumentException ex)
            {
                output.WriteLine(ex.Message);
                return 1;
            }

            output.WriteLine($"Stored (hash only) in {AuthService.FilePath(folder)}.");
            return 0;
        });
        Command auth = new("auth", "Sign-in settings for `ample serve --login`.") { setPassword };
        // the other side of an isolated Lite build (see LiteWorker): not for people, the server starts it
        Option<string> workerRoot = new("--root") { Description = "The AMPLE folder.", Required = true };
        Option<string> workerWork = new("--work") { Description = "The private folder of this build.", Required = true };
        Command worker = new("lite-worker", "Builds one Remix request that arrives on the standard input (started by the server, not by people).") { workerRoot, workerWork };
        worker.Hidden = true;
        worker.SetAction(async (p, t) => await Ample.Server.Lite.LiteWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), p.GetValue(workerRoot)!, p.GetValue(workerWork)!, t));
        return [serve, auth, worker];
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine() ?? string.Empty;
        System.Text.StringBuilder text = new();
        while (Console.ReadKey(true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (key.KeyChar >= ' ') text.Append(key.KeyChar);
        }

        Console.WriteLine();
        return text.ToString();
    }
}
