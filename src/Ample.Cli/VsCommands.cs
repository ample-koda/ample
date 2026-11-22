using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Cli;

/// <summary>The <c>ample vs init</c> command: puts a Visual Studio project file into an existing AMPLE folder, so Visual Studio becomes
/// another view of the same project. It only adds the project file, <c>Ample.targets</c> and <c>Rules\</c>; songs, <c>.ample\</c> and
/// history are never touched, and a file that already exists is left alone.</summary>
internal static class VsCommands
{
    private static readonly string[] Files = ["Ample.ampleproj", "Ample.targets", "Rules/AmpleDebugger.xaml", "Rules/AmpleProject.xaml", "Rules/DebuggerGeneral.xaml"];

    internal static Command Create(TextWriter output)
    {
        Argument<string> folder = new("folder") { Description = "The AMPLE project folder." };
        Option<string?> name = new("--name") { Description = "The project file name without extension (default: the folder name)." };
        Option<string?> song = new("--song") { Description = "The song F5 plays (default: the first .amplet in the folder)." };
        Command init = new("init", "Writes a Visual Studio project (.ampleproj) into an existing AMPLE folder.") { folder, name, song };
        init.SetAction(parse =>
        {
            string root = Path.GetFullPath(parse.GetValue(folder)!);
            if (!Directory.Exists(root)) { output.WriteLine($"No such folder: {root}"); return 1; }
            string baseName = parse.GetValue(name) ?? new DirectoryInfo(root).Name;
            string startup = parse.GetValue(song) ?? FirstSong(root) ?? "song.amplet";
            foreach (string file in Files)
            {
                string target = Path.Combine(root, file == "Ample.ampleproj" ? baseName + ".ampleproj" : file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) { output.WriteLine($"kept    {Path.GetRelativePath(root, target)} (already there)"); continue; }
                string text = Resource(file);
                if (file == "Ample.ampleproj") text = text.Replace("<AmpleStartupSong>demo.amplet</AmpleStartupSong>",
                    $"<AmpleStartupSong>{System.Security.SecurityElement.Escape(startup)}</AmpleStartupSong>", StringComparison.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text);
                output.WriteLine($"wrote   {Path.GetRelativePath(root, target)}");
            }

            output.WriteLine($"Open {baseName}.ampleproj in Visual Studio (File > Open > Project). F5 plays {startup}.");
            return 0;
        });
        Argument<string> local = new("local-folder") { Description = "The folder Visual Studio already has files in." };
        Option<string> project = new("--project") { Description = "The AMPLE project folder to connect to.", Required = true };
        Command connect = new("connect", "Connects a folder with existing files to an AMPLE project: identical files are fine, differing local files go to _conflicts/<id>/.") { local, project };
        connect.SetAction(async (parse, token) =>
        {
            string localFolder = parse.GetValue(local)!;
            string projectFolder = parse.GetValue(project)!;
            if (!Directory.Exists(localFolder) || !Directory.Exists(projectFolder)) { output.WriteLine("Both folders must exist."); return 1; }
            VsConnect.Result result;
            try { result = VsConnect.Run(localFolder, projectFolder, null, await StudioDraftsAsync(projectFolder, token)); }
            catch (InvalidOperationException ex) { output.WriteLine(ex.Message); return 1; }
            foreach (VsConnect.Step step in result.Steps.Where(s => s.Action != "same")) output.WriteLine($"{step.Action,-9}{step.Path}");
            output.WriteLine(result.ConflictId is null
                ? $"{result.Steps.Count} file(s) looked at, no conflicts."
                : $"Your differing files are in _conflicts/{result.ConflictId}/ (see README.txt there). The project's files are in place.");
            return 0;
        });
        Command vs = new("vs", "Visual Studio integration.") { init, connect };
        return vs;
    }

    /// <summary>The files with unsaved changes in a running Studio of this project (empty when none is running or it cannot be asked).</summary>
    private static async Task<IReadOnlyCollection<string>> StudioDraftsAsync(string projectFolder, CancellationToken token)
    {
        try
        {
            string manifest = Path.Combine(projectFolder, ".ample", "project.json");
            if (!File.Exists(manifest)) return [];
            string projectId = ProjectFile.Parse(await File.ReadAllTextAsync(manifest, token)).Id.ToString("D");
            InstanceRegistry registry = InstanceRegistry.ForCurrentUser(new SystemClock());
            if (await registry.FindAsync(projectId, token) is null || registry.ReadLaunchUrl(projectId) is not { Length: > 0 } launchUrl) return [];
            Uri launch = new(launchUrl);
            string fragment = launch.Fragment.TrimStart('#');
            if (!fragment.StartsWith("token=", StringComparison.Ordinal)) return [];
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add("X-Ample-Token", Uri.UnescapeDataString(fragment[6..]));
            string json = await client.GetStringAsync(new UriBuilder(launch) { Fragment = string.Empty, Query = string.Empty, Path = "/api/wc" }.Uri, token);
            using JsonDocument document = JsonDocument.Parse(json);
            return [.. document.RootElement.GetProperty("dirty").EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return [];   // no Studio to ask: nothing is unsaved there
        }
    }

    private static string? FirstSong(string root) =>
        Directory.EnumerateFiles(root, "*.amplet", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private static string Resource(string file)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("vs-template/" + file)
            ?? throw new InvalidOperationException("Missing template resource " + file);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
