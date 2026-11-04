namespace Ample.Cli.Tests;

public sealed class PackCommandTests
{
    [Fact]
    public void Where_finds_the_user_level_general_midi_pack()
    {
        string home = Path.Combine(Path.GetTempPath(), "ample-packs-home-" + Guid.NewGuid().ToString("N"));
        string expected = Path.Combine(home, "audio-packs", "licensed", "musescore-general", "MuseScore_General.sf2");
        using StringWriter output = new();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            File.WriteAllBytes(expected, [1]);

            int result = CliApplication.Run(["packs", "where", "--home", home], output);

            Assert.Equal(0, result);
            Assert.Equal(expected + Environment.NewLine, output.ToString());
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, true);
        }
    }
}
