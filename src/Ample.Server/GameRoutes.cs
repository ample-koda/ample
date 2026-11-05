using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Ample.Server;

/// <summary>The routes a game uses to read its score and report its music state (<c>/api/game/*</c>); see <c>docs/game-score.md</c>.</summary>
internal static class GameRoutes
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Declares the game routes (and records them when there is no project, for the API catalog).</summary>
    /// <param name="add">Adds a route: method, path, tag, summary, whether it changes the project, handler.</param>
    /// <param name="s">The project, or null when only the catalog is wanted.</param>
    /// <param name="events">The event bus, or null.</param>
    internal static void Declare(Action<string, string, string, string, bool, Delegate?> add, ProjectSession? s, EventBus? events)
    {
        GameStateLog log = new();
        add("GET", "/api/game/score", "game", "The game score of the project: its checked definition in short (layers, states, variants), any problems, and which builds exist (query score= picks the file, default game/score.json).", false, s is null ? null : (HttpContext c) =>
            Results.Json(GameScoreApi.Describe(s, GameScoreApi.ScorePath(c.Request.Query["score"].ToString())), Web));
        add("POST", "/api/game/score/build", "game", "Builds the song of a game score (or one variant), cuts the loopable stems on whole bars and writes them with the manifest to renders/game/ (estimate and disk check first; one build at a time).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await ReadBody(c);
            return Results.Json(await GameScoreApi.BuildAsync(s, GameScoreApi.ScorePath(Opt(b, "score")), Opt(b, "variant"), (name, data) => events!.Publish(name, data), c.RequestAborted), Web);
        });
        add("GET", "/api/game/manifest", "game", "The manifest of a built game score (query id= the score name, variant= optional): tempo, loop, stems with hashes, states, transitions and provenance.", false, s is null ? null : (HttpContext c) =>
        {
            string id = c.Request.Query["id"].ToString();
            string? variant = c.Request.Query["variant"].ToString() is { Length: > 0 } v ? v : null;
            string text = GameScoreApi.ReadManifest(s, id, variant) ?? throw new AmpleException("AMP5243", "This score has not been built yet; build it first.");
            return Results.Text(text, "application/json");
        });
        add("GET", "/api/game/stems/{file}", "game", "A built stem, stinger (WAV, range requests supported) or manifest file of a game score.", false, s is null ? null : (string file) =>
        {
            string full = GameScoreApi.FilePath(s, file) ?? throw new AmpleException("AMP5243", "That file has not been built.");
            return Results.File(File.OpenRead(full), file.EndsWith(".wav", StringComparison.Ordinal) ? "audio/wav" : "application/json", enableRangeProcessing: true);
        });
        add("POST", "/api/game/state", "game", "A game reports a change of music state with the numbers behind it. Only remembered in memory and published as the event game.state; it never changes the project.", false, s is null ? null : async (HttpContext c) =>
        {
            GameStateEntry entry = log.Add(await ReadBody(c), s.Clock.UtcNow);
            events!.Publish("game.state", new { seq = entry.Seq, at = entry.At, session = entry.Session, state = entry.State, previous = entry.Previous, bar = entry.Bar, intensity = entry.Intensity, advantage = entry.Advantage, revision = entry.Revision, signals = entry.Signals });
            return Results.Json(new { ok = true, entry.Seq }, Web);
        });
        add("GET", "/api/game/state", "game", "The latest state reports of the game (query count=, default 50) and the most recent one.", false, s is null ? null : (HttpContext c) =>
        {
            int count = int.TryParse(c.Request.Query["count"].ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 50;
            return Results.Json(new { latest = log.Latest(), entries = log.Recent(count) }, Web);
        });
        add("POST", "/api/game/variant", "game", "Asks for a variant of the score to be built in the background (a body with score and variant). The result arrives as the event game.score.ready, or game.variant.failed with the reason.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await ReadBody(c);
            string scorePath = GameScoreApi.ScorePath(Opt(b, "score"));
            string variant = Opt(b, "variant") ?? throw new AmpleException("AMP5285", "The field 'variant' is required.");
            CheckVariant(s, scorePath, variant);
            _ = Task.Run(async () =>
            {
                try
                {
                    await GameScoreApi.BuildAsync(s, scorePath, variant, (name, data) => events!.Publish(name, data), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AmpleException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
                {
                    events!.Publish("game.variant.failed", new { score = scorePath, variant, reason = ex.Message });
                }
            });
            return Results.Json(new { accepted = true, score = scorePath, variant }, Web);
        });
    }

    /// <summary>Checks that a variant exists in the score before the background build starts, so a typo is an error of the request.</summary>
    private static void CheckVariant(ProjectSession s, string scorePath, string variant)
    {
        GameScoreDefinition definition = GameScoreApi.Load(s, scorePath, out _);
        if (variant != "default" && !definition.Variants.Any(v => v.Id == variant))
        {
            throw new AmpleException("AMP5285", $"The score has no variant '{variant}'.");
        }
    }

    private static async Task<JsonElement> ReadBody(HttpContext c) => await c.Request.ReadFromJsonAsync<JsonElement>(c.RequestAborted);

    private static string? Opt(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
