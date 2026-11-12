using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Cli;

/// <summary>The <c>ample takes</c> and <c>ample plan</c> commands.</summary>
internal static class TakeCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static IReadOnlyList<Command> Create(TextWriter output) => [Takes(output), Plan(output)];

    private static Option<string?> ProjectOption() => new("--project") { Description = "The project folder (default: the current folder)." };

    private static Command Takes(TextWriter output)
    {
        Option<string?> project = ProjectOption();
        Command list = new("list", "Lists the takes of a project, newest first.") { project };
        list.SetAction(async (parse, token) => await Run(output, parse.GetValue(project), async bank =>
        {
            IReadOnlyList<TakeRecord> takes = bank.ListTakes();
            foreach (TakeRecord t in takes)
            {
                StoredPlan plan = bank.GetPlan(t.PlanId);
                output.WriteLine($"{Short(t.TakeId)}  {t.Created:u}  block {plan.Plan.BlockUuid}  plan {Short(t.PlanId)}  {bank.SizeOfTake(t.TakeId).ToString(CultureInfo.InvariantCulture)} bytes{(t.Tombstoned ? "  dropped: " + t.TombstoneReason : string.Empty)}");
            }

            output.WriteLine($"{takes.Count.ToString(CultureInfo.InvariantCulture)} take(s)");
            await Task.CompletedTask;
            return 0;
        }));

        Argument<string> id = new("take") { Description = "A take id or a unique prefix." };
        Option<string?> projectShow = ProjectOption();
        Command show = new("show", "Shows one take with its plan id and data.") { id, projectShow };
        show.SetAction(async (parse, token) => await Run(output, parse.GetValue(projectShow), async bank =>
        {
            TakeRead read = bank.ReadTake(bank.ResolveTakeId(parse.GetValue(id)!));
            output.WriteLine($"Take: {read.Take.TakeId}");
            output.WriteLine($"Plan: {read.Take.PlanId}");
            output.WriteLine($"Stored: {read.Take.Created:u}");
            foreach (TakeDataRef d in read.Take.Data)
            {
                output.WriteLine($"Data ({d.Role}): {d.DataId}");
            }

            foreach (DataProblem m in read.Missing)
            {
                output.WriteLine($"MISSING {m.DataId}: {m.Detail}. Re-render it from the plan or restore it from a backup.");
            }

            if (read.Take.Tombstoned)
            {
                output.WriteLine($"Dropped: {read.Take.TombstoneReason}");
            }

            await Task.CompletedTask;
            return read.Missing.Count == 0 ? 0 : 1;
        }));

        Argument<string?> verifyId = new("take") { Description = "A take id or prefix; without it every take is verified.", Arity = ArgumentArity.ZeroOrOne };
        Option<string?> projectVerify = ProjectOption();
        Command verify = new("verify", "Rehashes the data of a take (or of all takes); exit code 1 on missing, truncated or damaged data.") { verifyId, projectVerify };
        verify.SetAction(async (parse, token) => await Run(output, parse.GetValue(projectVerify), async bank =>
        {
            string? wanted = parse.GetValue(verifyId);
            List<string> ids = wanted is null ? [.. bank.ListTakes().Select(t => t.TakeId)] : [bank.ResolveTakeId(wanted)];
            int problems = 0;
            foreach (string takeId in ids)
            {
                foreach (DataProblem p in await bank.VerifyAsync(takeId, token))
                {
                    output.WriteLine($"{Short(takeId)}: {p.Kind} {p.DataId}: {p.Detail}");
                    problems++;
                }
            }

            output.WriteLine(problems == 0 ? $"verify: {ids.Count.ToString(CultureInfo.InvariantCulture)} take(s) ok" : $"verify: {problems.ToString(CultureInfo.InvariantCulture)} problem(s)");
            return problems == 0 ? 0 : 1;
        }));

        return new Command("takes", "Takebank commands.") { list, show, verify };
    }

    private static Command Plan(TextWriter output)
    {
        Argument<string> id = new("take") { Description = "A take id or a unique prefix." };
        Option<string?> project = ProjectOption();
        Command show = new("show", "Prints the plan (the complete recipe) a take was made from.") { id, project };
        show.SetAction(async (parse, token) => await Run(output, parse.GetValue(project), async bank =>
        {
            TakeRecord take = bank.GetTake(bank.ResolveTakeId(parse.GetValue(id)!));
            StoredPlan plan = bank.GetPlan(take.PlanId);
            using JsonDocument doc = JsonDocument.Parse(plan.CanonicalJson);
            output.WriteLine($"# plan {plan.PlanId}");
            output.WriteLine(JsonSerializer.Serialize(doc.RootElement, Indented));
            await Task.CompletedTask;
            return 0;
        }));
        return new Command("plan", "Plan commands.") { show };
    }

    private static string Short(string id) => id.Length > 19 ? id[..19] : id;

    private static async Task<int> Run(TextWriter output, string? project, Func<Takebank, Task<int>> action)
    {
        try
        {
            string root = Path.GetFullPath(project ?? Directory.GetCurrentDirectory());
            using OpenProject opened = await ProjectStore.OpenAsync(Fs, Clock, root, readOnly: true);
            return await action(Takebank.Open(Fs, Clock, root));
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }
}
