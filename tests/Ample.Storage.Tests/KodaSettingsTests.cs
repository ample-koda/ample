using Ample.Storage.Projects;

namespace Ample.Core.Tests;

public sealed class KodaSettingsTests
{
    [Fact]
    public void Koda_speaks_English_by_default_and_the_default_adds_nothing_to_project_json()
    {
        ProjectManifest manifest = ProjectFile.CreateNew(Guid.NewGuid(), "x", DateTimeOffset.UnixEpoch, null);
        Assert.Equal("en", manifest.Settings.Koda.Language);
        Assert.DoesNotContain("koda", ProjectFile.Serialize(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void The_language_of_Koda_survives_a_round_trip_and_an_unknown_one_falls_back_to_English()
    {
        ProjectManifest manifest = ProjectFile.CreateNew(Guid.NewGuid(), "x", DateTimeOffset.UnixEpoch, null);
        ProjectManifest dutch = manifest with { Settings = manifest.Settings with { Koda = new KodaSettings("nl") } };
        string json = ProjectFile.Serialize(dutch);
        Assert.Contains("\"koda\"", json, StringComparison.Ordinal);
        Assert.Equal("nl", ProjectFile.Parse(json).Settings.Koda.Language);
        Assert.Equal("en", ProjectFile.Parse(json.Replace("\"nl\"", "\"fr\"", StringComparison.Ordinal)).Settings.Koda.Language);
        Assert.True(KodaSettings.IsKnown("nl"));
        Assert.False(KodaSettings.IsKnown("fr"));
    }
}
