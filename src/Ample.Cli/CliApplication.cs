using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using Ample.Docs;

namespace Ample.Cli;

/// <summary>Builds and invokes the AMPLE command-line interface.</summary>
public static class CliApplication
{
    /// <summary>Runs the command-line interface and writes output to the supplied writer.</summary>
    public static int Run(string[] args, TextWriter output)
    {
        RootCommand rootCommand = BuildRootCommand("ample", output);
        return rootCommand.Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = output });
    }

    /// <summary>Describes the command tree for the generated CLI reference.</summary>
    public static CliCommandInfo Describe() => ToInfo(BuildRootCommand("ample", TextWriter.Null));

    private static RootCommand BuildRootCommand(string name, TextWriter output)
    {
        _ = name;
        RootCommand rootCommand = new("Reproducible song programming.");
        VersionOption versionOption = rootCommand.Options.OfType<VersionOption>().Single();
        versionOption.Action = new AmpleVersionAction();
        rootCommand.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(rootCommand.Description);
        });
        rootCommand.Subcommands.Add(DocsCommands.CreateDocs(output));
        rootCommand.Subcommands.Add(DocsCommands.CreateHelp(output));
        foreach (Command command in CheckCommands.Create(output).Concat(ProjectCommands.Create(output)).Concat(AddCommands.Create(output)).Concat(TakeCommands.Create(output)).Concat(BlackboxCommands.Create(output)).Concat(HistoryCliCommands.Create(output)).Concat(BuildCommands.Create(output)).Concat(ControlCommands.Create(output)).Concat(PluginCommands.Create(output)).Concat(ServeCommands.Create(output)).Append(OpenCommands.Create(output)).Append(PackCommands.Create(output)).Concat(LspCommands.Create()).Concat(McpCommands.Create()).Concat(LibraryCommands.Create(output)).Concat(BackupCommands.Create(output)).Concat(SecurityCommands.Create(output)).Concat(ModuleCommands.Create(output)).Concat(ComponentCommands.Create(output)).Append(MidiCommands.Create(output)).Concat(QualityCreditsCommands.Create(output)).Concat(AnalyzeCommands.Create(output)).Append(InstanceCommands.Create(output)).Append(StorageCommands.Create(output)).Concat(NoteBakeCommands.Create(output)))
        {
            rootCommand.Subcommands.Add(command);
        }

        return rootCommand;
    }

    private static CliCommandInfo ToInfo(Command command)
    {
        List<string> options = [.. command.Options.Where(o => o is not VersionOption).Select(o => $"{string.Join(", ", new[] { o.Name }.Concat(o.Aliases))}: {o.Description}")];
        options.AddRange(command.Arguments.Select(a => $"<{a.Name}>: {a.Description}"));
        return new CliCommandInfo(command.Name, command.Description ?? string.Empty, options, [.. command.Subcommands.Select(ToInfo)]);
    }
}

internal sealed class AmpleVersionAction : SynchronousCommandLineAction
{
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        parseResult.InvocationConfiguration.Output.WriteLine(CliVersionInfo.Current);
        return 0;
    }
}

/// <summary>Provides the display version for the AMPLE command-line interface.</summary>
public static class CliVersionInfo
{
    /// <summary>Gets the version and source commit compiled into the CLI.</summary>
    public static string Current => $"AMPLE {Version} (commit {Commit})";

    /// <summary>Gets the version without the commit.</summary>
    public static string Version
    {
        get
        {
            string version = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            int plus = version.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? version : version[..plus];
        }
    }

    private static string Commit =>
        typeof(CliApplication).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "Commit", StringComparison.Ordinal))?.Value ?? "unknown";
}
