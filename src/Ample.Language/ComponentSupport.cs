using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>Which version of a component a reference gets (docs/language/19-components.md, "Versions"): the written request, else the one marked current, else the highest release, never a pre-release by surprise.</summary>
public static class ComponentVersions
{
    /// <summary>Picks the default among the versions of one component family: the one marked <c>current</c> (when more are marked the highest of them), else the highest release, else (only pre-releases exist) the highest pre-release.</summary>
    /// <param name="versions">The versions of one family.</param>
    /// <returns>The default, or null when there is none.</returns>
    public static ComponentDefinition? Default(IEnumerable<ComponentDefinition> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        List<ComponentDefinition> all = [.. versions];
        if (all.Count == 0) return null;
        ComponentDefinition? current = all.Where(v => v.Current).OrderByDescending(v => v.Version).FirstOrDefault();
        if (current is not null) return current;
        ComponentDefinition? release = all.Where(v => !v.Version.IsPrerelease).OrderByDescending(v => v.Version).FirstOrDefault();
        return release ?? all.OrderByDescending(v => v.Version).First();
    }

    /// <summary>Picks the version a request asks for among the versions of one family.</summary>
    /// <param name="versions">The versions of one family.</param>
    /// <param name="request">The request (the default when it names no version).</param>
    /// <returns>The version, or null when none matches.</returns>
    public static ComponentDefinition? Pick(IEnumerable<ComponentDefinition> versions, VersionRequest request)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(request);
        List<ComponentDefinition> all = [.. versions];
        if (request.IsDefault) return Default(all);
        return all.Where(v => request.Matches(v.Version)).OrderByDescending(v => v.Version).FirstOrDefault();
    }
}

/// <summary>Turns the process of a component into the helper that the engine evaluates: the same helper system, types, limits and capabilities as every helper (A5 of the plan).</summary>
public static partial class ComponentHelper
{
    /// <summary>The name of the hidden parameter that says whether an optional input is connected.</summary>
    /// <param name="input">The input name.</param>
    /// <returns>The parameter name.</returns>
    public static string ConnectedName(string input) => input + "__connected";

    [GeneratedRegex(@"\bconnected\s*\(\s*([A-Za-z_][\w-]*)\s*\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConnectedCall();

    [GeneratedRegex(@"\b([A-Za-z_][\w-]*)\.connected\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConnectedField();

    /// <summary>The parameters of the helper, in order: for every input its notes (and for an optional input a Bool that says whether it is connected), then every property.</summary>
    /// <param name="d">The component.</param>
    /// <returns>The parameters as (type, name).</returns>
    public static IReadOnlyList<(string Type, string Name)> Parameters(ComponentDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        List<(string, string)> list = [];
        foreach (ComponentPort input in d.Inputs)
        {
            list.Add((input.Type.TrimEnd('?'), input.Name));
            if (input.Optional || input.Fallback is not null) list.Add(("Bool", ConnectedName(input.Name)));
        }

        foreach (ComponentProperty p in d.Properties) list.Add((p.Type.TrimEnd('?'), p.Name));
        return list;
    }

    /// <summary>The text of the helper. Its first line stands where the <c>process</c> line stands and its body is the process body as written, so that the line of a problem in the helper is the line of the component.</summary>
    /// <param name="d">The component.</param>
    /// <param name="all">The components that are visible (a call of another Notes component is written as the call of its helper), or null.</param>
    /// <param name="issues">Receives the problems with the calls of other components, or null.</param>
    /// <returns>The helper declaration.</returns>
    public static string Text(ComponentDefinition d, IReadOnlyList<ComponentDefinition>? all = null, List<NestIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        string head = $"helper {d.HelperName}({string.Join(", ", Parameters(d).Select(p => p.Type + " " + p.Name))}) -> Notes {{";
        List<string> body = [head];
        foreach (string line in all is null ? d.ProcessBody : ComponentNesting.Rewrite(d, all, issues))
        {
            string rewritten = ConnectedCall().Replace(line, m => ConnectedName(m.Groups[1].Value));
            rewritten = ConnectedField().Replace(rewritten, m => d.Inputs.Any(i => i.Name == m.Groups[1].Value) ? ConnectedName(m.Groups[1].Value) : m.Value);
            body.Add(rewritten);
        }

        body.Add("}");
        return string.Join('\n', body);
    }
}
