using Ample.Audio.Engine;

namespace Ample.Core.Tests;

/// <summary>Finals: their store and the cut of a master with its fades.</summary>
public sealed class FinalsTests
{
    [Fact]
    public void A_song_has_an_implicit_full_final_and_finals_are_kept_with_what_their_render_made()
    {
        string root = Directory.CreateTempSubdirectory("ample-finals-").FullName;
        try
        {
            FinalsStore store = new(root);
            Assert.Equal("full", Assert.Single(store.List("a.amplet")).Spec.Name);

            store.Upsert("a.amplet", new FinalSpec("teaser", 5, 8, 1, 2, "mp3"));
            Assert.Equal(["full", "teaser"], store.List("a.amplet").Select(f => f.Spec.Name));
            store.Record("a.amplet", new FinalSpec("teaser", 5, 8, 1, 2, "mp3"), "sha256:abc", "renders/a-teaser.mp3", 1234, DateTimeOffset.UnixEpoch);
            Assert.Equal("renders/a-teaser.mp3", store.List("a.amplet").Single(f => f.Spec.Name == "teaser").Path);
            Assert.Contains("sha256:abc", store.TakeIds());

            // changing the definition keeps what was rendered; another song is separate
            store.Upsert("a.amplet", new FinalSpec("teaser", 9, 4, 0, 0, "wav"));
            Assert.Equal("sha256:abc", store.List("a.amplet").Single(f => f.Spec.Name == "teaser").TakeId);
            Assert.Equal("full", Assert.Single(store.List("b.amplet")).Spec.Name);
            Assert.True(store.Remove("a.amplet", "teaser"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_cut_keeps_the_bars_and_fades_the_edges()
    {
        const int rate = 1000;   // tempo 60: a beat is 1000 samples and a bar 4000
        short[] loud = Enumerable.Repeat((short)10000, 20_000).ToArray();
        StereoBuffer master = new(loud, (short[])loud.Clone());
        StereoBuffer cut = FinalsStore.Cut(master, rate, 60, new FinalSpec("t", 2, 2, 1, 1, "wav"));
        Assert.Equal(8000, cut.Length);
        Assert.Equal(0, cut.Left[0]);
        Assert.Equal(10000, cut.Left[1500]);
        Assert.True(cut.Left[^1] < 10);
        Assert.Equal(StereoBuffer.FromMono(loud).Length, FinalsStore.Cut(master, rate, 60, FinalsStore.Full).Length);
    }
}
