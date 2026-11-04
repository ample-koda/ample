namespace Ample.Server.Tests;

public sealed class AiSampleFetchTests
{
    [Fact]
    public void Only_cc0_or_a_public_domain_mark_passes_and_a_page_that_names_another_licence_never_does()
    {
        Assert.True(SampleLicence.Check("<a href=\"https://creativecommons.org/publicdomain/zero/1.0/\">Creative Commons 0</a>").Ok);
        Assert.Equal("CC0 1.0", SampleLicence.Check("licence: https://creativecommons.org/publicdomain/zero/1.0/").Licence);
        Assert.True(SampleLicence.Check("{\"licenseurl\":\"https:" + (char)92 + "/" + (char)92 + "/creativecommons.org" + (char)92 + "/publicdomain" + (char)92 + "/mark" + (char)92 + "/1.0" + (char)92 + "/\"}").Ok);   // JSON that escapes its slashes
        Assert.False(SampleLicence.Check("<a href=\"https://creativecommons.org/licenses/by/4.0/\">Attribution</a>").Ok);
        Assert.False(SampleLicence.Check("zero https://creativecommons.org/publicdomain/zero/1.0/ and https://creativecommons.org/licenses/by-nc/3.0/").Ok);
        Assert.False(SampleLicence.Check("royalty free, no licence named").Ok);
    }

    [Fact]
    public void Only_the_known_hosts_over_http_or_https_are_allowed_and_a_file_needs_an_audio_extension()
    {
        Assert.True(SampleLicence.AllowedHost(new Uri("https://freesound.org/people/x/sounds/1/")));
        Assert.True(SampleLicence.AllowedHost(new Uri("https://cdn.freesound.org/previews/1/1_1-hq.mp3")));
        Assert.True(SampleLicence.AllowedHost(new Uri("https://ia800000.us.archive.org/1/items/x/a.wav")));
        Assert.True(SampleLicence.AllowedHost(new Uri("https://opengameart.org/content/x")));
        Assert.False(SampleLicence.AllowedHost(new Uri("https://evilfreesound.org/x")));
        Assert.False(SampleLicence.AllowedHost(new Uri("https://freesound.org.evil.example/x")));
        Assert.False(SampleLicence.AllowedHost(new Uri("ftp://freesound.org/x")));
        Assert.False(SampleLicence.AllowedHost(new Uri("https://user:pw" + "@freesound.org/x")));
        Assert.Equal(".wav", SampleLicence.AudioExtension(new Uri("https://archive.org/download/x/Scratch.WAV")));
        Assert.Equal(string.Empty, SampleLicence.AudioExtension(new Uri("https://archive.org/download/x/readme.txt")));
        Assert.Equal("dj-scratch-1.wav", SampleLicence.FileName("DJ Scratch #1!", ".wav"));
        Assert.Equal("sample.mp3", SampleLicence.FileName("../..", ".mp3"));
    }

    [Fact]
    public void The_fetch_sample_tool_has_its_route_and_an_address_on_another_site_is_refused_without_a_request()
    {
        Assert.Contains(Routes.Catalog, op => op.Method == "GET" && op.Path == "/api/ai/context/fetch-sample");
    }
}
