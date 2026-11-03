using Ample.Audio.Engine;

namespace Ample.Plugins.Tests;

public sealed class FaerElseLocalRendererTests
{
    private sealed class Fake(string id) : IBlockRenderer
    {
        public string BlockType => "ace-music";
        public string Id => id;
        public string Version => "1";
        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_missing_profile_means_local_and_the_decision_stays_fixed()
    {
        string none = Path.Combine(Path.GetTempPath(), "ample-no-faer-" + Guid.NewGuid().ToString("N"));
        FaerProfile profile = new(new Uri("https://faer.invalid"), Path.Combine(none, "token"), Path.Combine(none, "cert.pem"));
        FaerElseLocalRenderer renderer = new(new Fake("faer"), new Fake("local"), profile, "song");
        string[] ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => renderer.Id)));
        Assert.All(ids, i => Assert.Equal("local", i));
        Assert.Equal("local", renderer.Id);
        Assert.Equal("ace-music", renderer.BlockType);
    }
}
