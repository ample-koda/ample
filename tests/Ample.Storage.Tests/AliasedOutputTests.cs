using System.Text;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

[Trait("Category", "Takebank")]
public sealed class AliasedOutputTests
{
    [Fact]
    public async Task Aliased_output_buffers_keep_every_role_with_the_same_content_id()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] audio = Encoding.UTF8.GetBytes("one buffer under three names");
            MemoryStream Alias() => new(audio, 0, audio.Length, false, true);
            TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan());
            TakeRecord take = await ticket.CommitAsync([("mixed", Alias()), ("out", Alias()), ("audio", Alias())]);
            Assert.Equal(3, take.Data.Count);
            Assert.Single(take.Data.Select(d => d.DataId).Distinct());
        }
        finally { TakeFixtures.Cleanup(root); }
    }

    /// <summary>A memory stream that counts how often its content is read.</summary>
    private sealed class CountingMemory(byte[] buffer, int offset, int count) : MemoryStream(buffer, offset, count, false, true)
    {
        public int Reads { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(destination, cancellationToken);
        }
    }

    [Fact]
    public async Task Aliased_output_buffers_are_read_and_stored_once_and_other_roles_are_untouched()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] audio = Encoding.UTF8.GetBytes("shared audio shared audio");
            byte[] other = Encoding.UTF8.GetBytes("a report");
            CountingMemory first = new(audio, 0, audio.Length), second = new(audio, 0, audio.Length), third = new(audio, 0, audio.Length);
            TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan());
            TakeRecord take = await ticket.CommitAsync(
                [("mixed", first), ("out", second), ("report", new MemoryStream(other)), ("audio", third)]);

            Assert.Equal(["audio", "mixed", "out", "report"], take.Data.Select(d => d.Role).Order(StringComparer.Ordinal));
            Assert.True(first.Reads > 0);
            Assert.Equal(0, second.Reads);
            Assert.Equal(0, third.Reads);
            string sharedId = take.Data.Single(d => d.Role == "mixed").DataId;
            Assert.Equal(sharedId, take.Data.Single(d => d.Role == "out").DataId);
            Assert.Equal(sharedId, take.Data.Single(d => d.Role == "audio").DataId);
            Assert.NotEqual(sharedId, take.Data.Single(d => d.Role == "report").DataId);
            using (Stream stored = bank.OpenData(sharedId)) Assert.Equal(audio.Length, stored.Length);
        }
        finally { TakeFixtures.Cleanup(root); }
    }

    [Fact]
    public async Task Different_slices_of_one_buffer_and_streams_that_were_already_read_are_not_treated_as_aliases()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            byte[] audio = Encoding.UTF8.GetBytes("0123456789abcdef");
            MemoryStream moved = new(audio, 0, audio.Length, false, true) { Position = 4 };
            TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan());
            TakeRecord take = await ticket.CommitAsync([
                ("whole", new MemoryStream(audio, 0, audio.Length, false, true)),
                ("head", new MemoryStream(audio, 0, 8, false, true)),
                ("tail", new MemoryStream(audio, 8, 8, false, true)),
                ("moved", moved),
            ]);

            Assert.Equal(4, take.Data.Select(d => d.DataId).Distinct().Count());
        }
        finally { TakeFixtures.Cleanup(root); }
    }
}
