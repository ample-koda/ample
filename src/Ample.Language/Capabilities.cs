namespace Ample.Language;

/// <summary>What a host (the server) decides about capabilities, above every project and song: which are forbidden for good (the ceiling) and which are on when nobody says anything (the default). A public playground forbids everything sensitive; a local server only leaves the sensitive ones off by default.</summary>
/// <param name="Forbidden">The capabilities that can never be used here, whatever a project or a song says.</param>
/// <param name="DefaultOn">The capabilities that are on when no scope says otherwise.</param>
public sealed record ServerCapabilities(IReadOnlySet<string> Forbidden, IReadOnlySet<string> DefaultOn)
{
    /// <summary>Gets the names environment() may read on this server (patterns: TZ, LANG*, *_HOME); empty means every name that the song policy allows.</summary>
    public IReadOnlyList<string> EnvironmentAllow { get; init; } = [];

    /// <summary>Gets or sets what the open project says about capabilities (capability to on or off, environment.allow to a list of names): the host sets it when the project opens.</summary>
    public static IReadOnlyDictionary<string, string>? Project { get; set; }

    /// <summary>Gets or sets the ceiling and default of this server, used for every parse that does not bring its own: the host sets it once at startup (ample serve --forbid-capabilities environment,network).</summary>
    public static ServerCapabilities Current { get => current ?? Local; set => current = value; }

    private static ServerCapabilities? current;

    /// <summary>Gets the policy of a local server: nothing forbidden, only the harmless capabilities on by default.</summary>
    public static ServerCapabilities Local { get; } = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(["clock", "os-random"], StringComparer.Ordinal));
}

/// <summary>The effective state of one capability and why.</summary>
/// <param name="Name">The capability.</param>
/// <param name="Effective">Whether it can be used.</param>
/// <param name="Scope">The scope that decided: server, project, song, default.</param>
/// <param name="Reason">A sentence for hover and diagnostics.</param>
public sealed record CapabilityState(string Name, bool Effective, string Scope, string Reason);

/// <summary>A problem in the capability settings (an unknown name, a capability switched on that a higher scope forbids or turned off).</summary>
/// <param name="Line">The 0-based line of the settings, or -1 for the project.</param>
/// <param name="Id">The issue id.</param>
/// <param name="Message">The message.</param>
/// <param name="Needle">What to underline on that line.</param>
/// <param name="Suggestion">A replacement for the needle, or null.</param>
public sealed record CapabilityIssue(int Line, string Id, string Message, string Needle, string? Suggestion);

/// <summary>The capability policy of a song (docs/language/16): server ceiling and default, then the project, then the settings of the song in source order. A lower scope can only narrow: an explicit <c>off</c> locks the capability, and a later <c>on</c> is an error.</summary>
public sealed class CapabilityPolicy
{
    /// <summary>Gets the names of the capabilities.</summary>
    public static IReadOnlyList<string> Names { get; } = ["clock", "os-random", "environment", "network", "filesystem", "external-process", "external-plugin"];

    private readonly Dictionary<string, CapabilityState> states;

    private readonly List<string[]> allowLists;

    private CapabilityPolicy(Dictionary<string, CapabilityState> states, List<string[]>? allowLists = null)
    {
        this.states = states;
        this.allowLists = allowLists ?? [];
    }

    /// <summary>The lists of allowed environment names, one per scope that set one: a name must match every list (a scope can only narrow).</summary>
    /// <param name="capability">The capability (only environment has lists).</param>
    /// <returns>The lists.</returns>
    public IReadOnlyList<IReadOnlyList<string>> AllowLists(string capability) => capability == "environment" ? [.. allowLists.Select(l => (IReadOnlyList<string>)l)] : [];

    /// <summary>Whether a name may be read through a capability: always, unless allow-lists narrow it.</summary>
    /// <param name="capability">The capability.</param>
    /// <param name="name">The environment variable.</param>
    /// <returns>True when every list allows it.</returns>
    public bool NameAllowed(string capability, string name) => capability != "environment" || allowLists.All(list => list.Any(pattern => Matches(pattern, name)));

    private static bool Matches(string pattern, string name) => pattern == "*" || (pattern.StartsWith('*') && pattern.EndsWith('*') && pattern.Length > 2 ? name.Contains(pattern[1..^1], StringComparison.Ordinal) : pattern.StartsWith('*') ? name.EndsWith(pattern[1..], StringComparison.Ordinal) : pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == name);

    /// <summary>Gets a policy that allows everything (for tests and tools that do not care).</summary>
    public static CapabilityPolicy AllowAll { get; } = new(Names.ToDictionary(n => n, n => new CapabilityState(n, true, "server", "enabled"), StringComparer.Ordinal));

    /// <summary>Makes a policy in which the given capabilities are denied (with the reason) and everything else is allowed: for tests and tools that already know the outcome.</summary>
    /// <param name="denied">Capability to reason.</param>
    /// <param name="environmentAllow">The names environment() may read, or null for any.</param>
    /// <returns>The policy.</returns>
    public static CapabilityPolicy Denying(IReadOnlyDictionary<string, string> denied, IReadOnlyList<string>? environmentAllow = null) => new(Names.ToDictionary(n => n, n => denied.TryGetValue(n, out string? why) ? new CapabilityState(n, false, "test", why) : new CapabilityState(n, true, "test", "enabled"), StringComparer.Ordinal), environmentAllow is { Count: > 0 } ? [[.. environmentAllow]] : null);

    /// <summary>Gets the state of every capability.</summary>
    public IReadOnlyList<CapabilityState> States => [.. Names.Select(n => states[n])];

    /// <summary>Whether a capability can be used.</summary>
    /// <param name="name">The capability.</param>
    /// <returns>True when it can.</returns>
    public bool Allowed(string name) => states.TryGetValue(name, out CapabilityState? s) && s.Effective;

    /// <summary>Why a capability cannot be used (empty when it can).</summary>
    /// <param name="name">The capability.</param>
    /// <returns>The reason.</returns>
    public string Why(string name) => states.TryGetValue(name, out CapabilityState? s) && !s.Effective ? s.Reason : string.Empty;

    /// <summary>Works out the effective policy.</summary>
    /// <param name="server">The ceiling and the default of the host.</param>
    /// <param name="project">What the project says (capability to on or off), or null.</param>
    /// <param name="song">The capability settings of the song in source order: name, value and 0-based line.</param>
    /// <param name="issues">Receives the configuration problems.</param>
    /// <returns>The policy.</returns>
    public static CapabilityPolicy Resolve(ServerCapabilities server, IReadOnlyDictionary<string, string>? project, IEnumerable<(string Name, string Value, int Line)> song, List<CapabilityIssue> issues)
    {
        Dictionary<string, bool> value = Names.ToDictionary(n => n, n => server.DefaultOn.Contains(n) && !server.Forbidden.Contains(n), StringComparer.Ordinal);
        Dictionary<string, string?> locked = Names.ToDictionary(n => n, _ => (string?)null, StringComparer.Ordinal);
        Dictionary<string, string> decided = Names.ToDictionary(n => n, n => server.Forbidden.Contains(n) ? "server" : "default", StringComparer.Ordinal);
        List<string[]> lists = server.EnvironmentAllow.Count > 0 ? [[.. server.EnvironmentAllow]] : [];
        List<(string Scope, string Name, string Value, int Line)> entries = [];
        if (project is not null) entries.AddRange(project.Select(p => ("project", p.Key, p.Value, -1)));
        entries.AddRange(song.Select(s => ("song", s.Name, s.Value, s.Line)));
        foreach ((string scope, string name, string raw, int line) in entries)
        {
            string text = raw.Trim().ToLowerInvariant();
            if (name.EndsWith(".allow", StringComparison.Ordinal))
            {
                string[] patterns = [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                if (name != "environment.allow") issues.Add(new CapabilityIssue(line, "capability-config", $"capabilities.{name}: only environment has a list of allowed names (environment.allow).", name, null));
                else if (patterns.Length == 0) issues.Add(new CapabilityIssue(line, "capability-config", "capabilities.environment.allow needs the names it allows, like \"TZ, LANG*\".", raw, null));
                else lists.Add(patterns);
                continue;
            }

            if (!value.ContainsKey(name))
            {
                string? near = Names.OrderBy(n => Distance(name, n)).FirstOrDefault(n => Distance(name, n) <= 3);
                issues.Add(new CapabilityIssue(line, "capability-config", $"There is no capability \"{name}\" (capabilities: {string.Join(", ", Names)}).", name, near));
                continue;
            }

            if (text is not ("on" or "off"))
            {
                issues.Add(new CapabilityIssue(line, "capability-config", $"capabilities.{name} is on or off (found \"{raw}\").", raw, null));
                continue;
            }

            string label = scope == "song" ? $"song (line {line + 1})" : scope;
            if (text == "off")
            {
                value[name] = false;
                locked[name] ??= label;
                if (!server.Forbidden.Contains(name)) decided[name] = label;
                continue;
            }

            if (server.Forbidden.Contains(name))
            {
                issues.Add(new CapabilityIssue(line, "capability-config", $"capabilities.{name} on has no effect: the capability is disabled by server policy.", "on", null));
            }
            else if (locked[name] is { } by)
            {
                issues.Add(new CapabilityIssue(line, "capability-config", $"capabilities.{name} was turned off by {by}: a capability cannot be turned on again (fatal configuration error).", "on", null));
            }
            else
            {
                value[name] = true;
                decided[name] = label;
            }
        }

        Dictionary<string, CapabilityState> result = new(StringComparer.Ordinal);
        foreach (string name in Names)
        {
            bool forbidden = server.Forbidden.Contains(name);
            bool effective = !forbidden && value[name];
            string reason = effective ? "enabled"
                : forbidden ? "disabled by server policy"
                : locked[name] is { } l ? (l.StartsWith("song", StringComparison.Ordinal) ? $"permanently disabled by this song, {l["song ".Length..]}" : $"disabled by {l} policy")
                : $"off by default: enable it with settings {{ capabilities.{name} on }}";
            result[name] = new CapabilityState(name, effective, forbidden ? "server" : decided[name].StartsWith("song", StringComparison.Ordinal) ? "song" : decided[name], reason);
        }

        return new CapabilityPolicy(result, lists);
    }

    private static int Distance(string a, string b)
    {
        int[] row = [.. Enumerable.Range(0, b.Length + 1)];
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int keep = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = keep;
            }
        }

        return row[b.Length];
    }
}

/// <summary>The rule that nothing which is not the owner may widen the policy of a song: an AI proposal, a pasted snippet, a module. It compares two versions of a song and reports what would make the policy wider.</summary>
public static class PolicyGuard
{
    /// <summary>Lists what the second version widens compared to the first: a capability that becomes usable, a line that turns a capability off and is gone, a limit that is raised.</summary>
    /// <param name="before">The song as it is.</param>
    /// <param name="after">The proposed song.</param>
    /// <returns>The reasons, empty when the policy stays the same or gets narrower.</returns>
    public static IReadOnlyList<string> Widening(AmpleDocument before, AmpleDocument after)
    {
        List<string> reasons = [];
        foreach (CapabilityState a in after.Capabilities)
        {
            CapabilityState? b = before.Capabilities.FirstOrDefault(x => x.Name == a.Name);
            if (a.Effective && (b is null || !b.Effective)) reasons.Add($"it turns on the capability {a.Name}");
        }

        foreach ((string name, string value, int _) in before.CapabilitySettings.Where(x => x.Value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            if (!after.CapabilitySettings.Any(x => x.Name == name && x.Value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))) reasons.Add($"it removes the line that turns {name} off ({value})");
        }

        foreach ((string key, long effective, string _, long _) in after.Limits)
        {
            (string Key, long Effective, string Source, long Cap) old = before.Limits.FirstOrDefault(x => x.Key == key);
            if (old.Key is not null && effective > old.Effective) reasons.Add($"it raises limits.{key} from {old.Effective} to {effective}");
        }

        return reasons;
    }
}
