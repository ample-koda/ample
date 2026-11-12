using System.CommandLine;
using System.Globalization;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Docs;
using Ample.Engine.Library;
using Ample.Language;
using Ample.Storage.BlackBox;
using Ample.Storage.Library;
using Ample.Storage.Projects;
using LibraryService = Ample.Storage.Library.Library;

namespace Ample.Cli;

/// <summary>The <c>ample library</c> and <c>ample lock</c> commands.</summary>
internal static class LibraryCommands
{
    internal static IReadOnlyList<Command> Create(TextWriter output) => [Library(output), Lock(output)];

    private static Option<string?> HomeOption() => new("--home") { Description = "The library folder (default: AMPLE_HOME, else the local application data of the user)." };

    private static Option<string?> ProjectOption() => new("--project") { Description = "The project folder (default: the current folder)." };

    private static LibraryService OpenLibrary(string? home) => LibraryService.Open(home ?? LibraryService.DefaultHome());

    private static Mount Find(LibraryService lib, string idOrName)
    {
        IReadOnlyList<Mount> all = lib.Mounts();
        return all.FirstOrDefault(m => m.Id == idOrName) ?? all.FirstOrDefault(m => string.Equals(m.Name, idOrName, StringComparison.OrdinalIgnoreCase)) ?? throw new AmpleException("AMP5291", $"There is no linked folder '{idOrName}'. See 'ample library list'.");
    }

    private static Task<int> RunT(TextWriter output, Func<int> body) => Task.FromResult(Run(output, body));

    private static int Run(TextWriter output, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }

    private static Command Library(TextWriter output)
    {
        Command root = new("library", "Linked folders (catalogs), the assets in them, and what a project uses.");

        Option<string?> homeList = HomeOption();
        Command list = new("list", "Lists the linked folders with their state, and the assets.") { homeList };
        list.SetAction((parse, _) => RunT(output, () =>
        {
            using LibraryService lib = OpenLibrary(parse.GetValue(homeList));
            foreach (Mount m in lib.Mounts())
            {
                IReadOnlyList<AssetRecord> assets = lib.Assets(m.Id);
                output.WriteLine($"{m.Id}  {m.Name}  {m.State}  {(m.ReadOnly ? "read-only" : "read-write")}  {m.Scope}  {assets.Count.ToString(CultureInfo.InvariantCulture)} asset(s)  {m.Path}");
            }

            output.WriteLine($"{lib.Mounts().Count.ToString(CultureInfo.InvariantCulture)} linked folder(s), {lib.Assets().Count.ToString(CultureInfo.InvariantCulture)} asset(s), {lib.Duplicates().Count.ToString(CultureInfo.InvariantCulture)} group(s) of duplicates");
            return 0;
        }));

        Argument<string> scanMount = new("folder") { Description = "A linked folder (id or name)." };
        Option<string?> homeScan = HomeOption();
        Command scan = new("scan", "Scans a linked folder incrementally (unchanged files are not read).") { scanMount, homeScan };
        scan.SetAction((parse, token) => RunT(output, () =>
        {
            using LibraryService lib = OpenLibrary(parse.GetValue(homeScan));
            ScanReport r = lib.Scan(Find(lib, parse.GetValue(scanMount)!).Id, token);
            output.WriteLine($"{r.Added} added, {r.Changed} changed, {r.Moved} moved, {r.Missing} missing, {r.Unchanged} unchanged, {r.Hashed} read for hashing, {r.Milliseconds} ms");
            return 0;
        }));

        Argument<string> linkPath = new("path") { Description = "The folder to link (you choose it; nothing is found automatically)." };
        Option<string?> linkName = new("--name") { Description = "A name (default: the folder name)." };
        Option<bool> readWrite = new("--read-write") { Description = "Allow AMPLE to write into it (a marker file with the catalog id is written). Default: read-only." };
        Option<bool> forProject = new("--for-project") { Description = "Link it for the project only." };
        Option<string?> homeLink = HomeOption();
        Option<string?> projectLink = ProjectOption();
        Command link = new("link", "Links a folder and scans it.") { linkPath, linkName, readWrite, forProject, homeLink, projectLink };
        link.SetAction((parse, token) => RunT(output, () =>
        {
            using LibraryService lib = OpenLibrary(parse.GetValue(homeLink));
            string? projectId = parse.GetValue(forProject) ? ProjectId(parse.GetValue(projectLink)) : null;
            string path = Path.GetFullPath(parse.GetValue(linkPath)!);
            Mount m = lib.LinkMount(parse.GetValue(linkName) ?? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), path, projectId is null ? "global" : "project", projectId, !parse.GetValue(readWrite));
            ScanReport r = lib.Scan(m.Id, token);
            output.WriteLine($"Linked {m.Name} ({m.Id}, catalog {m.Uuid}, {(m.ReadOnly ? "read-only" : "read-write")}): {r.Added} asset(s)");
            return 0;
        }));

        Argument<string> addFile = new("file") { Description = "The file to copy into a writable linked folder." };
        Option<string> addInto = new("--into") { Description = "The linked folder (id or name); it must be read-write.", Required = true };
        Option<string?> addAs = new("--as") { Description = "Path inside the folder (default: the file name)." };
        Option<string?> homeAdd = HomeOption();
        Command add = new("add", "Copies a file into a writable linked folder and scans it. A read-only folder is refused.") { addFile, addInto, addAs, homeAdd };
        add.SetAction((parse, token) => RunT(output, () =>
        {
            using LibraryService lib = OpenLibrary(parse.GetValue(homeAdd));
            Mount m = Find(lib, parse.GetValue(addInto)!);
            string target = lib.ResolveWritable(m.Id, parse.GetValue(addAs) ?? Path.GetFileName(parse.GetValue(addFile)!));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(parse.GetValue(addFile)!, target, false);
            lib.Scan(m.Id, token);
            output.WriteLine($"Added {Path.GetRelativePath(m.Path, target).Replace('\\', '/')} to {m.Name}");
            return 0;
        }));

        Argument<string> relMount = new("folder") { Description = "A linked folder (id or name)." };
        Argument<string> relPath = new("new-path") { Description = "Where the folder is now." };
        Option<bool> force = new("--force") { Description = "Link it even if it does not look like the same catalog." };
        Option<string?> homeRel = HomeOption();
        Command relocate = new("relocate", "Points a linked folder at its new place (checked by its marker, fingerprint or files). No song changes.") { relMount, relPath, force, homeRel };
        relocate.SetAction((parse, token) => RunT(output, () =>
        {
            using LibraryService lib = OpenLibrary(parse.GetValue(homeRel));
            Mount m = lib.Relocate(Find(lib, parse.GetValue(relMount)!).Id, parse.GetValue(relPath)!, parse.GetValue(force));
            ScanReport r = lib.Scan(m.Id, token);
            output.WriteLine($"{m.Name} is now at {m.Path} ({r.Unchanged} unchanged, {r.Changed} changed, {r.Missing} missing)");
            return 0;
        }));

        Argument<string?> verifyMount = new("folder") { Description = "A linked folder (id or name); default: all.", Arity = ArgumentArity.ZeroOrOne };
        Option<string?> homeVerify = HomeOption();
        Command verify = new("verify", "Hashes the assets again and compares them with the catalog; exit code 1 when anything is not ok.") { verifyMount, homeVerify };
        verify.SetAction((parse, _) => RunT(output, () =>
        {
            using LibraryService lib = OpenLibrary(parse.GetValue(homeVerify));
            string? which = parse.GetValue(verifyMount);
            int problems = 0;
            foreach (Mount m in which is null ? lib.Mounts() : [Find(lib, which)])
            {
                foreach ((AssetRecord a, string status) in lib.Verify(m.Id).Where(x => x.Status != "ok"))
                {
                    problems++;
                    output.WriteLine($"{status.ToUpperInvariant()}  {m.Name}/{a.RelPath}");
                }
            }

            output.WriteLine(problems == 0 ? "All assets are as the catalog knows them." : $"{problems.ToString(CultureInfo.InvariantCulture)} problem(s).");
            return problems == 0 ? 0 : 1;
        }));

        foreach (Command c in new[] { list, scan, link, add, relocate, verify })
        {
            root.Add(c);
        }

        return root;
    }

    private static Command Lock(TextWriter output)
    {
        Command root = new("lock", "ample.lock: the assets, packs and plugins a project depends on, pinned by hash.");
        Option<string?> homeUpdate = HomeOption();
        Option<string?> projectUpdate = ProjectOption();
        Option<string?> rootUpdate = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Command update = new("update", "Works out what the songs use and writes ample.lock (accepts changed assets).") { homeUpdate, projectUpdate, rootUpdate };
        update.SetAction(async (parse, token) =>
        {
            try
            {
                string project = Path.GetFullPath(parse.GetValue(projectUpdate) ?? Directory.GetCurrentDirectory());
                using LibraryService lib = OpenLibrary(parse.GetValue(homeUpdate));
                PhysicalFileSystem fs = new();
                SystemClock clock = new();
                using OpenProject open = await ProjectStore.OpenAsync(fs, clock, project, readOnly: false, token);
                using Journal journal = await Journal.OpenAsync(fs, clock, project, open.Manifest.Id.ToString("D"), cancellationToken: token);
                (List<AmpleDocument> songs, List<ModuleUsage> modules) = Songs(project, parse.GetValue(rootUpdate), lib);
                string text = await LockFile.UpdateAsync(project, lib, songs, Path.Combine(project, "plugins"), new CommandBus(journal, new CommandContext(fs, project, clock), "cli"), modules, token);
                output.WriteLine($"Wrote ample.lock ({LockFile.Parse(text).Count.ToString(CultureInfo.InvariantCulture)} entr{(LockFile.Parse(text).Count == 1 ? "y" : "ies")})");
                return 0;
            }
            catch (AmpleException ex)
            {
                output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
                return 1;
            }
        });

        Option<string?> homeVerify = HomeOption();
        Option<string?> projectVerify = ProjectOption();
        Command verify = new("verify", "Compares ample.lock with the catalog; exit code 1 when anything is not ok.") { homeVerify, projectVerify };
        verify.SetAction((parse, _) => RunT(output, () =>
        {
            string project = Path.GetFullPath(parse.GetValue(projectVerify) ?? Directory.GetCurrentDirectory());
            using LibraryService lib = OpenLibrary(parse.GetValue(homeVerify));
            IReadOnlyList<LockCheck> checks = LockFile.Verify(project, lib, Path.Combine(project, "plugins"));
            foreach (LockCheck c in checks.Where(c => c.Status != "ok"))
            {
                output.WriteLine($"{c.Status.ToUpperInvariant()}  {c.Message}");
            }

            output.WriteLine(checks.All(c => c.Status == "ok") ? $"ample.lock matches ({checks.Count.ToString(CultureInfo.InvariantCulture)} entr{(checks.Count == 1 ? "y" : "ies")})." : $"{checks.Count(c => c.Status != "ok")} entr{(checks.Count(c => c.Status != "ok") == 1 ? "y" : "ies")} do not match.");
            return checks.All(c => c.Status == "ok") ? 0 : 1;
        }));
        root.Add(update);
        root.Add(verify);
        return root;
    }

    private static string ProjectId(string? project)
    {
        string path = Path.GetFullPath(project ?? Directory.GetCurrentDirectory());
        string file = Path.Combine(path, ".ample", "project.json");
        return File.Exists(file) ? Ample.Storage.Projects.ProjectFile.Parse(File.ReadAllText(file)).Id.ToString("D") : throw new AmpleException("AMP5201", $"'{path}' is not an AMPLE project (there is no .ample/project.json).");
    }

    private static (List<AmpleDocument> Songs, List<ModuleUsage> Modules) Songs(string project, string? ampleRoot, LibraryService lib)
    {
        SpecData spec = (ampleRoot is null ? SpecRoot.Find(Directory.GetCurrentDirectory()) : SpecRoot.At(ampleRoot)).LoadSpecData();
        Ample.Engine.Modules.ModuleService service = new(project, (LibraryService?)lib);
        List<AmpleDocument> docs = [];
        List<ModuleUsage> used = [];
        foreach (string f in Directory.EnumerateFiles(project, "*.amplet", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".ample" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            ExpandedSong expanded = service.Expand(File.ReadAllText(f), spec);
            docs.Add(Parser.Parse(expanded.Text, spec));
            used.AddRange(expanded.LockUsages);
        }

        return (docs, used);
    }
}
