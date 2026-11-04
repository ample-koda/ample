namespace Ample.Server.Tests;

public sealed class AiSampleFindTests
{
    [Fact]
    public void A_freesound_search_page_gives_the_pages_of_single_sounds_without_doubles()
    {
        const string html = "<a href=\"/people/SaschaRettberg/sounds/661407/\">x</a><a href=\"/people/SaschaRettberg/sounds/661407/\">again</a><a href=\"/people/templeofhades/sounds/151822/\">y</a><a href=\"/people/templeofhades/\">user</a><a href=\"/browse/tags/scratch/\">tag</a>";
        Assert.Equal(["/people/SaschaRettberg/sounds/661407/", "/people/templeofhades/sounds/151822/"], SampleFinder.SoundPaths(html));
    }

    [Fact]
    public void The_high_quality_mp3_preview_is_taken_and_the_title_loses_the_site_name()
    {
        const string html = "<title>Freesound - Dj_Puzzle_Scratch_05.wav by SaschaRettberg</title><audio data-lq=\"https://cdn.freesound.org/previews/661/661407_14498354-lq.mp3\"><a data-ogg=\"https://cdn.freesound.org/previews/661/661407_14498354-lq.ogg\"></a><a data-hq=\"https://cdn.freesound.org/previews/661/661407_14498354-hq.mp3\"></a>https://freesound.orghttps://cdn.freesound.org/previews/661/661407_14498354-lq.mp3";
        Assert.Equal("https://cdn.freesound.org/previews/661/661407_14498354-hq.mp3", SampleFinder.PreviewUrl(html));
        Assert.Equal("Dj_Puzzle_Scratch_05.wav by SaschaRettberg", SampleFinder.PageTitle(html));
        Assert.Equal(string.Empty, SampleFinder.PreviewUrl("<p>no previews</p>"));
        Assert.True(SampleLicence.AllowedHost(new Uri(SampleFinder.PreviewUrl(html))));
    }

    [Fact]
    public void An_archive_item_gives_its_smallest_audio_file_up_to_25_mb_and_nothing_when_all_are_bigger()
    {
        const string small = "{\"files\":[{\"name\":\"cover.jpg\",\"size\":\"5000\"},{\"name\":\"big.wav\",\"size\":\"40000000\"},{\"name\":\"a scratch.mp3\",\"size\":\"900000\"},{\"name\":\"a scratch.ogg\",\"size\":\"700000\"}]}";
        Assert.Equal("https://archive.org/download/item1/a%20scratch.ogg", SampleFinder.ArchiveFile(small, "item1"));
        Assert.Equal(string.Empty, SampleFinder.ArchiveFile("{\"files\":[{\"name\":\"podcast.mp3\",\"size\":\"67817664\"}]}", "item2"));
        Assert.Equal(string.Empty, SampleFinder.ArchiveFile("not json", "item3"));
    }

    [Fact]
    public void The_find_samples_tool_has_its_route()
    {
        Assert.Contains(Routes.Catalog, op => op.Method == "GET" && op.Path == "/api/ai/context/find-samples");
    }
}
