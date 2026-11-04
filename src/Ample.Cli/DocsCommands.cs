using System.CommandLine;
using System.Text;
using Ample.Docs;

namespace Ample.Cli;

/// <summary>The <c>ample docs</c> and <c>ample help</c> commands.</summary>
internal static class DocsCommands
{
    private const string DefaultHelpFile = "docs/help/help.html";

    internal static Command CreateDocs(TextWriter output)
    {
        Option<string?> rootOption = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Option<string?> outOption = new("--out") { Description = "Output file for the help page (default: docs/help/help.html in the AMPLE folder)." };

        Command build = new("build", "Generates the offline help.html from the spec, docs and command tree.") { rootOption, outOption };
        build.SetAction(parse =>
        {
            SpecRoot root = Locate(parse.GetValue(rootOption));
            HelpResult result = HelpGenerator.Build(root, CliApplication.Describe(), CliVersionInfo.Version);
            string target = parse.GetValue(outOption) ?? root.Combine(DefaultHelpFile.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            File.WriteAllText(target, result.Html, new UTF8Encoding(false));
            output.WriteLine($"Wrote {target} ({result.Pages.Count} pages, {result.Html.Length / 1024} KB).");
            foreach (string problem in result.Problems)
            {
                output.WriteLine("problem: " + problem);
            }

            return result.Problems.Count == 0 ? 0 : 1;
        });

        Command check = new("check", "Fails when the committed help.html is out of date or a source has problems (dead links, failing examples).") { rootOption };
        check.SetAction(parse =>
        {
            SpecRoot root = Locate(parse.GetValue(rootOption));
            HelpResult result = HelpGenerator.Build(root, CliApplication.Describe(), CliVersionInfo.Version);
            int failures = 0;
            foreach (string problem in result.Problems)
            {
                output.WriteLine("problem: " + problem);
                failures++;
            }

            string committed = root.Combine(DefaultHelpFile.Split('/'));
            if (!File.Exists(committed))
            {
                output.WriteLine($"problem: {DefaultHelpFile} does not exist; run 'ample docs build'.");
                failures++;
            }
            else if (HelpGenerator.StripBuildInfo(File.ReadAllText(committed).Replace("\r\n", "\n", StringComparison.Ordinal)) != HelpGenerator.StripBuildInfo(result.Html))
            {
                output.WriteLine($"problem: {DefaultHelpFile} is out of date; run 'ample docs build'.");
                failures++;
            }

            output.WriteLine(failures == 0 ? $"docs check: ok ({result.Pages.Count} pages)." : $"docs check: {failures} problem(s).");
            return failures == 0 ? 0 : 1;
        });

        return new Command("docs", "Builds and checks the generated help.") { build, check };
    }

    internal static Command CreateHelp(TextWriter output)
    {
        Option<string?> rootOption = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Argument<string?> topic = new("topic") { Description = "A page id or part of a title; without it the topics are listed.", Arity = ArgumentArity.ZeroOrOne };
        Command help = new("help", "Shows help topics in the terminal (the same sources as help.html).") { rootOption, topic };
        help.SetAction(parse =>
        {
            SpecRoot root = Locate(parse.GetValue(rootOption));
            HelpResult result = HelpGenerator.Build(root, CliApplication.Describe(), CliVersionInfo.Version);
            string? wanted = parse.GetValue(topic);
            if (string.IsNullOrWhiteSpace(wanted))
            {
                foreach (IGrouping<string, DocPage> group in result.Pages.GroupBy(p => p.Section))
                {
                    output.WriteLine(group.Key);
                    foreach (DocPage page in group)
                    {
                        output.WriteLine($"  {page.Id}  {page.Title}");
                    }
                }

                return 0;
            }

            IReadOnlyList<DocPage> found = HelpGenerator.FindTopic(result.Pages, wanted);
            if (found.Count == 0)
            {
                output.WriteLine($"No help topic matches '{wanted}'. Run 'ample help' to list the topics.");
                return 1;
            }

            if (found.Count > 1)
            {
                output.WriteLine($"{found.Count} topics match '{wanted}':");
                foreach (DocPage page in found)
                {
                    output.WriteLine($"  {page.Id}  {page.Title}");
                }

                return 0;
            }

            output.WriteLine(found[0].Title);
            output.WriteLine(new string('=', found[0].Title.Length));
            output.WriteLine(found[0].SearchText.Trim());
            return 0;
        });
        return help;
    }

    private static SpecRoot Locate(string? explicitRoot) => explicitRoot is null ? SpecRoot.Find(Directory.GetCurrentDirectory()) : SpecRoot.At(explicitRoot);
}
