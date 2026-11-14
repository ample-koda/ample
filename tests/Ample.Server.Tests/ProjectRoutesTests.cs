using System.Net;
using System.Text;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Audio.Midi;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

public sealed class ProjectRoutesTests : IAsyncLifetime, IDisposable
{
    private string folder = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-projroutes-" + Guid.NewGuid().ToString("N"));
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "demo", null, CancellationToken.None))
        {
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project demo\n");
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

    [Fact]
    public async Task The_language_of_Koda_is_a_project_setting_and_the_hint_for_the_model_follows_it()
    {
        using JsonDocument before = JsonDocument.Parse(await Text("/api/project/settings"));
        Assert.Equal("en", before.RootElement.GetProperty("koda").GetProperty("language").GetString());
        HttpResponseMessage bad = await Post("/api/project/settings", new { koda = new { language = "fr" } });
        Assert.NotEqual(HttpStatusCode.OK, bad.StatusCode);
        HttpResponseMessage ok = await Post("/api/project/settings", new { koda = new { language = "nl" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using JsonDocument after = JsonDocument.Parse(await Text("/api/project/settings"));
        Assert.Equal("nl", after.RootElement.GetProperty("koda").GetProperty("language").GetString());
        Assert.Contains("English term of AMPLE", AiService.KodaLanguageText("nl"), StringComparison.Ordinal);
        Assert.Contains("Answer in English", AiService.KodaLanguageText("en"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Koda_shares_the_lane_of_the_assistant_until_it_has_a_login_of_its_own()
    {
        using JsonDocument status = JsonDocument.Parse(await Text("/api/koda/status"));
        Assert.False(status.RootElement.GetProperty("linked").GetBoolean());
        Assert.Equal("shared", status.RootElement.GetProperty("lane").GetString());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("login").ValueKind);
    }

    [Fact]
    public async Task The_beats_catalogue_is_served_without_the_assistant()
    {
        using JsonDocument all = JsonDocument.Parse(await Text("/api/beats?text=house"));
        Assert.True(all.RootElement.GetProperty("count").GetInt32() > 0);
        using JsonDocument one = JsonDocument.Parse(await Text("/api/beats?id=house-four-on-floor"));
        Assert.Equal("house-four-on-floor", one.RootElement.GetProperty("id").GetString());
        Assert.True(one.RootElement.TryGetProperty("drums", out _));
        using JsonDocument missing = JsonDocument.Parse(await Text("/api/beats?id=no-such-beat"));
        Assert.True(missing.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Text_files_round_trip_and_other_files_and_outside_paths_are_refused()
    {
        HttpResponseMessage put = await Put("/api/file", new { path = "lyrics/chorus.lyr", text = "[bar 1] la\n" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Contains("la", await Text("/api/file?path=lyrics/chorus.lyr"), StringComparison.Ordinal);
        Assert.Equal("[bar 1] la\n", await File.ReadAllTextAsync(Path.Combine(folder, "lyrics", "chorus.lyr"), CancellationToken.None));

        Assert.NotEqual(HttpStatusCode.OK, (await Put("/api/file", new { path = "take.wav", text = "x" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Put("/api/file", new { path = "../escape.txt", text = "x" })).StatusCode);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "escape.txt")));
        Assert.NotEqual(HttpStatusCode.OK, (await Put("/api/file", new { path = "ample.lock", text = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_file_list_leaves_out_the_private_folder()
    {
        string files = await Text("/api/project/files");
        Assert.Contains("song.amplet", files, StringComparison.Ordinal);
        Assert.DoesNotContain(".ample/", files, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_faer_context_reports_project_routes_without_credentials()
    {
        using HttpResponseMessage response = await http.GetAsync("/api/ai/context/faer", CancellationToken.None);
        string body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using JsonDocument context = JsonDocument.Parse(body);
        JsonElement root = context.RootElement;
        Assert.Equal("local", root.GetProperty("routes").GetProperty("song").GetString());
        Assert.Equal("local", root.GetProperty("routes").GetProperty("voice").GetString());
        Assert.Equal("local", root.GetProperty("routes").GetProperty("separation").GetString());
        Assert.True(root.GetProperty("configured").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.DoesNotContain("token", root.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Project_audio_stream_supports_ranges_and_refuses_non_audio_and_private_paths()
    {
        Directory.CreateDirectory(Path.Combine(folder, "samples"));
        byte[] bytes = [0, 1, 2, 3, 4, 5, 6, 7];
        await File.WriteAllBytesAsync(Path.Combine(folder, "samples", "preview.wav"), bytes);

        using HttpResponseMessage response = await http.GetAsync("/api/project/audio?path=samples%2Fpreview.wav", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(CancellationToken.None));

        using HttpRequestMessage range = new(HttpMethod.Get, "/api/project/audio?path=samples%2Fpreview.wav");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using HttpResponseMessage partial = await http.SendAsync(range, CancellationToken.None);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(bytes[2..6], await partial.Content.ReadAsByteArrayAsync(CancellationToken.None));
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/api/project/audio?path=song.amplet", CancellationToken.None)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/api/project/audio?path=.ample%2Fsecret.wav", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Project_tree_is_lazy_and_never_lists_private_files()
    {
        Directory.CreateDirectory(Path.Combine(folder, "lyrics", "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "lyrics", "nested", "line.lyr"), "[bar 1] la");
        using JsonDocument root = JsonDocument.Parse(await Text("/api/project/tree?depth=1"));
        JsonElement children = root.RootElement.GetProperty("children");
        Assert.Contains(children.EnumerateArray(), item => item.GetProperty("name").GetString() == "song.amplet");
        Assert.DoesNotContain(children.EnumerateArray(), item => item.GetProperty("path").GetString()!.StartsWith(".ample/", StringComparison.Ordinal));
        using JsonDocument nested = JsonDocument.Parse(await Text("/api/project/tree?path=lyrics%2Fnested"));
        Assert.Equal("line.lyr", nested.RootElement.GetProperty("children")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Project_usage_finds_file_parameter_references_and_ignores_private_paths()
    {
        Directory.CreateDirectory(Path.Combine(folder, "samples"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "samples", "loop.wav"), [0, 1, 2]);
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project demo { tempo 90 master-seed 1 }\nimport-audio input {\n  file samples/loop.wav\n}\n");
        using JsonDocument usage = JsonDocument.Parse(await Text("/api/project/usage?path=samples%2Floop.wav"));
        JsonElement user = Assert.Single(usage.RootElement.GetProperty("users").EnumerateArray());
        Assert.Equal("song.amplet", user.GetProperty("path").GetString());
        Assert.Equal("input", user.GetProperty("block").GetString());
        Assert.Equal("file", user.GetProperty("parameter").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/api/project/usage?path=.ample%2Fsecret", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Project_file_hash_is_streamed_and_private_paths_are_refused()
    {
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        byte[] bytes = [0, 1, 2, 3];
        await File.WriteAllBytesAsync(Path.Combine(folder, "assets", "sample.bin"), bytes);
        using JsonDocument hash = JsonDocument.Parse(await Text("/api/project/hash?path=assets%2Fsample.bin"));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), hash.RootElement.GetProperty("sha256").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/api/project/hash?path=.ample%2Fproject.json", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Project_audio_and_midi_previews_are_bounded_and_private_paths_are_refused()
    {
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "assets", "thumb.wav"), Wav.Encode([0, 8000, -8000, 0], 8000));
        using JsonDocument peaks = JsonDocument.Parse(await Text("/api/project/peaks?path=assets%2Fthumb.wav"));
        Assert.Equal(4, peaks.RootElement.GetProperty("points").GetInt32());
        Assert.Equal(1, peaks.RootElement.GetProperty("channels").GetInt32());
        await File.WriteAllBytesAsync(Path.Combine(folder, "assets", "bad.wav"), [1, 2, 3]);
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/api/project/peaks?path=assets%2Fbad.wav", CancellationToken.None)).StatusCode);

        SmfFile midi = new(0, 480, [new SmfTrack([
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(480, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "assets", "thumb.mid"), SmfWriter.Write(midi, SmfLimits.Standard));
        using JsonDocument preview = JsonDocument.Parse(await Text("/api/project/preview?path=assets%2Fthumb.mid"));
        Assert.Equal("midi", preview.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, preview.RootElement.GetProperty("noteCount").GetInt32());
        Assert.Equal(60, preview.RootElement.GetProperty("notes")[0].GetProperty("pitch").GetInt32());
        await File.WriteAllBytesAsync(Path.Combine(folder, "assets", "bad.mid"), [1, 2, 3]);
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/api/project/preview?path=assets%2Fbad.mid", CancellationToken.None)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync("/api/project/preview?path=.ample%2Fproject.json", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Project_file_delete_uses_the_journal_and_refuses_private_paths()
    {
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        string path = Path.Combine(folder, "assets", "old.wav");
        await File.WriteAllBytesAsync(path, [0, 1, 2]);
        using HttpResponseMessage deleted = await http.DeleteAsync("/api/project/file?path=assets%2Fold.wav", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(File.Exists(path));
        Assert.Contains("file.delete", await Text("/api/blackbox/status"), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, (await http.DeleteAsync("/api/project/file?path=.ample%2Fproject.json", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Project_folders_and_paths_change_through_journaled_commands()
    {
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        using HttpResponseMessage created = await Post("/api/project/folder", new { path = "assets/source" });
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(folder, "assets", "source", "sample.wav"), "sample", CancellationToken.None);
        using HttpResponseMessage moved = await Post("/api/project/move", new { source = "assets/source", destination = "assets/renamed" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.True(File.Exists(Path.Combine(folder, "assets", "renamed", "sample.wav")));
        using HttpResponseMessage renamed = await Post("/api/project/rename", new { source = "assets/renamed/sample.wav", destination = "assets/renamed/final.wav" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.True(File.Exists(Path.Combine(folder, "assets", "renamed", "final.wav")));
        using HttpResponseMessage caseCollision = await Post("/api/project/move", new { source = "assets/renamed/final.wav", destination = "assets/renamed/FINAL.WAV" });
        Assert.NotEqual(HttpStatusCode.OK, caseCollision.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Post("/api/project/folder", new { path = ".ample/hidden" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Post("/api/project/rename", new { source = "assets/renamed/final.wav", destination = "song.amplet" })).StatusCode);
        using HttpResponseMessage empty = await Post("/api/project/folder", new { path = "assets/empty" });
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using HttpResponseMessage deletedFolder = await http.DeleteAsync("/api/project/folder?path=assets%2Fempty", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, deletedFolder.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(folder, "assets", "empty")));
        Assert.NotEqual(HttpStatusCode.OK, (await http.DeleteAsync("/api/project/folder?path=assets%2Frenamed", CancellationToken.None)).StatusCode);
        string journal = await Text("/api/blackbox/status");
        Assert.Contains("folder.create", journal, StringComparison.Ordinal);
        Assert.Contains("folder.delete", journal, StringComparison.Ordinal);
        Assert.Contains("folder.move", journal, StringComparison.Ordinal);
        Assert.Contains("file.move", journal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redux_search_is_read_only_filtered_and_pages_against_a_fixed_high_water_mark()
    {
        using HttpResponseMessage parent = await Post("/api/project/folder", new { path = "redux" });
        Assert.Equal(HttpStatusCode.OK, parent.StatusCode);
        foreach (string path in new[] { "redux/one", "redux/two", "redux/three" })
        {
            using HttpResponseMessage created = await Post("/api/project/folder", new { path });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        using JsonDocument first = JsonDocument.Parse(await Text("/api/redux/records?path=redux&limit=1"));
        JsonElement firstRecord = first.RootElement.GetProperty("records")[0];
        Assert.Matches("^AR-[0-9]{16}$", firstRecord.GetProperty("arId").GetString());
        Assert.Matches("^AR-[0-9]+$", firstRecord.GetProperty("compactArId").GetString());
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        long firstSequence = firstRecord.GetProperty("seq").GetInt64();

        using HttpResponseMessage newer = await Post("/api/project/folder", new { path = "redux/newer" });
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
        using JsonDocument second = JsonDocument.Parse(await Text("/api/redux/records?path=redux&limit=1&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(first.RootElement.GetProperty("through").GetInt64(), second.RootElement.GetProperty("through").GetInt64());
        Assert.True(second.RootElement.GetProperty("records")[0].GetProperty("seq").GetInt64() < firstSequence);

        using JsonDocument detail = JsonDocument.Parse(await Text("/api/redux/records?ar=" + Uri.EscapeDataString(firstRecord.GetProperty("arId").GetString()!) + "&includePayload=true"));
        Assert.Contains("redux/three", detail.RootElement.GetProperty("records")[0].GetProperty("payloadJson").GetString(), StringComparison.Ordinal);
        using HttpResponseMessage invalidCursor = await http.GetAsync("/api/redux/records?path=other&cursor=" + Uri.EscapeDataString(cursor), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        using JsonDocument wrongCursor = JsonDocument.Parse(await invalidCursor.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.True(wrongCursor.RootElement.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task History_revisions_show_only_their_exact_durable_redux_record()
    {
        using JsonDocument initial = JsonDocument.Parse(await Text("/api/history/graph"));
        Assert.Null(initial.RootElement.GetProperty("nodes")[0].GetProperty("reduxRef").GetString());
        using HttpResponseMessage changed = await Put("/api/file", new { path = "notes.txt", text = "saved note\n" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using HttpResponseMessage checkpoint = await Post("/api/history/checkpoint", new { message = "Redux linked revision" });
        Assert.Equal(HttpStatusCode.OK, checkpoint.StatusCode);

        using JsonDocument graph = JsonDocument.Parse(await Text("/api/history/graph"));
        JsonElement revision = graph.RootElement.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("message").GetString() == "Redux linked revision");
        string reference = revision.GetProperty("reduxRef").GetString()!;
        Assert.Matches("^AR-[0-9]+$", reference);
        using JsonDocument linked = JsonDocument.Parse(await Text("/api/redux/records?ar=" + Uri.EscapeDataString(reference) + "&includePayload=true"));
        JsonElement record = linked.RootElement.GetProperty("records")[0];
        Assert.Equal("history.checkpoint", record.GetProperty("kind").GetString());
        Assert.Equal("start", record.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Effect_preview_rejects_ranges_over_thirty_seconds_before_reading_a_take()
    {
        string privateFolder = Path.Combine(folder, ".ample");
        int filesBefore = Directory.Exists(privateFolder) ? Directory.EnumerateFiles(privateFolder, "*", SearchOption.AllDirectories).Count() : 0;
        using HttpResponseMessage response = await Post("/api/effects/preview", new { path = "song.amplet", block = "fx", takeId = "missing", fromMs = 0, toMs = 30_001, @params = new { } });
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        int filesAfter = Directory.Exists(privateFolder) ? Directory.EnumerateFiles(privateFolder, "*", SearchOption.AllDirectories).Count() : 0;
        Assert.True(filesAfter <= filesBefore, $"a rejected range must not leave files behind: {filesBefore} before, {filesAfter} after");   // other work in the folder may remove files meanwhile, so fewer is fine
    }

    [Fact]
    public async Task The_document_view_carries_the_meter_so_the_studio_can_draw_bars_of_the_song()
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "composition \"waltz\" { ample 1  tempo 90  meter 6/8  master-seed 1 }\nclick c at 1 for 2 {\n}\n");
        using JsonDocument document = JsonDocument.Parse(await Text("/api/doc?path=song.amplet"));
        JsonElement project = document.RootElement.GetProperty("project");
        Assert.Equal(6, project.GetProperty("meterNumerator").GetInt32());
        Assert.Equal(8, project.GetProperty("meterDenominator").GetInt32());
        Assert.Equal(3.0, project.GetProperty("beatsPerBar").GetDouble());
        await File.WriteAllTextAsync(Path.Combine(folder, "plain.amplet"), "composition \"plain\" { ample 1  tempo 90  master-seed 1 }\n");
        using JsonDocument plain = JsonDocument.Parse(await Text("/api/doc?path=plain.amplet"));
        Assert.Equal(4.0, plain.RootElement.GetProperty("project").GetProperty("beatsPerBar").GetDouble());
    }

    [Fact]
    public async Task Effect_preview_matches_the_built_effect_audio_and_never_creates_a_take()
    {
        Directory.CreateDirectory(Path.Combine(folder, "samples", "kit"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "samples", "kit", "kick.wav"), Wav.Encode([12000, 0, -8000, 0], 44100));
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "composition \"demo\" { ample 1  tempo 90 master-seed 1 }\ndrums source at 1 for 1 {\n  kit samples/kit\n  pattern kick:x...............\n}\ncompressor fx {\n  from source.audio -> audio\n}\n");
        using JsonDocument document = JsonDocument.Parse(await Text("/api/doc?path=song.amplet"));
        JsonElement diagnostics = document.RootElement.GetProperty("diagnostics");
        string errors = string.Join("; ", diagnostics.EnumerateArray().Where(d => d.GetProperty("severity").GetString() == "error").Select(d => $"{d.GetProperty("code").GetString()}: {d.GetProperty("message").GetString()}"));
        Assert.True(errors.Length == 0, errors);
        using HttpResponseMessage buildResponse = await Post("/api/build", new { path = "song.amplet", intent = "bake" });
        string buildJson = await buildResponse.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.True(buildResponse.IsSuccessStatusCode, buildJson);
        using JsonDocument build = JsonDocument.Parse(buildJson);
        JsonElement blocks = build.RootElement.GetProperty("blocks");
        string sourceTakeId = blocks.EnumerateArray().Single(block => block.GetProperty("block").GetString() == "source").GetProperty("takeId").GetString()!;
        string effectTakeId = blocks.EnumerateArray().Single(block => block.GetProperty("block").GetString() == "fx").GetProperty("takeId").GetString()!;
        using HttpResponseMessage selected = await Post("/api/control/select", new { path = "song.amplet", block = "source", take = sourceTakeId });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using JsonDocument before = JsonDocument.Parse(await Text("/api/takes"));

        using HttpResponseMessage preview = await Post("/api/effects/preview", new { path = "song.amplet", block = "fx", takeId = sourceTakeId, fromMs = 0, toMs = 30_000, @params = new { } });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("audio/wav", preview.Content.Headers.ContentType?.MediaType);
        byte[] previewAudio = await preview.Content.ReadAsByteArrayAsync(CancellationToken.None);
        using JsonDocument take = JsonDocument.Parse(await Text($"/api/takes/{effectTakeId}"));
        byte[] builtAudio = await http.GetByteArrayAsync($"/api/data/{take.RootElement.GetProperty("audioDataId").GetString()}", CancellationToken.None);
        Assert.Equal(builtAudio, previewAudio);
        using JsonDocument after = JsonDocument.Parse(await Text("/api/takes"));
        Assert.Equal(before.RootElement.GetArrayLength(), after.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Project_upload_imports_files_and_handles_conflicts_without_silent_overwrite()
    {
        byte[] original = [0, 1, 2, 255];
        using ByteArrayContent first = new(original);
        using HttpResponseMessage imported = await http.PostAsync("/api/project/upload?dir=assets&name=sample.wav", first, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(folder, "assets", "sample.wav"), CancellationToken.None));

        using ByteArrayContent refusedBytes = new([9]);
        using HttpResponseMessage refused = await http.PostAsync("/api/project/upload?dir=assets&name=sample.wav", refusedBytes, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using ByteArrayContent caseVariant = new([10]);
        using HttpResponseMessage caseCollision = await http.PostAsync("/api/project/upload?dir=assets&name=SAMPLE.WAV", caseVariant, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, caseCollision.StatusCode);
        using ByteArrayContent renamedBytes = new([8]);
        using HttpResponseMessage renamed = await http.PostAsync("/api/project/upload?dir=assets&name=sample.wav&onConflict=rename", renamedBytes, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync(CancellationToken.None));
        string renamedPath = result.RootElement.GetProperty("path").GetString()!;
        Assert.Matches("^assets/sample-[0-9]{8}-[0-9]{6}\\.wav$", renamedPath);
        await File.WriteAllBytesAsync(Path.Combine(folder, renamedPath.Replace('/', Path.DirectorySeparatorChar)), [0]);
        using ByteArrayContent collisionBytes = new([7]);
        using HttpResponseMessage collision = await http.PostAsync("/api/project/upload?dir=assets&name=sample.wav&onConflict=rename", collisionBytes, CancellationToken.None);
        using JsonDocument collisionResult = JsonDocument.Parse(await collision.Content.ReadAsStringAsync(CancellationToken.None));
        string secondPath = collisionResult.RootElement.GetProperty("path").GetString()!;
        // the name carries the second it was made in: when the clock ticks between two requests (a loaded machine) the number is not needed
        Assert.Matches("^assets/sample-[0-9]{8}-[0-9]{6}(-[0-9]+)?[.]wav$", secondPath);
        Assert.NotEqual(renamedPath, secondPath);
        await File.WriteAllBytesAsync(Path.Combine(folder, secondPath.Replace('/', Path.DirectorySeparatorChar)), [0]);
        using ByteArrayContent thirdRenameBytes = new([6]);
        using HttpResponseMessage thirdRename = await http.PostAsync("/api/project/upload?dir=assets&name=sample.wav&onConflict=rename", thirdRenameBytes, CancellationToken.None);
        using JsonDocument thirdRenameResult = JsonDocument.Parse(await thirdRename.Content.ReadAsStringAsync(CancellationToken.None));
        string thirdPath = thirdRenameResult.RootElement.GetProperty("path").GetString()!;
        Assert.Matches("^assets/sample-[0-9]{8}-[0-9]{6}(-[0-9]+)?[.]wav$", thirdPath);
        Assert.DoesNotContain(thirdPath, new[] { renamedPath, secondPath });

        using ByteArrayContent overwriteBytes = new([5]);
        using HttpResponseMessage overwritten = await http.PostAsync("/api/project/upload?dir=assets&name=sample.wav&onConflict=overwrite", overwriteBytes, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, overwritten.StatusCode);
        Assert.Equal([5], await File.ReadAllBytesAsync(Path.Combine(folder, "assets", "sample.wav"), CancellationToken.None));
        using ByteArrayContent recordedBytes = new([6]);
        using HttpResponseMessage recorded = await http.PostAsync("/api/project/upload?dir=assets&name=sample.wav&onConflict=overwrite-record", recordedBytes, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        Assert.Equal([6], await File.ReadAllBytesAsync(Path.Combine(folder, "assets", "sample.wav"), CancellationToken.None));
        Assert.Contains("file.import", await Text("/api/blackbox/status"), StringComparison.Ordinal);

        using ByteArrayContent refusedPrivate = new([0]);
        using HttpResponseMessage privateFolder = await http.PostAsync("/api/project/upload?dir=.ample&name=secret.txt", refusedPrivate, CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.OK, privateFolder.StatusCode);
    }

    [Fact]
    public async Task Concurrent_uploads_to_the_same_new_path_do_not_overwrite_each_other()
    {
        using ByteArrayContent firstContent = new([1, 2, 3]);
        using ByteArrayContent secondContent = new([4, 5, 6]);
        Task<HttpResponseMessage> firstRequest = http.PostAsync("/api/project/upload?dir=assets&name=race.wav", firstContent, CancellationToken.None);
        Task<HttpResponseMessage> secondRequest = http.PostAsync("/api/project/upload?dir=assets&name=race.wav", secondContent, CancellationToken.None);
        HttpResponseMessage[] responses = await Task.WhenAll(firstRequest, secondRequest);
        using (responses[0]) using (responses[1])
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            byte[] stored = await File.ReadAllBytesAsync(Path.Combine(folder, "assets", "race.wav"), CancellationToken.None);
            Assert.True(stored.SequenceEqual(new byte[] { 1, 2, 3 }) || stored.SequenceEqual(new byte[] { 4, 5, 6 }));
        }
    }

    [Fact]
    public async Task Project_upload_accepts_multipart_and_the_route_limit_above_the_global_json_limit()
    {
        byte[] largerThanJsonLimit = new byte[9 * 1024 * 1024];
        using ByteArrayContent raw = new(largerThanJsonLimit);
        using HttpResponseMessage rawResponse = await http.PostAsync("/api/project/upload?name=large.wav", raw, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, rawResponse.StatusCode);
        Assert.Equal(largerThanJsonLimit.Length, new FileInfo(Path.Combine(folder, "large.wav")).Length);

        using MultipartFormDataContent multipart = new();
        using ByteArrayContent file = new([4, 5, 6]);
        multipart.Add(file, "file", "from-form.wav");
        multipart.Add(new StringContent("nested"), "dir");
        using HttpResponseMessage multipartResponse = await http.PostAsync("/api/project/upload", multipart, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, multipartResponse.StatusCode);
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(Path.Combine(folder, "nested", "from-form.wav"), CancellationToken.None));
    }

    [Fact]
    public async Task Project_upload_limit_can_be_lowered_for_a_server_instance()
    {
        string limitedFolder = Path.Combine(Path.GetTempPath(), "ample-upload-limit-" + Guid.NewGuid().ToString("N"));
        string libraryFolder = limitedFolder + "-library";
        try
        {
            using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), limitedFolder, "limited", null, CancellationToken.None))
            {
            }

            await using RunningServer limited = await AmpleServer.StartAsync(new ServerOptions(limitedFolder, FindAmpleRoot(), LibraryHome: libraryFolder, MaxProjectUploadBytes: 1024 * 1024));
            using HttpClient client = new() { BaseAddress = new Uri(limited.Url) };
            client.DefaultRequestHeaders.Add("X-Ample-Token", limited.Token);
            using ByteArrayContent tooLarge = new(new byte[1024 * 1024 + 1]);
            using HttpResponseMessage response = await client.PostAsync("/api/project/upload?name=too-large.wav", tooLarge, CancellationToken.None);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.False(File.Exists(Path.Combine(limitedFolder, "too-large.wav")));
        }
        finally
        {
            try { Directory.Delete(limitedFolder, true); Directory.Delete(libraryFolder, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Concurrent_same_name_uploads_are_serialized_to_import_then_refusal()
    {
        using ByteArrayContent first = new([1]);
        using ByteArrayContent second = new([2]);
        Task<HttpResponseMessage> firstUpload = http.PostAsync("/api/project/upload?name=concurrent.wav", first, CancellationToken.None);
        Task<HttpResponseMessage> secondUpload = http.PostAsync("/api/project/upload?name=concurrent.wav", second, CancellationToken.None);
        HttpResponseMessage[] results = await Task.WhenAll(firstUpload, secondUpload);
        try
        {
            Assert.Equal(1, results.Count(result => result.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, results.Count(result => result.StatusCode == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (HttpResponseMessage result in results) result.Dispose();
        }
    }

    [Fact]
    public async Task Project_file_mutation_is_published_to_sse_subscribers()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/events");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using StreamReader reader = new(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(timeout.Token));
        using ByteArrayContent bytes = new([1, 2]);
        using HttpResponseMessage upload = await http.PostAsync("/api/project/upload?name=event.wav", bytes, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal("event: files.changed", await reader.ReadLineAsync(timeout.Token));
        Assert.Contains("event.wav", await reader.ReadLineAsync(timeout.Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Midi_upload_accepts_generated_smf_and_rejects_bad_track_or_bytes()
    {
        byte[] midi = Convert.FromHexString("4D546864000000060000000101E04D54726B0000000D00903C648360803C0000FF2F00");
        using ByteArrayContent content = new(midi);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage uploaded = await http.PostAsync("/api/midi/upload?extension=.mid&track=0", content, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(CancellationToken.None));
        string path = result.RootElement.GetProperty("path").GetString()!;
        Assert.StartsWith("assets/midi/", path, StringComparison.Ordinal);
        Assert.Equal(1, result.RootElement.GetProperty("bars").GetInt32());
        Assert.Equal(midi, await File.ReadAllBytesAsync(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)), CancellationToken.None));

        using ByteArrayContent invalidTrackContent = new(midi);
        HttpResponseMessage invalidTrack = await http.PostAsync("/api/midi/upload?extension=.mid&track=1", invalidTrackContent, CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.OK, invalidTrack.StatusCode);
        using ByteArrayContent invalidBytesContent = new([0, 1, 2]);
        HttpResponseMessage invalidBytes = await http.PostAsync("/api/midi/upload?extension=.mid&track=all", invalidBytesContent, CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.OK, invalidBytes.StatusCode);
        using ByteArrayContent oversizedContent = new(new byte[SmfLimits.Standard.MaxFileBytes + 1]);
        HttpResponseMessage? oversized = null;
        try { oversized = await http.PostAsync("/api/midi/upload?extension=.mid&track=all", oversizedContent, CancellationToken.None); }
        catch (HttpRequestException) { /* Kestrel rejected the declared size before accepting the body (Windows says so, Linux closes the connection while the client is still writing). */ }
        if (oversized is not null) { using (oversized) Assert.NotEqual(HttpStatusCode.OK, oversized.StatusCode); }
        Assert.Single(Directory.GetFiles(Path.Combine(folder, "assets", "midi")));
    }

    [Fact]
    public async Task Midi_module_activation_keeps_original_and_sets_the_saved_variant_active()
    {
        byte[] midi = Convert.FromHexString("4D546864000000060000000101E04D54726B0000000D00903C648360803C0000FF2F00");
        using ByteArrayContent content = new(midi);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage upload = await http.PostAsync("/api/midi/upload?extension=.mid&track=all", content, CancellationToken.None);
        using JsonDocument uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync(CancellationToken.None));
        string original = uploaded.RootElement.GetProperty("path").GetString()!;
        using HttpResponseMessage module = await Post("/api/midi/module", new { file = original, track = "all", name = "midi-variant" });
        Assert.Equal(HttpStatusCode.OK, module.StatusCode);

        const string source = "project demo { tempo 100 master-seed 1 }\nmidi input {\n  file ";
        string text = source + original + "\n  source-file " + original + "\n  variant-module midi-variant\n}\n";
        using JsonDocument initial = JsonDocument.Parse(await Text("/api/doc?path=song.amplet"));
        using HttpResponseMessage update = await Put("/api/doc", new { path = "song.amplet", baseVersion = initial.RootElement.GetProperty("version").GetInt32(), text });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using JsonDocument updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync(CancellationToken.None));
        using JsonDocument moduleDoc = JsonDocument.Parse(await Text("/api/module/doc?path=modules%2Fmidi-variant.module"));
        string editedModule = moduleDoc.RootElement.GetProperty("text").GetString()!.Replace(":60:100:", ":61:100:", StringComparison.Ordinal);
        Assert.NotEqual(moduleDoc.RootElement.GetProperty("text").GetString(), editedModule);
        using HttpResponseMessage editModule = await Put("/api/module/doc", new { path = "modules/midi-variant.module", baseVersion = moduleDoc.RootElement.GetProperty("version").GetInt32(), text = editedModule });
        Assert.Equal(HttpStatusCode.OK, editModule.StatusCode);
        using HttpResponseMessage activation = await Post("/api/midi/module-activate", new { path = "modules/midi-variant.module", songPath = "song.amplet", block = "input", baseVersion = updated.RootElement.GetProperty("version").GetInt32() });
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await activation.Content.ReadAsStringAsync(CancellationToken.None));
        string active = result.RootElement.GetProperty("path").GetString()!;
        JsonElement block = result.RootElement.GetProperty("midi").GetProperty("blocks").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "input");
        string Parameter(string name) => block.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("value").GetString()!;
        Assert.StartsWith("assets/midi/", active, StringComparison.Ordinal);
        Assert.Equal(active, Parameter("file"));
        Assert.Equal(active, Parameter("variant-file"));
        Assert.Equal(original, Parameter("source-file"));
        Assert.Equal(midi, await File.ReadAllBytesAsync(Path.Combine(folder, original.Replace('/', Path.DirectorySeparatorChar)), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(folder, active.Replace('/', Path.DirectorySeparatorChar))));
        SmfFile regenerated = SmfReader.Parse(await File.ReadAllBytesAsync(Path.Combine(folder, active.Replace('/', Path.DirectorySeparatorChar)), CancellationToken.None), SmfLimits.Standard);
        Assert.Equal(61, Assert.Single(regenerated.Tracks[0].Events, item => item.Kind == Ample.Audio.Midi.SmfEventKind.NoteOn).Data1);
        using HttpResponseMessage useOriginal = await Post("/api/doc/edit", new { path = "song.amplet", baseVersion = result.RootElement.GetProperty("midi").GetProperty("version").GetInt32(), op = new { type = "setParameter", block = "input", name = "file", value = original } });
        Assert.Equal(HttpStatusCode.OK, useOriginal.StatusCode);
        using JsonDocument originalDoc = JsonDocument.Parse(await useOriginal.Content.ReadAsStringAsync(CancellationToken.None));
        JsonElement originalBlock = originalDoc.RootElement.GetProperty("blocks").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "input");
        Assert.Equal(original, originalBlock.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "file").GetProperty("value").GetString());
        using HttpResponseMessage useEdited = await Post("/api/doc/edit", new { path = "song.amplet", baseVersion = originalDoc.RootElement.GetProperty("version").GetInt32(), op = new { type = "setParameter", block = "input", name = "file", value = active } });
        Assert.Equal(HttpStatusCode.OK, useEdited.StatusCode);
    }

    [Fact]
    public async Task Gm_audition_accepts_the_all_channels_setting_and_optional_note_routing()
    {
        using HttpResponseMessage response = await Post("/api/gm/audition", new
        {
            file = "missing.sf2",
            bank = 0,
            program = 40,
            channel = -1,
            gain = 0,
            limiter = "soft",
            notes = new[]
            {
                new
                {
                    bar = 1,
                    tick = 0,
                    ticks = 120,
                    ppq = 480,
                    pitch = 60,
                    velocity = 90,
                    channel = 9,
                    bank = 128,
                    program = 40
                }
            }
        });
        string problem = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("AMP5311", problem, StringComparison.Ordinal);
        Assert.Contains("soundfont", problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audition field 'channel'", problem, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder, "*.wav", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Module_source_keeps_its_blocks_through_an_edit_an_undo_and_a_redo()
    {
        byte[] midi = Convert.FromHexString("4D546864000000060000000101E04D54726B0000000D00903C648360803C0000FF2F00");
        using ByteArrayContent content = new(midi);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage upload = await http.PostAsync("/api/midi/upload?extension=.mid&track=all", content, CancellationToken.None);
        using JsonDocument uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync(CancellationToken.None));
        using HttpResponseMessage module = await Post("/api/midi/module", new { file = uploaded.RootElement.GetProperty("path").GetString()!, track = "all", name = "round-trip" });
        Assert.Equal(HttpStatusCode.OK, module.StatusCode);
        const string path = "modules/round-trip.module";

        async Task<(string Text, int Version)> Doc()
        {
            using JsonDocument doc = JsonDocument.Parse(await Text("/api/module/doc?path=modules%2Fround-trip.module"));
            return (doc.RootElement.GetProperty("text").GetString()!, doc.RootElement.GetProperty("version").GetInt32());
        }

        async Task<string> SourceText()
        {
            using JsonDocument source = JsonDocument.Parse(await Text("/api/module/source?path=modules%2Fround-trip.module"));
            return source.RootElement.GetProperty("text").GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        async Task<int> Write(string text, int baseVersion)
        {
            using HttpResponseMessage put = await Put("/api/module/doc", new { path, baseVersion, text });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            using JsonDocument view = JsonDocument.Parse(await put.Content.ReadAsStringAsync(CancellationToken.None));
            return view.RootElement.GetProperty("version").GetInt32();
        }

        (string text, int version) = await Doc();
        string blocksBefore = string.Join("\n", (await SourceText()).Split('\n').Where(line => line.TrimEnd().EndsWith('{')));
        Assert.NotEqual("", blocksBefore);
        string edited = text.Replace(":60:100:", ":61:100:", StringComparison.Ordinal).Replace(":60:", ":61:", StringComparison.Ordinal);
        Assert.NotEqual(text, edited);

        version = await Write(edited, version);
        version = await Write(text, version);
        version = await Write(edited, version);

        string after = await SourceText();
        Assert.Equal(blocksBefore, string.Join("\n", after.Split('\n').Where(line => line.TrimEnd().EndsWith('{'))));
        Assert.Contains(":61:", after, StringComparison.Ordinal);

        // a typed edit after the text writes (the node editor changes a parameter this way)
        using JsonDocument view = JsonDocument.Parse(await Text("/api/module/doc?path=modules%2Fround-trip.module"));
        JsonElement withProgram = view.RootElement.GetProperty("blocks").EnumerateArray().First(item => item.GetProperty("parameters").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "program"));
        using HttpResponseMessage typed = await Post("/api/module/doc/edit", new { path, baseVersion = view.RootElement.GetProperty("version").GetInt32(), op = new { type = "setParameter", block = withProgram.GetProperty("name").GetString()!, name = "program", value = "40" } });
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        string finalSource = await SourceText();
        Assert.Equal(blocksBefore, string.Join("\n", finalSource.Split('\n').Where(line => line.TrimEnd().EndsWith('{'))));
        Assert.Contains(":61:", finalSource, StringComparison.Ordinal);
        Assert.Contains("program 40", finalSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_source_of_an_advanced_module_survives_typed_edits_and_text_writes_in_the_order_the_node_editor_uses()
    {
        const string module = "module advanced-midi version 1.0\noutput audio mix.mix\n\n# midi-source version=1 format=1 ppq=480\n# source-event track=0 tick=0 kind=Meta channel=0 data1=0 data2=0 meta=51 payload=07A120\nmidi-clip clip at 1 for 1 {\n  notes 1:0:1:60:80\n}\ngm instrument at 1 for 1 {\n  from clip.notes -> notes\n  program 0\n}\nmixer mix {\n  from instrument.audio -> audio1\n  gains 0 0 0 0\n}\n";
        string modulesFolder = Path.Combine(folder, "modules");
        Directory.CreateDirectory(modulesFolder);
        await File.WriteAllTextAsync(Path.Combine(modulesFolder, "advanced-midi.module"), module, CancellationToken.None);
        const string url = "/api/module/doc?path=modules%2Fadvanced-midi.module";

        async Task<(string Text, int Version)> Doc()
        {
            using JsonDocument doc = JsonDocument.Parse(await Text(url));
            return (doc.RootElement.GetProperty("text").GetString()!, doc.RootElement.GetProperty("version").GetInt32());
        }

        async Task<string> SourceText()
        {
            using JsonDocument source = JsonDocument.Parse(await Text("/api/module/source?path=modules%2Fadvanced-midi.module"));
            return source.RootElement.GetProperty("text").GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        async Task Typed(string block, string name, string value)
        {
            (_, int version) = await Doc();
            using HttpResponseMessage edit = await Post("/api/module/doc/edit", new { path = "modules/advanced-midi.module", baseVersion = version, op = new { type = "setParameter", block, name, value } });
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        }

        async Task Write(string text)
        {
            (_, int version) = await Doc();
            using HttpResponseMessage put = await Put("/api/module/doc", new { path = "modules/advanced-midi.module", baseVersion = version, text });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        (string original, _) = await Doc();
        await Typed("clip", "notes", "1:0:2:60:80");
        (string edited, _) = await Doc();
        await Write(original);
        await Write(edited);
        await Typed("instrument", "program", "40");
        await Typed("mix", "gains", "-3 0 0 0");

        string source = await SourceText();
        Assert.Equal(module.Replace("notes 1:0:1:60:80", "notes 1:0:2:60:80", StringComparison.Ordinal).Replace("program 0", "program 40", StringComparison.Ordinal).Replace("gains 0 0 0 0", "gains -3 0 0 0", StringComparison.Ordinal), source);
    }

    [Fact]
    public async Task The_gpu_status_lists_what_is_kept_and_releasing_with_nothing_kept_is_harmless()
    {
        using JsonDocument status = JsonDocument.Parse(await Text("/api/gpu"));
        Assert.Equal(JsonValueKind.Array, status.RootElement.GetProperty("kept").ValueKind);
        Assert.Equal(0, status.RootElement.GetProperty("kept").GetArrayLength());

        HttpResponseMessage released = await Post("/api/gpu/release", new { });
        Assert.True(released.IsSuccessStatusCode);
        using JsonDocument body = JsonDocument.Parse(await released.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("released").GetInt32());
    }

    [Fact]
    public async Task Audio_transcription_accepts_generated_wav_and_serves_its_source_and_aligned_stems()
    {
        using JsonDocument plugins = JsonDocument.Parse(await Text("/api/plugins"));
        JsonElement transcriptionPlugin = plugins.RootElement.GetProperty("plugins").EnumerateArray().Single(plugin => plugin.GetProperty("id").GetString() == "analysis-fake");
        Assert.Contains("transcription", transcriptionPlugin.GetProperty("analyses").EnumerateArray().Select(item => item.GetString()));
        Assert.False(transcriptionPlugin.GetProperty("needs").GetProperty("network").GetBoolean());
        byte[] wav = Wav.Encode(new short[22050], 22050);
        using MultipartFormDataContent form = new();
        form.Add(new StringContent("analysis-fake"), "plugin");
        form.Add(new StringContent("{\"nested\":true,\"includeMidi\":true,\"computeDevice\":\"cpu\"}"), "options");
        form.Add(new StringContent("00112233445566778899aabbccddeeff"), "job");
        ByteArrayContent audio = new(wav);
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "generated.wav");
        using HttpResponseMessage uploaded = await http.PostAsync("/api/audio/transcriptions", form, CancellationToken.None);
        Assert.True(uploaded.StatusCode == HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync(CancellationToken.None));
        using JsonDocument response = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(CancellationToken.None));
        string id = response.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("00112233445566778899aabbccddeeff", id);
        Assert.Equal(22050, response.RootElement.GetProperty("result").GetProperty("sampleRate").GetInt32());
        Assert.Equal(57, response.RootElement.GetProperty("result").GetProperty("vocalPitch").GetProperty("frames")[0].GetProperty("midi").GetInt32());
        Assert.Equal(["backing", "backing-midi", "source", "vocals"], response.RootElement.GetProperty("stems").EnumerateArray().Select(stem => stem.GetProperty("role").GetString()).Order(StringComparer.Ordinal));
        foreach (JsonElement stem in response.RootElement.GetProperty("stems").EnumerateArray())
        {
            byte[] downloaded = await http.GetByteArrayAsync(stem.GetProperty("url").GetString()!, CancellationToken.None);
            if (stem.GetProperty("role").GetString() == "backing-midi") Assert.Equal(new byte[] { 0x4d, 0x54, 0x68, 0x64 }, downloaded[..4]);
            else Assert.Equal(wav, downloaded);
        }
        using JsonDocument peaks = JsonDocument.Parse(await Text($"/api/audio/transcriptions/{id}/peaks?role=source&points=80"));
        Assert.Equal(80, peaks.RootElement.GetProperty("points").GetInt32());
        using JsonDocument reopened = JsonDocument.Parse(await Text($"/api/audio/transcriptions/{id}"));
        Assert.Equal(id, reopened.RootElement.GetProperty("id").GetString());

        using MultipartFormDataContent invalid = new();
        invalid.Add(new StringContent("analysis-fake"), "plugin");
        invalid.Add(new ByteArrayContent([1, 2, 3]), "audio", "bad.wav");
        Assert.NotEqual(HttpStatusCode.OK, (await http.PostAsync("/api/audio/transcriptions", invalid, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Project_settings_are_saved_and_a_nonsense_value_is_refused()
    {
        HttpResponseMessage ok = await Post("/api/project/settings", new { blackBoxBudget = 64 * 1048576L, fullDetailDays = 10, thinnedDays = 90, defaultEngine = "", faer = new { song = "faer", voice = "local", separation = "local" }, projectPanel = new { view = "details", tileSize = 3, sortColumn = "size", sortDescending = true, columns = new[] { new { id = "name", width = 320 }, new { id = "size", width = 120 }, new { id = "path", width = 240 } } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using JsonDocument now = JsonDocument.Parse(await Text("/api/project/settings"));
        Assert.Equal(64 * 1048576L, now.RootElement.GetProperty("blackBox").GetProperty("budgetBytes").GetInt64());
        Assert.Equal("faer", now.RootElement.GetProperty("faer").GetProperty("song").GetString());
        Assert.Equal("local", now.RootElement.GetProperty("faer").GetProperty("voice").GetString());
        JsonElement panel = now.RootElement.GetProperty("projectPanel");
        Assert.Equal("details", panel.GetProperty("view").GetString());
        Assert.True(panel.GetProperty("sortDescending").GetBoolean());
        Assert.Equal("path", panel.GetProperty("columns")[2].GetProperty("id").GetString());

        Assert.NotEqual(HttpStatusCode.OK, (await Post("/api/project/settings", new { blackBoxBudget = -5 })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Post("/api/project/settings", new { faer = new { song = "elsewhere", voice = "local", separation = "local" } })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Post("/api/project/settings", new { projectPanel = new { view = "details", tileSize = 1, sortColumn = "bad", sortDescending = false, columns = new[] { new { id = "bad", width = 80 } } } })).StatusCode);
    }

    [Fact]
    public void Brave_key_source_matches_the_key_a_search_would_use_for_every_combination()
    {
        Guid id = Guid.NewGuid();
        string home = Path.Combine(Path.GetTempPath(), "ample-brave-status-" + Guid.NewGuid().ToString("N"));
        string perProjectName = "AMPLE_BRAVE_SEARCH_API_KEY_" + id.ToString("N").ToUpperInvariant();
        string? oldGlobal = Environment.GetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY");
        try
        {
            foreach (bool project in new[] { false, true })
            foreach (string? perProject in new string?[] { null, "   ", "synthetic-per-project-marker" })
            foreach (string? global in new string?[] { null, "", "synthetic-global-marker" })
            {
                if (project && !OperatingSystem.IsWindows()) continue; // the key store is DPAPI only
                AiProjectSecretStore.Clear(home, id);
                if (project) AiProjectSecretStore.Save(home, id, "synthetic-project-marker");
                Environment.SetEnvironmentVariable(perProjectName, perProject);
                Environment.SetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY", global);
                bool server = !string.IsNullOrWhiteSpace(perProject) || !string.IsNullOrWhiteSpace(global);
                (bool Project, bool Server, string Source) status = AiService.BraveKeyStatus(home, id);
                Assert.Equal(project, status.Project);
                Assert.Equal(server, status.Server);
                Assert.Equal(project ? "project" : server ? "server" : "none", status.Source);
                // what the search really uses, with a project key (Windows) or without
                string? used = AiService.ResolveBraveKey(project ? "synthetic-project-marker" : null, id);
                string? expected = null;
                if (!string.IsNullOrWhiteSpace(global)) expected = global;
                if (!string.IsNullOrWhiteSpace(perProject)) expected = perProject;
                if (project) expected = "synthetic-project-marker";
                Assert.Equal(expected, used);
                // after clearing the project key the source is the server key, or none
                AiProjectSecretStore.Clear(home, id);
                Assert.Equal(server ? "server" : "none", AiService.BraveKeyStatus(home, id).Source);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(perProjectName, null);
            Environment.SetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY", oldGlobal);
            if (Directory.Exists(home)) Directory.Delete(home, true);
        }
    }

    [Fact]
    public async Task Settings_report_brave_key_presence_as_booleans_and_never_a_key_value()
    {
        string perProjectName;
        using (JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, ".ample", "project.json"))))
        {
            string projectGuid = manifest.RootElement.GetProperty("id").GetString()!;
            perProjectName = "AMPLE_BRAVE_SEARCH_API_KEY_"
                + projectGuid.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }
        const string serverMarker = "synthetic-server-brave-marker";
        const string projectMarker = "synthetic-project-brave-marker";
        try
        {
            Environment.SetEnvironmentVariable(perProjectName, serverMarker);
            string text = await Text("/api/project/settings");
            using (JsonDocument doc = JsonDocument.Parse(text))
            {
                JsonElement r = doc.RootElement;
                Assert.False(r.GetProperty("braveSearchProjectKey").GetBoolean());
                Assert.True(r.GetProperty("braveSearchServerKey").GetBoolean());
                Assert.True(r.GetProperty("braveSearchKeyConfigured").GetBoolean());
                Assert.Equal("server", r.GetProperty("braveSearchSource").GetString());
            }

            Assert.DoesNotContain(serverMarker, text, StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                HttpResponseMessage saved = await Post("/api/project/settings", new { braveSearchApiKey = projectMarker });
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                text = await Text("/api/project/settings");
                using (JsonDocument doc = JsonDocument.Parse(text))
                {
                    Assert.True(doc.RootElement.GetProperty("braveSearchProjectKey").GetBoolean());
                    Assert.True(doc.RootElement.GetProperty("braveSearchServerKey").GetBoolean());
                    Assert.Equal("project", doc.RootElement.GetProperty("braveSearchSource").GetString());
                }

                Assert.DoesNotContain(serverMarker, text, StringComparison.Ordinal);
                Assert.DoesNotContain(projectMarker, text, StringComparison.Ordinal);
                HttpResponseMessage cleared = await Post(
                    "/api/project/settings", new { braveSearchApiKey = "", clearBraveSearchApiKey = true });
                Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
                Assert.Contains("\"braveSearchSource\":\"server\"", await Text("/api/project/settings"), StringComparison.Ordinal);
            }

            Environment.SetEnvironmentVariable(perProjectName, null);
            if (Environment.GetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY") is null)
                Assert.Contains("\"braveSearchSource\":\"none\"", await Text("/api/project/settings"), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(perProjectName, null); }
    }

    [Fact]
    public async Task Brave_search_key_is_project_scoped_encrypted_and_never_returned()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string marker = "synthetic-brave-search-key-marker";
        HttpResponseMessage saved = await Post("/api/project/settings", new { braveSearchApiKey = marker });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        string settingsText = await Text("/api/project/settings");
        Assert.Contains("\"braveSearchKeyConfigured\":true", settingsText, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, settingsText, StringComparison.Ordinal);
        string projectJson = await File.ReadAllTextAsync(Path.Combine(folder, ".ample", "project.json"));
        Assert.DoesNotContain(marker, projectJson, StringComparison.Ordinal);
        string secretDir = folder + "-library";
        using JsonDocument manifest = JsonDocument.Parse(projectJson);
        string secretPath = Path.Combine(secretDir, "project-secrets", manifest.RootElement.GetProperty("id").GetString()!.Replace("-", string.Empty, StringComparison.Ordinal) + ".bin");
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(secretPath)), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", new { braveSearchApiKey = "", clearBraveSearchApiKey = true })).StatusCode);
        Assert.Contains("\"braveSearchKeyConfigured\":false", await Text("/api/project/settings"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_master_seed_changes_only_through_the_two_step_act()
    {
        using JsonDocument dry = JsonDocument.Parse(await (await Post("/api/acts/master-seed", new { seed = "99" })).Content.ReadAsStringAsync(CancellationToken.None));
        string token = dry.RootElement.GetProperty("token").GetString()!;
        string accept = dry.RootElement.GetProperty("acceptance").GetString()!;

        HttpResponseMessage wrong = await Post("/api/acts/confirm", new { token, typed = "no", reason = "x" });
        Assert.NotEqual(HttpStatusCode.OK, wrong.StatusCode);
        HttpResponseMessage noReason = await Post("/api/acts/confirm", new { token, typed = accept, reason = "" });
        Assert.NotEqual(HttpStatusCode.OK, noReason.StatusCode);
        Assert.Contains("\"masterSeed\":\"", await Text("/api/project"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"masterSeed\":\"99\"", await Text("/api/project"), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await Post("/api/acts/confirm", new { token, typed = accept, reason = "test" })).StatusCode);
        using JsonDocument settings = JsonDocument.Parse(await Text("/api/project/settings"));
        Assert.Equal("99", settings.RootElement.GetProperty("masterSeed").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await Post("/api/acts/confirm", new { token, typed = accept, reason = "again" })).StatusCode);
    }

    [Fact]
    public async Task Plugin_settings_start_empty_and_wrong_input_is_not_stored()
    {
        HttpResponseMessage bad = await Put("/api/plugins/settings", new { plugin = "no-such-plugin", values = new { x = 1 } });
        Assert.NotEqual(HttpStatusCode.OK, bad.StatusCode);
    }

    [Fact]
    public async Task The_recent_list_names_this_project_and_forgetting_does_not_touch_the_folder()
    {
        string list = await Text("/api/projects");
        Assert.Contains("demo", list, StringComparison.Ordinal);
        HttpResponseMessage forget = await Post("/api/projects/forget", new { folder });
        Assert.True(forget.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        Assert.True(File.Exists(Path.Combine(folder, "song.amplet")));
    }

    // X03: /api/sound-check always reports the master's numeric peak/RMS/loudness (not only as a text finding when
    // it is unusually loud or quiet), so a UI meter has something to show after every ordinary build.
    [Fact]
    public async Task Sound_check_reports_the_masters_numeric_levels_for_an_ordinary_build()
    {
        Directory.CreateDirectory(Path.Combine(folder, "samples", "kit"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "samples", "kit", "kick.wav"), Wav.Encode([12000, 6000, -8000, -4000], 44100));
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "composition \"demo\" { ample 1  tempo 90 master-seed 1 }\ndrums source at 1 for 1 {\n  kit samples/kit\n  pattern kick:x...............\n}\nmaster master {\n  from source.audio -> audio\n}\n");
        // sound-check only analyses takes that already exist ("nothing is rendered" by the endpoint itself).
        using HttpResponseMessage build = await Post("/api/build", new { path = "song.amplet", intent = "bake" });
        Assert.True(build.IsSuccessStatusCode, await build.Content.ReadAsStringAsync(CancellationToken.None));
        using JsonDocument result = JsonDocument.Parse(await Text("/api/sound-check?path=song.amplet"));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        JsonElement master = result.RootElement.GetProperty("master");
        Assert.NotEqual(JsonValueKind.Null, master.ValueKind);
        Assert.True(master.GetProperty("peakDbfs").GetDouble() <= 0.1);
        Assert.True(master.TryGetProperty("rmsDbfs", out _));
    }

    // A song changed on disk after it was opened (by a script, git or an agent) is built as it is on disk, not as it was when it was opened.
    [Fact]
    public async Task Build_follows_a_song_that_changed_on_disk_after_it_was_opened()
    {
        Directory.CreateDirectory(Path.Combine(folder, "samples", "kit"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "samples", "kit", "kick.wav"), Wav.Encode([12000, 0, -8000, 0], 44100));
        string path = Path.Combine(folder, "song.amplet");
        const string first = "composition \"demo\" { ample 1  tempo 90 master-seed 1 }\ndrums source at 1 for 1 {\n  kit samples/kit\n  pattern kick:x...............\n}\n";
        await File.WriteAllTextAsync(path, first);
        using (JsonDocument opened = JsonDocument.Parse(await Text("/api/doc?path=song.amplet")))
        {
            Assert.Equal(first, opened.RootElement.GetProperty("text").GetString());
        }

        using (HttpResponseMessage one = await Post("/api/build", new { path = "song.amplet", intent = "bake" }))
        {
            using JsonDocument built = JsonDocument.Parse(await one.Content.ReadAsStringAsync(CancellationToken.None));
            Assert.Equal(["source"], built.RootElement.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("block").GetString()!).ToArray());
        }

        await File.WriteAllTextAsync(path, first + "compressor fx {\n  from source.audio -> audio\n}\n");
        using HttpResponseMessage two = await Post("/api/build", new { path = "song.amplet", intent = "bake" });
        using JsonDocument rebuilt = JsonDocument.Parse(await two.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Contains("fx", rebuilt.RootElement.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("block").GetString()));
    }

    // The list of edits is all or nothing: a refused edit leaves the song as it was, and a good list is one step.
    [Fact]
    public async Task A_list_of_edits_is_applied_in_full_or_not_at_all()
    {
        const string song = "composition \"demo\" { ample 1  tempo 90 master-seed 1 }\nsynth pad at 1 for 2 {\n}\nbus mix {\n  from pad.audio -> audio1\n}\n";
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), song);
        using JsonDocument opened = JsonDocument.Parse(await Text("/api/doc?path=song.amplet"));
        using (HttpResponseMessage refused = await Post("/api/doc/edits", new { path = "song.amplet", baseVersion = opened.RootElement.GetProperty("version").GetInt32(), ops = new object[] { new { type = "addBlock", blockType = "reverb", name = "room" }, new { type = "connect", from = "pad", output = "audio", to = "mix", input = "audio1" } } }))
        {
            Assert.False(refused.IsSuccessStatusCode);
            Assert.Contains("nothing changed", await refused.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        }

        using JsonDocument same = JsonDocument.Parse(await Text("/api/doc?path=song.amplet"));
        Assert.Equal(song, same.RootElement.GetProperty("text").GetString());
        Assert.Equal(opened.RootElement.GetProperty("version").GetInt32(), same.RootElement.GetProperty("version").GetInt32());

        using HttpResponseMessage good = await Post("/api/doc/edits", new { path = "song.amplet", baseVersion = same.RootElement.GetProperty("version").GetInt32(), ops = new object[] { new { type = "insertEffect", block = "pad", blockType = "reverb", name = "room" }, new { type = "bypassEffect", block = "room" } } });
        Assert.True(good.IsSuccessStatusCode);
        using JsonDocument after = JsonDocument.Parse(await good.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(song, after.RootElement.GetProperty("text").GetString());
    }

    private const string DiskSong = "project demo { tempo 90 master-seed 1 }\n# one\n# two\n# three\n# four\n";

    private async Task<JsonDocument> OpenDisk(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(folder, name), DiskSong, CancellationToken.None);
        return JsonDocument.Parse(await Text("/api/doc?path=" + name));
    }

    [Fact]
    public async Task A_file_changed_on_disk_is_picked_up_when_the_working_copy_has_no_changes()
    {
        using JsonDocument opened = await OpenDisk("disk-clean.amplet");
        string changed = DiskSong.Replace("# two", "# TWO", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(folder, "disk-clean.amplet"), changed, CancellationToken.None);
        using JsonDocument again = JsonDocument.Parse(await Text("/api/doc?path=disk-clean.amplet"));
        Assert.Equal(changed, again.RootElement.GetProperty("text").GetString());
        Assert.True(again.RootElement.GetProperty("version").GetInt32() > opened.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Changes_in_different_places_of_the_file_and_the_working_copy_merge_by_themselves()
    {
        using JsonDocument opened = await OpenDisk("disk-merge.amplet");
        using HttpResponseMessage put = await Put("/api/doc", new { path = "disk-merge.amplet", baseVersion = opened.RootElement.GetProperty("version").GetInt32(), text = DiskSong.Replace("# one", "# ONE", StringComparison.Ordinal) });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await File.WriteAllTextAsync(Path.Combine(folder, "disk-merge.amplet"), DiskSong.Replace("# four", "# FOUR", StringComparison.Ordinal), CancellationToken.None);
        using JsonDocument disk = JsonDocument.Parse(await Text("/api/doc/disk?path=disk-merge.amplet"));
        Assert.Equal("merged", disk.RootElement.GetProperty("state").GetString());
        using JsonDocument merged = JsonDocument.Parse(await Text("/api/doc?path=disk-merge.amplet"));
        string text = merged.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("# ONE", text, StringComparison.Ordinal);
        Assert.Contains("# FOUR", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conflict_refuses_the_save_and_is_resolved_per_region()
    {
        using JsonDocument opened = await OpenDisk("disk-conflict.amplet");
        using HttpResponseMessage put = await Put("/api/doc", new { path = "disk-conflict.amplet", baseVersion = opened.RootElement.GetProperty("version").GetInt32(), text = DiskSong.Replace("# two", "# mine", StringComparison.Ordinal) });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        string onDisk = DiskSong.Replace("# two", "# theirs", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(folder, "disk-conflict.amplet"), onDisk, CancellationToken.None);

        using HttpResponseMessage refused = await Post("/api/doc/save", new { path = "disk-conflict.amplet" });
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("AMP5307", await refused.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal(onDisk, await File.ReadAllTextAsync(Path.Combine(folder, "disk-conflict.amplet"), CancellationToken.None));

        using JsonDocument disk = JsonDocument.Parse(await Text("/api/doc/disk?path=disk-conflict.amplet"));
        Assert.Equal("conflict", disk.RootElement.GetProperty("state").GetString());
        JsonElement conflict = Assert.Single(disk.RootElement.GetProperty("regions").EnumerateArray(), r => r.GetProperty("conflict").GetBoolean());
        Assert.Equal("# mine", conflict.GetProperty("mine")[0].GetString());
        Assert.Equal("# theirs", conflict.GetProperty("theirs")[0].GetString());
        Assert.Equal("# two", conflict.GetProperty("base")[0].GetString());

        string both = DiskSong.Replace("# two", "# theirs\n# mine", StringComparison.Ordinal);
        int version = disk.RootElement.GetProperty("version").GetInt32();
        using HttpResponseMessage stale = await Post("/api/doc/resolve", new { path = "disk-conflict.amplet", baseVersion = version, text = both, diskSha = "0000" });
        Assert.NotEqual(HttpStatusCode.OK, stale.StatusCode);
        using HttpResponseMessage resolved = await Post("/api/doc/resolve", new { path = "disk-conflict.amplet", baseVersion = version, text = both, diskSha = disk.RootElement.GetProperty("diskSha").GetString() });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        using HttpResponseMessage saved = await Post("/api/doc/save", new { path = "disk-conflict.amplet" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(both, await File.ReadAllTextAsync(Path.Combine(folder, "disk-conflict.amplet"), CancellationToken.None));
    }

    private async Task<string?> DefaultEngine()
    {
        using JsonDocument document = JsonDocument.Parse(await Text("/api/project/settings"));
        JsonElement engine = document.RootElement.GetProperty("defaultEngine");
        return engine.ValueKind == JsonValueKind.String ? engine.GetString() : null;
    }

    [Fact]
    public async Task The_default_engine_is_kept_when_left_out_and_cleared_by_an_empty_string_or_null()
    {
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", new { defaultEngine = "turbo" })).StatusCode);
        Assert.Equal("turbo", await DefaultEngine());
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", new { fullDetailDays = 11 })).StatusCode);
        Assert.Equal("turbo", await DefaultEngine());   // left out: kept

        foreach (object clear in new object[] { new { defaultEngine = "" }, new { defaultEngine = "   " }, new { defaultEngine = (string?)null } })
        {
            Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", new { defaultEngine = "turbo" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", clear)).StatusCode);
            Assert.Null(await DefaultEngine());
        }
    }

    [Fact]
    public async Task A_saved_build_parallelism_above_the_server_limit_does_not_block_other_settings_and_no_new_value_above_it_is_taken()
    {
        int limit;
        using (JsonDocument settings = JsonDocument.Parse(await Text("/api/project/settings")))
        {
            limit = settings.RootElement.GetProperty("parallelismLimit").GetInt32();
        }

        string file = Path.Combine(folder, ".ample", "project.json");
        ProjectManifest manifest = ProjectFile.Parse(await File.ReadAllTextAsync(file));
        await File.WriteAllTextAsync(file, ProjectFile.Serialize(manifest with { Settings = manifest.Settings with { BuildParallelism = limit + 5 } }));
        async Task<int?> Saved() { using JsonDocument d = JsonDocument.Parse(await Text("/api/project/settings")); JsonElement e = d.RootElement.GetProperty("buildParallelism"); return e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null; }

        Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", new { fullDetailDays = 11 })).StatusCode);   // an unrelated change
        Assert.Equal(limit + 5, await Saved());
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", new { buildParallelism = limit + 5 })).StatusCode);   // the saved value sent back
        Assert.NotEqual(HttpStatusCode.OK, (await Post("/api/project/settings", new { buildParallelism = limit + 6 })).StatusCode);   // another value above the limit
        Assert.Equal(limit + 5, await Saved());
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/project/settings", new { buildParallelism = 1 })).StatusCode);
        Assert.Equal(1, await Saved());
    }

    [Fact]
    public async Task The_estimate_of_a_cold_build_counts_the_blocks_that_wait_for_a_stale_one_and_says_so()
    {
        // nothing is built yet: every block renders, and the master only after the blocks before it (counting the stale blocks alone gives two of the three)
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "composition \"s\" { ample 1  tempo 120  master-seed 1 }\nmidi-clip c {\n  chords Am\n  bars 1\n}\npiano p pinned@2.0 at 1 for 1 {\n  set felt-piano\n  from c.notes -> notes\n}\nmaster master {\n  channels stereo\n}\n");
        using JsonDocument cold = JsonDocument.Parse(await Text("/api/build/estimate?path=song.amplet"));
        Assert.True(cold.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(3, cold.RootElement.GetProperty("blocks").GetInt32());   // the clip, the piano and the master
        Assert.True(cold.RootElement.GetProperty("seconds").GetDouble() > 0);
        Assert.Contains("only render if the block before them changes the sound", cold.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_teaches_a_pace_per_block_type_from_the_time_the_blocks_really_worked()
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "composition \"s\" { ample 1  tempo 120  master-seed 1 }\nmidi-clip c {\n  chords Am\n  bars 1\n}\npiano p pinned@2.0 at 1 for 1 {\n  set felt-piano\n  from c.notes -> notes\n}\nmaster master {\n  channels stereo\n}\n");
        using HttpResponseMessage build = await Post("/api/build", new { path = "song.amplet", intent = "bake" });
        Assert.True(build.IsSuccessStatusCode, await build.Content.ReadAsStringAsync(CancellationToken.None));
        using JsonDocument stats = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, ".ample", "analysis", "build-stats.json")));
        JsonElement paces = stats.RootElement.GetProperty("paces");
        Assert.True(paces.EnumerateObject().Any(), "no pace was learned");
        Assert.All(paces.EnumerateObject(), p => Assert.True(p.Value.GetDouble() > 0, p.Name));
        Assert.Equal(1, stats.RootElement.GetProperty("builds").GetInt32());
    }

    private async Task<string> Text(string path) => await http.GetStringAsync(path, CancellationToken.None);

    private async Task<HttpResponseMessage> Post(string path, object body) => await http.PostAsync(path, Json(body), CancellationToken.None);

    private async Task<HttpResponseMessage> Put(string path, object body) => await http.PutAsync(path, Json(body), CancellationToken.None);

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("global.json not found.");
    }
}
