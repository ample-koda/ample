using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Language;
using Ample.Speech;

namespace Ample.Audio.Engine;

/// <summary>Renders the local speech block in ordinary text or Notes-driven word mode.</summary>
internal sealed class SpeechRenderer : IBlockRenderer, IConfigurableRenderer, IBlockDurationPlanner
{
    private const int WordsMixVoiceHeadroom = 4;
    private const int WordsMaximumVoices = 4;
    public string BlockType => "speech";

    public string Id => "builtin.speech";

    public string Version => "1.5";

    public string Determinism => "machine-exact";

    public string SettingsKey => "speech";

    private static readonly string[] ToolprintEngines = ["retro/formant-2", HumanicoSpeechEngine.HumanicoIdentity];

    public string ToolprintJson => JsonSerializer.Serialize(new
    {
        engines = ToolprintEngines,
        algorithm = "splitmix64-formant-glottal-linear-resample",
        algorithmVersion = 6,
        humanicoCalibrationSha256 = HumanicoCalibratedSegments.CalibrationSha256,
        humanicoPcmScale = HumanicoCalibratedVoice.PcmScale,
        seedAlgorithm = SpeechSeed.AlgorithmIdentity,
        wordsMixVoiceHeadroom = 4,
        gateFadeMilliseconds = 8,
        internalSampleRate = 10_000,
        dictionarySha256 = HumanicoReferenceManifest.PinnedFixture.DictionarySha256,
        upstreamRevision = HumanicoReferenceManifest.PinnedFixture.UpstreamRevision,
        outputSampleRate = "request",
        floatingPoint = true
    });

    /// <summary>
    /// An unplaced text-mode block lasts as long as its speech: the same plan that the render uses (so the two cannot differ), counted in samples; the build
    /// rounds only the bar count up. Words mode has no length of its own yet (the Notes decide), and a block that cannot be planned says nothing: the render reports it.
    /// </summary>
    public long? PlannedSamples(BlockNode block, TemplateSpec blockTemplate, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(block);
        string Written(string name) => Decode(block.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? string.Empty);
        double Number(string name, double fallback) => double.TryParse(Written(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;
        if (Written("mode").Equals("words", StringComparison.OrdinalIgnoreCase)) return null;
        string text = Written("text"), phonemes = Written("phonemes");
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(phonemes)) return null;
        string engine = Written("engine").ToLowerInvariant() is { Length: > 0 } named ? named : "humanico";
        try
        {
            SpeechRequest request = new(text, sampleRate, Number("rate", 1), Number("pitch_hz", 105))
            {
                Engine = engine,
                Phonemes = phonemes.Length == 0 ? null : phonemes
            };
            SpeechPlan plan = SpeechPlanner.Plan(request);
            return plan.OutputSamples > 0 ? plan.OutputSamples : null;
        }
        catch (ArgumentException)
        {
            return null;   // invalid phonemes, an unknown engine: the render says so
        }
    }

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        string Raw(string name) => request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value
            ?? request.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        string Text(string name) => Decode(Raw(name));
        double Number(string name, double fallback)
        {
            string value = Decode(Raw(name));
            return value.Length == 0 ? fallback : double.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number)
                ? number
                : throw new AmpleException("AMP5351", $"The speech parameter '{name}' must be a finite number.");
        }
        int maxSamples = ParseLimit(request.Settings, "max_output_samples");
        int maxWorkingSamples = ParseLimit(request.Settings, "max_working_samples");
        int maxInputCharacters = ParseLimit(request.Settings, "max_input_characters");
        int maxNoteEvents = ParseLimit(request.Settings, "max_note_events");
        // text and phonemes come only from the lines of the block: the template default of text is a sample sentence for a new block in the Studio,
        // it must not fill in a text that the block left out (phonemes only) or hide the error of a block without either
        string Written(string name) => Decode(request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? string.Empty);
        string inputText = Written("text");
        string phonemes = Written("phonemes");
        if (maxInputCharacters > 0 && (inputText.Length > maxInputCharacters || phonemes.Length > maxInputCharacters))
            throw new SpeechResourceLimitException($"Speech input exceeds the configured limit of {maxInputCharacters} characters.");
        int length = checked(request.BarCount * request.SamplesPerBar);
        string mode = Text("mode").ToLowerInvariant() is { Length: > 0 } selected ? selected : "text";
        cancellationToken.ThrowIfCancellationRequested();
        if (maxSamples > 0 && length > maxSamples)
            throw new SpeechResourceLimitException($"Speech output requires {length} samples, above the configured limit of {maxSamples}.");
        long outputWorkingEquivalent = checked(((long)length * 10_000 + request.SampleRate - 1) / request.SampleRate);
        if (mode == "words") outputWorkingEquivalent = checked(outputWorkingEquivalent * 2);
        if (maxWorkingSamples > 0 && outputWorkingEquivalent > maxWorkingSamples)
            throw new SpeechResourceLimitException($"Speech output buffer needs {outputWorkingEquivalent} working samples, above the configured limit of {maxWorkingSamples}.");
        short[] output = new short[length];
        double rate = Number("rate", 1), pitch = Number("pitch_hz", 105);
        double gainDb = Number("gain", 0);
        if (gainDb is < -60 or > 12) throw new AmpleException("AMP5351", "The speech gain must be between -60 and 12 dB.");
        double fitMaxRate = Number("fit_max_rate", 2);
        string engine = Text("engine").ToLowerInvariant() is { Length: > 0 } selectedEngine ? selectedEngine : "humanico";
        if (engine is not ("retro" or "humanico")) throw new AmpleException("AMP5351", "The speech engine must be 'retro' or 'humanico'.");
        // plan 3.x: the text must be given on purpose. An explicit empty text (or one with no words in it) is silence; leaving it out is a mistake
        // owner decision 2026-11-19: text and phonemes are given on purpose and are never empty (leave the line out instead); never both
        var textParameter = request.Block.Parameters.FirstOrDefault(p => p.Name == "text");
        var phonemesParameter = request.Block.Parameters.FirstOrDefault(p => p.Name == "phonemes");
        bool hasText = textParameter is not null, hasPhonemes = phonemesParameter is not null;
        if (hasText && string.IsNullOrWhiteSpace(Decode(textParameter!.Value)))
            throw new AmpleException("AMP5351", "The speech text must not be empty: write the words to speak, or remove the text line.");
        if (hasPhonemes && string.IsNullOrWhiteSpace(Decode(phonemesParameter!.Value)))
            throw new AmpleException("AMP5351", "The speech phonemes must not be empty: write the phonemes to speak, or remove the phonemes line.");
        if (mode == "words" && !hasText) throw new AmpleException("AMP5351", "Words mode needs a text parameter with the words to speak.");
        if (mode == "text" && !hasText && !hasPhonemes) throw new AmpleException("AMP5351", "Give text or phonemes to speak.");
        if (hasText && hasPhonemes) throw new AmpleException("AMP5351", "Provide either text or phonemes, not both.");
        if (mode == "words" && hasPhonemes) throw new AmpleException("AMP5351", "phonemes is only valid in text mode.");
        int originBar = request.ActiveBars.Count == 0 ? request.FirstBar : request.ActiveBars.Min();
        if (mode is not ("text" or "words")) throw new AmpleException("AMP5351", "The speech mode must be 'text' or 'words'.");
        string durationPolicy = Text("duration_policy").ToLowerInvariant() is { Length: > 0 } policy ? policy : "natural";
        if (durationPolicy is not ("natural" or "gate" or "fit")) throw new AmpleException("AMP5351", "The speech duration_policy must be 'natural', 'gate' or 'fit'.");
        if (mode == "text" && durationPolicy != "natural") throw new AmpleException("AMP5351", "duration_policy is only valid in words mode.");
        if (mode == "words" && durationPolicy == "fit" && (fitMaxRate < rate || fitMaxRate > 2))
            throw new AmpleException("AMP5351", "fit_max_rate must be at least rate and no greater than 2.");
        SpeechResult speech = mode == "text" && request.ActiveBars.Count > 0
            ? HumanicoSpeechEngine.Render(new SpeechRequest(inputText, request.SampleRate, rate, pitch, request.Seed, maxSamples, maxWorkingSamples)
            {
                Engine = engine,
                Phonemes = phonemes.Length == 0 ? null : phonemes,
                Progress = progress => request.Report?.Invoke(new ProgressReport((double)progress.Completed / Math.Max(1, progress.Total), progress.Stage, (double)progress.Completed / Math.Max(1, progress.Total)))
            }, cancellationToken)
            : new([], request.SampleRate, [], engine == "humanico" ? HumanicoSpeechEngine.HumanicoIdentity : "retro/formant-2");
        SpeechWordsRender musical = mode == "words" && request.ActiveBars.Count > 0
            ? RenderWords(request, output, inputText, rate, pitch, fitMaxRate, durationPolicy, engine,
                maxWorkingSamples, maxNoteEvents, cancellationToken)
            : new([], []);
        IReadOnlyList<SpeechWordTiming> musicalTimings = musical.Timings;
        if (mode != "words") CopyText(request, output, speech, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        long gainClipped = ApplyGain(output, gainDb, cancellationToken);
        // saturation is never the normal strategy and never silent: it is reported with the take (kind and the number of samples that were cut)
        List<SpeechWarning> reported = [.. musical.Warnings];
        if (musical.MixClippedSamples > 0) reported.Add(new("mix-clipped", -1) { Samples = musical.MixClippedSamples });
        if (gainClipped > 0) reported.Add(new("gain-clipped", -1) { Samples = gainClipped });
        // where each word stands in the (decoded) text of this take, so that an editor can mark it; none for phonemes (they are not words of a text)
        IReadOnlyList<SpeechWordSpan>? spans = phonemes.Length == 0 ? SpeechText.WordSpans(inputText) : null;
        List<SpeechTimingMetadata> wordMetadata = [];
        foreach (SpeechWordTiming word in speech.Words.Concat(musicalTimings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpeechWordSpan? span = spans is not null && word.WordIndex >= 0 && word.WordIndex < spans.Count ? spans[word.WordIndex] : null;
            wordMetadata.Add(new(word.WordIndex, word.StartSample, word.EndSample, word.SourceWord, span?.From, span?.To));
        }
        string metadata = JsonSerializer.Serialize(new
        {
            speech = new
            {
                mode,
                engine = mode == "words" ? (engine == "humanico" ? HumanicoSpeechEngine.HumanicoIdentity : "retro/formant-2") : speech.EngineIdentity,
                provisional = engine == "humanico",
                algorithm = engine == "humanico" ? "candidate-calibrated-klatt-linear-resample" : "splitmix64-formant-glottal-linear-resample",
                calibrationSha256 = engine == "humanico" ? HumanicoCalibratedSegments.CalibrationSha256 : null,
                pcmScale = engine == "humanico" ? HumanicoCalibratedVoice.PcmScale : (double?)null,
                internalSampleRate = 10_000,
                dictionarySha256 = HumanicoReferenceManifest.PinnedFixture.DictionarySha256,
                upstreamRevision = HumanicoReferenceManifest.PinnedFixture.UpstreamRevision,
                sampleRate = speech.SampleRate,
                renderer = Id,
                rendererVersion = Version,
                placementStartBar = originBar,
                rangeFirstBar = request.FirstBar,
                words = wordMetadata,
                warnings = reported
            }
        });
        cancellationToken.ThrowIfCancellationRequested();
        byte[] wav = Wav.Encode(output, request.SampleRate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", wav) { MetadataJson = metadata }]);
    }

    private static void CopyText(RenderRequest request, short[] output, SpeechResult speech, CancellationToken token)
    {
        int originBar = request.ActiveBars.Count == 0 ? request.FirstBar : request.ActiveBars.Min();
        long speechStart = checked((long)(request.FirstBar - originBar) * request.SamplesPerBar);
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            int absoluteBar = request.FirstBar + i / request.SamplesPerBar;
            long source = speechStart + i;
            if (request.ActiveBars.Contains(absoluteBar) && source >= 0 && source < speech.Samples.Length) output[i] = speech.Samples[(int)source];
        }
    }

    private static long ApplyGain(short[] samples, double gainDb, CancellationToken token)
    {
        if (gainDb == 0) return 0;
        long clipped = 0;
        double factor = Math.Pow(10, gainDb / 20);
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            double scaled = Math.Round(samples[i] * factor, MidpointRounding.ToEven);
            if (scaled is > short.MaxValue or < short.MinValue) clipped++;
            samples[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }

        return clipped;
    }

    private static SpeechWordsRender RenderWords(RenderRequest request, short[] output, string text, double rate, double pitch,
        double fitMaxRate, string policy, string engine, int maxWorkingSamples, int maxNoteEvents, CancellationToken token)
    {
        int[] mix = new int[output.Length];
        string[] words = [.. SpeechText.Words(text)];
        if (words.Length == 0) return new([], []);   // an explicit empty text, or one without words (only punctuation): silence, with or without Notes
        List<(SpeechNoteEvent Note, long Start, long Gate)> events = [];
        long ordinal = 0;
        int lastBar = checked(request.FirstBar + request.BarCount - 1);
        for (int bar = 1; bar <= lastBar; bar++)
        {
            token.ThrowIfCancellationRequested();
            double planningProgress = (double)(bar - request.FirstBar + 1) / Math.Max(1, request.BarCount);
            request.Report?.Invoke(new ProgressReport(planningProgress, "words-planning", planningProgress));
            token.ThrowIfCancellationRequested();
            foreach ((int midi, double beat, double beats, int velocity) in BuiltInRenderers.NoteEventsOf(request, bar))
            {
                token.ThrowIfCancellationRequested();
                if (maxNoteEvents > 0 && events.Count >= maxNoteEvents)
                    throw new SpeechResourceLimitException($"Speech note events exceed the configured limit of {maxNoteEvents}.");
                long start = checked((long)(bar - request.FirstBar) * request.SamplesPerBar + (long)Math.Round(beat / request.BeatsPerBar * request.SamplesPerBar));
                long gate = Math.Max(1, (long)Math.Round(beats / request.BeatsPerBar * request.SamplesPerBar));
                long tick = checked((bar * 4L * 1_000_000) + (long)Math.Round(beat * 1_000_000));
                events.Add((new SpeechNoteEvent(tick, midi, velocity, 0, ordinal++), start, gate));
            }
        }
        if (events.Count == 0) throw new AmpleException("AMP5351", "Words mode needs at least one positive Notes event.");
        int referenceMidi = (int)Math.Round(ReadNumber(request, "reference_midi", 60));
        double referenceHz = ReadNumber(request, "reference_hz", pitch);
        IReadOnlyList<SpeechWordNote> assignments = [.. HumanicoWordNotePlanner.Assign(words, events.Select(e => e.Note))];
        if (maxWorkingSamples > 0)
        {
            long plannedWorkingSamples = 0;
            foreach (SpeechWordNote assignment in assignments)
            {
                token.ThrowIfCancellationRequested();
                SpeechNoteEvent note = assignment.Note;
                SpeechNotePitch mapped = HumanicoNotePitchMapper.Map(note, referenceMidi, referenceHz);
                // a word may use only what is left of the cumulative budget, so nothing large is planned once the limit is reached
                int remainingFrames = (int)Math.Max(1, maxWorkingSamples / 50 - plannedWorkingSamples / 50);
                int frames = engine == "humanico"
                    ? HumanicoCalibratedVoice.PlanPhones(HumanicoFrontEnd.WordToPhones(assignment.Word), rate,
                        remainingFrames, token).Count
                    : HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord(assignment.Word, token), mapped.FundamentalHz, rate, token).Count;
                plannedWorkingSamples = checked(plannedWorkingSamples + (long)frames * 50);
                if (plannedWorkingSamples > maxWorkingSamples)
                    throw new SpeechResourceLimitException($"Speech words require {plannedWorkingSamples} working samples, above the configured limit of {maxWorkingSamples}.");
            }
        }
        List<SpeechWordTiming> timings = [];
        List<SpeechWarning> warnings = [];
        List<WordVoice> voices = [];
        foreach (SpeechWordNote assignment in assignments)
        {
            token.ThrowIfCancellationRequested();
            double progress = (double)assignment.WordIndex / words.Length;
            request.Report?.Invoke(new ProgressReport(progress, "words-synthesis", progress));
            token.ThrowIfCancellationRequested();
            (SpeechNoteEvent Note, long Start, long Gate) placement = events.Single(e => e.Note.Ordinal == assignment.Note.Ordinal);
            SpeechNotePitch mapped = HumanicoNotePitchMapper.Map(placement.Note, referenceMidi, referenceHz);
            IReadOnlyList<SpeechPhone> phones = HumanicoFrontEnd.WordToPhones(assignment.Word);
            int frameLimit = maxWorkingSamples > 0 ? Math.Max(1, maxWorkingSamples / 50) : 0;
            double effectiveRate = rate;
            int frameCount = engine == "humanico"
                ? HumanicoCalibratedVoice.PlanPhones(phones, rate, frameLimit, token).Count
                : SpeechPlanner.PlanWord(assignment.Word, mapped.FundamentalHz, rate, token).Count;
            int internalSamples = checked(frameCount * 50);
            if (maxWorkingSamples > 0 && internalSamples > maxWorkingSamples)
                throw new SpeechResourceLimitException($"Speech word {assignment.WordIndex + 1} requires {internalSamples} working samples, above the configured limit of {maxWorkingSamples}.");
            if (policy == "fit")
            {
                double naturalSamples = frameCount * 50d * request.SampleRate / 10_000d;
                double fitRate = Math.Min(fitMaxRate, rate * naturalSamples / placement.Gate);
                if (fitRate > rate)
                {
                    warnings.Add(new("fit-accelerated", assignment.WordIndex));
                    effectiveRate = fitRate;
                }
            }
            ulong wordSeed = SpeechSeed.ForWord(request.Seed, assignment.WordIndex);
            short[] samples;
            if (engine == "humanico")
            {
                double[] audio = HumanicoCalibratedVoice.RenderPhones(phones, mapped.FundamentalHz,
                    effectiveRate, wordSeed, frameLimit, token);
                HumanicoPcmResult pcm = HumanicoCalibratedVoice.ToPcm(audio, token);
                if (pcm.ClippedSamples > 0) throw new InvalidOperationException("Humanico voice exceeded its PCM headroom.");
                samples = pcm.Samples;
            }
            else
            {
                IReadOnlyList<SpeechFrame> frames = SpeechPlanner.PlanWord(assignment.Word, mapped.FundamentalHz, effectiveRate, token);
                samples = HumanicoFrameSynthesizer.Render(frames, wordSeed, token);
            }
            samples = Resample(samples, request.SampleRate, request.Report, token);
            long start = placement.Start;
            long limit = policy == "natural" ? samples.Length : Math.Min(samples.Length, placement.Gate);
            if (policy != "natural" && samples.Length > placement.Gate)
                warnings.Add(new("duration-cropped", assignment.WordIndex));
            int fadeSamples = policy == "natural" ? 0 : Math.Min((int)limit, (int)Math.Round(request.SampleRate * 0.008, MidpointRounding.ToEven));
            long timingStart = Math.Max(0, placement.Start), timingEnd = Math.Min(output.Length, placement.Start + limit);
            if (timingEnd > timingStart) timings.Add(new(assignment.WordIndex, checked((int)timingStart), checked((int)timingEnd), assignment.Word));
            voices.Add(new(start, limit, samples, mapped.Amplitude, fadeSamples));
        }

        // the polyphony limit is judged on what is audible: the words that were assigned, for as long as each one sounds (a natural word
        // goes on after its note-off; a note without a word, such as the fifth note of a chord with one word, is not a voice)
        ValidatePolyphony(voices);
        foreach (WordVoice voice in voices)
        {
            for (int i = 0; i < voice.Length && voice.Start + i < output.Length; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                if (voice.Start + i < 0) continue;
                int index = checked((int)(voice.Start + i));
                double fade = voice.FadeSamples > 0 && i >= voice.Length - voice.FadeSamples
                    ? (voice.Length - i - 1d) / voice.FadeSamples
                    : 1;
                int contribution = (int)Math.Round(voice.Samples[i] * voice.Amplitude * Math.Max(0, fade) / WordsMixVoiceHeadroom, MidpointRounding.ToEven);
                mix[index] = checked(mix[index] + contribution);
            }
        }

        long mixClipped = 0;
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (mix[i] is > short.MaxValue or < short.MinValue) mixClipped++;
            output[i] = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);
        }

        return new(timings, warnings, mixClipped);
    }

    /// <summary>One word that sounds: where it starts, how many samples it lasts, its samples, its note amplitude and its release fade.</summary>
    private sealed record WordVoice(long Start, long Length, short[] Samples, double Amplitude, int FadeSamples);

    private static void ValidatePolyphony(IReadOnlyList<WordVoice> voices)
    {
        List<long> activeEnds = [];
        foreach (WordVoice voice in voices.OrderBy(v => v.Start))
        {
            activeEnds.RemoveAll(end => end <= voice.Start);
            if (activeEnds.Count >= WordsMaximumVoices)
                throw new AmpleException("AMP5351", $"Words mode supports at most {WordsMaximumVoices} overlapping Notes voices.");
            activeEnds.Add(checked(voice.Start + voice.Length));
        }
    }

    private static short[] Resample(short[] source, int outputRate, Action<ProgressReport>? report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int length = checked((int)Math.Round(source.Length * outputRate / 10_000d));
        short[] output = new short[length];
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 255) == 0)
            {
                token.ThrowIfCancellationRequested();
                double fraction = (double)i / Math.Max(1, output.Length);
                report?.Invoke(new ProgressReport(fraction, "speech-resampling", fraction));
                token.ThrowIfCancellationRequested();
            }
            double position = i * 10_000d / outputRate;
            int left = Math.Min((int)position, source.Length - 1);
            int right = Math.Min(left + 1, source.Length - 1);
            output[i] = (short)Math.Round(source[left] + (source[right] - source[left]) * (position - left), MidpointRounding.ToEven);
        }
        return output;
    }

    private static double ReadNumber(RenderRequest request, string name, double fallback)
    {
        string value = request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? string.Empty;
        if (value.Length == 0) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
            throw new AmpleException("AMP5351", $"The speech parameter '{name}' must be a finite number.");
        return parsed;
    }

    private static string Decode(string value)
    {
        string text = value.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? text[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
            : text;
    }

    private static int ParseLimit(IReadOnlyDictionary<string, string>? settings, string name)
    {
        string? text = settings?.GetValueOrDefault(name)
            ?? settings?.GetValueOrDefault($"speech.{name}");
        return string.IsNullOrWhiteSpace(text) ? 0 : int.TryParse(Decode(text), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            && value > 0
            ? value
            : throw new AmpleException("AMP5351", $"The speech setting '{name}' must be a positive whole number.");
    }

    private sealed record SpeechTimingMetadata(int Index, int StartSample, int EndSample, string Text, int? From = null, int? To = null);
    private sealed record SpeechWarning(string Kind, int WordIndex)
    {
        /// <summary>For a clipping warning: how many samples were cut; 0 for the others.</summary>
        public long Samples { get; init; }
    }

    private sealed record SpeechWordsRender(IReadOnlyList<SpeechWordTiming> Timings, IReadOnlyList<SpeechWarning> Warnings, long MixClippedSamples = 0);
}
