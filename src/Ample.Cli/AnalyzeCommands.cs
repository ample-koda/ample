using System.CommandLine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Plugins;
using Ample.Plugins.Analysis;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Cli;

/// <summary>The <c>ample analyze</c> command: analysers on takes (tempo, key, loudness, spectrum, pitch, melody), built in or from an analysis plugin.</summary>
internal static class AnalyzeCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Argument<string?> take = new("take") { Description = "The take id (or the start of one).", Arity = ArgumentArity.ZeroOrOne };
        Option<string?> with = new("--with") { Description = "The analyser: tempo, key, loudness, spectrum, pitch or melody (an analysis plugin offers its own)." };
        Option<string?> project = new("--project") { Description = "The project folder (default: the current folder)." };
        Option<string> options = new("--options") { Description = "Options as JSON, for example {\"tempo\":120} for melody.", DefaultValueFactory = _ => "{}" };
        Option<string?> plugin = new("--plugin") { Description = "Use this analysis plugin instead of a built-in analyser." };
        Option<bool> allowGpu = new("--allow-gpu") { Description = "Allow a plugin that needs the GPU to run (it is never started without this)." };
        Option<bool> allowNetwork = new("--allow-network") { Description = "Allow a plugin that needs the network to run (it is never started without this)." };
        Option<bool> list = new("--list") { Description = "List the analysers and exit." };
        Command analyze = new("analyze", "Analyses a take and keeps the result next to the project (computed once per audio, analyser, version and options; the take never changes).") { take, with, project, options, plugin, allowGpu, allowNetwork, list };
        analyze.SetAction(async (parse, token) =>
        {
            try
            {
                if (parse.GetValue(list))
                {
                    foreach (IAnalyzer a in BuiltInAnalyzers.All)
                    {
                        output.WriteLine($"{a.Id} {a.Version}: {a.Description}");
                    }

                    return 0;
                }

                string root = Path.GetFullPath(parse.GetValue(project) ?? Directory.GetCurrentDirectory());
                string kind = parse.GetValue(with) ?? throw new AmpleException("AMP5341", "Say which analysis: --with tempo|key|loudness|spectrum|pitch|melody (or --list).");
                string takeId = parse.GetValue(take) ?? throw new AmpleException("AMP5342", "Give the take to analyse.");
                using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, root, readOnly: false, token);
                Takebank bank = Takebank.Open(Fs, Clock, root);
                if (parse.GetValue(plugin) is { } pluginId)
                {
                    (IReadOnlyList<DiscoveredPlugin> found, _) = PluginCommands.Find(null, Path.Combine(root, "plugins"));
                    DiscoveredPlugin p = found.FirstOrDefault(x => x.Manifest.Id == pluginId) ?? throw new AmpleException("AMP5344", $"There is no analysis plugin '{pluginId}'.");
                    TakeRecord t = bank.GetTake(bank.ResolveTakeId(takeId));
                    string dataId = Ample.Storage.Quality.QualityService.AudioDataId(t) ?? throw new AmpleException("AMP5342", "This take has no audio to analyse.");
                    string job = Path.Combine(root, ".ample", "tmp", "analysis-" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(Path.Combine(job, "in"));
                    try
                    {
                        await using (Stream src = bank.OpenData(dataId))
                        await using (FileStream dest = File.Create(Path.Combine(job, "in", "audio.wav")))
                        {
                            await src.CopyToAsync(dest, token);
                        }

                        using PluginHost host = new();
                        string json = await host.AnalyzeAsync(p, new AnalysisJob(job, kind, parse.GetValue(options)!, TimeSpan.FromMinutes(10), null), question =>
                        {
                            bool gpu = p.Manifest.Needs.Gpu;
                            bool net = p.Manifest.Needs.Network;
                            output.WriteLine(question);
                            return (!gpu || parse.GetValue(allowGpu)) && (!net || parse.GetValue(allowNetwork));
                        }, token);
                        output.WriteLine(json);
                    }
                    finally
                    {
                        Directory.Delete(job, true);
                    }

                    return 0;
                }

                AnalysisRecord r = new AnalysisService(bank, root).Analyze(takeId, kind, parse.GetValue(options)!);
                output.WriteLine(r.Json);
                output.WriteLine($"({r.Analyzer} {r.Version}, {(r.FromCache ? "from the stored result" : "computed now")}, key {r.Key[..12]})");
                return 0;
            }
            catch (AmpleException ex)
            {
                output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
                return 1;
            }
        });
        return [analyze];
    }
}
