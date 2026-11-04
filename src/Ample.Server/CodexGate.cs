namespace Ample.Server;

/// <summary>
/// One Codex account is used by one task at a time: its login has a refresh token that works once, so two Codex processes on the same home would spoil each other's login. The gate is a lock
/// file per account (the user's own Codex home, or the Codex home of a project), so it holds across the projects and the servers of this machine. Tasks wait in line instead of being refused.
/// </summary>
internal static class CodexGate
{
    /// <summary>Waits until the account is free and holds it until the result is disposed.</summary>
    public static async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
    {
        string folder = Path.Combine(Ample.Audio.IO.AmpleHome.DefaultPath(), "codex-locks");
        Directory.CreateDirectory(folder);
        string name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..24] + ".lock";
        string path = Path.Combine(folder, name);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);   // somebody else has the account
            }
        }
    }
}
