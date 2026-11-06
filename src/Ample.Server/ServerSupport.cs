using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>Options of the local server.</summary>
/// <param name="ProjectRoot">The project folder.</param>
/// <param name="AmpleRoot">The AMPLE folder (spec, docs, web, plugins).</param>
/// <param name="Port">Port on 127.0.0.1; 0 picks a free one.</param>
/// <param name="Token">Session token; null makes a random one.</param>
/// <param name="AllowLan">Accept other host names (off by default; use only on a trusted network).</param>
/// <param name="LibraryHome">The library folder (null: <c>AMPLE_HOME</c>, else the local application data of the user).</param>
/// <param name="MaxProjectUploadBytes">The maximum project-file upload size (default: 512 MiB).</param>
public sealed record ServerOptions(string ProjectRoot, string AmpleRoot, int Port = 0, string? Token = null, bool AllowLan = false, string? LibraryHome = null, long MaxProjectUploadBytes = 512L * 1024 * 1024)
{
    /// <summary>Gets the declared source of the server start.</summary>
    public string StartedBy { get; init; } = "cli";

    /// <summary>Gets how the tray hub can start this server again (shown in the instance registry), or null.</summary>
    public Ample.Storage.Projects.InstanceRelaunch? Relaunch { get; init; }

    /// <summary>Gets whether the tray hub must leave this server alone (<c>--no-hub</c>, or <c>AMPLE_NO_HUB=1</c>).</summary>
    public bool NoHub { get; init; }

    /// <summary>Gets whether the sign-in with the stored user and password is on (<c>--login</c>): its session tokens work next to the launch token.</summary>
    public bool Login { get; init; }

    /// <summary>Gets whether any host name is accepted and the server listens on every address (<c>--allow-all</c>; only together with <see cref="Login"/>).</summary>
    public bool AllowAll { get; init; }

    /// <summary>Gets the address to listen on (an IP address), or null for the default: <c>0.0.0.0</c> with <c>--allow-lan</c> or <c>--allow-all</c>, else <c>127.0.0.1</c>. A server behind a reverse proxy on the same machine uses <c>127.0.0.1</c> together with <c>--allow-all</c>, so the proxy can send any host name but nothing else can reach the port.</summary>
    public string? BindAddress { get; init; }

    /// <summary>Gets whether this is the public Lite (<c>--lite-only</c>): only the Lite routes and files exist (everything else is a 404), no sign-in or token is asked, the limits of the public profile hold and builds run in a process of their own.</summary>
    public bool LiteOnly { get; init; }

    /// <summary>Gets whether a project that has no song at all gets the demo song when this server opens it (<c>ample serve</c> and <c>ample open</c> turn it on; tests and helper servers do not).</summary>
    public bool SeedDemo { get; init; }

    /// <summary>Gets the self-reported agent when an agent started this server.</summary>
    public Ample.Storage.Projects.InstanceAgent? Agent { get; init; }
}

/// <summary>One recorded API operation (the source of the OpenAPI document).</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Route.</param>
/// <param name="Tag">Area.</param>
/// <param name="Summary">What it does.</param>
/// <param name="Mutating">True when it changes state (it is journaled as a command).</param>
/// <param name="RequiresToken">True when the route requires the session token.</param>
public sealed record ApiOperation(string Method, string Path, string Tag, string Summary, bool Mutating, bool RequiresToken);

/// <summary>Server-sent events: build progress, document changes, history changes.</summary>
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> subscribers = new();

    /// <summary>Publishes an event to everyone listening.</summary>
    public void Publish(string type, object data)
    {
        string message = $"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n";
        foreach (Channel<string> c in subscribers.Values)
        {
            c.Writer.TryWrite(message);
        }
    }

    /// <summary>Subscribes; dispose the result to stop.</summary>
    public (ChannelReader<string> Reader, IDisposable Subscription) Subscribe()
    {
        Guid id = Guid.NewGuid();
        Channel<string> channel = Channel.CreateUnbounded<string>();
        subscribers[id] = channel;
        return (channel.Reader, new Unsubscribe(() => subscribers.TryRemove(id, out _)));
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>Writes 64-bit numbers as strings: seeds do not fit a JavaScript number.</summary>
public sealed class UInt64AsStringConverter : System.Text.Json.Serialization.JsonConverter<ulong>
{
    /// <inheritdoc />
    public override ulong Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.String ? ulong.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : reader.GetUInt64();

    /// <inheritdoc />
    public override void Write(System.Text.Json.Utf8JsonWriter writer, ulong value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Maps stable error codes to HTTP statuses and builds RFC 9457 problem details.</summary>
public static class Problems
{
    /// <summary>Gets the HTTP status for a diagnostic code.</summary>
    public static int StatusOf(string code) => code switch
    {
        "AMP5204" or "AMP5240" or "AMP5260" or "AMP5280" or "AMP5245" or "AMP5292" or "AMP5294" or "AMP5300" or "AMP5301" => 409,
        "AMP5302" or "AMP5305" => 422,
        "AMP5223" or "AMP5243" or "AMP5210" or "AMP5290" or "AMP5291" or "AMP5303" => 404,
        "AMP5201" or "AMP5202" or "AMP5203" => 400,
        "AMP5232" or "AMP5293" => 403,
        "AMP5296" => 422,
        _ => 400,
    };

    /// <summary>Builds the problem body.</summary>
    public static object Body(string code, string message, int status) => new { type = "https://ample.invalid/errors/" + code, title = code, status, detail = message, code };

    /// <summary>Builds the body for an exception.</summary>
    public static (int Status, object Body) From(AmpleException ex) => (StatusOf(ex.DiagnosticCode), Body(ex.DiagnosticCode, ex.Message, StatusOf(ex.DiagnosticCode)));
}
