namespace Ample.Golden;

/// <summary>Provides the shared Verify entry point for AMPLE golden-file tests.</summary>
public static class GoldenFile
{
    /// <summary>Verifies text against a committed golden file.</summary>
    public static async Task VerifyAsync(string text)
    {
        await Verifier.Verify(text);
    }
}
