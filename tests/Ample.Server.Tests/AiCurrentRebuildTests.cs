using Ample.Engine.Engine;

namespace Ample.Server.Tests;

public sealed class AiCurrentRebuildTests
{
    [Fact]
    public void Rebuild_ignores_selected_takes_but_keeps_seed_and_pinned_melody()
    {
        IBuildControl source = new TestControl();
        IgnoreTakeSelections rebuild = new(source);

        Assert.Null(rebuild.SelectedTake("voice"));
        Assert.Equal(42UL, rebuild.SeedFor("voice"));
        Assert.Equal("sha256:pinned-melody", rebuild.PinnedMelodyDataId("voice"));
    }

    private sealed class TestControl : IBuildControl
    {
        public ulong? SeedFor(string block) => 42;
        public string? SelectedTake(string block) => "take:stale-selection";
        public string? PinnedMelodyDataId(string block) => "sha256:pinned-melody";
    }
}
