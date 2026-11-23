namespace Ample.VisualStudio.Remote;

/// <summary>The settings of one remote project, kept in <c>.amplerm/remote.txt</c> of the cache folder (never the password: that is in the credential store).</summary>
public sealed class RemoteSettings
{
    /// <summary>Gets or sets the address (https://host for direct, http://127.0.0.1:port as the server sees it for a tunnel).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets how to connect.</summary>
    public RemoteMode Mode { get; set; }

    /// <summary>Gets or sets the SSH target for a tunnel.</summary>
    public string SshHost { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Gets the key under which the password is kept in the credential store.</summary>
    public string CredentialTarget => CredentialStore.Target(Mode == RemoteMode.Tunnel ? SshHost + "/" + Url : Url, User);

    private static string FileOf(string folder) => System.IO.Path.Combine(folder, ".amplerm", "remote.txt");

    /// <summary>Gets whether a folder is a remote cache.</summary>
    public static bool Exists(string folder) => System.IO.File.Exists(FileOf(folder));

    /// <summary>Reads the settings of a cache folder, or null when it is not one.</summary>
    public static RemoteSettings? Load(string folder)
    {
        if (!Exists(folder)) return null;
        RemoteSettings settings = new();
        foreach (string line in System.IO.File.ReadAllLines(FileOf(folder)))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
            if (key == "url") settings.Url = value;
            else if (key == "mode") settings.Mode = value == "tunnel" ? RemoteMode.Tunnel : RemoteMode.Direct;
            else if (key == "ssh") settings.SshHost = value;
            else if (key == "user") settings.User = value;
        }

        return settings;
    }

    /// <summary>Writes the settings into a cache folder.</summary>
    public void Save(string folder)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FileOf(folder))!);
        System.IO.File.WriteAllLines(FileOf(folder), ["url=" + Url, "mode=" + (Mode == RemoteMode.Tunnel ? "tunnel" : "direct"), "ssh=" + SshHost, "user=" + User]);
    }

    /// <summary>Makes the options for a <see cref="RemoteClient"/>, with the password read from the credential store.</summary>
    public RemoteOptions ToOptions(System.Func<string?>? password = null) => new()
    {
        Url = Url,
        Mode = Mode,
        SshHost = SshHost,
        User = User,
        PasswordProvider = password ?? (() => CredentialStore.Read(CredentialTarget)),
    };
}
