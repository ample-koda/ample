using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Cli.Tests;

public sealed class DapTests
{
    private const string Song = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\nhelper late(Notes n) -> Notes {\n  let x = n\n  return x\n}\n\n"
        + "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes late(a.notes)\n}\n";

    private static void Send(Stream stream, int seq, string command, JsonObject? arguments = null)
    {
        byte[] body = Encoding.UTF8.GetBytes(new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = arguments ?? [] }.ToJsonString());
        stream.Write(Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n"));
        stream.Write(body);
        stream.Flush();
    }

    private static JsonNode Read(Stream stream)
    {
        StringBuilder header = new();
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int b = stream.ReadByte();
            Assert.True(b >= 0, "the adapter closed the stream");
            header.Append((char)b);
        }

        int length = int.Parse(header.ToString().Split("\r\n")[0].Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length) read += stream.Read(buffer, read, length - read);
        return JsonNode.Parse(buffer)!;
    }

    private static JsonNode ReadUntil(Stream stream, Func<JsonNode, bool> match)
    {
        for (int i = 0; i < 200; i++)
        {
            JsonNode message = Read(stream);
            if (match(message)) return message;
        }

        throw new InvalidOperationException("The expected message did not come.");
    }

    private const string EchoComponent = "component koda.notes.echoNotes version 1.0.0 current {\n  id \"demo.koda.notes.echo-notes\"\n\n  input source: Notes\n\n"
        + "  property plays: Int = 3\n\n  property delay: Duration = 1bar\n\n  property interval: Int = 2\n\n  output notes: Notes\n\n"
        + "  process {\n    for i in 0..plays {\n      emit source |> shift(i * delay) |> transpose(i * interval)\n    }\n  }\n}\n";

    private const string ChainSong = "composition \"c\" { ample 1  tempo 96  master-seed 2 }\n\nimport \"components/echo-notes.amplec\"\n\n"
        + "midi-clip motif at 1 for 1 {\n  notes 1:0:1:60:100 1:1:0.5:64:90\n}\n\n"
        + "koda.notes.echoNotes echo at 2 for 8 {\n  plays 2\n  delay 2bars\n  interval 3\n  from motif.notes -> source\n}\n\npiano lead at 2 for 8 {\n  from echo.notes -> notes\n}\n";

    private const string NestedSong = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\n"
        + "helper inner(Notes n) -> Notes {\n  let a = n\n  return a\n}\n\n"
        + "helper outer(Notes n) -> Notes {\n  let marker = 5\n  let r = inner(n)\n  return r\n}\n\n"
        + "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes outer(a.notes)\n}\n";

    [Fact]
    public async Task Deep_in_a_call_the_stack_shows_every_caller_with_its_own_variables()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-dap-n-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(
                new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "dap", null, CancellationToken.None))
            {
                _ = project;
            }

            string song = Path.Combine(folder, "nested.amplet");
            await File.WriteAllTextAsync(song, NestedSong);
            using AnonymousPipeServerStream toAdapter = new(PipeDirection.Out);
            using AnonymousPipeServerStream fromAdapter = new(PipeDirection.In);
            using AnonymousPipeClientStream adapterIn = new(PipeDirection.In, toAdapter.ClientSafePipeHandle);
            using AnonymousPipeClientStream adapterOut = new(PipeDirection.Out, fromAdapter.ClientSafePipeHandle);
            using DapCommands.DapSession session = new(adapterIn, adapterOut);
            Task adapter = Task.Run(() => session.RunAsync(CancellationToken.None));

            Send(toAdapter, 1, "initialize");
            ReadUntil(fromAdapter, m => (string?)m["event"] == "initialized");
            Send(toAdapter, 2, "launch", new JsonObject { ["program"] = song });
            Send(toAdapter, 3, "setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = song }, ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 4 }) });
            Send(toAdapter, 4, "configurationDone");
            ReadUntil(fromAdapter, m => (string?)m["event"] is "stopped" or "terminated");

            Send(toAdapter, 5, "stackTrace", new JsonObject { ["threadId"] = 1 });
            JsonArray frames = ReadUntil(fromAdapter, m => (int?)m["request_seq"] == 5)["body"]!["stackFrames"]!.AsArray();
            Assert.Equal(2, frames.Count);
            Assert.Equal("inner", (string?)frames[0]!["name"]);
            Assert.Equal(4, (int?)frames[0]!["line"]);
            Assert.Equal("outer", (string?)frames[1]!["name"]);
            Assert.Equal(10, (int?)frames[1]!["line"]);   // the call of inner() inside outer

            Send(toAdapter, 6, "scopes", new JsonObject { ["frameId"] = 1 });
            int reference = (int)ReadUntil(fromAdapter, m => (int?)m["request_seq"] == 6)["body"]!["scopes"]![0]!["variablesReference"]!;
            Send(toAdapter, 7, "variables", new JsonObject { ["variablesReference"] = reference });
            JsonArray outerVariables = ReadUntil(fromAdapter, m => (int?)m["request_seq"] == 7)["body"]!["variables"]!.AsArray();
            Assert.Contains(outerVariables, v => (string?)v!["name"] == "marker" && (string?)v["value"] == "5");

            Send(toAdapter, 8, "setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = song }, ["breakpoints"] = new JsonArray() });
            Send(toAdapter, 9, "continue", new JsonObject { ["threadId"] = 1 });
            ReadUntil(fromAdapter, m => (string?)m["event"] == "terminated");
            Send(toAdapter, 10, "disconnect");
            await adapter.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_breakpoint_in_a_component_file_stops_there_with_the_component_name_as_frame()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-dap-c-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(
                new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "dap", null, CancellationToken.None))
            {
                _ = project;
            }

            string song = Path.Combine(folder, "chain.amplet");
            string component = Path.Combine(folder, "components", "echo-notes.amplec");
            Directory.CreateDirectory(Path.GetDirectoryName(component)!);
            await File.WriteAllTextAsync(song, ChainSong);
            await File.WriteAllTextAsync(component, EchoComponent);
            using AnonymousPipeServerStream toAdapter = new(PipeDirection.Out);
            using AnonymousPipeServerStream fromAdapter = new(PipeDirection.In);
            using AnonymousPipeClientStream adapterIn = new(PipeDirection.In, toAdapter.ClientSafePipeHandle);
            using AnonymousPipeClientStream adapterOut = new(PipeDirection.Out, fromAdapter.ClientSafePipeHandle);
            using DapCommands.DapSession session = new(adapterIn, adapterOut);
            Task adapter = Task.Run(() => session.RunAsync(CancellationToken.None));

            Send(toAdapter, 1, "initialize");
            ReadUntil(fromAdapter, m => (string?)m["event"] == "initialized");
            Send(toAdapter, 2, "launch", new JsonObject { ["program"] = song });
            Send(toAdapter, 3, "setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = component }, ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 15 }) });
            Send(toAdapter, 4, "configurationDone");
            JsonNode stopped = ReadUntil(fromAdapter, m => (string?)m["event"] is "stopped" or "terminated");
            Assert.Equal("stopped", (string?)stopped["event"]);

            Send(toAdapter, 5, "stackTrace", new JsonObject { ["threadId"] = 1 });
            JsonNode top = ReadUntil(fromAdapter, m => (int?)m["request_seq"] == 5)["body"]!["stackFrames"]![0]!;
            Assert.Equal("echo-notes.amplec", (string?)top["source"]!["name"]);
            Assert.Equal(15, (int?)top["line"]);
            Assert.DoesNotContain("__c_", (string?)top["name"], StringComparison.Ordinal);

            Send(toAdapter, 6, "setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = component }, ["breakpoints"] = new JsonArray() });
            Send(toAdapter, 7, "continue", new JsonObject { ["threadId"] = 1 });
            ReadUntil(fromAdapter, m => (string?)m["event"] == "terminated");
            Send(toAdapter, 8, "disconnect");
            await adapter.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_breakpoint_in_a_helper_stops_the_build_shows_the_stack_and_continue_ends_it()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-dap-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(
                new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "dap", null, CancellationToken.None))
            {
                _ = project;
            }

            string song = Path.Combine(folder, "dbg.amplet");
            await File.WriteAllTextAsync(song, Song);
            using AnonymousPipeServerStream toAdapter = new(PipeDirection.Out);
            using AnonymousPipeServerStream fromAdapter = new(PipeDirection.In);
            using AnonymousPipeClientStream adapterIn = new(PipeDirection.In, toAdapter.ClientSafePipeHandle);
            using AnonymousPipeClientStream adapterOut = new(PipeDirection.Out, fromAdapter.ClientSafePipeHandle);
            using DapCommands.DapSession session = new(adapterIn, adapterOut);
            Task adapter = Task.Run(() => session.RunAsync(CancellationToken.None));

            Send(toAdapter, 1, "initialize");
            ReadUntil(fromAdapter, m => (string?)m["event"] == "initialized");
            Send(toAdapter, 2, "launch", new JsonObject { ["program"] = song });
            Send(toAdapter, 3, "setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = song }, ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 4 }) });
            Send(toAdapter, 4, "configurationDone");
            JsonNode stopped = ReadUntil(fromAdapter, m => (string?)m["event"] is "stopped" or "terminated");
            Assert.Equal("stopped", (string?)stopped["event"]);
            Assert.Equal("breakpoint", (string?)stopped["body"]!["reason"]);

            Send(toAdapter, 5, "stackTrace", new JsonObject { ["threadId"] = 1 });
            JsonNode stack = ReadUntil(fromAdapter, m => (int?)m["request_seq"] == 5);
            JsonNode top = stack["body"]!["stackFrames"]![0]!;
            Assert.Equal("late", (string?)top["name"]);
            Assert.Equal(4, (int?)top["line"]);
            Assert.Equal("dbg.amplet", (string?)top["source"]!["name"]);

            Send(toAdapter, 9, "variables", new JsonObject { ["variablesReference"] = 1 });
            JsonNode variables = ReadUntil(fromAdapter, m => (int?)m["request_seq"] == 9);
            JsonArray list = variables["body"]!["variables"]!.AsArray();
            Assert.Contains(list, v => (string?)v!["name"] == "n" && (string?)v["type"] == "Notes");
            Assert.StartsWith("2 notes: 1:0:1:60:100", (string?)list.First(v => (string?)v!["name"] == "n")!["value"], StringComparison.Ordinal);

            // no more breakpoints: continue runs the build to its end
            Send(toAdapter, 6, "setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = song }, ["breakpoints"] = new JsonArray() });
            Send(toAdapter, 7, "continue", new JsonObject { ["threadId"] = 1 });
            ReadUntil(fromAdapter, m => (string?)m["event"] == "terminated");
            Send(toAdapter, 8, "disconnect");
            await adapter.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
        }
    }
}
