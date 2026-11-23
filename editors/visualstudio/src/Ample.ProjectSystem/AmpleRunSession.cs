using System.Media;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Ample.VisualStudio;

internal sealed class AmpleRunSession : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly string _projectId;
    private readonly int _processId;
    private readonly DateTimeOffset _processStart;
    private readonly bool _ownsInstance;
    private readonly WebClient _client = new();
    private System.IO.MemoryStream? _audioStream;
    private SoundPlayer? _player;

    private AmpleRunSession(string baseUrl, string token, OpenResult result)
    {
        _baseUrl = baseUrl;
        _token = token;
        _projectId = result.ProjectId;
        _processId = result.ProcessId;
        _processStart = DateTimeOffset.Parse(result.ProcessStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        _ownsInstance = result.Status == "started";
        _client.Headers["X-Ample-Token"] = token;
        _client.Headers[HttpRequestHeader.ContentType] = "application/json";
    }

    internal static AmpleRunSession Open(string projectRoot, bool attachOnly)
    {
        string attachOption = attachOnly ? " --attach-only" : string.Empty;
        System.Diagnostics.ProcessStartInfo start = new(AmpleCli.For(AmpleCli.ProjectFileIn(projectRoot)), $"open --no-open --json{attachOption} {Quote(projectRoot)}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The AMPLE CLI did not start.");
        StringBuilder error = new();
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) error.AppendLine(args.Data); };
        process.BeginErrorReadLine();
        string? output = process.StandardOutput.ReadLine();
        if (output is null)
        {
            process.WaitForExit();
            throw new InvalidOperationException($"AMPLE could not open the project (exit code {process.ExitCode}): {error.ToString().Trim()}");
        }

        OpenResult result = Deserialize<OpenResult>(output.Trim()) ?? throw new InvalidOperationException("AMPLE returned invalid open details.");
        if (result.Status is not ("started" or "attached") || string.IsNullOrWhiteSpace(result.ProjectId) || result.ProcessId < 1
            || string.IsNullOrWhiteSpace(result.ProcessStart)) throw new InvalidOperationException("AMPLE returned incomplete instance ownership details.");
        string launchUrl = result.LaunchUrl;
        Uri launch = new(launchUrl);
        string fragment = launch.Fragment.TrimStart('#');
        string? token = fragment.StartsWith("token=", StringComparison.Ordinal) ? Uri.UnescapeDataString(fragment.Substring(6)) : null;
        UriBuilder server = new(launch) { Fragment = string.Empty, Query = string.Empty };
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("AMPLE returned an invalid launch URL.");
        AmpleRunSession session = new(server.Uri.ToString().TrimEnd('/'), token!, result);
        string ping = Call("ping", () => session._client.DownloadString(session._baseUrl + "/api/ping"));
        if (string.IsNullOrWhiteSpace(ping)) throw new InvalidOperationException("The AMPLE instance did not respond to its health check.");
        return session;
    }

    /// <summary>Builds the song through the instance and writes its master WAV to <paramref name="wavPath"/> (the file F5's player plays).</summary>
    internal void BuildToFile(string songPath, string wavPath)
    {
        string request = Serialize(new BuildRequest { Path = songPath, Intent = "bake" });
        _client.Headers[HttpRequestHeader.ContentType] = "application/json";   // WebClient clears it after every request
        string response = Call("build " + songPath, () => _client.UploadString(_baseUrl + "/api/build", "POST", request));
        BuildResult result = Deserialize<BuildResult>(response) ?? throw new InvalidOperationException("AMPLE returned an empty build result.");
        if (string.IsNullOrWhiteSpace(result.MasterTakeId)) throw new InvalidOperationException("The AMPLE build did not produce a master take.");
        string takeUrl = _baseUrl + "/api/takes/" + Uri.EscapeDataString(result.MasterTakeId) + "/download?format=wav";
        byte[] wav = Call("download " + result.MasterTakeId, () => _client.DownloadData(takeUrl));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(wavPath)!);
        System.IO.File.WriteAllBytes(wavPath, wav);
    }

    internal void BuildAndPlay(string songPath)
    {
        string request = Serialize(new BuildRequest { Path = songPath, Intent = "bake" });
        _client.Headers[HttpRequestHeader.ContentType] = "application/json";   // WebClient clears it after every request
        string response = Call("build " + songPath, () => _client.UploadString(_baseUrl + "/api/build", "POST", request));
        BuildResult result = Deserialize<BuildResult>(response)
            ?? throw new InvalidOperationException("AMPLE returned an empty build result.");
        if (string.IsNullOrWhiteSpace(result.MasterTakeId)) throw new InvalidOperationException("The AMPLE build did not produce a master take.");
        StopPlayback();
        string takeUrl = _baseUrl + "/api/takes/" + Uri.EscapeDataString(result.MasterTakeId) + "/download?format=wav";
        byte[] wav = Call("download " + result.MasterTakeId, () => _client.DownloadData(takeUrl));
        _audioStream = new System.IO.MemoryStream(wav, false);
        _player = new SoundPlayer(_audioStream);
        _player.Play();
    }

    /// <summary>Runs one HTTP call to the instance; a failure says what the server answered (its problem detail), not only the status code.</summary>
    private static T Call<T>(string what, Func<T> call)
    {
        try
        {
            T result = call();
            AmpleLog.Write("ok " + what);
            return result;
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse response)
        {
            string body;
            using (System.IO.StreamReader reader = new(response.GetResponseStream()!)) body = reader.ReadToEnd();
            AmpleLog.Write($"failed {what}: {(int)response.StatusCode} {response.ResponseUri} {body}");
            throw new InvalidOperationException($"AMPLE could not {what}: {(int)response.StatusCode} {body}", ex);
        }
    }

    internal void StopPlayback()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        _audioStream?.Dispose();
        _audioStream = null;
    }

    public void Dispose()
    {
        StopPlayback();
        StopOwnedInstance();
        _client.Dispose();
    }

    private void StopOwnedInstance()
    {
        if (!_ownsInstance) return;
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(_processId);
            if (new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) != _processStart.ToUniversalTime()) return;
            string ping = _client.DownloadString(_baseUrl + "/api/ping");
            PingResult? identity = Deserialize<PingResult>(ping);
            if (identity?.ProjectId != _projectId) return;
            _client.Headers[HttpRequestHeader.ContentType] = "application/json";
            _client.UploadString(_baseUrl + "/api/server/shutdown", "POST", "{}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.InvalidOperationException or WebException or SerializationException or FormatException) { }
    }

    private static string Serialize<T>(T value)
    {
        using System.IO.MemoryStream stream = new();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Quote(string value)
    {
        System.Text.StringBuilder result = new("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') slashes++;
            else
            {
                if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static T? Deserialize<T>(string value)
    {
        using System.IO.MemoryStream stream = new(Encoding.UTF8.GetBytes(value));
        return (T?)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
    }

    [DataContract]
    private sealed class OpenResult
    {
        [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
        [DataMember(Name = "projectId")] public string ProjectId { get; set; } = string.Empty;
        [DataMember(Name = "pid")] public int ProcessId { get; set; }
        [DataMember(Name = "processStart")] public string ProcessStart { get; set; } = string.Empty;
        [DataMember(Name = "launchUrl")] public string LaunchUrl { get; set; } = string.Empty;
    }

    [DataContract]
    private sealed class PingResult
    {
        [DataMember(Name = "id")] public string ProjectId { get; set; } = string.Empty;
    }

    [DataContract]
    private sealed class BuildRequest
    {
        [DataMember(Name = "path")] public string Path { get; set; } = string.Empty;
        [DataMember(Name = "intent")] public string Intent { get; set; } = string.Empty;
    }

    [DataContract]
    private sealed class BuildResult
    {
        [DataMember(Name = "masterTakeId")] public string? MasterTakeId { get; set; }
    }
}
