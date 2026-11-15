using Ample.Audio.Instruments;

namespace Ample.Core.Tests.Instruments;

public sealed class Sf2SharedReadTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-sf2-shared-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // a locked file must not fail the run
        }
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void The_same_unchanged_file_is_parsed_once_and_shared()
    {
        string path = Write("a.sf2", Sf2Fixture.Tiny());

        Assert.Same(Sf2Reader.ReadShared(path), Sf2Reader.ReadShared(path));
    }

    [Fact]
    public void Blocks_that_ask_at_the_same_time_get_the_same_bank()
    {
        string path = Write("b.sf2", Sf2Fixture.Tiny());

        Sf2Bank[] banks = [.. Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8).Select(_ => Sf2Reader.ReadShared(path))];

        Assert.All(banks, bank => Assert.Same(banks[0], bank));
    }

    [Fact]
    public void A_replaced_file_is_read_again()
    {
        string path = Write("c.sf2", Sf2Fixture.Tiny());
        Sf2Bank first = Sf2Reader.ReadShared(path);
        byte[] other = Sf2Fixture.Tiny();
        File.WriteAllBytes(path, [.. other, 0, 0, 0, 0]);   // another size: another file
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.NotSame(first, Sf2Reader.ReadShared(path));
    }

    [Fact]
    public void A_missing_file_says_so_and_is_not_kept()
    {
        string path = Path.Combine(folder, "missing.sf2");

        Assert.Throws<Sf2FormatException>(() => Sf2Reader.ReadShared(path));
        Assert.Throws<Sf2FormatException>(() => Sf2Reader.ReadShared(path));
    }
}
