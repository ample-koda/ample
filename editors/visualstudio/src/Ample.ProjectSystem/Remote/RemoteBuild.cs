namespace Ample.VisualStudio.Remote;

/// <summary>Builds a song on the server and brings the master here: the same two calls as a local build (<c>/api/build</c> and the take download).</summary>
public static class RemoteBuild
{
    /// <summary>Builds <paramref name="songPath"/> (project-relative) on the server and writes its master WAV to <paramref name="wavPath"/>.</summary>
    public static async System.Threading.Tasks.Task BuildToFileAsync(RemoteClient client, string songPath, string wavPath, System.Threading.CancellationToken cancellationToken = default)
    {
        string request = "{\"path\":" + Quote(songPath) + ",\"intent\":\"bake\"}";
        string response = await client.PostJsonAsync("/api/build", request, cancellationToken).ConfigureAwait(false);
        System.Text.RegularExpressions.Match take = System.Text.RegularExpressions.Regex.Match(response, "\"masterTakeId\"\\s*:\\s*\"([^\"]+)\"");
        if (!take.Success) throw new System.InvalidOperationException("The server built " + songPath + " but gave no master take.");
        byte[] wav = await client.GetBytesAsync("/api/takes/" + System.Uri.EscapeDataString(take.Groups[1].Value) + "/download?format=wav", cancellationToken).ConfigureAwait(false);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(wavPath)!);
        System.IO.File.WriteAllBytes(wavPath, wav);
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
