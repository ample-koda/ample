using System.Text.RegularExpressions;
using Ample.Language;
using Ample.Server.Lite;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The starter song of a new song: Studio's (StarterSong) and Remix's (web/remix/lite-templates.mjs) are the same, and it builds and sounds with built-in synths only.</summary>
public sealed class StarterSongTests : IDisposable
{
    private static readonly string Root = FindRoot();
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-starter-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(home, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }

    [Fact]
    public void Studio_and_Remix_start_a_new_song_with_the_same_text()
    {
        string module = File.ReadAllText(Path.Combine(Root, "web", "remix", "lite-templates.mjs"));
        Match match = Regex.Match(module, @"STARTER_BODY = `(?<body>[^`]*)`;", RegexOptions.Singleline);
        Assert.True(match.Success, "the body of the starter song is a template literal in lite-templates.mjs");
        string remix = match.Groups["body"].Value.ReplaceLineEndings("\n").TrimEnd('\n');
        Assert.Equal(StarterSong.Body.ReplaceLineEndings("\n").TrimEnd('\n'), remix);
        Assert.DoesNotContain("${", match.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_song_has_a_composition_line_a_mixer_and_a_master()
    {
        string text = StarterSong.Text("Fresh", "100", "7");
        Assert.StartsWith("composition \"Fresh\" { ample 1  tempo 100  master-seed 7 }\n", text, StringComparison.Ordinal);
        Assert.Contains("\nmixer console {", text, StringComparison.Ordinal);
        Assert.Contains("\nmaster out {", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_starter_song_builds_with_the_limits_of_Lite_and_gives_about_ten_seconds_of_sound()
    {
        SpecData spec = Ample.Docs.SpecRoot.At(Root).LoadSpecData();
        List<LiteFile> files = [new LiteFile("song.amplet", System.Text.Encoding.UTF8.GetBytes(StarterSong.Text("Starter", "100", "1")))];
        LiteBuildResult result = await LiteBuilder.BuildAsync(spec, home, "song.amplet", files, LiteLimits.Default, new LiteOutputs(), CancellationToken.None);
        Assert.True(result.Ok, string.Join("; ", result.Diagnostics.Where(d => d.Severity == "error").Select(d => $"{d.Code} line {d.Line}: {d.Message}")));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == "error");
        Assert.InRange(result.AudioSeconds, 9, 11);   // four bars of four beats at 100 BPM: 9.6 s, and the tails
        Assert.True(result.Bytes > 100_000, "the sound is not silence");
    }
}
