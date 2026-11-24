using System.IO.Compression;
using Ample.Audio.IO;
using Ample.Core.Abstractions;

namespace Ample.Audio.Tests;

/// <summary>A zip from outside must not leave its folder, fill the disk or the memory, or hold a hundred thousand files.</summary>
public sealed class SafeZipTests
{
    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "ample-zip-" + Guid.NewGuid().ToString("N"));

    private static string MakeZip(string folder, params (string Name, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "in.zip");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in entries)
        {
            using Stream s = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
            s.Write(data, 0, data.Length);
        }

        return path;
    }

    private static void Clean(string folder)
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
    }

    [Fact]
    public void A_zip_that_expands_to_more_than_the_limit_is_refused_and_stops_early()
    {
        string work = NewFolder();
        try
        {
            string zip = MakeZip(work, ("a/big.bin", new byte[8 * 1024 * 1024]), ("a/small.txt", [1]));
            Assert.True(new FileInfo(zip).Length < 100_000, "the test zip must be small on disk (it is the bomb)");
            string target = Path.Combine(work, "out");
            AmpleException ex = Assert.Throws<AmpleException>(() => SafeZip.ExtractAll(zip, target, maxTotalBytes: 1024 * 1024, maxEntries: 100));
            Assert.Equal("AMP5285", ex.DiagnosticCode);
            Assert.True(Directory.Exists(target) && Directory.GetFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) <= 1024 * 1024);
        }
        finally
        {
            Clean(work);
        }
    }

    [Fact]
    public void The_limit_is_for_the_whole_zip_not_for_each_file()
    {
        string work = NewFolder();
        try
        {
            string zip = MakeZip(work, ("a.bin", new byte[600_000]), ("b.bin", new byte[600_000]));
            Assert.Throws<AmpleException>(() => SafeZip.ExtractAll(zip, Path.Combine(work, "out"), maxTotalBytes: 1_000_000, maxEntries: 100));
            SafeZip.ExtractAll(zip, Path.Combine(work, "ok"), maxTotalBytes: 1_300_000, maxEntries: 100);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(work, "ok")).Length);
        }
        finally
        {
            Clean(work);
        }
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("file.txt:stream")]
    public void A_path_that_leaves_the_folder_is_refused_and_nothing_is_written_outside(string name)
    {
        string work = NewFolder();
        try
        {
            string zip = MakeZip(work, (name, [1, 2, 3]));
            string target = Path.Combine(work, "out", "inner");
            Assert.Throws<AmpleException>(() => SafeZip.ExtractAll(zip, target, maxTotalBytes: 1_000_000, maxEntries: 100));
            Assert.Empty(Directory.GetFiles(work, "evil*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Path.Combine(work, "out"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            Clean(work);
        }
    }

    [Fact]
    public void Too_many_entries_are_refused_before_anything_is_written()
    {
        string work = NewFolder();
        try
        {
            string zip = MakeZip(work, Enumerable.Range(0, 30).Select(i => ("f" + i + ".txt", new byte[] { 1 })).ToArray());
            string target = Path.Combine(work, "out");
            Assert.Throws<AmpleException>(() => SafeZip.ExtractAll(zip, target, maxTotalBytes: 1_000_000, maxEntries: 10));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            Clean(work);
        }
    }

    [Fact]
    public void A_normal_zip_extracts_with_its_folders()
    {
        string work = NewFolder();
        try
        {
            string zip = MakeZip(work, ("pack.json", "{}"u8.ToArray()), ("samples/kick.wav", [9, 9]));
            SafeZip.ExtractAll(zip, Path.Combine(work, "out"), maxTotalBytes: 1_000_000, maxEntries: 100);
            Assert.Equal("{}", File.ReadAllText(Path.Combine(work, "out", "pack.json")));
            Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(work, "out", "samples", "kick.wav")));
        }
        finally
        {
            Clean(work);
        }
    }

    [Fact]
    public void A_text_part_that_expands_past_the_limit_is_refused_and_a_small_one_is_read()
    {
        string work = NewFolder();
        try
        {
            string zip = MakeZip(work, ("score.xml", new byte[2_000_000]), ("tiny.xml", "<a/>"u8.ToArray()));
            using ZipArchive archive = ZipFile.OpenRead(zip);
            Assert.Throws<AmpleException>(() => SafeZip.ReadText(archive.GetEntry("score.xml")!, 1_000_000));
            Assert.Equal("<a/>", SafeZip.ReadText(archive.GetEntry("tiny.xml")!, 1_000_000));
        }
        finally
        {
            Clean(work);
        }
    }

    [Fact]
    public void One_entry_over_the_limit_leaves_no_partial_file()
    {
        string work = NewFolder();
        try
        {
            string zip = MakeZip(work, ("big.bin", new byte[2_000_000]));
            using ZipArchive archive = ZipFile.OpenRead(zip);
            string target = Path.Combine(work, "big.out");
            Assert.Throws<AmpleException>(() => SafeZip.ExtractEntry(archive.GetEntry("big.bin")!, target, 1_000_000));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Clean(work);
        }
    }
}
