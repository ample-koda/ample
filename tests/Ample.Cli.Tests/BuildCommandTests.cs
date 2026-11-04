using Ample.Audio.Engine;

namespace Ample.Cli.Tests;

public sealed class BuildCommandTests : IDisposable
{
    private const string Song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 2 {\n  chords Am F\n}\nbass bass at 1 for 2 {\n  from chords.notes -> notes\n}\nmixer mix {\n  from bass.audio -> audio1\n}\nmaster master {\n  from mix.audio -> audio\n}\n";
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-build-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Build_play_and_bake_work_end_to_end_and_the_second_build_comes_from_the_cache()
    {
        string project = Path.Combine(folder, "p");
        Assert.Equal(0, Run("new", project).Code);
        string song = Path.Combine(project, "song.amplet");
        await File.WriteAllTextAsync(song, Song);

        (int code, string text) = Run("build", song);
        Assert.Equal(0, code);
        Assert.Contains("rendered  master", text, StringComparison.Ordinal);
        string wav = Path.Combine(project, "out", "song.wav");
        Assert.True(File.Exists(wav));
        (short[] samples, int rate) = Wav.Decode(await File.ReadAllBytesAsync(wav));
        Assert.Equal(44100, rate);
        Assert.Equal(2 * 88200, samples.Length);

        (code, text) = Run("build", song);
        Assert.Contains("4 from cache, 0 rendered", text, StringComparison.Ordinal);

        (code, text) = Run("bake", song, "bass");
        Assert.Equal(0, code);
        Assert.Contains("Baked 'bass'", text, StringComparison.Ordinal);
        Assert.Contains("bass bass at 1 for 2 bake {", await File.ReadAllTextAsync(song), StringComparison.Ordinal);
        Assert.Contains("4 from cache, 0 rendered", Run("build", song).Output, StringComparison.Ordinal);

        (code, text) = Run("play", song, "--out", Path.Combine(folder, "preview.wav"));
        Assert.Equal(0, code);
        (short[] previewSamples, int previewRate) = Wav.Decode(await File.ReadAllBytesAsync(Path.Combine(folder, "preview.wav")));
        Assert.Equal(Ample.Engine.Engine.BuildEngine.PreviewRate, previewRate);
        Assert.Equal(2 * 2 * Ample.Engine.Engine.BuildEngine.PreviewRate, previewSamples.Length);   // two bars of two seconds
    }

    [Fact]
    public async Task A_song_with_errors_is_not_built()
    {
        string project = Path.Combine(folder, "q");
        Assert.Equal(0, Run("new", project).Code);
        string song = Path.Combine(project, "bad.amplet");
        await File.WriteAllTextAsync(song, "composition \"x\" { ample 1  tempo 120  master-seed 1 }\ndrumz d {\n}\n");
        (int code, string text) = Run("build", song);
        Assert.Equal(1, code);
        Assert.Contains("AMP2001", text, StringComparison.Ordinal);
    }
}
