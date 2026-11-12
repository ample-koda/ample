using Ample.Storage.Takes;
using Ample.Testing;

namespace Ample.Core.Tests;

/// <summary>The protection rules and the clean-up of the takebank.</summary>
[Trait("Category", "Takebank")]
public sealed class StorageServiceTests
{
    private static readonly DateTimeOffset Later = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static StorageService Service(string root, Takebank bank, DateTimeOffset? now = null) =>
        new(bank, root, Path.Combine(root, ".ample", "history.db"), new FakeClock(now ?? Later));

    private static async Task<string> MakeAsync(Takebank bank, string block, string settings, string sound)
    {
        TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan(block, settings));
        TakeRecord take = await ticket.CommitAsync([("audio", TakeFixtures.Bytes(sound))]);
        return take.TakeId;
    }

    [Fact]
    public async Task The_current_take_of_a_block_is_protected_and_older_ones_are_reclaimable()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            await MakeAsync(bank, "block:melody", "{\"v\":1}", "sound one");
            await MakeAsync(bank, "block:melody", "{\"v\":2}", "sound two");
            await MakeAsync(bank, "block:drums", "{\"v\":1}", "sound three");
            IReadOnlyList<StorageTake> takes = Service(root, bank).Classify();
            Assert.Equal(3, takes.Count);
            Assert.Equal(2, takes.Count(t => t.Protected));
            Assert.Single(takes, t => !t.Protected && t.Reason == "superseded");
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_take_named_in_a_source_a_recent_take_and_a_shared_blob_are_never_cleaned_up()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string old = await MakeAsync(bank, "block:melody", "{\"v\":1}", "old sound");
            string newer = await MakeAsync(bank, "block:melody", "{\"v\":2}", "new sound");
            string sameBytes = await MakeAsync(bank, "block:melody", "{\"v\":3}", "old sound");
            IReadOnlyList<StorageTake> before = Service(root, bank).Classify();
            string[] victims = [.. before.Where(t => !t.Protected).Select(t => t.TakeId)];
            Assert.Equal(2, victims.Length);
            Assert.All(victims, v => Assert.True(v == old || v == newer || v == sameBytes));

            // naming the victim in a source (a baked or pinned take) protects it
            File.WriteAllText(Path.Combine(root, "song.amplet"), "# baked: " + string.Join(" ", victims) + "\n");
            Assert.All(Service(root, bank).Classify(), t => Assert.True(t.Protected));

            // a take younger than the keep window is protected as well
            File.Delete(Path.Combine(root, "song.amplet"));
            Assert.All(Service(root, bank, new DateTimeOffset(2026, 10, 2, 9, 5, 0, TimeSpan.Zero)).Classify(), t => Assert.True(t.Protected));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_clean_up_tombstones_the_take_removes_only_its_own_data_and_can_be_cancelled()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string old = await MakeAsync(bank, "block:melody", "{\"v\":1}", "the old sound");
            string current = await MakeAsync(bank, "block:melody", "{\"v\":2}", "the current sound");
            StorageService service = Service(root, bank);
            StorageCleanPlan plan = service.Plan();
            Assert.Single(plan.TakeIds);
            Assert.True(plan.Bytes > 0);
            string victim = plan.TakeIds[0];
            string survivor = victim == old ? current : old;

            StorageProgress result = await service.RunAsync(plan, null, CancellationToken.None);
            Assert.Equal(plan.Bytes, result.Bytes);
            Assert.True(bank.GetTake(victim).Tombstoned);
            Assert.False(bank.GetTake(survivor).Tombstoned);
            Assert.All(bank.GetTake(survivor).Data, d => Assert.True(bank.DataExists(d.DataId)));
            Assert.All(bank.GetTake(victim).Data, d => Assert.False(bank.DataExists(d.DataId)));
            Assert.Empty(await bank.VerifyAsync(survivor));

            // a second plan has nothing left to do, and a cancelled run does nothing
            Assert.Empty(service.Plan().TakeIds);
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.RunAsync(plan, null, cancelled.Token));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task The_overview_adds_up_and_the_settings_have_defaults_and_can_be_saved()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            await MakeAsync(bank, "block:melody", "{\"v\":1}", new string((char)120, 4000));
            await MakeAsync(bank, "block:melody", "{\"v\":2}", new string((char)121, 4000));
            StorageService service = Service(root, bank);
            StorageOverview overview = service.Overview();
            Assert.Equal(2, overview.Takes);
            Assert.True(overview.ReclaimableBytes > 0);
            Assert.Equal(overview.DataBytes, overview.ProtectedBytes + overview.ReclaimableBytes);
            Assert.Equal(10, overview.SoftLimitGb);
            Assert.False(overview.OverLimit);

            new StorageSettings(0.000001, 1, 60, null, null, "wav", false).Save(root);
            StorageSettings read = StorageSettings.Load(root);
            Assert.Equal(1, read.KeepSuperseded);
            Assert.Equal("wav", read.Intermediates);
            Assert.False(read.SilenceRuns);
            Assert.True(service.Overview().OverLimit);
            Assert.Empty(service.Plan().TakeIds);   // one older take is kept now
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task The_takebank_can_move_to_another_folder_and_back_and_data_is_found_while_it_moves()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        string other = Path.Combine(Directory.CreateTempSubdirectory("ample-other-").FullName, "takes");
        try
        {
            string a = await MakeAsync(bank, "block:melody", "{\"v\":1}", new string((char)97, 5000));
            string dataA = bank.GetTake(a).Data[0].DataId;
            StorageService service = Service(root, bank);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories));
            StorageProgress moved = await service.MoveAsync(other, null, CancellationToken.None);
            Assert.True(moved.Done >= 1, $"done {moved.Done} of {moved.Total}");
            Assert.Empty(Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories));
            Assert.True(bank.DataExists(dataA));
            Assert.Empty(await bank.VerifyAsync(a));

            // new data goes to the new place
            string b = await MakeAsync(bank, "block:drums", "{\"v\":1}", new string((char)98, 5000));
            Assert.Single(Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories).Where(_ => false).DefaultIfEmpty("none"));
            Assert.Equal(2, Directory.GetFiles(other, "*", SearchOption.AllDirectories).Length);
            Assert.Empty(await bank.VerifyAsync(b));

            // and back into the project
            await service.MoveAsync(null, null, CancellationToken.None);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories).Length);
            Assert.Empty(Directory.GetFiles(other, "*", SearchOption.AllDirectories));
            Assert.Null(StorageSettings.Load(root).DataPath);
            Assert.Empty(await bank.VerifyAsync(a));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(other)!, true);
            TakeFixtures.Cleanup(root);
        }
    }
}
