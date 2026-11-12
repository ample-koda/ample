using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Audio.Random;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Testing;
using Microsoft.Data.Sqlite;

namespace Ample.Core.Tests;

internal static class TakeFixtures
{
    internal static readonly Guid ProjectId = Guid.Parse("01234567-89ab-7cde-8f01-23456789abcd");

    internal static PlanRecord Plan(string block = "block-1", string settings = "{\"steps\":8,\"style\":\"airy\"}", ulong seed = 42, string? parent = null, string intent = "bake") =>
        new(ProjectId, block, "voice lead", "sha256:" + new string('a', 64), [new PlanInput("data", "sha256:" + new string('b', 64), null)], [new PlanSeed("seed", seed), new PlanSeed("lm_seed", seed + 1)], RngVersion.Current, "acestep", "0.9", "{\"gpu\":\"RTX-4090\",\"driver\":\"555\"}", settings, intent, parent);

    internal static Stream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    internal static async Task<(string Root, Takebank Bank)> NewBankAsync(IFileSystem? fs = null)
    {
        string root = Path.Combine(Directory.CreateTempSubdirectory("ample-bank-").FullName, "proj");
        using OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), root, "bank test");
        return (root, Takebank.Open(fs ?? new PhysicalFileSystem(), new FakeClock(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)), root));
    }

    internal static void Cleanup(string root) => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
}

[Trait("Category", "Takebank")]
public sealed class PlanTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void The_canonical_form_and_ids_are_frozen_by_a_golden_file()
    {
        PlanRecord[] plans = [TakeFixtures.Plan(), TakeFixtures.Plan("block-2", "{\"b\":[1,2],\"a\":{\"y\":true,\"x\":null}}", 7, null, "variant"), TakeFixtures.Plan("é😀", "{}", ulong.MaxValue, "sha256:" + new string('c', 64), "freeze")];
        string[] canonical = [.. plans.Select(PlanCanonical.Serialize)];
        string actual = JsonSerializer.Serialize(canonical.Select((c, i) => new { name = "plan" + i, canonical = c, planId = PlanCanonical.IdOf(c) }), Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "spec-tests")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string golden = Path.Combine(root, "spec-tests", "plans", "canonical-golden.json");
        if (!File.Exists(golden))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, actual);
            Assert.Fail("The golden file did not exist and was created: review it, commit it and run the test again.");
        }

        Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    [Fact]
    public void The_plan_id_ignores_key_order_seed_order_and_spacing_but_notices_every_real_change()
    {
        string id = PlanCanonical.PlanId(TakeFixtures.Plan());
        Assert.Equal(id, PlanCanonical.PlanId(TakeFixtures.Plan(settings: " { \"style\" : \"airy\", \"steps\" : 8 } ")));
        PlanRecord reordered = TakeFixtures.Plan() with { Seeds = [.. TakeFixtures.Plan().Seeds.AsEnumerable().Reverse()] };
        Assert.Equal(id, PlanCanonical.PlanId(reordered));

        Assert.NotEqual(id, PlanCanonical.PlanId(TakeFixtures.Plan(seed: 43)));
        Assert.NotEqual(id, PlanCanonical.PlanId(TakeFixtures.Plan(block: "block-2")));
        Assert.NotEqual(id, PlanCanonical.PlanId(TakeFixtures.Plan(settings: "{\"steps\":9,\"style\":\"airy\"}")));
        Assert.NotEqual(id, PlanCanonical.PlanId(TakeFixtures.Plan(intent: "preview")));
        Assert.NotEqual(id, PlanCanonical.PlanId(TakeFixtures.Plan(parent: "sha256:" + new string('d', 64))));
        Assert.NotEqual(id, PlanCanonical.PlanId(TakeFixtures.Plan() with { ToolprintJson = "{\"gpu\":\"RTX-3070\",\"driver\":\"555\"}" }));
    }

    [Fact]
    public void A_canonical_plan_parses_back_to_the_same_canonical_text()
    {
        string canonical = PlanCanonical.Serialize(TakeFixtures.Plan(parent: "sha256:" + new string('d', 64)));
        Assert.Equal(canonical, PlanCanonical.Serialize(PlanCanonical.Parse(canonical)));
        Assert.Equal("AMP5225", Assert.Throws<AmpleException>(() => PlanCanonical.Serialize(TakeFixtures.Plan(intent: "nonsense"))).DiagnosticCode);
        Assert.Equal("AMP5225", Assert.Throws<AmpleException>(() => PlanCanonical.Parse("{}")).DiagnosticCode);
    }
}

[Trait("Category", "Takebank")]
public sealed class TakebankTests
{
    [Fact]
    public async Task Read_only_bank_exposes_cached_audio_without_allowing_takebank_writes()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            TakeRecord saved = await (await bank.BeginAsync(TakeFixtures.Plan())).CommitAsync([("audio", TakeFixtures.Bytes("sound"))]);
            Takebank reader = Takebank.OpenReadOnly(new PhysicalFileSystem(), new SystemClock(), root);
            TakeRecord cached = Assert.IsType<TakeRecord>(reader.FindTakeForPlan(saved.PlanId));
            Assert.Equal(saved.TakeId, cached.TakeId);
            using (StreamReader audio = new(reader.OpenData(cached.Data[0].DataId))) Assert.Equal("sound", await audio.ReadToEndAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.BeginAsync(TakeFixtures.Plan("other")));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_a_take_without_a_plan_and_any_change_or_delete()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan());
            TakeRecord take = await ticket.CommitAsync([("audio", TakeFixtures.Bytes("sound"))]);

            using SqliteConnection raw = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, ".ample", "history.db"), Pooling = false }.ToString());
            raw.Open();
            void Sql(string sql)
            {
                using SqliteCommand c = raw.CreateCommand();
                c.CommandText = sql;
                c.ExecuteNonQuery();
            }

            SqliteException noPlan = Assert.Throws<SqliteException>(() => Sql("INSERT INTO takes (take_id, plan_id, created_at, meta_json) VALUES ('sha256:x', 'sha256:nope', 'now', '{}')"));
            Assert.Contains("AMP5220", noPlan.Message, StringComparison.Ordinal);
            Assert.Contains("AMP5224", Assert.Throws<SqliteException>(() => Sql($"UPDATE takes SET plan_id = 'sha256:other' WHERE take_id = '{take.TakeId}'")).Message, StringComparison.Ordinal);
            Assert.Contains("AMP5224", Assert.Throws<SqliteException>(() => Sql($"DELETE FROM takes WHERE take_id = '{take.TakeId}'")).Message, StringComparison.Ordinal);
            Assert.Contains("AMP5224", Assert.Throws<SqliteException>(() => Sql($"UPDATE plans SET json = '{{}}' WHERE plan_id = '{take.PlanId}'")).Message, StringComparison.Ordinal);
            Assert.Contains("AMP5224", Assert.Throws<SqliteException>(() => Sql($"DELETE FROM plans WHERE plan_id = '{take.PlanId}'")).Message, StringComparison.Ordinal);
            Assert.Contains("AMP5224", Assert.Throws<SqliteException>(() => Sql("DELETE FROM take_data")).Message, StringComparison.Ordinal);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    private sealed class FullDiskStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("disk full") { HResult = unchecked((int)0x80070070) };

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_full_disk_while_storing_a_take_says_so_and_failing_the_ticket_afterwards_does_not_hide_it()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan());
            AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await ticket.CommitAsync([("audio", new FullDiskStream())]));
            Assert.Equal("AMP5600", error.DiagnosticCode);
            await ticket.FailAsync("disk full");   // must not throw "already used"
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Begin_stores_the_plan_first_and_commit_makes_the_take_with_its_data_and_files()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            PlanRecord plan = TakeFixtures.Plan();
            TakeTicket ticket = await bank.BeginAsync(plan);
            Assert.Equal(PlanCanonical.PlanId(plan), ticket.PlanId);
            Assert.Empty(bank.ListTakes());
            Assert.Equal("started", Assert.Single(bank.ListAttempts()).Outcome);
            Assert.True(File.Exists(Path.Combine(root, ".ample", "takes", "plans", ticket.PlanId["sha256:".Length..] + ".plan.json")));
            Assert.Equal(PlanCanonical.Serialize(plan), bank.GetPlan(ticket.PlanId).CanonicalJson);

            TakeRecord take = await ticket.CommitAsync([("audio", TakeFixtures.Bytes("the sound")), ("melody", TakeFixtures.Bytes("{\"notes\":[60,62]}"))], "{\"seconds\":12}");
            Assert.Equal(ticket.PlanId, take.PlanId);
            Assert.Equal(["audio", "melody"], take.Data.Select(d => d.Role));
            Assert.Equal("completed", Assert.Single(bank.ListAttempts()).Outcome);
            Assert.Equal(take.TakeId, bank.ListAttempts()[0].TakeId);
            Assert.True(File.Exists(Path.Combine(root, ".ample", "takes", take.TakeId["sha256:".Length..][..2], take.TakeId["sha256:".Length..] + ".take.json")));

            TakeRead read = bank.ReadTake(take.TakeId);
            Assert.Empty(read.Missing);
            Assert.Equal(plan.BlockUuid, read.Plan.Plan.BlockUuid);
            using StreamReader reader = new(bank.OpenData(take.Data[0].DataId));
            Assert.Equal("the sound", await reader.ReadToEndAsync());
            Assert.Empty(await bank.VerifyAsync(take.TakeId));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await ticket.CommitAsync([("audio", TakeFixtures.Bytes("again"))]));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Identical_data_is_stored_once_and_identical_commits_give_the_same_take()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string first = await bank.PutDataAsync(TakeFixtures.Bytes("same bytes"));
            string second = await bank.PutDataAsync(TakeFixtures.Bytes("same bytes"));
            Assert.Equal(first, second);
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("same bytes"))), first);
            Assert.Single(Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories));

            TakeRecord a = await (await bank.BeginAsync(TakeFixtures.Plan())).CommitAsync([("audio", TakeFixtures.Bytes("same bytes"))]);
            TakeRecord b = await (await bank.BeginAsync(TakeFixtures.Plan())).CommitAsync([("audio", TakeFixtures.Bytes("same bytes"))]);
            Assert.Equal(a.TakeId, b.TakeId);
            Assert.Single(bank.ListTakes());
            Assert.Equal(2, bank.ListAttempts().Count);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Concurrent_puts_of_the_same_content_leave_one_file_and_no_leftovers()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string[] ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await bank.PutDataAsync(TakeFixtures.Bytes(new string('x', 200000)))));
            Assert.Single(ids.Distinct());
            Assert.Single(Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, ".ample", "tmp")));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Failed_and_cancelled_renders_keep_their_plan_and_the_reason_and_make_no_take()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            TakeTicket failed = await bank.BeginAsync(TakeFixtures.Plan(seed: 1));
            await failed.FailAsync("the model ran out of memory");
            TakeTicket cancelled = await bank.BeginAsync(TakeFixtures.Plan(seed: 2));
            await cancelled.CancelAsync("the user pressed stop");

            Assert.Empty(bank.ListTakes());
            IReadOnlyList<AttemptRecord> attempts = bank.ListAttempts();
            Assert.Equal(["failed", "cancelled"], attempts.Select(a => a.Outcome));
            Assert.Equal(["the model ran out of memory", "the user pressed stop"], attempts.Select(a => a.Reason));
            Assert.Single(bank.ListAttempts("failed"));
            Assert.Equal(PlanCanonical.PlanId(TakeFixtures.Plan(seed: 1)), bank.GetPlan(failed.PlanId).PlanId);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await failed.FailAsync("again"));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_parent_plan_must_exist_and_is_kept_as_lineage()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            AmpleException missing = await Assert.ThrowsAsync<AmpleException>(async () => await bank.BeginAsync(TakeFixtures.Plan(parent: "sha256:" + new string('d', 64))));
            Assert.Equal("AMP5223", missing.DiagnosticCode);

            TakeTicket parent = await bank.BeginAsync(TakeFixtures.Plan());
            TakeTicket child = await bank.BeginAsync(TakeFixtures.Plan(seed: 99, parent: parent.PlanId));
            Assert.Equal(parent.PlanId, bank.GetPlan(child.PlanId).Plan.ParentPlanId);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Verification_detects_missing_truncated_and_damaged_data_and_reading_still_gives_the_plan()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            TakeRecord take = await (await bank.BeginAsync(TakeFixtures.Plan())).CommitAsync([("audio", TakeFixtures.Bytes("0123456789abcdef"))]);
            string path = Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories).Single();

            byte[] bytes = await File.ReadAllBytesAsync(path);
            bytes[3] ^= 0x01;
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Equal(["hash"], (await bank.VerifyAsync(take.TakeId)).Select(p => p.Kind));

            await File.WriteAllBytesAsync(path, bytes[..8]);
            Assert.Equal(["size", "hash"], (await bank.VerifyAsync(take.TakeId)).Select(p => p.Kind));

            File.Delete(path);
            Assert.Equal(["missing"], (await bank.VerifyAsync(take.TakeId)).Select(p => p.Kind));
            TakeRead read = bank.ReadTake(take.TakeId);
            Assert.Single(read.Missing);
            Assert.Equal("block-1", read.Plan.Plan.BlockUuid);
            Assert.Equal("AMP5221", Assert.Throws<AmpleException>(() => bank.OpenData(take.Data[0].DataId)).DiagnosticCode);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Orphan_blobs_are_found_and_quarantined_never_deleted()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string loose = await bank.PutDataAsync(TakeFixtures.Bytes("never used by a take"));
            string hex = new('e', 64);
            string strayDir = Path.Combine(root, ".ample", "data", "ee");
            Directory.CreateDirectory(strayDir);
            await File.WriteAllTextAsync(Path.Combine(strayDir, hex), "an interrupted put");
            TakeRecord kept = await (await bank.BeginAsync(TakeFixtures.Plan())).CommitAsync([("audio", TakeFixtures.Bytes("used"))]);

            IReadOnlyList<Orphan> orphans = bank.FindOrphans();
            Assert.Contains(orphans, o => o.DataId == loose && o.InDatabase && !o.Quarantined);
            Assert.Contains(orphans, o => o.DataId == "sha256:" + hex && !o.InDatabase);
            Assert.DoesNotContain(orphans, o => o.DataId == kept.Data[0].DataId);

            Assert.Equal(2, bank.QuarantineOrphans());
            Assert.Equal(0, bank.QuarantineOrphans());
            Assert.All(bank.FindOrphans(), o => Assert.True(o.Quarantined));
            Assert.Equal(3, Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_tombstone_keeps_the_take_and_its_plan_and_sizes_count_shared_data_once()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            TakeRecord a = await (await bank.BeginAsync(TakeFixtures.Plan(seed: 1))).CommitAsync([("audio", TakeFixtures.Bytes("1234567890")), ("melody", TakeFixtures.Bytes("abc"))]);
            TakeRecord b = await (await bank.BeginAsync(TakeFixtures.Plan(seed: 2))).CommitAsync([("audio", TakeFixtures.Bytes("1234567890"))]);
            Assert.NotEqual(a.TakeId, b.TakeId);
            Assert.Equal(13, bank.SizeOfTake(a.TakeId));
            Assert.Equal(10, bank.SizeOfTake(b.TakeId));
            Assert.Equal(13, bank.SizeOfBlock("block-1"));
            Assert.Equal(0, bank.SizeOfBlock("block-none"));

            bank.Tombstone(a.TakeId, "not good enough");
            TakeRecord dropped = bank.GetTake(a.TakeId);
            Assert.True(dropped.Tombstoned);
            Assert.Equal("not good enough", dropped.TombstoneReason);
            Assert.Equal(a.PlanId, dropped.PlanId);
            Assert.Equal(2, bank.ListTakes().Count);
            Assert.Equal("AMP5223", Assert.Throws<AmpleException>(() => bank.Tombstone("sha256:none", "x")).DiagnosticCode);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Take_ids_can_be_resolved_from_a_unique_prefix()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            TakeRecord a = await (await bank.BeginAsync(TakeFixtures.Plan(seed: 1))).CommitAsync([("audio", TakeFixtures.Bytes("one"))]);
            TakeRecord b = await (await bank.BeginAsync(TakeFixtures.Plan(seed: 2))).CommitAsync([("audio", TakeFixtures.Bytes("two"))]);
            Assert.Equal(a.TakeId, bank.ResolveTakeId(a.TakeId));
            Assert.Equal(b.TakeId, bank.ResolveTakeId(b.TakeId[..14]));
            Assert.Equal("AMP5223", Assert.Throws<AmpleException>(() => bank.ResolveTakeId("sha256:")).DiagnosticCode);
            Assert.Equal("AMP5223", Assert.Throws<AmpleException>(() => bank.ResolveTakeId("nothing")).DiagnosticCode);
            Assert.Equal("AMP5223", Assert.Throws<AmpleException>(() => bank.GetTake("sha256:none")).DiagnosticCode);
            Assert.Equal("AMP5223", Assert.Throws<AmpleException>(() => bank.GetPlan("sha256:none")).DiagnosticCode);
            Assert.Equal("AMP5223", Assert.Throws<AmpleException>(() => bank.OpenData("not-an-id")).DiagnosticCode);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_failure_at_any_step_leaves_no_take_without_a_plan_and_no_temporary_files()
    {
        for (int failAt = 1; failAt <= 10; failAt++)
        {
            (string root, Takebank clean) = await TakeFixtures.NewBankAsync();
            try
            {
                Takebank faulty = Takebank.Open(new FaultyFileSystem(new PhysicalFileSystem(), failAt), new FakeClock(DateTimeOffset.UnixEpoch), root);
                try
                {
                    TakeTicket ticket = await faulty.BeginAsync(TakeFixtures.Plan());
                    await ticket.CommitAsync([("audio", TakeFixtures.Bytes("some sound"))]);
                }
                catch (Exception ex) when (ex is IOException or AmpleException { DiagnosticCode: "AMP5601" })
                {
                }

                foreach (TakeRecord take in clean.ListTakes())
                {
                    Assert.Equal(take.PlanId, clean.GetPlan(take.PlanId).PlanId);
                    Assert.Empty(await clean.VerifyAsync(take.TakeId));
                }

                Assert.Empty(Directory.GetFiles(root, "*" + AtomicFile.TempSuffix, SearchOption.AllDirectories));
                Assert.All(clean.ListAttempts("completed"), a => Assert.NotNull(a.TakeId));
                Assert.DoesNotContain(clean.ListAttempts(), a => a.Outcome == "completed" && clean.ListTakes().All(t => t.TakeId != a.TakeId));
            }
            finally
            {
                TakeFixtures.Cleanup(root);
            }
        }
    }

    [Fact]
    public async Task In_a_randomly_built_store_every_take_has_a_resolvable_plan_and_verifies()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            StableRng rng = new(2026);
            for (int i = 0; i < 150; i++)
            {
                TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan("block-" + rng.NextUInt(5), JsonSerializer.Serialize(new { i = rng.NextUInt(20) }), rng.NextUInt(10)));
                switch (rng.NextUInt(4))
                {
                    case 0:
                        await ticket.FailAsync("random failure");
                        break;
                    case 1:
                        await ticket.CancelAsync("random cancel");
                        break;
                    default:
                        await ticket.CommitAsync([("audio", TakeFixtures.Bytes("data " + rng.NextUInt(30)))]);
                        break;
                }
            }

            IReadOnlyList<TakeRecord> takes = bank.ListTakes();
            Assert.NotEmpty(takes);
            foreach (TakeRecord t in takes)
            {
                Assert.Equal(t.PlanId, bank.GetPlan(t.PlanId).PlanId);
                Assert.Empty(await bank.VerifyAsync(t.TakeId));
            }

            using SqliteConnection raw = bank.OpenConnection();
            using SqliteCommand c = raw.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM takes t LEFT JOIN plans p ON p.plan_id = t.plan_id WHERE p.plan_id IS NULL";
            Assert.Equal(0L, c.ExecuteScalar());
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }
}
