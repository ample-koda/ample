using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

public sealed class AiHowToParameterTests
{
    [Fact]
    public async Task Lookup_returns_matching_how_tos_and_search_results()
    {
        string? ampleRoot = AppContext.BaseDirectory;
        while (ampleRoot is not null && !File.Exists(Path.Combine(ampleRoot, "Ample.sln"))) ampleRoot = Path.GetDirectoryName(ampleRoot);
        string root = ampleRoot ?? throw new InvalidOperationException("AMPLE root not found.");
        string folder = Path.Combine(Path.GetTempPath(), "ample-ai-lookup-" + Guid.NewGuid().ToString("N"));
        string library = folder + "-library";
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "lookup", null, CancellationToken.None)) { }
            await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project lookup\n");
            await using ProjectSession session = await ProjectSession.OpenAsync(new ServerOptions(folder, root, LibraryHome: library), new EventBus(), CancellationToken.None);

            var howto = AiContext.Lookup(session, "howto", string.Empty, "automation", null);
            Assert.True(howto["found"]!.GetValue<bool>());
            Assert.Contains("automation", howto["howtos"]![0]!["about"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            var search = AiContext.Lookup(session, "search", string.Empty, "volume", null);
            Assert.True(search["found"]!.GetValue<bool>());
        }
        finally
        {
            try { Directory.Delete(folder, true); Directory.Delete(library, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Automation_how_to_only_names_parameters_declared_by_its_block_template()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root);
        string spec = Path.Combine(root ?? throw new InvalidOperationException("AMPLE root not found."), "spec");
        Dictionary<string, HashSet<string>> parameters = Directory.GetFiles(Path.Combine(spec, "templates"), "*.json")
            .Select(file => JsonDocument.Parse(File.ReadAllText(file)))
            .ToDictionary(doc => doc.RootElement.GetProperty("id").GetString()!, doc => doc.RootElement.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        MethodInfo method = typeof(AiContext).GetMethod("HowTo", BindingFlags.Static | BindingFlags.NonPublic)!;
        JsonElement howTos = JsonSerializer.SerializeToElement(method.Invoke(null, ["automation"]));
        string text = howTos.GetProperty("howtos")[0].GetProperty("howto").GetString()!;

        foreach (Match match in Regex.Matches(text, @"\b(?<type>[a-z][a-z-]*)\.(?<parameter>[a-z_]+)"))
        {
            string type = match.Groups["type"].Value;
            string parameter = match.Groups["parameter"].Value;
            Assert.True(parameters.TryGetValue(type, out HashSet<string>? declared), $"How-to names unknown block type '{type}'.");
            Assert.Contains(parameter, declared!);
        }
    }
}
