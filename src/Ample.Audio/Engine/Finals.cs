using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>One deliverable of a song: the whole song, an edit, a teaser.</summary>
/// <param name="Name">The name, also the end of the exported file name.</param>
/// <param name="FromBar">The first bar of the master that is kept (1 is the start).</param>
/// <param name="Bars">How many bars are kept (0: to the end of the song).</param>
/// <param name="FadeInBeats">A fade in at the start, in beats (0: none).</param>
/// <param name="FadeOutBars">A fade out at the end, in bars (0: none).</param>
/// <param name="Format"><c>mp3</c> or <c>wav</c>.</param>
public sealed record FinalSpec(string Name, int FromBar, int Bars, double FadeInBeats, double FadeOutBars, string Format);

/// <summary>A final and what its last render produced.</summary>
/// <param name="Spec">The definition.</param>
/// <param name="TakeId">The master take it was cut from, or null when it was never rendered.</param>
/// <param name="Path">The exported file (project relative), or null.</param>
/// <param name="Rendered">When it was rendered.</param>
/// <param name="Bytes">The size of the exported file.</param>
public sealed record FinalInfo(FinalSpec Spec, string? TakeId, string? Path, DateTimeOffset? Rendered, long Bytes);

/// <summary>
/// The finals of a project's songs, kept in <c>.ample/finals.json</c>. A song without any has one implicit final, <c>full</c> (the whole song).
/// The take a final was cut from is protected from clean-up (the storage rules read <see cref="TakeIds"/>).
/// </summary>
public sealed class FinalsStore(string projectRoot)
{
    private string FilePath => System.IO.Path.Combine(projectRoot, ".ample", "finals.json");

    /// <summary>Gets the implicit final of a song without any.</summary>
    public static FinalSpec Full { get; } = new("full", 1, 0, 0, 0, "mp3");

    /// <summary>Lists the finals of a song.</summary>
    public IReadOnlyList<FinalInfo> List(string song)
    {
        List<FinalInfo> finals = [.. Read().Where(x => x.Song == song).Select(x => x.Info)];
        return finals.Count == 0 ? [new FinalInfo(Full, null, null, null, 0)] : finals;
    }

    /// <summary>Adds a final or replaces the definition of the one with the same name (what was rendered before is kept).</summary>
    public void Upsert(string song, FinalSpec spec)
    {
        List<(string Song, FinalInfo Info)> all = Read();
        int index = all.FindIndex(x => x.Song == song && x.Info.Spec.Name == spec.Name);
        FinalInfo info = index >= 0 ? all[index].Info with { Spec = spec } : new FinalInfo(spec, null, null, null, 0);
        if (index >= 0)
        {
            all[index] = (song, info);
        }
        else
        {
            if (!all.Any(x => x.Song == song) && spec.Name != Full.Name)
            {
                all.Add((song, new FinalInfo(Full, null, null, null, 0)));
            }

            all.Add((song, info));
        }

        Write(all);
    }

    /// <summary>Removes a final (the exported file stays; it is the person's file).</summary>
    public bool Remove(string song, string name)
    {
        List<(string Song, FinalInfo Info)> all = Read();
        int removed = all.RemoveAll(x => x.Song == song && x.Info.Spec.Name == name);
        Write(all);
        return removed > 0;
    }

    /// <summary>Records a render of a final.</summary>
    public void Record(string song, FinalSpec spec, string takeId, string path, long bytes, DateTimeOffset when)
    {
        List<(string Song, FinalInfo Info)> all = Read();
        int index = all.FindIndex(x => x.Song == song && x.Info.Spec.Name == spec.Name);
        FinalInfo info = new(spec, takeId, path, when, bytes);
        if (index >= 0)
        {
            all[index] = (song, info);
        }
        else
        {
            all.Add((song, info));
        }

        Write(all);
    }

    /// <summary>Gets the take ids that finals were cut from (protected from clean-up).</summary>
    public IReadOnlyCollection<string> TakeIds() => [.. Read().Select(x => x.Info.TakeId).Where(id => id is { Length: > 0 }).Select(id => id!).Distinct(StringComparer.Ordinal)];

    /// <summary>Cuts the final out of a rendered master: the kept bars, with the fades.</summary>
    /// <param name="master">The whole master.</param>
    /// <param name="sampleRate">Its sample rate.</param>
    /// <param name="tempo">The tempo of the song.</param>
    /// <param name="spec">The final.</param>
    public static StereoBuffer Cut(StereoBuffer master, int sampleRate, double tempo, FinalSpec spec)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(spec);
        double beat = sampleRate * 60.0 / tempo;
        double bar = beat * 4;
        int start = (int)Math.Clamp(Math.Round((Math.Max(1, spec.FromBar) - 1) * bar), 0, master.Length);
        int end = spec.Bars <= 0 ? master.Length : (int)Math.Clamp(Math.Round(((Math.Max(1, spec.FromBar) - 1 + spec.Bars) * bar)), start, master.Length);
        int length = end - start;
        short[] left = new short[length], right = new short[length];
        Array.Copy(master.Left, start, left, 0, length);
        Array.Copy(master.Right, start, right, 0, length);
        int fadeIn = (int)Math.Min(length, Math.Round(spec.FadeInBeats * beat));
        int fadeOut = (int)Math.Min(length, Math.Round(spec.FadeOutBars * bar));
        for (int i = 0; i < fadeIn; i++)
        {
            double g = (double)i / fadeIn;
            left[i] = (short)(left[i] * g);
            right[i] = (short)(right[i] * g);
        }

        for (int i = 0; i < fadeOut; i++)
        {
            double g = (double)i / fadeOut;
            int at = length - 1 - i;
            left[at] = (short)(left[at] * g);
            right[at] = (short)(right[at] * g);
        }

        return new StereoBuffer(left, right);
    }

    private List<(string Song, FinalInfo Info)> Read()
    {
        List<(string, FinalInfo)> list = [];
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject root)
            {
                return list;
            }

            foreach (KeyValuePair<string, JsonNode?> song in root)
            {
                foreach (JsonNode? item in (song.Value as JsonArray) ?? [])
                {
                    if (item is not JsonObject o || (string?)o["name"] is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    FinalSpec spec = new(name, (int?)o["fromBar"] ?? 1, (int?)o["bars"] ?? 0, (double?)o["fadeInBeats"] ?? 0, (double?)o["fadeOutBars"] ?? 0, (string?)o["format"] == "wav" ? "wav" : "mp3");
                    DateTimeOffset? when = DateTimeOffset.TryParse((string?)o["rendered"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t) ? t : null;
                    list.Add((song.Key, new FinalInfo(spec, (string?)o["takeId"], (string?)o["path"], when, (long?)o["bytes"] ?? 0)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            // a damaged file is treated as no finals; the next save writes it again
        }

        return list;
    }

    private void Write(List<(string Song, FinalInfo Info)> all)
    {
        JsonObject root = [];
        foreach (IGrouping<string, (string Song, FinalInfo Info)> song in all.GroupBy(x => x.Song, StringComparer.Ordinal))
        {
            JsonArray array = [];
            foreach ((string _, FinalInfo info) in song)
            {
                array.Add(new JsonObject
                {
                    ["name"] = info.Spec.Name,
                    ["fromBar"] = info.Spec.FromBar,
                    ["bars"] = info.Spec.Bars,
                    ["fadeInBeats"] = info.Spec.FadeInBeats,
                    ["fadeOutBars"] = info.Spec.FadeOutBars,
                    ["format"] = info.Spec.Format,
                    ["takeId"] = info.TakeId,
                    ["path"] = info.Path,
                    ["rendered"] = info.Rendered?.ToString("O", CultureInfo.InvariantCulture),
                    ["bytes"] = info.Bytes,
                });
            }

            root[song.Key] = array;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + "\n");
        File.Move(temp, FilePath, true);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
