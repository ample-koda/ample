using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Language;

namespace Ample.Cli;

/// <summary>
/// The <c>ample dap</c> command: a Debug Adapter Protocol server on stdio (plan/visual-studio-integration.md, VS06). It debugs the
/// building of a song: helper code is evaluated statement by statement, so a breakpoint on a line of a helper, a step, the call stack
/// and the stack of an error make sense. Nothing runs during playback. Variables are not shown yet.
/// </summary>
internal static class DapCommands
{
    internal static Command Create()
    {
        Command dap = new("dap", "Runs a Debug Adapter Protocol server on stdin and stdout, for editors that debug the build of a song.");
        dap.SetAction(async (_, token) =>
        {
            using DapSession session = new(Console.OpenStandardInput(), Console.OpenStandardOutput());
            return await session.RunAsync(token);
        });
        return dap;
    }

    internal sealed class DapSession(Stream input, Stream output) : IDisposable
    {
        private readonly object writeLock = new();
        private readonly Dictionary<string, HashSet<int>> breakpoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim resume = new(0);
        private readonly CancellationTokenSource stop = new();
        private int seq;
        private string program = string.Empty;
        private string project = string.Empty;
        private volatile bool stepping;
        private volatile NotePipeline.DebugStop? current;
        private int lastLine = -1;
        private Task? build;

        public void Dispose()
        {
            stop.Cancel();
            resume.Release();
            stop.Dispose();
            resume.Dispose();
        }

        public async Task<int> RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                JsonNode? message = await ReadAsync(token);
                if (message is null) return 0;
                if (!Handle(message)) return 0;
            }

            return 0;
        }

        private bool Handle(JsonNode request)
        {
            string command = (string?)request["command"] ?? string.Empty;
            JsonNode? args = request["arguments"];
            JsonObject body = [];
            bool success = true;
            string? error = null;
            switch (command)
            {
                case "initialize":
                    body["supportsConfigurationDoneRequest"] = true;
                    Respond(request, true, body, null);
                    Event("initialized", null);
                    return true;
                case "launch":
                    program = Path.GetFullPath((string?)args?["program"] ?? string.Empty);
                    project = Path.GetFullPath((string?)args?["project"] ?? FindProject(program));
                    stepping = (bool?)args?["stopOnEntry"] ?? false;
                    break;
                case "setBreakpoints":
                    SetBreakpoints(args, body);
                    break;
                case "configurationDone":
                    build = Task.Run(RunBuildAsync);
                    break;
                case "setExceptionBreakpoints":
                case "setFunctionBreakpoints":
                case "setDataBreakpoints":
                    body["breakpoints"] = new JsonArray();   // nothing to set: AMPLE stops on lines and steps; an error is reported with its stack at the end
                    break;
                case "threads":
                    body["threads"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "build" });
                    break;
                case "stackTrace":
                    body["stackFrames"] = Frames();
                    break;
                case "scopes":
                    body["scopes"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "Locals",
                        ["variablesReference"] = ((int?)args?["frameId"] ?? 0) + 1,   // one scope per frame: reference = frame id + 1
                        ["expensive"] = false,
                    });
                    break;
                case "variables":
                    body["variables"] = Variables((int?)args?["variablesReference"] ?? 1);
                    break;
                case "continue":
                    body["allThreadsContinued"] = true;
                    Resume(false);
                    break;
                case "next":
                case "stepIn":
                case "stepOut":
                    Resume(true);
                    break;
                case "disconnect":
                    Respond(request, true, body, null);
                    stop.Cancel();
                    resume.Release();
                    return false;
                default:
                    success = false;
                    error = "Not supported: " + command;
                    break;
            }

            Respond(request, success, body, error);
            return true;
        }

        private static string FindProject(string song)
        {
            for (string? folder = Path.GetDirectoryName(song); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                if (Directory.Exists(Path.Combine(folder, ".ample"))) return folder;
            }

            return Path.GetDirectoryName(song) ?? ".";
        }

        private void SetBreakpoints(JsonNode? args, JsonObject body)
        {
            string path = Path.GetFullPath((string?)args?["source"]?["path"] ?? string.Empty);
            HashSet<int> lines = [];
            JsonArray verified = [];
            foreach (JsonNode? bp in args?["breakpoints"]?.AsArray() ?? [])
            {
                int line = (int?)bp?["line"] ?? 0;
                lines.Add(line);
                verified.Add(new JsonObject { ["verified"] = true, ["line"] = line });
            }

            lock (breakpoints) breakpoints[path] = lines;
            body["breakpoints"] = verified;
        }

        private void Resume(bool step)
        {
            stepping = step;
            resume.Release();
        }

        private async Task RunBuildAsync()
        {
            StringWriter text = new();
            int exit;
            try
            {
                NotePipeline.AmbientDebug.Value = OnStatement;
                string wav = Path.Combine(Path.GetTempPath(), "ample-debug-" + Guid.NewGuid().ToString("N") + ".wav");
                exit = await BuildCommands.RunDirectAsync(text, program, project, wav, stop.Token);
                if (File.Exists(wav)) File.Delete(wav);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                text.WriteLine(ex.Message);
                exit = 1;
            }
            catch (OperationCanceledException)
            {
                exit = 1;
            }

            Event("output", new JsonObject { ["category"] = "console", ["output"] = text + "\n" });
            Event("exited", new JsonObject { ["exitCode"] = exit });
            Event("terminated", null);
        }

        /// <summary>Called on the build's thread before each statement of helper code: stops when a breakpoint or a step asks for it.</summary>
        private void OnStatement(NotePipeline.DebugStop at)
        {
            if (at.Site is null || stop.IsCancellationRequested) return;
            string file = ResolveFile(at.Site.Origin);
            bool onBreakpoint;
            lock (breakpoints) onBreakpoint = breakpoints.TryGetValue(file, out HashSet<int>? lines) && lines.Contains(at.Site.Line);
            if (!stepping && !onBreakpoint) return;
            if (at.Site.Line == lastLine && !onBreakpoint) return;   // several statements of one line stop once when stepping
            lastLine = at.Site.Line;
            current = at;
            Event("stopped", new JsonObject
            {
                ["reason"] = onBreakpoint ? "breakpoint" : "step",
                ["threadId"] = 1,
                ["allThreadsStopped"] = true,
            });
            resume.Wait(stop.Token);
            current = null;
        }

        private string ResolveFile(string origin)
        {
            if (origin == "song") return program;
            string relative = Path.Combine(project, origin.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(relative) ? Path.GetFullPath(relative) : origin;
        }

        private JsonArray Frames()
        {
            JsonArray frames = [];
            NotePipeline.DebugStop? at = current;
            if (at is null) return frames;
            // frame 0 is where the statement runs now (in the innermost helper); frame k is the call that leads into frame k-1,
            // made from the next helper out (or from a block's expression, called "block")
            if (at.Site is { } site) frames.Add(Frame(0, at.Stack.Count > 0 ? at.Stack[0].Name : "block", site));
            for (int k = 0; k < at.Stack.Count; k++)
            {
                if (at.Stack[k].Site is not { } callSite) continue;
                frames.Add(Frame(k + 1, k + 1 < at.Stack.Count ? at.Stack[k + 1].Name : "block", callSite));
            }

            return frames;
        }

        private JsonArray Variables(int reference)
        {
            JsonArray variables = [];
            IReadOnlyList<NotePipeline.DebugVariable> list = current is { } at && reference >= 1 && reference <= at.FrameVariables.Count
                ? at.FrameVariables[reference - 1]
                : [];
            foreach (NotePipeline.DebugVariable v in list)
            {
                variables.Add(new JsonObject { ["name"] = v.Name, ["value"] = v.Value, ["type"] = v.Type, ["variablesReference"] = 0 });
            }

            return variables;
        }

        /// <summary>A component's helper has an internal name (__c_&lt;id&gt;_&lt;version&gt;): show the component's id part.</summary>
        private static string FriendlyName(string name) =>
            name.StartsWith("__c_", StringComparison.Ordinal) ? System.Text.RegularExpressions.Regex.Replace(name[4..], "_[0-9]+_[0-9]+_[0-9]+$", string.Empty) : name;

        private JsonObject Frame(int id, string name, SourceSite site) => new()
        {
            ["id"] = id,
            ["name"] = FriendlyName(name),
            ["source"] = new JsonObject { ["name"] = Path.GetFileName(ResolveFile(site.Origin)), ["path"] = ResolveFile(site.Origin) },
            ["line"] = site.Line,
            ["column"] = site.Col,
        };

        private void Respond(JsonNode request, bool success, JsonObject body, string? error)
        {
            JsonObject message = new()
            {
                ["type"] = "response",
                ["request_seq"] = (int?)request["seq"] ?? 0,
                ["success"] = success,
                ["command"] = (string?)request["command"],
            };
            if (error is not null) message["message"] = error;
            else message["body"] = body;
            Write(message);
        }

        private void Event(string name, JsonObject? body)
        {
            JsonObject message = new() { ["type"] = "event", ["event"] = name };
            if (body is not null) message["body"] = body;
            Write(message);
        }

        private void Write(JsonObject message)
        {
            lock (writeLock)
            {
                message["seq"] = ++seq;
                byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());
                byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + payload.Length + "\r\n\r\n");
                output.Write(header);
                output.Write(payload);
                output.Flush();
            }
        }

        private async Task<JsonNode?> ReadAsync(CancellationToken token)
        {
            int length = -1;
            while (true)
            {
                string? line = await ReadLineAsync(token);
                if (line is null) return null;
                if (line.Length == 0) break;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }

            if (length < 0) return null;
            byte[] buffer = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await input.ReadAsync(buffer.AsMemory(read, length - read), token);
                if (n == 0) return null;
                read += n;
            }

            return JsonNode.Parse(buffer);
        }

        private async Task<string?> ReadLineAsync(CancellationToken token)
        {
            StringBuilder line = new();
            byte[] one = new byte[1];
            while (await input.ReadAsync(one, token) == 1)
            {
                if (one[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)one[0]);
            }

            return line.Length == 0 ? null : line.ToString();
        }
    }
}
