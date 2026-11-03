using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ample.Storage.Projects;

/// <summary>Version 1 request and response envelope for the per-user AMPLE hub pipe.</summary>
public static class HubPipeProtocol
{
    /// <summary>The protocol version shared by the CLI and hub.</summary>
    public const int Version = 1;

    /// <summary>The largest UTF-8 JSON line accepted from either side.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>The time allowed for connecting to and exchanging one request with the hub.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Gets JSON options for the protocol's camel-case field names.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Gets the deterministic per-user pipe name; the server must also enforce current-user-only access.</summary>
    public static string PipeNameForCurrentUser()
    {
        string identity = Environment.UserDomainName + "-" + Environment.UserName;
        string safe = string.Concat(identity.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')).Trim('-');
        if (safe.Length == 0) safe = "user";
        if (safe.Length > 64) safe = safe[..48] + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12].ToLowerInvariant();
        return "ample-hub-" + safe;
    }
}

/// <summary>A single JSON-lines request to the hub.</summary>
public sealed record HubPipeRequest(int Version, Guid RequestId, string Operation, string? ProjectId = null, string? ProjectFolder = null, HubPipeAgent? Agent = null, string? HomeFolder = null);

/// <summary>Self-reported attribution for an agent-started project.</summary>
public sealed record HubPipeAgent(string Name, string Reason);

/// <summary>A single JSON-lines response from the hub.</summary>
public sealed record HubPipeResponse(int Version, Guid RequestId, bool Success, string? ErrorCode = null, string? Message = null, string? ProjectId = null, string? Status = null, string? LaunchUrl = null, IReadOnlyList<HubPipeInstance>? Instances = null);

/// <summary>A non-secret instance summary returned by the hub's list operation.</summary>
public sealed record HubPipeInstance(string ProjectId, string Name, string Folder, int Port, string StartedBy, HubPipeAgent? Agent = null);
