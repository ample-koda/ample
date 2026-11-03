using Ample.Core.Abstractions;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class EngineChoiceTests
{
    [Fact]
    public void An_empty_engine_follows_the_project()
    {
        Assert.Null(EngineChoice.Parse(null, "ace-music"));
        Assert.Null(EngineChoice.Parse("", "ace-music"));
        Assert.Null(EngineChoice.Parse("\"  \"", "ai-voice"));
    }

    [Theory]
    [InlineData("local", "local")]
    [InlineData("\"faer\"", "faer")]
    [InlineData(" faer-else-local ", "faer-else-local")]
    public void A_route_alone_is_a_route_without_a_model(string text, string route)
    {
        EngineChoice? choice = EngineChoice.Parse(text, "ace-music");
        Assert.NotNull(choice);
        Assert.Equal(route, choice.Route);
        Assert.False(choice.HasModel);
        Assert.Empty(choice.Settings());
    }

    [Theory]
    [InlineData("faer:xl-sft", "faer", "xl-sft")]
    [InlineData("local:turbo", "local", "turbo")]
    [InlineData("\"faer-else-local:base\"", "faer-else-local", "base")]
    public void A_family_keyword_sets_the_synth_model(string text, string route, string family)
    {
        EngineChoice? choice = EngineChoice.Parse(text, "ace-music");
        Assert.NotNull(choice);
        Assert.Equal(route, choice.Route);
        Assert.Equal(family, Assert.Single(choice.Settings(), s => s.Key == "synthModel").Value);
        Assert.DoesNotContain("synthModelFile", choice.Settings().Keys);
    }

    [Fact]
    public void A_gguf_file_name_sets_the_synth_model_file()
    {
        EngineChoice? choice = EngineChoice.Parse("local:acestep-v15-xl-sft-Q8_0.gguf", "ai-voice");
        Assert.NotNull(choice);
        Assert.Equal("acestep-v15-xl-sft-Q8_0.gguf", choice.Settings()["synthModelFile"]);
        Assert.DoesNotContain("synthModel", choice.Settings().Keys);
    }

    [Theory]
    [InlineData("cloud", "must start with local, faer or faer-else-local")]
    [InlineData(":sft", "must start with local, faer or faer-else-local")]
    [InlineData("sft", "must start with local, faer or faer-else-local")]
    [InlineData("faer:", "neither a family")]
    [InlineData("faer:huge", "neither a family")]
    [InlineData("local:../models/a.gguf", "neither a family")]
    [InlineData("local:.gguf", "neither a family")]
    public void Anything_else_is_AMP5383(string text, string part)
    {
        AmpleException error = Assert.Throws<AmpleException>(() => EngineChoice.Parse(text, "ace-music"));
        Assert.Equal("AMP5383", error.DiagnosticCode);
        Assert.Contains(part, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_voice_clone_takes_a_route_only()
    {
        Assert.Equal("faer", EngineChoice.Parse("faer", "voice-clone")!.Route);
        AmpleException error = Assert.Throws<AmpleException>(() => EngineChoice.Parse("faer:sft", "voice-clone"));
        Assert.Equal("AMP5383", error.DiagnosticCode);
        Assert.Contains("own model parameter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_three_voice_and_music_blocks_have_an_engine()
    {
        Assert.True(EngineChoice.Applies("ace-music"));
        Assert.True(EngineChoice.Applies("ai-voice"));
        Assert.True(EngineChoice.Applies("voice-clone"));
        Assert.False(EngineChoice.Applies("drums"));
    }
}
