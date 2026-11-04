using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The note pipelines through the server: the notes a clip plays, the bake, the detach and the write-back into an editable piece.</summary>
public sealed class NotePipelineRoutesTests : IAsyncLifetime, IDisposable
{
    private const string Song = "composition \"demo\" { ample 1  tempo 100  master-seed 1 }\n\n" +
        "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100 1:2:1:64:100\"\n}\n\n" +
        "midi-clip b at 2 for auto {\n  notes a.notes\n    |> transpose(+2)\n    |> concat(\n         editable {\n           notes 1:0:1:70:100\n         }\n       )\n}\n";

    private string folder = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-notes-" + Guid.NewGuid().ToString("N"));
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "demo", null, CancellationToken.None))
        {
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), Song);
        server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library"));
        http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
    }

    public void Dispose() => http?.Dispose();

    public async Task DisposeAsync()
    {
        Dispose();
        await server.DisposeAsync();
        try
        {
            Directory.Delete(folder, true);
            Directory.Delete(folder + "-library", true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<JsonElement> Post(string url, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using HttpResponseMessage r = await http.PostAsJsonAsync(url, body, CancellationToken.None);
        Assert.Equal(expected, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
    }

    [Fact]
    public async Task A_clip_with_a_pipeline_plays_the_notes_it_makes()
    {
        JsonElement notes = await Post("/api/midi/clip-notes", new { path = "song.amplet", block = "b" });
        Assert.Equal(4, notes.GetProperty("count").GetInt32());
        string text = notes.GetProperty("text").GetString()!;
        Assert.Contains("1:0:1:62:100", text, StringComparison.Ordinal);
        Assert.Contains("2:0:1:70:100", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bake_returns_the_new_text_and_writes_nothing()
    {
        JsonElement result = await Post("/api/notes/bake", new { path = "song.amplet", block = "b", stage = "mid" });
        string text = result.GetProperty("text").GetString()!;
        Assert.Contains("bake mid {", text, StringComparison.Ordinal);
        Assert.Contains("notes-hash", text, StringComparison.Ordinal);
        Assert.Equal(4, result.GetProperty("notes").GetInt32());
        Assert.Equal(Song, await File.ReadAllTextAsync(Path.Combine(folder, "song.amplet")));
    }

    [Fact]
    public async Task Detach_returns_plain_notes()
    {
        JsonElement result = await Post("/api/notes/bake", new { path = "song.amplet", block = "b", detach = true });
        string text = result.GetProperty("text").GetString()!;
        Assert.DoesNotContain("|>", text, StringComparison.Ordinal);
        Assert.Contains("1:0:1:62:100", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_editable_piece_is_rewritten_and_a_missing_piece_is_refused()
    {
        JsonElement result = await Post("/api/notes/editable", new { path = "song.amplet", block = "b", index = 0, notes = "\"1:0:1:80:100 1:1:1:82:100\"" });
        string text = result.GetProperty("text").GetString()!;
        Assert.Contains("1:1:1:82:100", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1:0:1:70:100", text, StringComparison.Ordinal);
        await Post("/api/notes/editable", new { path = "song.amplet", block = "b", index = 3, notes = "1:0:1:60:100" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bake_of_a_clip_that_is_not_a_pipeline_is_refused()
    {
        using HttpResponseMessage r = await http.PostAsJsonAsync("/api/notes/bake", new { path = "song.amplet", block = "a" }, CancellationToken.None);
        Assert.False(r.IsSuccessStatusCode);
        Assert.Contains("AMP5702", await r.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Random_seeds_are_picked_once_and_written_as_numbers()
    {
        const string song = "composition \"demo\" { ample 1  tempo 100  master-seed 1 }\n\nmidi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes\n    |> humanize(timing=10ms, seed=random-seed())\n}\n";
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), song);
        JsonElement result = await Post("/api/notes/random-seed", new { path = "song.amplet" });
        Assert.Equal(1, result.GetProperty("count").GetInt32());
        string text = result.GetProperty("text").GetString()!;
        Assert.DoesNotContain("random-seed()", text, StringComparison.Ordinal);
        Assert.Matches(@"seed=\d{6,}\)", text);
    }

    [Fact]
    public async Task A_copy_with_a_mode_comes_back_as_text_and_a_wrong_mode_is_refused()
    {
        const string song = "composition \"demo\" { ample 1  tempo 100  master-seed 1 }\n\nmidi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes\n    |> humanize(timing=10ms, seed=stable(\"verse\"))\n}\n";
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), song);
        JsonElement fork = await Post("/api/notes/duplicate", new { path = "song.amplet", block = "b", name = "b-2", mode = "fork" });
        Assert.Contains("stable(\"verse\").fork(\"copy-b-2\")", fork.GetProperty("text").GetString(), StringComparison.Ordinal);
        JsonElement independent = await Post("/api/notes/duplicate", new { path = "song.amplet", block = "b", name = "b-2", mode = "independent" });
        Assert.Contains("stable(\"verse_2\")", independent.GetProperty("text").GetString(), StringComparison.Ordinal);
        await Post("/api/notes/duplicate", new { path = "song.amplet", block = "b", name = "b-2", mode = "nonsense" }, HttpStatusCode.BadRequest);
        await Post("/api/notes/duplicate", new { path = "song.amplet", block = "b", name = "a", mode = "reuse" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_provenance_lists_the_seeds_of_a_clip_and_the_names_of_the_song()
    {
        const string song = "composition \"demo\" { ample 1  tempo 100  master-seed 1 }\n\nmidi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip b at 2 for auto {\n  random-stream d stable(\"verse\").fork(\"d\")\n  notes a.notes\n    |> humanize(timing=10ms, seed=stable(\"verse\"))\n    |> shift(d.next(0ms..=10ms))\n}\n";
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), song);
        JsonElement result = await Post("/api/notes/provenance", new { path = "song.amplet", block = "b" });
        Assert.Contains(result.GetProperty("seeds").EnumerateArray(), x => x.GetProperty("operator").GetString() == "humanize" && x.GetProperty("draws").GetInt64() == 2);
        Assert.Equal(1, result.GetProperty("streams")[0].GetProperty("consumed").GetInt32());
        Assert.Equal("verse", result.GetProperty("names")[0].GetProperty("name").GetString());
        Assert.Equal(2, result.GetProperty("names")[0].GetProperty("count").GetInt32());
    }

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }
}
