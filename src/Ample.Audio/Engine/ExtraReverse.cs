namespace Ample.Audio.Engine;

/// <summary>Plays the sound backwards (a reversed hit makes a swell that lands on the beat).</summary>
internal sealed class ReverseRenderer : ExtraEffect
{
    public override string BlockType => "reverse";

    public override string Version => "1";

    public override string Determinism => "exact";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        short[] l = [.. Enumerable.Reverse(left)];
        short[] rr = stereo ? [.. Enumerable.Reverse(right)] : l;
        return Out(Encode(l, rr, rate, stereo));
    }
}
