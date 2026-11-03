using Ample.Audio.IO;
using Ample.Storage.BlackBox;
using Ample.Storage.History;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Testing;

namespace Ample.Core.Tests;

internal sealed class HistoryRig : IDisposable
{
    private readonly string folder;
    private readonly OpenProject project;
    public readonly string Root;
    public readonly Takebank Bank;
    public readonly Journal Journal;
    public readonly CommandContext Context;
    public readonly HistoryService History;
    public readonly FakeClock Clock = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));

    private HistoryRig(string folder, OpenProject project, string root, Takebank bank, Journal journal, CommandContext context, HistoryService history)
    {
        this.folder = folder;
        this.project = project;
        Root = root;
        Bank = bank;
        Journal = journal;
        Context = context;
        History = history;
    }

    public static async Task<HistoryRig> CreateAsync(params (string Path, string Text)[] files)
    {
        string folder = Directory.CreateTempSubdirectory("ample-hist-").FullName;
        string root = Path.Combine(folder, "proj");
        PhysicalFileSystem fs = new();
        FakeClock clock = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        OpenProject project = await ProjectStore.CreateAsync(fs, clock, new GuidV7Generator(), root, "history test");
        foreach ((string path, string text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            await File.WriteAllTextAsync(Path.Combine(root, path), text);
        }

        Journal journal = await Journal.OpenAsync(fs, clock, root, "p");
        CommandContext context = new(fs, root, clock);
        Takebank bank = Takebank.Open(fs, clock, root);
        return new HistoryRig(folder, project, root, bank, journal, context, await HistoryService.OpenAsync(fs, clock, root, bank, new CommandBus(journal, context, "test")));
    }

    public void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, path))!);
        File.WriteAllText(Path.Combine(Root, path), text);
    }

    public string? Read(string path) => File.Exists(Path.Combine(Root, path)) ? File.ReadAllText(Path.Combine(Root, path)) : null;

    public void Dispose()
    {
        Journal.Dispose();
        project.Dispose();
        Directory.Delete(folder, recursive: true);
    }
}
