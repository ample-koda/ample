using System.CommandLine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Docs;
using Ample.Language;

namespace Ample.Cli;

internal static class NoteBakeCommands
{
    internal static IEnumerable<Command> Create(TextWriter output)
    {
        Argument<string> song = new("song") { Description = "The .amplet file." };
        Argument<string> block = new("block") { Description = "The midi-clip whose note pipeline is baked." };
        Option<string?> stage = new("--stage") { Description = "The bake stage to bake (default: the last one; a stage called baked is added when there is none)." };
        Option<string?> file = new("--file") { Description = "Keep the notes in this project file instead of in the stage." };
        Option<bool> detach = new("--detach") { Description = "Turn the pipeline into plain notes instead of baking it." };
        Option<string?> root = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Command command = new("bake-notes", "Bakes the note pipeline of a midi-clip: stores its notes, signed, in a bake stage (or in a file).") { song, block, stage, file, detach, root };
        command.SetAction(parse =>
        {
            try
            {
                string path = Path.GetFullPath(parse.GetValue(song)!);
                string folder = Path.GetDirectoryName(path)!;
                // project files (a bake kept in a file) are relative to the project: the folder above the song that has .ample
                for (string? up = folder; up is not null; up = Path.GetDirectoryName(up))
                {
                    if (Directory.Exists(Path.Combine(up, ".ample"))) { folder = up; break; }
                }

                SpecData spec = (parse.GetValue(root) is { } r ? SpecRoot.At(r) : SpecRoot.Find(Directory.GetCurrentDirectory())).LoadSpecData();
                string? Read(string relative)
                {
                    try
                    {
                        string full = ProjectPath.Resolve(folder, relative);
                        return File.Exists(full) ? File.ReadAllText(full) : null;
                    }
                    catch (AmpleException)
                    {
                        return null;
                    }
                }

                using Ample.Engine.Modules.ModuleService modules = Ample.Engine.Modules.ModuleService.WithUserLibrary(folder, new Ample.Audio.IO.SystemClock());
                string text = File.ReadAllText(path);
                BakeOutcome outcome = parse.GetValue(detach)
                    ? NoteBake.Detach(text, spec, parse.GetValue(block)!, Read, NotePipeline.HardCap, modules.ReadMidiNotes)
                    : NoteBake.Bake(text, spec, parse.GetValue(block)!, parse.GetValue(stage), parse.GetValue(file), Read, NotePipeline.HardCap, modules.ReadMidiNotes);
                if (outcome.FilePath is not null)
                {
                    string target = ProjectPath.Resolve(folder, outcome.FilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, outcome.FileContent);
                }

                File.WriteAllText(path, outcome.Text);
                output.WriteLine(parse.GetValue(detach) ? $"{outcome.Notes} notes written as plain notes." : $"stage {outcome.Stage}: {outcome.Notes} notes, {outcome.NotesHash[..19]}…");
                return 0;
            }
            catch (Exception ex) when (ex is NoteBakeException or AmpleException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine(ex is NoteBakeException bake ? $"{bake.Code}: {bake.Message}" : ex.Message);
                return 1;
            }
        });
        Argument<string> seedSong = new("song") { Description = "The .amplet file." };
        Option<bool> pick = new("--pick") { Description = "Replace every random-seed() by a number chosen once from OS randomness." };
        Option<string?> seedRoot = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Command seeds = new("seeds", "Lists the stable random namespaces of a song with how often each is used and the seeds of every note pipeline; with --pick it writes a number for every random-seed().") { seedSong, pick, seedRoot };
        seeds.SetAction(parse =>
        {
            try
            {
                string path = Path.GetFullPath(parse.GetValue(seedSong)!);
                SpecData spec = (parse.GetValue(seedRoot) is { } r ? SpecRoot.At(r) : SpecRoot.Find(Directory.GetCurrentDirectory())).LoadSpecData();
                string text = File.ReadAllText(path);
                if (parse.GetValue(pick))
                {
                    (string picked, int count) = NoteBake.PickSeeds(text, () => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue) * 1_048_576L + System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_048_576));
                    File.WriteAllText(path, picked);
                    output.WriteLine($"{count} random-seed() picked and written into the song.");
                    return 0;
                }

                AmpleDocument doc = Parser.Parse(text.Replace("\r\n", "\n", StringComparison.Ordinal), spec);
                output.WriteLine($"master-seed {doc.Project?.MasterSeed.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}");
                bool names = false;
                foreach (BlockNode clip in doc.Blocks.Where(b => b.Type == "midi-clip"))
                {
                    (IReadOnlyList<SeedUseInfo> uses, IReadOnlyList<StreamUseInfo> streams, IReadOnlyList<(string Name, int Count)> all) = NoteBake.Provenance(text, spec, clip.Name, null, null);
                    if (!names) { foreach ((string name, int count) in all) output.WriteLine($"stable \"{name}\": used {count}x"); names = true; }
                    foreach (SeedUseInfo u in uses) output.WriteLine($"{clip.Name}: {u.Operator} seed {u.Seed} key {u.Key} drew {u.Draws}");
                    foreach (StreamUseInfo st in streams) output.WriteLine($"{clip.Name}: stream {st.Name} seed {st.Seed} key {st.Key} consumed {st.Consumed}");
                }

                return 0;
            }
            catch (Exception ex) when (ex is NoteBakeException or AmpleException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine(ex is NoteBakeException bake ? $"{bake.Code}: {bake.Message}" : ex.Message);
                return 1;
            }
        });
        return [command, seeds];
    }
}
