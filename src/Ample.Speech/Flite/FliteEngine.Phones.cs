using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Prosody;
using Ample.Speech.Flite.Synth;
using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_synth.c (utt_synth_phones, synth_method_phones). Translated to C# and modified: see Text/UsText.cs.

public sealed partial class FliteEngine
{
    /// <summary>
    /// Speaks explicit phones, as <c>flite -p</c> does: the phones of the voice, separated by white space, such as <c>hh ax l ow</c>
    /// (lower case, a 1 or 0 behind a phone sets the stress of its syllable, <c>-</c> ends a syllable). There is no text analysis, no phrasing, no
    /// pauses and no intonation: the durations come from the duration model and the pitch from the F0 model with flat accents, as in C.
    /// </summary>
    /// <param name="phones">The phones.</param>
    /// <param name="options">The parameters, or null for the values of the voice.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <exception cref="FliteRenderException">The phones cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The input or the waveform is larger than the limits of <paramref name="options"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteRenderResult RenderPhones(string phones, FliteOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(phones);
        options ??= new FliteOptions();
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(phones);
        if (utf8.Length > options.MaxTextBytes)
        {
            throw new SpeechResourceLimitException($"The phones have more than {options.MaxTextBytes} bytes.");
        }

        try
        {
            return RenderPhonesCore(ByteText.FromUtf8(utf8), options, cancellation);
        }
        catch (FliteException e)
        {
            throw new FliteRenderException("The phones cannot be spoken: " + e.Message, e);
        }
    }

    private FliteRenderResult RenderPhonesCore(string byteText, FliteOptions options, CancellationToken cancellation)
    {
        Utterance u = prosody.NewUtterance();
        u.Features.SetFloat("duration_stretch", options.DurationStretch);
        u.Features.SetFloat("int_f0_target_mean", options.F0Mean);
        u.Features.SetFloat("int_f0_target_stddev", options.F0Stddev);

        // the modules of synth_method_phones (cst_synth.c) with the hooks of this voice: its f0_model_func (us_f0_model) wins over flat_prosody; no postlex
        UsEnglishFrontEnd.Tokenize(u, byteText);
        PhoneInput.TokenToSegments(u);
        frontEnd.PosTag(u, cancellation);
        cancellation.ThrowIfCancellationRequested();
        FliteProsody.Duration(u);
        cancellation.ThrowIfCancellationRequested();
        LimitCheck(u, options);
        FliteProsody.F0Model(u, FliteEnglishData.Instance.F0Terms);

        List<Item> segmentItems = [];
        List<FliteSegmentInput> segments = [];
        for (Item? s = u.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s))
        {
            segmentItems.Add(s);
            segments.Add(new FliteSegmentInput(
                Hrg.Name(s), Hrg.FeatFloat(s, "end"), FeaturePath.FfeatureString(s, "ph_vc"), FeaturePath.FfeatureString(s, "R:SylStructure.n.ph_vc")));
        }

        List<FliteTarget> targets = [];
        for (Item? t = u.GetRelation("Target").Head; t is not null; t = Hrg.Next(t))
        {
            targets.Add(new FliteTarget(Hrg.FeatFloat(t, "pos"), Hrg.FeatFloat(t, "f0")));
        }

        FliteSynthResult made = FliteSynthesizer.Synthesize(voice, segments, targets, cancellation);
        if (made.Samples.Length > options.MaxSamples)
        {
            throw new SpeechResourceLimitException($"The speech is longer than {options.MaxSamples} samples.");
        }

        return Describe(u, byteText, 0, out _, segmentItems, made);
    }
}
