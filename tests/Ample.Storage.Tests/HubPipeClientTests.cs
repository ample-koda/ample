using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Ample.Storage.Projects;

namespace Ample.Core.Tests;

public sealed class HubPipeClientTests
{
    [Fact]
    public async Task Exchanges_one_versioned_correlated_json_line()
    {
        string pipeName = "ample-hub-test-" + Guid.NewGuid().ToString("N");
        Guid requestId = Guid.NewGuid();
        using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task<HubPipeResponse?> client = HubPipeClient.SendAsync(pipeName, new(HubPipeProtocol.Version, requestId, "start", "project-id", "C:\\Songs", new("Codex", "Testing the hub contract.")), TimeSpan.FromSeconds(3), CancellationToken.None);
        await server.WaitForConnectionAsync();
        using StreamReader reader = new(server, new UTF8Encoding(false), leaveOpen: true);
        string requestLine = await reader.ReadLineAsync() ?? throw new EndOfStreamException();
        HubPipeRequest request = JsonSerializer.Deserialize<HubPipeRequest>(requestLine, HubPipeProtocol.JsonOptions) ?? throw new InvalidDataException();
        Assert.Equal(requestId, request.RequestId);
        Assert.Equal("start", request.Operation);
        using StreamWriter writer = new(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new HubPipeResponse(HubPipeProtocol.Version, requestId, true, ProjectId: "project-id", Status: "started", LaunchUrl: "http://127.0.0.1:52341/#token=fake-token"), HubPipeProtocol.JsonOptions));

        HubPipeResponse response = await client ?? throw new InvalidDataException();
        Assert.True(response.Success);
        Assert.Equal("started", response.Status);
        Assert.Equal("project-id", response.ProjectId);
    }

    [Fact]
    public async Task Returns_null_when_no_hub_is_listening()
    {
        HubPipeResponse? response = await HubPipeClient.SendAsync("ample-hub-missing-" + Guid.NewGuid().ToString("N"), new(HubPipeProtocol.Version, Guid.NewGuid(), "list"), TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Null(response);
    }
}
