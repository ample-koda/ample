using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ample.Server.Tests;

/// <summary>The assistant's sample-set tool: an audio file becomes a WAV of a set folder in the staged proposal, with loop points and an envelope written into the set's set.json.</summary>
public sealed class AiSampleSetAddTests : IDisposable
{
    private readonly string live = Path.Combine(Path.GetTempPath(), "ample-ai-set-" + Guid.NewGuid().ToString("N"));
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "ample-ai-set-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(live, true); Directory.Delete(workspace, true); }
        catch (IOException) { }
    }

    private AiTask NewTask(string kind = "comment")
    {
        AiTask task = new() { Request = new AiTaskRequest(kind, "file", "make a pad", "song.amplet", "song.amplet", null, null, null, null), Workspace = workspace };
        Directory.CreateDirectory(task.ProjectDir);
        FieldInfo tokens = typeof(AiService).GetField("ContextTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((ConcurrentDictionary<string, AiTask>)tokens.GetValue(null)!)[task.ContextToken] = task;
        return task;
    }

    private static byte[] Wav(int frames, double hz)
    {
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        int bytes = frames * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVEfmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(bytes);
        for (int i = 0; i < frames; i++) w.Write((short)(9000 * Math.Sin(2 * Math.PI * hz * i / 44100)));
        return stream.ToArray();
    }

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void A_project_wav_becomes_a_sample_of_the_set_with_its_loop_and_the_envelope_in_set_json()
    {
        Directory.CreateDirectory(Path.Combine(live, "raw"));
        File.WriteAllBytes(Path.Combine(live, "raw", "pad.wav"), Wav(88200, 220));
        AiTask task = NewTask();

        JsonObject answer = AiService.SampleSetAdd(task.ContextToken, Body(new { source = "raw/pad.wav", destination = "samples/campfire/c3.wav", loop = true, envelope = new { attack = 200, release = 800, sustain = 70 } }), live);

        Assert.True(File.Exists(Path.Combine(task.ProjectDir, "samples", "campfire", "c3.wav")));
        Assert.True(answer["loop"]!["found"]!.GetValue<bool>());
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(task.ProjectDir, "samples", "campfire", "set.json")));
        Assert.True(settings.RootElement.GetProperty("loops").GetProperty("c3").GetProperty("end").GetInt32() > settings.RootElement.GetProperty("loops").GetProperty("c3").GetProperty("start").GetInt32());
        Assert.Equal(200, settings.RootElement.GetProperty("envelope").GetProperty("attack").GetInt32());
        Assert.True(File.Exists(Path.Combine(live, "raw", "pad.wav")));
    }

    [Fact]
    public void A_second_sample_adds_its_own_loop_to_the_same_set_and_a_layer_uses_the_base_name()
    {
        Directory.CreateDirectory(Path.Combine(live, "raw"));
        File.WriteAllBytes(Path.Combine(live, "raw", "a.wav"), Wav(88200, 220));
        File.WriteAllBytes(Path.Combine(live, "raw", "b.wav"), Wav(88200, 330));
        AiTask task = NewTask();
        AiService.SampleSetAdd(task.ContextToken, Body(new { source = "raw/a.wav", destination = "samples/s/c3.wav", loop = true }), live);
        AiService.SampleSetAdd(task.ContextToken, Body(new { source = "raw/b.wav", destination = "samples/s/e3_v2_r1.wav", loop = true }), live);
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(task.ProjectDir, "samples", "s", "set.json")));
        JsonElement loops = settings.RootElement.GetProperty("loops");
        Assert.True(loops.TryGetProperty("c3", out _));
        Assert.True(loops.TryGetProperty("e3", out _), "a layer file loops under its base name");
    }

    [Fact]
    public void It_never_overwrites_wants_a_wav_in_a_folder_and_an_explanation_task_cannot_add_files()
    {
        Directory.CreateDirectory(Path.Combine(live, "raw"));
        File.WriteAllBytes(Path.Combine(live, "raw", "a.wav"), Wav(20000, 220));
        AiTask task = NewTask();
        AiService.SampleSetAdd(task.ContextToken, Body(new { source = "raw/a.wav", destination = "samples/s/c3.wav" }), live);
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => AiService.SampleSetAdd(task.ContextToken, Body(new { source = "raw/a.wav", destination = "samples/s/c3.wav" }), live));
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => AiService.SampleSetAdd(task.ContextToken, Body(new { source = "raw/a.wav", destination = "c3.wav" }), live));
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => AiService.SampleSetAdd(task.ContextToken, Body(new { source = "raw/a.wav", destination = "samples/s/c3.mp3" }), live));
        AiTask explain = NewTask("explain");
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => AiService.SampleSetAdd(explain.ContextToken, Body(new { source = "raw/a.wav", destination = "samples/s/d3.wav" }), live));
    }
}
