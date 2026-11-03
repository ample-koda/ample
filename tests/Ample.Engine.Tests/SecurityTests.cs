using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Engine.Security;
using Ample.Storage.Backup;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Security;
using Ample.Testing;
using Microsoft.Data.Sqlite;

namespace Ample.Core.Tests;

[Trait("Category", "Vault")]
public sealed class SecurityTests : IDisposable
{
    private const string Canary = "CANARY-7f3a91-do-not-leak";
    private const string Passphrase = "correct horse battery staple";
    private const int Fast = 1000;
    private readonly string folder = Directory.CreateTempSubdirectory("ample-security-").FullName;
    private readonly FakeClock clock = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly PhysicalFileSystem fs = new();
    private readonly Func<string, string?> previousSource = ProjectEncryption.PassphraseSource;

    public void Dispose()
    {
        ProjectEncryption.PassphraseSource = previousSource;
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private string P(params string[] parts) => Path.Combine([folder, .. parts]);

    private async Task<(string Root, Guid Id)> ProjectAsync(string name = "proj")
    {
        string root = P(name);
        using OpenProject p = await ProjectStore.CreateAsync(fs, clock, new GuidV7Generator(), root, name);
        File.WriteAllText(Path.Combine(root, "song.amplet"), "composition \"x\" { ample 1  tempo 92  master-seed 1 }\n");
        return (root, p.Manifest.Id);
    }

    private async Task<Journal> JournalAsync(string root, Guid id) => await Journal.OpenAsync(fs, clock, root, id.ToString("D"));

    private static string AllSegmentText(string root) => string.Concat(Directory.EnumerateFiles(Path.Combine(root, ".ample", "blackbox"), "*.seg").Order(StringComparer.Ordinal).Select(File.ReadAllText));

    private static bool TreeContains(string folder, string text)
    {
        byte[] needle = Encoding.UTF8.GetBytes(text);
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => Contains(File.ReadAllBytes(f), needle));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void The_vault_round_trips_refuses_a_wrong_passphrase_and_keeps_values_out_of_its_file()
    {
        string home = P("home");
        PassphraseVault vault = PassphraseVault.Create(home, Passphrase, Fast);
        vault.Set("API_KEY", Canary);
        Assert.Equal(["API_KEY"], PassphraseVault.Open(home, Passphrase).Names);
        Assert.Equal(Canary, PassphraseVault.Open(home, Passphrase).Get("API_KEY"));
        Assert.Null(PassphraseVault.Open(home, Passphrase).Get("OTHER"));

        string file = File.ReadAllText(PassphraseVault.FileOf(home));
        Assert.DoesNotContain(Canary, file, StringComparison.Ordinal);
        Assert.Contains("API_KEY", file, StringComparison.Ordinal);

        AmpleException wrong = Assert.Throws<AmpleException>(() => PassphraseVault.Open(home, "not the passphrase at all"));
        Assert.Equal("AMP5311", wrong.DiagnosticCode);
        Assert.Throws<AmpleException>(() => PassphraseVault.Create(home, Passphrase, Fast));
        Assert.Throws<AmpleException>(() => PassphraseVault.Create(P("home2"), "short", Fast));
        Assert.Throws<AmpleException>(() => vault.Set("bad name!", "x"));
        Assert.True(vault.Remove("API_KEY"));
        Assert.Empty(PassphraseVault.Open(home, Passphrase).Names);
    }

    [Fact]
    public void A_sealed_value_cannot_be_moved_to_another_name()
    {
        string home = P("home");
        PassphraseVault vault = PassphraseVault.Create(home, Passphrase, Fast);
        vault.Set("A", "value-a");
        string file = PassphraseVault.FileOf(home);
        string text = File.ReadAllText(file);
        File.WriteAllText(file, text.Replace("\"A\":", "\"B\":", StringComparison.Ordinal));
        Assert.Throws<AmpleException>(() => PassphraseVault.Open(home, Passphrase).Get("B"));
    }

    [Fact]
    public void Lines_and_files_round_trip_and_tampering_or_a_wrong_key_is_noticed()
    {
        DataCipher a = new(Crypto.Random(32));
        DataCipher b = new(Crypto.Random(32));
        string sealedLine = a.SealLine("hello " + Canary);
        Assert.DoesNotContain(Canary, sealedLine, StringComparison.Ordinal);
        Assert.Equal("hello " + Canary, a.OpenLine(sealedLine));
        Assert.Equal("plain", a.OpenLine("plain"));
        Assert.Equal("AMP5310", Assert.Throws<AmpleException>(() => b.OpenLine(sealedLine)).DiagnosticCode);
        string tampered = sealedLine[..^6] + (sealedLine[^6] == 'A' ? "B" : "A") + sealedLine[^5..];
        Assert.Throws<AmpleException>(() => a.OpenLine(tampered));

        string source = P("big.bin");
        byte[] data = Crypto.Random((2 * 1024 * 1024) + 123);
        File.WriteAllBytes(source, data);
        a.SealFile(source, P("big.enc"));
        Assert.True(DataCipher.IsSealedFile(P("big.enc")));
        Assert.False(DataCipher.IsSealedFile(source));
        a.OpenFile(P("big.enc"), P("big.out"));
        Assert.Equal(data, File.ReadAllBytes(P("big.out")));
        Assert.Throws<AmpleException>(() => b.OpenFile(P("big.enc"), P("big.bad")));

        byte[] damaged = File.ReadAllBytes(P("big.enc"));
        damaged[200] ^= 0xFF;
        File.WriteAllBytes(P("big.damaged"), damaged);
        Assert.Throws<AmpleException>(() => a.OpenFile(P("big.damaged"), P("big.bad2")));

        File.WriteAllBytes(P("empty.bin"), []);
        a.SealFile(P("empty.bin"), P("empty.enc"));
        a.OpenFile(P("empty.enc"), P("empty.out"));
        Assert.Empty(File.ReadAllBytes(P("empty.out")));
    }

    [Fact]
    public async Task Encryption_is_off_by_default_and_the_acts_need_their_typed_words()
    {
        (string root, Guid id) = await ProjectAsync();
        using Journal journal = await JournalAsync(root, id);
        Assert.False(ProjectEncryption.IsEnabled(root));
        CommandContext context = new(fs, root, clock);
        EnableEncryptionAct act = new(Passphrase, clock.UtcNow, Fast);
        AmpleException refused = await Assert.ThrowsAsync<AmpleException>(async () => await act.RunAsync(journal, context, "test", "yes", "because"));
        Assert.Equal("AMP5232", refused.DiagnosticCode);
        Assert.False(ProjectEncryption.IsEnabled(root));
        await Assert.ThrowsAsync<AmpleException>(async () => await new EnableEncryptionAct("short", clock.UtcNow, Fast).RunAsync(journal, context, "test", "encrypt this project", "because"));
        await Assert.ThrowsAsync<AmpleException>(async () => await new DisableEncryptionAct().RunAsync(journal, context, "test", "decrypt this project", "because"));
    }

    [Fact]
    public async Task Enabling_seals_the_black_box_on_disk_and_it_still_reads_verifies_and_comes_back_in_the_clear_when_disabled()
    {
        (string root, Guid id) = await ProjectAsync();
        using (Journal journal = await JournalAsync(root, id))
        {
            await journal.AppendAsync("user", "file.write", "ok", "c1", JsonSerializer.Serialize(new { text = Canary }));
            Assert.Contains(Canary, AllSegmentText(root), StringComparison.Ordinal);

            await new EnableEncryptionAct(Passphrase, clock.UtcNow, Fast).RunAsync(journal, new CommandContext(fs, root, clock), "test", "encrypt this project", "testing");
            await journal.AppendAsync("user", "file.write", "ok", "c2", JsonSerializer.Serialize(new { text = Canary + "-again" }));

            string onDisk = AllSegmentText(root);
            Assert.DoesNotContain(Canary, onDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("file.write", onDisk, StringComparison.Ordinal);
            Assert.True(ProjectEncryption.IsEnabled(root));
            Assert.Null((await journal.VerifyAsync()).BadSeq);
            Assert.Contains(await journal.ReadAllAsync(), r => r.PayloadJson.Contains(Canary + "-again", StringComparison.Ordinal));
        }

        // A new process: nothing is unlocked, the passphrase comes from the source.
        ProjectEncryption.Forget(root);
        ProjectEncryption.PassphraseSource = _ => null;
        AmpleException locked = await Assert.ThrowsAsync<AmpleException>(async () => await JournalAsync(root, id));
        Assert.Equal("AMP5310", locked.DiagnosticCode);
        ProjectEncryption.PassphraseSource = _ => "a wrong passphrase here";
        Assert.Equal("AMP5310", (await Assert.ThrowsAsync<AmpleException>(async () => await JournalAsync(root, id))).DiagnosticCode);
        ProjectEncryption.PassphraseSource = _ => Passphrase;
        using (Journal journal = await JournalAsync(root, id))
        {
            Assert.Null((await journal.VerifyAsync()).BadSeq);
            Assert.Contains(await journal.ReadAllAsync(), r => r.PayloadJson.Contains(Canary, StringComparison.Ordinal));
            await new DisableEncryptionAct().RunAsync(journal, new CommandContext(fs, root, clock), "test", "decrypt this project", "testing");
            Assert.False(ProjectEncryption.IsEnabled(root));
            Assert.Contains(Canary, AllSegmentText(root), StringComparison.Ordinal);
            Assert.Null((await journal.VerifyAsync()).BadSeq);
        }
    }

    [Fact]
    public async Task A_changed_sealed_record_is_found_by_verify()
    {
        (string root, Guid id) = await ProjectAsync();
        using Journal journal = await JournalAsync(root, id);
        await journal.AppendAsync("user", "a", "ok", "c1", "{}");
        await journal.AppendAsync("user", "b", "ok", "c2", "{}");
        await new EnableEncryptionAct(Passphrase, clock.UtcNow, Fast).RunAsync(journal, new CommandContext(fs, root, clock), "test", "encrypt this project", "testing");
        string seg = Directory.EnumerateFiles(Path.Combine(root, ".ample", "blackbox"), "*.seg").First();
        string[] lines = File.ReadAllLines(seg);
        lines[1] = lines[1][..^8] + (lines[1][^8] == 'A' ? "B" : "A") + lines[1][^7..];
        File.WriteAllText(seg, string.Concat(lines.Select(l => l + (char)10)));
        Assert.NotNull((await journal.VerifyAsync()).BadSeq);
    }

    [Fact]
    public async Task The_passphrase_can_change_without_re_encrypting_and_the_recovery_kit_holds_the_key()
    {
        (string root, Guid id) = await ProjectAsync();
        using Journal journal = await JournalAsync(root, id);
        await new EnableEncryptionAct(Passphrase, clock.UtcNow, Fast).RunAsync(journal, new CommandContext(fs, root, clock), "test", "encrypt this project", "testing");
        string kit = ProjectEncryption.RecoveryKit(root, Passphrase);
        Assert.Contains("AMPLE recovery kit", kit, StringComparison.Ordinal);

        ProjectEncryption.ChangePassphrase(root, Passphrase, "another long passphrase", Fast);
        ProjectEncryption.Forget(root);
        ProjectEncryption.PassphraseSource = _ => Passphrase;
        Assert.Equal("AMP5310", (await Assert.ThrowsAsync<AmpleException>(async () => await JournalAsync(root, id))).DiagnosticCode);
        ProjectEncryption.PassphraseSource = _ => "another long passphrase";
        using Journal again = await JournalAsync(root, id);
        Assert.Null((await again.VerifyAsync()).BadSeq);

        DataCipher fromKit = ProjectEncryption.FromRecoveryKit(kit);
        string line = ProjectEncryption.CipherFor(root)!.SealLine("x");
        Assert.Equal("x", fromKit.OpenLine(line));
        Assert.Throws<AmpleException>(() => ProjectEncryption.FromRecoveryKit("nothing"));
    }

    [Fact]
    public async Task Backups_of_an_encrypted_project_are_sealed_verify_and_restore_and_the_wrong_key_is_refused()
    {
        (string root, Guid id) = await ProjectAsync();
        File.WriteAllText(Path.Combine(root, "lyrics.txt"), "words " + Canary);
        using (Journal journal = await JournalAsync(root, id))
        {
            await new EnableEncryptionAct(Passphrase, clock.UtcNow, Fast).RunAsync(journal, new CommandContext(fs, root, clock), "test", "encrypt this project", "testing");
        }

        BackupStore store = new(P("target"), clock);
        BackupReport report = store.Create(root, id, "proj");
        Assert.False(TreeContains(P("target", "objects"), Canary));
        Assert.True(File.Exists(P("target", "encryption.json")));
        Assert.True(store.Verify(report.Id).Ok);

        // A fresh process can restore from the folder with the passphrase.
        ProjectEncryption.PassphraseSource = _ => Passphrase;
        ProjectEncryption.Forget(P("target"));
        string restored = P("restored");
        new BackupStore(P("target"), clock).RestoreToFolder(report.Id, restored);
        Assert.Equal("words " + Canary, File.ReadAllText(Path.Combine(restored, "lyrics.txt")));
        Assert.True(ProjectEncryption.IsEnabled(restored));

        // Another project's key must not be mixed into this folder.
        (string other, Guid otherId) = await ProjectAsync("other");
        using (Journal j2 = await JournalAsync(other, otherId))
        {
            await new EnableEncryptionAct("a completely different one", clock.UtcNow, Fast).RunAsync(j2, new CommandContext(fs, other, clock), "test", "encrypt this project", "testing");
        }

        AmpleException mismatch = Assert.Throws<AmpleException>(() => new BackupStore(P("target"), clock).Create(other, otherId, "other"));
        Assert.Equal("AMP5314", mismatch.DiagnosticCode);
    }

    [Fact]
    public async Task An_export_of_an_encrypted_project_is_readable_by_any_zip_tool_but_its_black_box_stays_sealed()
    {
        (string root, Guid id) = await ProjectAsync();
        File.WriteAllText(Path.Combine(root, "lyrics.txt"), "words for sharing");
        using (Journal journal = await JournalAsync(root, id))
        {
            await journal.AppendAsync("user", "file.write", "ok", "c1", JsonSerializer.Serialize(new { text = Canary }));
            await new EnableEncryptionAct(Passphrase, clock.UtcNow, Fast).RunAsync(journal, new CommandContext(fs, root, clock), "test", "encrypt this project", "testing");
        }

        string file = P("song.ample");
        Archive.Export(root, id, "proj", file, true, clock);
        using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(file);
        Assert.Contains(zip.Entries, e => e.FullName == "files/lyrics.txt");
        using StreamReader r = new(zip.GetEntry("files/lyrics.txt")!.Open());
        Assert.Equal("words for sharing", r.ReadToEnd());
        foreach (System.IO.Compression.ZipArchiveEntry e in zip.Entries.Where(e => e.FullName.Contains("blackbox", StringComparison.Ordinal)))
        {
            using StreamReader s = new(e.Open());
            Assert.DoesNotContain(Canary, s.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Signatures_verify_and_tampering_cut_records_a_forged_log_and_the_wrong_key_are_found()
    {
        (string root, Guid id) = await ProjectAsync();
        string home = P("home");
        SigningKeys keys = new(home);
        SigningKeyInfo key = keys.Generate(Passphrase, clock.UtcNow, Fast);
        Assert.Equal(key.KeyId, keys.Active!.KeyId);
        Assert.Contains("BEGIN PUBLIC KEY", keys.ExportPublic(key.KeyId), StringComparison.Ordinal);

        using Journal journal = await JournalAsync(root, id);
        await journal.AppendAsync("user", "a", "ok", "c1", "{}");
        await journal.AppendAsync("user", "b", "ok", "c2", "{}");
        Signatures.SignHead(journal, root, id, keys, Passphrase, clock.UtcNow);
        await journal.AppendAsync("user", "c", "ok", "c3", "{}");
        Signatures.SignHead(journal, root, id, keys, Passphrase, clock.UtcNow);

        SignatureReport good = await Signatures.VerifyHeadsAsync(journal, root, id, null);
        Assert.True(good.Ok);
        Assert.Equal(2, good.Checked);
        Assert.False((await Signatures.VerifyHeadsAsync(journal, root, id, "0000000000000000")).Ok);
        Assert.Equal("AMP5313", Assert.Throws<AmpleException>(() => keys.Sign("the wrong passphrase", [1])).DiagnosticCode);

        // The signature log is forged: another seq under the same signature.
        string log = Signatures.LogOf(root);
        string original = File.ReadAllText(log);
        File.WriteAllText(log, original.Replace("\"seq\":2", "\"seq\":1", StringComparison.Ordinal));
        Assert.False((await Signatures.VerifyHeadsAsync(journal, root, id, null)).Ok);
        File.WriteAllText(log, original);

        // The last signed record is changed (with a valid checksum, so only the signature notices).
        string seg = Directory.EnumerateFiles(Path.Combine(root, ".ample", "blackbox"), "*.seg").Single();
        string[] lines = File.ReadAllLines(seg);
        string[] lastParts = lines[^1].Split('\t');
        string changed = lastParts[0].Replace("\"actor\":\"user\"", "\"actor\":\"evil\"", StringComparison.Ordinal);
        lines[^1] = changed + "\t" + Journal.Crc32c(Encoding.UTF8.GetBytes(changed)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(seg, string.Concat(lines.Select(l => l + (char)10)));
        SignatureReport tampered = await Signatures.VerifyHeadsAsync(journal, root, id, null);
        Assert.False(tampered.Ok);
        Assert.Contains("changed after", string.Join(" | ", tampered.Problems), StringComparison.Ordinal);

        // Records were cut off.
        File.WriteAllText(seg, string.Concat(lines.Take(2).Select(l => l + (char)10)));
        Assert.Contains("cut off", string.Concat((await Signatures.VerifyHeadsAsync(journal, root, id, null)).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rotated_key_still_verifies_its_old_signatures_but_cannot_sign()
    {
        string home = P("home");
        SigningKeys keys = new(home);
        SigningKeyInfo first = keys.Generate(Passphrase, clock.UtcNow, Fast);
        string file = P("export.ample");
        File.WriteAllText(file, "the export");
        Signatures.SignFile(file, keys, Passphrase, clock.UtcNow);
        SigningKeyInfo second = keys.Rotate(Passphrase, clock.UtcNow.AddDays(1), Fast);
        Assert.NotEqual(first.KeyId, second.KeyId);
        Assert.Equal(second.KeyId, keys.Active!.KeyId);
        Assert.Equal(first.KeyId, Signatures.VerifyFile(file));
        Assert.Equal(2, keys.List().Count);
        Assert.NotNull(keys.List().Single(k => k.KeyId == first.KeyId).Retired);

        Assert.Equal("AMP5312", Assert.Throws<AmpleException>(() => Signatures.VerifyFile(file, second.KeyId)).DiagnosticCode);
        File.WriteAllText(file, "the export, changed");
        Assert.Equal("AMP5312", Assert.Throws<AmpleException>(() => Signatures.VerifyFile(file)).DiagnosticCode);
    }

    [Fact]
    public async Task A_canary_secret_in_the_vault_never_reaches_the_project_the_journal_a_backup_or_an_export()
    {
        string home = P("home");
        PassphraseVault.Create(home, Passphrase, Fast).Set("TEST_TOKEN", Canary);
        new SigningKeys(home).Generate(Passphrase, clock.UtcNow, Fast);

        (string root, Guid id) = await ProjectAsync();
        File.WriteAllText(Path.Combine(root, ".ample", "plugin-settings.json"), "{\"voice\":{\"token\":\"TEST_TOKEN\"}}");
        using (Journal journal = await JournalAsync(root, id))
        {
            await journal.AppendAsync("plugin", "plugin.render", "ok", "c1", "{\"needs\":[\"TEST_TOKEN\"]}");
            await new EnableEncryptionAct(Passphrase, clock.UtcNow, Fast).RunAsync(journal, new CommandContext(fs, root, clock), "test", "encrypt this project", "testing");
            Signatures.SignHead(journal, root, id, new SigningKeys(home), Passphrase, clock.UtcNow);
        }

        new BackupStore(P("target"), clock).Create(root, id, "proj");
        Directory.CreateDirectory(P("out"));
        Archive.Export(root, id, "proj", P("out", "song.ample"), true, clock);
        using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(P("out", "song.ample")))
        {
            foreach (System.IO.Compression.ZipArchiveEntry e in zip.Entries)
            {
                using StreamReader reader = new(e.Open());
                Assert.DoesNotContain(Canary, reader.ReadToEnd(), StringComparison.Ordinal);
            }
        }

        Assert.False(TreeContains(root, Canary));
        Assert.False(TreeContains(P("target"), Canary));
        Assert.True(TreeContains(home, "TEST_TOKEN"));
        Assert.False(TreeContains(home, Canary));
    }
}
