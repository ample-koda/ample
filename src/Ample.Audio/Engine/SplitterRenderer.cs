namespace Ample.Audio.Engine;

/// <summary>Splits audio into two mono channel outputs without changing their level or duration.</summary>
internal sealed class SplitterRenderer : IBlockRenderer
{
    public string BlockType => "splitter";
    public string Id => "builtin.splitter";
    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] input = request.Inputs["in"];
        (StereoBuffer channels, int rate) = ChannelAudio.Decode(input);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([
            new RenderOutput("left", Wav.Encode(channels.Left, rate)),
            new RenderOutput("right", Wav.Encode(channels.Right, rate)),
            new RenderOutput("audio", input),
        ]);
    }
}
