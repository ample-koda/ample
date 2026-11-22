using Ample.Speech.Flite.Cg;

namespace Ample.Speech;

/// <summary>
/// The pipeline of a clustergen voice (<c>slt</c>, <c>rms</c> or <c>awb</c>): <see cref="FliteCgEngine"/> (the translation of Flite for these voices), with the spoken words placed in the
/// waveform and in the text exactly as for kal16 (<see cref="FliteDefaultPipeline.Speak"/>). It speaks text, with the markup and the words of the block that have their own phones; it does not
/// speak phonemes.
/// </summary>
internal sealed class FliteCgPipeline(string voice) : IFlitePipeline
{
    private readonly Lazy<FliteCgEngine> engine = new(() => new FliteCgEngine(FliteCgVoiceData.Embedded(voice)), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the engine of the voice (made when it is first needed).</summary>
    internal FliteCgEngine Engine => engine.Value;

    /// <inheritdoc />
    public FliteRender Run(string text, FliteSettings settings, CancellationToken cancellationToken)
    {
        FliteCgEngine cg = engine.Value;
        FliteCgOptions options = OptionsOf(cg.Voice, settings);
        return FliteDefaultPipeline.Speak(text, settings, cg.SampleRate, options.MaxSamples, spoken => cg.RenderText(spoken, options, cancellationToken));
    }

    /// <summary>The settings in the units of the voice: a pitch or a range that is the one of the recording is not set at all (as <c>flite</c> leaves it), so the sound is the plain sound of the voice.</summary>
    private static FliteCgOptions OptionsOf(FliteCgVoiceData data, FliteSettings settings)
    {
        float mean = (float)settings.F0Mean, stddev = (float)settings.F0Stddev;
        return new FliteCgOptions
        {
            DurationStretch = (float)settings.DurationStretch,
            F0Mean = mean == data.F0Mean ? null : mean,
            F0Stddev = stddev == data.F0Stddev ? null : stddev,
            MaxSamples = settings.MaxSamples > 0 ? (int)Math.Min(settings.MaxSamples, int.MaxValue) : FliteCgOptions.DefaultMaxSamples,
            UserLexicon = settings.User,
            Markup = settings.Markup
        };
    }
}
