using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Library;
using Ample.Storage.Projects;

namespace Ample.Cli;

/// <summary>The <c>ample instances</c> command.</summary>
internal static class InstanceCommands
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static Command Create(TextWriter output)
    {
        Option<bool> asJson = new("--json") { Description = "Write active instances as JSON." };
        Option<bool> clean = new("--clean") { Description = "Remove stale registry records before listing." };
        Option<string?> home = new("--home") { Description = "The AMPLE home folder (default: AMPLE_HOME, else the local application data folder)." };
        Command command = new("instances", "Lists running AMPLE server instances.") { asJson, clean, home };
        command.SetAction(async (parse, cancellationToken) =>
        {
            InstanceRegistry registry = new(parse.GetValue(home) ?? Library.DefaultHome(), new SystemClock(), new SystemInstanceProbe());
            if (parse.GetValue(clean)) await registry.CleanupAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<InstanceRecord> instances = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
            if (parse.GetValue(asJson))
            {
                output.WriteLine(JsonSerializer.Serialize(instances, Json));
                return 0;
            }

            foreach (InstanceRecord instance in instances)
            {
                string startedBy = instance.Agent is null ? instance.StartedBy : $"agent {instance.Agent.Name}: {instance.Agent.Reason}";
                output.WriteLine($"{instance.Name}  {instance.ProjectId}  127.0.0.1:{instance.Port.ToString(CultureInfo.InvariantCulture)}  pid {instance.Pid.ToString(CultureInfo.InvariantCulture)}  {startedBy}  {instance.Folder}");
            }

            output.WriteLine($"{instances.Count.ToString(CultureInfo.InvariantCulture)} running AMPLE instance(s)");
            return 0;
        });
        return command;
    }
}
