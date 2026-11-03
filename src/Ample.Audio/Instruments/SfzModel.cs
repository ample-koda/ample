namespace Ample.Audio.Instruments;

/// <summary>One region of an SFZ instrument after inheritance. Integers only: no floating point reaches the audio path.</summary>
internal sealed class SfzRegion
{
    /// <summary>Sample path with default_path applied and separators normalised to '/'.</summary>
    public string Sample { get; set; } = "";
    /// <summary>Lowest key (0..127).</summary>
    public int LoKey { get; set; }
    /// <summary>Highest key (0..127).</summary>
    public int HiKey { get; set; } = 127;
    /// <summary>Root key (default 60).</summary>
    public int PitchKeycenter { get; set; } = 60;
    /// <summary>Lowest velocity.</summary>
    public int LoVel { get; set; }
    /// <summary>Highest velocity.</summary>
    public int HiVel { get; set; } = 127;
    /// <summary>Loop mode: empty (unset), no_loop, one_shot, loop_continuous or loop_sustain.</summary>
    public string LoopMode { get; set; } = "";
    /// <summary>Loop start in frames, -1 when unset.</summary>
    public long LoopStart { get; set; } = -1;
    /// <summary>Loop end in frames, -1 when unset.</summary>
    public long LoopEnd { get; set; } = -1;
    /// <summary>Round robin length.</summary>
    public int SeqLength { get; set; } = 1;
    /// <summary>Round robin position (1-based).</summary>
    public int SeqPosition { get; set; } = 1;
    /// <summary>Lower random bound in millionths.</summary>
    public long LoRand { get; set; }
    /// <summary>Upper random bound in millionths.</summary>
    public long HiRand { get; set; } = 1_000_000;
    /// <summary>Start offset in frames.</summary>
    public long Offset { get; set; }
    /// <summary>End frame, -1 when unset.</summary>
    public long End { get; set; } = -1;
    /// <summary>Fine tune in cents.</summary>
    public int TuneCents { get; set; }
    /// <summary>Transpose in semitones.</summary>
    public int Transpose { get; set; }
    /// <summary>Volume in hundredths of a dB.</summary>
    public long VolumeCentiDb { get; set; }
    /// <summary>Envelope delay in microseconds.</summary>
    public long AmpegDelayUs { get; set; }
    /// <summary>Envelope attack in microseconds.</summary>
    public long AmpegAttackUs { get; set; }
    /// <summary>Envelope hold in microseconds.</summary>
    public long AmpegHoldUs { get; set; }
    /// <summary>Envelope decay in microseconds.</summary>
    public long AmpegDecayUs { get; set; }
    /// <summary>Envelope sustain in thousandths of a percent (100000 = 100%).</summary>
    public long AmpegSustainMilliPercent { get; set; } = 100_000;
    /// <summary>Envelope release in microseconds.</summary>
    public long AmpegReleaseUs { get; set; }
    /// <summary>Unsupported opcodes kept verbatim (lower-case name to value).</summary>
    public SortedDictionary<string, string> Unknown { get; } = new(StringComparer.Ordinal);
}

/// <summary>A reader diagnostic. Holds only an opcode or header name, never file content.</summary>
internal sealed record SfzWarning(string Code, string Message);

/// <summary>Result of reading an SFZ file.</summary>
internal sealed class SfzInstrument
{
    /// <summary>The regions in file order.</summary>
    public List<SfzRegion> Regions { get; } = new();
    /// <summary>Warnings, one per distinct problem.</summary>
    public List<SfzWarning> Warnings { get; } = new();
}

/// <summary>Raised for a missing, unreadable or unusable SFZ file (diagnostic AMP5301).</summary>
internal sealed class SfzFormatException(string message) : Exception(message);
