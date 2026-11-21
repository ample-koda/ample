namespace Ample.Speech;

/// <summary>Sound-determining inputs for the internal Humanico port/reference harness.</summary>
public sealed record HumanicoReferenceManifest(
    string UpstreamRepository,
    string UpstreamRevision,
    string DictionaryIdentity,
    string DictionarySha256,
    int InternalSampleRate,
    int FrameMilliseconds,
    string NoiseAlgorithm)
{
    public static HumanicoReferenceManifest PinnedFixture { get; } = new(
        "https://github.com/JacobEGarcia/humanico-voice",
        "ce793bc9304cf34637199b0efde72c8afe130b1d",
        HumanicoPronunciationLexicon.SourceIdentity,
        "81917843C7F44CE2B094AC63873C2C7A4CF802040792C455BA3CA406891C3D22",
        10_000,
        5,
        SpeechSeed.AlgorithmIdentity);
}
