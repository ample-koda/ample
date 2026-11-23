using Ample.VisualStudio.Remote;

namespace Ample.VisualStudio;

/// <summary>Builds a song of a project on a server: sends the changes made here first (so the server builds what is on screen), builds there and writes the master WAV.</summary>
internal static class RemoteRun
{
    internal static async System.Threading.Tasks.Task BuildToFileAsync(string projectRoot, string songPath, string wavPath)
    {
        RemoteSettings settings = RemoteSettings.Load(projectRoot) ?? throw new System.InvalidOperationException("This folder is not a project on a server.");
        using RemoteClient client = new(settings.ToOptions());
        client.Status += RemoteStatus.Show;
        SyncResult pushed = await new RemoteSync(client, projectRoot).PushAsync().ConfigureAwait(false);
        if (pushed.Conflicts.Count > 0)
        {
            throw new System.InvalidOperationException("Changed on the server as well, pull first: " + string.Join(", ", pushed.Conflicts));
        }

        await RemoteBuild.BuildToFileAsync(client, songPath.Replace('\\', '/'), wavPath).ConfigureAwait(false);
    }
}
