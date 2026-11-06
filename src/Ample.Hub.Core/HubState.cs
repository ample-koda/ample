using System.Globalization;
using System.Text;
using Ample.Storage.Projects;

namespace Ample.Hub.Core;

/// <summary>One running instance as the hub shows it.</summary>
/// <param name="Record">The registry record.</param>
/// <param name="IsMain">Whether it is the main instance.</param>
public sealed record HubInstance(InstanceRecord Record, bool IsMain)
{
    /// <summary>Gets whether the hub can start it again after a stop: it is the main instance, or it announced how.</summary>
    public bool CanRestart => IsMain || Record.Relaunch is not null;
}

/// <summary>What the tray shows: the main instance, and the other instances that run and want the hub.</summary>
/// <param name="Main">The running main instance, or null when it is stopped.</param>
/// <param name="Others">The other instances, in the order of their start.</param>
/// <param name="Settings">The hub settings.</param>
public sealed record HubState(HubInstance? Main, IReadOnlyList<HubInstance> Others, HubSettings Settings)
{
    /// <summary>Works out the state from the registry records.</summary>
    public static HubState Build(IReadOnlyList<InstanceRecord> all, HubSettings settings, string mainFolder)
    {
        string main = Normalise(mainFolder);
        InstanceRecord? mainRecord = all.FirstOrDefault(r => Normalise(r.Folder) == main);
        List<HubInstance> others = settings.AcceptOthers
            ? all.Where(r => !r.NoHub && Normalise(r.Folder) != main).OrderBy(r => r.Started).Select(r => new HubInstance(r, false)).ToList()
            : [];
        return new HubState(mainRecord is null ? null : new HubInstance(mainRecord, true), others, settings);
    }

    /// <summary>Writes the overview as plain text (for the report window and for a bug report).</summary>
    public string ToReport(DateTimeOffset now)
    {
        StringBuilder text = new();
        text.AppendLine("AMPLE instances");
        text.AppendLine(Main is null ? "Main instance: stopped" : "Main instance: " + Line(Main.Record, now));
        if (Others.Count == 0) text.AppendLine("Other instances: none");
        foreach (HubInstance other in Others) text.AppendLine("Other instance: " + Line(other.Record, now));
        return text.ToString();
    }

    private static string Line(InstanceRecord r, DateTimeOffset now)
    {
        TimeSpan up = now - r.Started;
        string by = r.Agent is null ? r.StartedBy : "agent " + r.Agent.Name;
        return string.Create(CultureInfo.InvariantCulture, $"{r.Name}, 127.0.0.1:{r.Port}, pid {r.Pid}, up {(int)up.TotalHours}h {up.Minutes}m, started by {by}, version {r.Version}");
    }

    private static string Normalise(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/').ToUpperInvariant();
}
