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
}
