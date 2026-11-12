using System.Buffers.Binary;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ample.Cli;

/// <summary>
/// <c>ample mcp</c>: a task-scoped MCP server on stdio that gives an AI agent (Codex) AMPLE context and constrained project actions, including the approved visible-browser tools. It asks the running AMPLE server for
/// everything using a token scoped to one task.
/// </summary>
internal static class McpCommands
{
    private static readonly JsonObject NoArguments = new() { ["type"] = "object", ["properties"] = new JsonObject() };

    private static readonly JsonObject SampleSetAddSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source"] = new JsonObject { ["type"] = "string", ["description"] = "A project audio file, or workspace/<path> for a fetched preview" },
            ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "New project-relative .wav inside the set folder, named by note, for example samples/campfire/c4.wav" },
            ["loop"] = new JsonObject { ["type"] = "boolean", ["description"] = "Find loop points for this sample and write them into the folder's set.json" },
            ["envelope"] = new JsonObject { ["type"] = "object", ["description"] = "One ADSR for the whole set: attack, hold, decay, release in ms and sustain 0-100", ["properties"] = new JsonObject { ["attack"] = new JsonObject { ["type"] = "number" }, ["hold"] = new JsonObject { ["type"] = "number" }, ["decay"] = new JsonObject { ["type"] = "number" }, ["sustain"] = new JsonObject { ["type"] = "number" }, ["release"] = new JsonObject { ["type"] = "number" } } },
        },
        ["required"] = new JsonArray("source", "destination"),
        ["additionalProperties"] = false,
    };

    private static async Task ShowThoughtAsync(HttpClient http, string text, CancellationToken ct)
    {
        try { using HttpResponseMessage _ = await http.GetAsync("/api/ai/context/thought?text=" + Uri.EscapeDataString(text), ct).ConfigureAwait(false); }
        catch (HttpRequestException) { /* a hint is optional */ }
    }

    private static JsonObject Schema(params (string Name, string Description, bool Required)[] fields)
    {
        JsonObject properties = [];
        JsonArray required = [];
        foreach ((string name, string description, bool must) in fields)
        {
            properties[name] = new JsonObject { ["type"] = "string", ["description"] = description };
            if (must)
            {
                required.Add(name);
            }
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static JsonObject WithTrial(JsonObject schema)
    {
        JsonObject properties = (JsonObject)schema["properties"]!;
        properties["path"] = new JsonObject { ["type"] = "string", ["description"] = "The song to build and measure when it is a .amplet of your proposal (default: the song of the task; give it for a new song you made), and the file the trial edits apply to" };
        properties["edits"] = new JsonObject { ["type"] = "array", ["description"] = "Optional trial: edits in the format of ample_edit, applied only for this call (the proposal does not change), to hear a variant.", ["items"] = new JsonObject { ["type"] = "object" } };
        return schema;
    }

    private static readonly JsonObject BeatsSchema = WithQueries(Schema(("id", "One entry id for the full entry (optional)", false), ("parts", "With id: only these parts, comma separated: say, tempo, feel, tips, howToBringIn, fast, slow, drums, bass, chords, sections, pairsWith, avoid, genres, tags, mood (optional)", false), ("overview", "true: counts per family, genre, mood, form and novelty, to aim a search (optional)", false), ("similar", "An entry id: the entries most like it (optional)", false), ("genre", "Genre or tag, for example house, funk, lofi, singer-songwriter, rap, country (optional)", false), ("family", "Instrument/style family: house, hiphop, lofi, trap, rock, funk, disco, reggae, dnb, latin, afro, jazz, blues, ambient, techno, country, ballad, reggaeton (optional)", false), ("mood", "Mood word, for example dark, relaxed, tender (optional)", false), ("form", "loop (one bar), phrase (2-4 bars with a fill) or arrangement (8-32 bars with sections: a LONG entry); omit for all (optional)", false), ("kind", "drum-groove or groove-set (optional)", false), ("bpm", "Your tempo; also matches half and double time (optional)", false), ("maxNovelty", "1-5: at most this rare; use 2 for a background bed (optional)", false), ("minNovelty", "1-5: at least this rare (optional)", false), ("energy", "1-5, matches within one step (optional)", false), ("text", "Words that must all occur somewhere in the entry (optional)", false), ("sort", "tempo, novelty, energy or name (default: closest to bpm) (optional)", false), ("limit", "How many summaries, 1-40 (default 12) (optional)", false), ("offset", "Skip this many, to page on (optional)", false)));

    private static readonly string[] GenerateKeys = ["kind", "style", "steps", "hits", "rotate", "variation", "swing", "chord", "progression", "pattern", "octave", "octaves", "rate", "barsPerChord", "rhythm", "voices", "seed"];

    private static readonly string[] BeatsKeys = ["id", "parts", "overview", "similar", "genre", "family", "mood", "form", "kind", "bpm", "maxNovelty", "minNovelty", "energy", "text", "sort", "limit", "offset"];

    private static JsonObject WithQueries(JsonObject schema)
    {
        JsonObject properties = (JsonObject)schema["properties"]!;
        properties["queries"] = new JsonObject { ["type"] = "array", ["description"] = "Several searches in ONE call (at most 8): a list of query objects with the same fields as above (genre, family, mood, form, bpm, maxNovelty, energy, text, limit, similar, id, parts). The answers come back in the same order.", ["items"] = new JsonObject { ["type"] = "object" } };
        return schema;
    }

    private static readonly JsonObject ListenSchema = WithTrial(Schema(("block", "The block to measure (empty: the master)", false), ("with", "Analysers, comma separated (default loudness,spectrum)", false)));
    private static readonly JsonObject BuildSchema = WithTrial(Schema(("block", "A block to build (empty: the whole song)", false)));

    static McpCommands()
    {
        ((JsonObject)BuildSchema["properties"]!)["forceRender"] = new JsonObject { ["type"] = "boolean", ["description"] = "For one named ACE song/voice block, bypass AMPLE and Faer caches and run the full renderer now." };
        ((JsonObject)BatchSchema["properties"]!)["thinking"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: one short sentence on why you make this batch; shown to the user as a hint." };
    }

    // an empty answer (for example a refusal without a body) must not look like success with no content
    private static string Visible(string body, HttpResponseMessage response) => string.IsNullOrWhiteSpace(body) ? (response.IsSuccessStatusCode ? "(AMPLE answered with no text)" : $"AMPLE answered {(int)response.StatusCode} {response.ReasonPhrase} with no text: the task may have ended or the request was refused.") : body;

    // every field an edit can take, so a client that only sends what the schema lists still sends 'old', 'new', 'block' and the rest
    private static JsonObject EditItemSchema
    {
        get
        {
            JsonObject properties = new() { ["op"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("replace", "insert", "delete_lines", "set_param", "note_transform", "note_add", "note_remove", "add_block", "remove_block", "set_flag", "sequence_row", "sequence_channel", "connect", "disconnect", "create") } };
            foreach (string name in new[] { "old", "new", "after", "before", "text", "block", "name", "after_block", "from_block", "from_output", "to_block", "to_input", "action", "note", "flag", "pattern", "channel" })
            {
                properties[name] = new JsonObject { ["type"] = "string", ["description"] = "Used by some ops (see the tool description)." };
            }

            foreach (string name in new[] { "at_line", "from", "to", "occurrence", "amount", "grid", "bar", "beat" })
            {
                properties[name] = new JsonObject { ["type"] = "number", ["description"] = "Used by some ops (see the tool description)." };
            }

            properties["all"] = new JsonObject { ["type"] = "boolean", ["description"] = "replace/note_remove: change every match." };
            properties["on"] = new JsonObject { ["type"] = "boolean", ["description"] = "set_flag: true (default) puts the flag on, false takes it off." };
            properties["value"] = new JsonObject { ["description"] = "set_param: the new value (text or number)." };
            properties["pitch"] = new JsonObject { ["description"] = "note_remove: the pitch to match (number or name)." };
            return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("op"), ["additionalProperties"] = true };
        }
    }

    private static readonly JsonObject EditSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file, for example song.amplet (default: the song of the task)" },
            ["why"] = new JsonObject { ["type"] = "string", ["description"] = "Optional concise reason for changing this file, shown to the user with the file's diff." },
            ["edits"] = new JsonObject { ["type"] = "array", ["description"] = "The edits, in order.", ["items"] = EditItemSchema },
        },
        ["required"] = new JsonArray("edits"),
    };

    private static readonly JsonObject VariantsSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["variants"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The alternatives, 2 to 6.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string", ["description"] = "A short name, for example Warm piano" },
                        ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "One line about what makes it different" },
                        ["blocks"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "The names of the blocks that belong to this variant (added or changed for it)" },
                        ["listen"] = new JsonObject { ["type"] = "string", ["description"] = "The block to listen to for this variant: audible at normal level, never muted or turned down (default: the last of blocks)" },
                    },
                    ["required"] = new JsonArray("name", "blocks"),
                },
            },
        },
        ["required"] = new JsonArray("variants"),
    };

    private static readonly JsonObject BatchSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["calls"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The calls, in order.",
                ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["type"] = "string" }, ["arguments"] = new JsonObject { ["type"] = "object" }, ["thinking"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: one short sentence on why you make this call (also accepted as reason, description or action). It is shown to the user as a hint and echoed in the result." } }, ["required"] = new JsonArray("tool") },
            },
        },
        ["required"] = new JsonArray("calls"),
    };


    private static readonly (string Name, string Description, JsonObject Schema)[] Tools =
    [
        ("ample_project", "What the project is: its name, tempo, master seed, linked sample-pack catalogs (global and project-scoped, never truncated away) and a page of its files. The files page defaults to the first 300; when filesTruncated is true in the answer, narrow with filesQuery (a plain substring/grep on the path), filesKind (text or binary) and/or filesExt (for example wav), or page further with filesOffset. For Codex tasks, taskWorkFiles is a separate read-only list from the assistant work folder. Pass location=work on a path-based read call to select one; location=project selects project files. Start here.", Schema(("filesQuery", "Case-insensitive substring to match in file paths (a plain grep)", false), ("filesKind", "text or binary", false), ("filesExt", "File extension to keep, e.g. wav or .wav", false), ("filesOffset", "How many matching files to skip (paging), default 0", false), ("filesLimit", "Max files to return, default 300, max 2000", false))),
        ("ample_random_seed", "Return OS-cryptographic unsigned 64-bit random seeds for project master-seed and block seed/lm_seed fields. Use blockSeed for block seeds; do not invent random numbers.", NoArguments),
        ("ample_faer_status", "Read the independent song, voice and stem-separation routes for this project and live machine-local Faer readiness, including the installed ACE model filenames, roles and sizes. For model specialties and when to choose them, read the ACE model catalog with ample_docs. Keep the hardcoded synthModel families and combine them with server-offered files. For a quality-first request, start with the catalog's highest-precision compatible file listed by Faer through synthModelFile (currently XL SFT BF16 when present), even if ACE /props omits it; on failure, report the raw reason and retry explicitly with XL SFT Q5. Never invent a model file or silently substitute one. Status excludes credentials and job content. Routes set to local stay local.", NoArguments),
        ("ample_docs", "The AMPLE reference documents. Without arguments: the list. With name: one document (for example 'language/03-syntax' or 'sequencer'). With query: the places where a word is found.", Schema(("name", "The document to read", false), ("query", "A word or phrase to search for", false))),
        ("ample_block_types", "The block types of AMPLE. Without type: all of them in short. With type: its ports, parameters, defaults and what they do.", Schema(("type", "A block type, for example reverb or sequencer", false))),
        ("ample_song", "A song: its project line, every block with its settings, how they are connected, the comments above them, and the problems the parser finds. This is the live working copy, including changes that are not saved. Choose location=work for a song in the separate assistant work folder.", Schema(("path", "The song file, for example song.amplet", true), ("location", "project (default) or work, the separate assistant work folder", false))),
        ("ample_block", "One block of a song with its text, what feeds it and what it feeds. Choose location=work for a song in the separate assistant work folder.", Schema(("path", "The song file", true), ("name", "The block name", true), ("location", "project (default) or work, the separate assistant work folder", false))),
        ("ample_analyze", "Measure what a block of the CURRENT project sounds like (not your proposal: use ample_listen for that), from its built audio: with=tempo (BPM, confidence, beat grid), key (key and mode), loudness (LUFS, true peak, range), spectrum (brightness and band shares) or pitch (the pitch contour). The song must have been built.", Schema(("path", "The song file", true), ("block", "The block whose audio to measure", true), ("with", "tempo, key, loudness, spectrum or pitch", true))),
        ("ample_request_asset", "Ask the user for permission to read a large or non-text source asset of the project (a sample, a recording). When they allow it AMPLE copies the file into your working folder and tells you where. Always give the reason.", Schema(("path", "The project path of the asset", true), ("reason", "Why you need to read it", true))),
        ("ample_edit", "Change a file of your proposal with structured edits (never with shell text replacement or regular expressions). edits is a list, applied in order, all or nothing: {op:replace, old, new, all?}; {op:insert, after|before|at_line, text}; {op:delete_lines, from, to}; {op:set_param, block, name, value}; {op:note_transform, block, action:transpose|velocity|quantize, amount, grid?, bar?, pitch?}; {op:note_add, block, note:bar:beat:length:pitch:velocity}; {op:note_remove, block, bar?, pitch?, beat?, all?} provide targeted note editing in compact structured midi-clip notes; transpose is in semitones, velocity is a delta, quantize and beat are in beats; filters can select bar and pitch. Expanded named note bodies are not supported by these note operations yet. Other edits: {op:add_block, text, after_block?}; {op:remove_block, block}; {op:set_flag, block, flag:disabled|lock|bake|refresh, on?:true|false} (to DISABLE a block use flag disabled: the block stays visible but returns no take; on:false enables it again; this is the block header flag, not the enabled parameter of effects); {op:connect|disconnect, from_block, from_output, to_block, to_input}; {op:create, text}. Optionally give why with a concise reason for changing this file. The answer lists the new errors and warnings and a diff.", EditSchema),
        ("ample_fetch_url", "Direct downloads use this approval-gated tool: give the address (a link from the user's request is fetched at once, another one needs the user's approval) and AMPLE downloads it into assets/web/ of your working folder. For text, SVG and JSON the content comes back in the answer. Public web addresses only; images, text, audio, JSON, XML and PDF up to 10 MB. For live page browsing use the visible ample_browser_* tools.", Schema(("url", "The address to fetch", true), ("reason", "Why you need it", false))),
        ("ample_install_pack", "Download a .zip sample-pack archive from a public web address and install it into the user's library (packs/<name>@<version>), available to every project from then on — check ample_project's catalogs afterward. This ALWAYS needs the user's approval, even when the link was given in the request, because it changes the shared library, not just this task. The archive must contain a pack.json at its root or inside one wrapping folder; at most 200 MB.", Schema(("url", "The address of the .zip pack archive", true), ("reason", "Why you need this pack", false))),
        ("ample_browser_open", "Open a URL in AMPLE's persistent isolated Chromium browser, headless by default. The owner must approve browser access once per AI task. Detected sign-in/CAPTCHA states automatically open a visible window for manual input; use ample_browser_show for an unexpected state. The owner enters all credentials manually; never ask them to share credentials. Numbered purchase-related clicks are blocked; ample_browser_js can bypass this and submit arbitrary page actions. Supply url and a short reason.", Schema(("url", "The http(s) page to open", true), ("reason", "Why this page is needed", true))),
        ("ample_browser_show", "Bring the isolated AMPLE browser to the foreground when an unexpected page state needs human inspection. It runs headless by default and is automatically shown for detected sign-in or CAPTCHA steps. Browser access must already be approved for this task. Supply a short reason.", Schema(("reason", "Why human inspection is needed", true))),
        ("ample_browser_read", "Read the current browser page title and readable text, and get a numbered list of visible links, buttons and safe text fields. The browser runs headless unless manual input is needed. Use the returned indexes with ample_browser_click or ample_browser_type; do not use selectors. Browser access needs approval in each task.", NoArguments),
        ("ample_browser_click", "Click one numbered visible link or button from the latest ample_browser_read in the isolated browser, which is headless by default. Purchase-related actions are blocked; if matched, the owner must do it in the visible window. Audio preview links return their URL only; use ample_fetch_url for its separate download approval. Never click purchase actions.", Schema(("index", "The numbered element from the latest ample_browser_read", true))),
        ("ample_web_search", "Choose DuckDuckGo Instant Answers or Brave full search. Each search requires approval. Brave is capped at 10 actual API calls per Codex turn, one per second, and AMPLE estimates use against 1,000 requests per UTC month per API key using its own persisted request count; this is not the provider balance and does not count calls made elsewhere. Identical Brave query, count and offset responses are reused from a five-minute memory-only cache; Brave results are not persisted to disk. Brave offset is 0-9; use moreResultsAvailable before paging.", Schema(("query", "A concise public web search query", true), ("limit", "Maximum number of results (1-10; default 5)", false), ("offset", "Brave result page offset, 0-9 (default 0)", false), ("provider", "duckduckgo (free Instant Answers) or brave (full results, API key required)", false))),
        ("ample_web_read", "Fetch a user-supplied URL or a URL returned by this task's approved web search, without cookies or JavaScript and without another approval. Returns page title, extracted text and links; mode=html returns at most 40,000 raw HTML characters, mode=links returns links only. Public HTML only, 2 MB response cap, redirects remain restricted to public internet. If JavaScript, sign-in or CAPTCHA is required, continue with ample_browser_open after its browser approval.", Schema(("url", "A URL in the user's request or a result from this task's approved web search", true), ("mode", "text (default), links, or html", false))),
        ("ample_browser_type", "Fill a numbered non-credential text field from the latest ample_browser_read, such as a search field. Credential fields are never exposed or filled. Supply index and text.", Schema(("index", "The numbered safe text field from the latest ample_browser_read", true), ("text", "Search or other non-credential text", true))),
        ("ample_browser_mouse_move", "Move the pointer within the isolated page viewport for hover menus. It does not click. The browser is headless unless manual inspection is needed.", Schema(("x", "Horizontal viewport coordinate in pixels", true), ("y", "Vertical viewport coordinate in pixels", true))),
        ("ample_browser_media_mute", "Mute or unmute HTML audio/video elements on the current visible page. This controls media playback only, not browser tab or system audio.", Schema(("muted", "true to mute page audio/video, false to restore it", true))),
        ("ample_browser_screenshot", "Capture the current isolated browser page screenshot for visual inspection. The image is returned to Codex and is not saved as a download; it can capture a headless page too.", NoArguments),
        ("ample_browser_network_stats", "Read recent console messages with common credential-like values redacted and URLs reduced to origin, aggregate request/response/status/resource counts, and URLs only for responses identified as audio. Audio preview URLs can be passed to ample_fetch_url for its separate approval. No other network URLs, headers or response bodies.", NoArguments),
        ("ample_browser_js", "Execute JavaScript in the current isolated browser page, headless by default. This can change page state, submit forms, initiate purchases or bypass numbered-click purchase blocking. Use only when the owner explicitly requests the script; never read or return credentials, tokens or cookies. Supply a JavaScript expression as script.", Schema(("script", "The JavaScript expression to evaluate in the current page", true))),
        ("ample_present", "Let the user HEAR something: the render of a block (empty: the master), whole or a slice (from and to in seconds, or in bars with unit=bars), as your proposal has it (source=proposal, the default) or as the current project is (source=current). It shows up in the task window as a card with a player, with your label and why. Use it when you are not sure which instrument or part the user means (present the candidates), to let them compare before and after, or to let them check a result. It reports the take id, plan id and whether the current take is frozen. If a frozen current take could hide the requested change, or the sample does not match the expected scope, autonomously call again with source=current and rebuildCurrent=true; compare the returned take id, plan id and audio identity to diagnose the mismatch, then continue correcting the proposal without asking the user to identify cache or take ids. This renders current song text in a temporary project, bypassing selected takes while preserving pinned melody choices and reusing exact-plan audio from the active project's read-only takebank cache when available. It never changes the live project or takebank. Clearly label it as a reconstructed current-source render. Optional edits (ample_edit format, with optional path) create a temporary trial without changing the proposal. Labels and why do not alter sound. Check settings and identicalTo in the result; never claim identical clips sound different. It is remembered in the history.", WithTrial(Schema(("block", "The block to render (empty: the master)", false), ("from", "Start (seconds, or bar with unit=bars)", false), ("to", "End (seconds, or the last bar with unit=bars)", false), ("unit", "seconds (default) or bars", false), ("label", "A short name for the card", false), ("why", "What you want them to notice", false), ("source", "proposal or current", false), ("rebuildCurrent", "For source=current only: render current song text temporarily, bypassing selected takes while preserving pinned melody choices", false)))),
        ("ample_obsolete_sample", "Mark a previously presented sample obsolete when later proposal edits mean it no longer represents the current work. Give the sample clipId and a concise reason. It remains visible, dimmed, until the user dismisses it.", Schema(("clipId", "The clipId returned by ample_present", true), ("reason", "Why this sample is no longer relevant", true))),
        ("ample_changes_since", "For a resumed conversation: what the project did since your working copy was based on it. No arguments: the files of your copy that are changed or removed in the project. path=<file>: the diff of that file (your copy's base to the project now). path with import=true: brings the project's version into your copy (merged with your own edits; a clash is reported, never guessed). Use it when the AMPLE NOTE says the conversation was resumed, and only for the files the new request needs.", Schema(("path", "The file (project-relative)", false), ("import", "true to bring the project's version into your copy", false))),
        ("ample_revision", "A saved point (revision) of the project history, for when the user points at one in the History tab. id=<revision id, a branch or a tag>: what that revision changed against the point before it (files, message, time). Add path=<file> for the diff of that file. A branch name such as garage is a line of the history, not a block of the song.", Schema(("id", "The revision id (or its first characters), a branch or a tag", true), ("path", "A file of the revision, for its diff", false))),
        ("ample_answer", "Deliver the answer to a question from the user (Koda's 'what does this do?', 'how do these fit?'). title: 3 to 6 words. answer: at most three sentences. details: markdown the user can unfold, written as ordinary explanation about the IMPACT of each thing (bullets, with the name in code), a final heading Summary with a more detailed account, and a small markdown table where facts line up. Deliver it with this tool and do not repeat it in your final message. action: ONE short sentence saying what the Do that button would do if the user pressed it (for example 'Add a 2 s low-pass filter on scratch_room'); give it whenever your answer suggests a concrete change to the song (and never tell the user to press Do that without it: the button only exists when action is given), and leave it out when the answer only explains (then the button is not shown).", Schema(("answer", "The short answer, at most three sentences", true), ("title", "A title of 3 to 6 words", false), ("details", "Markdown for the part the user unfolds", false), ("action", "One short sentence: what the Do that button would do; leave out when the answer only explains", false))),
        ("ample_history", "What the user asked before in this project and what came out of it, newest first: the request, the result, whether it was accepted, the files it changed. id=<id> gives one request in full with its changes. Look here when the user refers to earlier work ('like before', 'continue', 'undo that last thing', 'the slowdown we made') instead of saying you do not know.", Schema(("id", "One earlier request, in full", false), ("song", "Only requests about this song", false), ("query", "A word in the request or the result", false), ("limit", "How many (default 8)", false))),
        ("ample_redux_search", "Read-only search in AMPLE Redux. It defaults to this task's archive; set scope=session for this Codex session or scope=project to expand to project history. Filter by AR, task/session, path, block, field, time or text; keep filters and scope unchanged while following nextCursor. Payload and pre-image contents are omitted unless includePayload=true. Results are capped at 200 per page.", Schema(("scope", "task (default), session, or project", false), ("ar", "AR-1 or canonical AR-0000000000000001", false), ("task", "Task id", false), ("session", "Assistant session id", false), ("path", "Project-relative file path", false), ("block", "Block name or stable block id", false), ("field", "Parameter or field name", false), ("from", "Inclusive UTC timestamp", false), ("to", "Inclusive UTC timestamp", false), ("q", "Case-insensitive text search", false), ("limit", "Page size, default 50, maximum 200", false), ("cursor", "The nextCursor returned by the previous page", false), ("includePayload", "true to include recorded payload and pre-image text", false))),
        ("ample_offered", "The audio files the user offered you as material (dropped, picked or recorded): for each one the length, sample rate, peak and average level, silence at the ends, the pitch when it has one clear note, the path to use in the song (projectPath, like samples/offered/kick.wav) and whether your proposal already uses it. They only come along into the project when the user chooses so at Accept: say which ones you used.", NoArguments),
        ("ample_hum", "When the user hummed a melody for you (the raw audio is in assets/hum/<id>.wav): the melody AMPLE found in it, as notes with pitch names, the key, and the notes text of a midi-clip at the tempo of the song. It is an attempt: check it against the request. Options re-run the finding: tempo (BPM), grid (beats: 0.25 sixteenth, 0.5 eighth, 1 beat), key (auto, or like 'A minor': the notes snap to it), bar (the bar the first beat is placed at).", Schema(("id", "The recording (default: the last attached)", false), ("tempo", "BPM to count in", false), ("grid", "Snap in beats", false), ("key", "auto or a key", false), ("bar", "Start bar", false))),
        ("ample_arrangement", "Check that the goal is reached: where every block that reaches the master sits (bars and seconds), the slice of the master and the length that comes out, before your task and now, with what changed. measure=true builds your proposal and gives the real length of the master in seconds, its peak (dBFS) and the silence at the start and end, next to the same facts of the current project.", Schema(("measure", "true to build and measure the real master", false), ("path", "A song of your proposal (default: the song of the task): give it for a new song you made", false))),
        ("ample_lookup", "Ask for more information when you need it. topic=units: what dB, %, ms, s, Hz, bars, LUFS mean, how time and seeds work, and how 'x% quieter' converts to dB. topic=parameter, name=type.parameter (for example reverb.mix): its unit, range, default, how to write it and what a change reaches. topic=impact, name=<block>: what feeds it, what it feeds (what is rendered again when you change it), the files it reads, whether it reaches the master. topic=search, query=<word>: which block types, parameters and documents mention it. Without a topic: the list of topics.", Schema(("topic", "units, parameter, impact or search", false), ("name", "type.parameter, or a block of the song", false), ("query", "a word to search for", false))),
        ("ample_build", "Build your proposal in a temporary project and say whether it builds. When it does not, you get why for every block that failed: the message and its code, a hint from the documentation, what feeds the block and its text (and for errors in the song, the lines with hints). block limits it to one block and what it needs. Use forceRender=true with a named ACE song/voice block only when the user asks for a genuine full render; it bypasses AMPLE and Faer caches.", BuildSchema),
        ("ample_listen", "You cannot hear, so measure: AMPLE builds your proposal in a temporary project and analyses a block (or the master when block is empty) with with=loudness,spectrum (default) or tempo, key, pitch, next to the same block of the current project, so you can check that a change sounds like what was asked (for example 20% quieter is about -2 dB). It can take half a minute the first time; use it for changes that can be heard.", ListenSchema),
        ("ample_check", "The errors and warnings that your proposal introduced (the ones the song did not have before). Call it when you are done editing: if it lists problems, fix them and call it again; you are finished when it says ok. path is the song (default: the song of the task).", Schema(("path", "The song file (optional)", false))),
        ("ample_skip_build", "Honor an explicit user request not to build or render. Give a short reason; AMPLE will still validate the source and let the user accept without a build.", Schema(("reason", "Why the user asked to skip building and rendering", true))),
        ("ample_batch", "Many read calls in ONE request, much faster than one by one. calls is a list of {tool, arguments} (for example [{\"tool\":\"ample_project\"},{\"tool\":\"ample_docs\",\"arguments\":{\"name\":\"sequencer\"}},{\"tool\":\"ample_song\",\"arguments\":{\"path\":\"song.amplet\"}}]). Any tool except ample_batch and ample_request_asset; at most 12 calls; the answers come back in the same order.", BatchSchema),
        ("ample_offer_variants", "Offer the user 2 to 6 alternatives of one thing (for example three instruments for a phrase, or other beats), all made in this one proposal. Make every alternative as its own blocks under its own names, keep the original, then call this once. For each variant give name, summary (one line), blocks (the names of the blocks that belong to it) and listen (the block to hear it from: the end of its chain, at normal level, so the user hears it as it would sound when kept; never a muted or turned-down block). The user sees them side by side, hears each, and keeps one.", VariantsSchema),
        ("ample_thinking", "Show the user what you are thinking about right now, in one or two short sentences (for example 'Comparing the loop tempo with the project tempo'). It replaces the previous thought; call it when you start something new. Every other tool also accepts an optional 'thinking' argument that does the same.", Schema(("thinking", "What you are thinking about, in one or two short sentences", true))),
        ("ample_memory_read", "Read the notes AMPLE keeps for later tasks, outside the song: scope=project (taste, agreements and decisions of the whole project) or scope=song (what this song is for, what was tried). Read both at the start of a task; they are not part of the song or of your proposal.", Schema(("scope", "project or song", true))),
        ("ample_memory_write", "Keep a short note for later tasks, outside the song: scope=project or song; mode=append (default, one durable fact, dated by AMPLE) or replace (rewrite the whole memory to tidy or shorten it). Write what stays true: the owner's taste, decisions and why, what was tried and did not work. Never chat, audio data or what the proposal already says. At most about 24000 characters per memory.", Schema(("scope", "project or song", true), ("text", "The note, or with mode=replace the whole new memory", true), ("mode", "append (default) or replace", false))),
        ("ample_move_file", "Move a text file in the staged proposal without overwriting. source and destination are project-relative; source workspace/filename recovers a misplaced workspace-root text file. No live project changes until Accept. Run ample_check afterwards.", Schema(("source", "Existing text file", true), ("destination", "New project-relative path", true))),
        ("ample_find_samples", "Find real, legal samples yourself (a DJ scratch, a vinyl crackle, a foley sound): AMPLE reads the CC0 search of Freesound and the public-domain search of the Internet Archive without a sign-in or a browser, reads every candidate page and checks its licence, and answers with candidates (title, page, file, licence) that you can hand straight to ample_fetch_sample. text=words (dj scratch, record scratch, turntable, vinyl crackle), source=freesound (default), archive or all, limit up to 8. Read-only. Use this before browsing: it needs no browser, no sign-in and no guessing of links.", Schema(("text", "The words of the sound, for example dj scratch", true), ("source", "freesound (default), archive or all", false), ("limit", "How many candidates, 1 to 8", false))),
        ("ample_fetch_sample", "Fetch a real sample into the proposal yourself: give page (the page of the sound, on freesound.org, archive.org or opengameart.org), file (the address of the audio file: wav, mp3, ogg, flac or aif, at most 25 MB) and a title. AMPLE reads the page itself and only a CC0 or public-domain licence lets the file through (another licence is refused, so you do not have to ask the owner); no approval is asked. The answer has the path in assets/web and a ready SOURCE.md text: then ample_analyze_audio, ample_sample_set_add (or a sampler block), and write SOURCE.md next to the sample. Find candidates with ample_sample_sources (where to look and what to search for), ample_web_search and ample_web_read (the page and its file link). If the page or the file needs a sign-in (login only, no licence shown, 401 or 403), call it again with browser=true after the owner signed in: ample_browser_open the sign-in page (AMPLE shows its browser window by itself when a person has to act), tell the owner to sign in there and to say when it is done; the session stays in the browser profile of AMPLE. Never buy anything with it.", Schema(("page", "The page of the sound (freesound.org, archive.org or opengameart.org)", true), ("file", "The address of the audio file", true), ("title", "A short name for the sample", true), ("browser", "true: use the signed-in browser of AMPLE (for a sound that needs a sign-in)", false))),
        ("ample_sample_sources", "Where real, legal samples can be found (a DJ scratch, a vinyl crackle, a foley sound, a riser) and how they come into a project correctly. Use it when the owner needs a sound AMPLE cannot make itself, instead of guessing a source: it gives the rules (CC0 or public domain by default, CC BY only with the credit written down, never unclear or non-commercial, the source written down next to the file, nothing bought without asking) and the places with their licence (Freesound with the CC0 filter, OpenGameArt, AMPLE's own audio packs, the Internet Archive). You cannot browse: give the owner the place, the search words and the licence filter. text=words (for example dj scratch) puts the places that fit first.", Schema(("text", "Words for the sound, for example dj scratch or vinyl crackle", false))),
        ("ample_beats", "The beats catalogue (160+ original, free-to-use grooves: loops, 2-4 bar phrases and LONG 8-32 bar arrangements with sections) so you need not invent a beat from nothing. It is big, so explore in small steps: (1) overview=true gives the counts per family, genre, mood, form and novelty; (2) search with genre/family/mood/form/bpm/energy/maxNovelty/text, answers are short summaries (limit, offset to page); (3) similar=<id> for neighbours; (4) id=<id> for one entry, parts=say,drums,tips,howToBringIn,pairsWith,sections to read only what you need. To explore many directions in ONE call give queries: a list of such query objects (up to 8). novelty 1 = common, safe as a background bed; 5 = rare, use sparingly. An entry holds a human say form, tempo advice (also fast/slow), tips, how to bring it in, drum grids and named patterns you can paste into a drums block (16 steps per bar), bass and chord ideas, the KINDS of instruments that suit it (not just guitar but what kind, GM program numbers, role, register) and what to avoid.", BeatsSchema),
        ("ample_analyze_audio", "A deterministic look at one audio file (no AI, a second or two): its length, level, brightness, the pitch of a single note (and the file name it should get in a sample set), the likely key, the tempo and bars, and whether it is a loop of whole bars, with a confidence for each and advice on how to use it (cut, stretch, name, place). Use it on a fetched sample (workspace/<path>) or any audio file of the project before you place it in a song. All values are estimates: trust pitch above 0.85, tempo above 0.5 and key above 0.3, and listen otherwise.", Schema(("source", "An audio file of the project, or workspace/<path> for a fetched preview (wav, mp3, flac, ogg, aiff, m4a)", true))),
        ("ample_generate", "Small deterministic generators (the same query and seed always give the same answer; no AI). kind=euclid: steps, hits, rotate: a euclidean rhythm grid. kind=groove: style (fourfloor, backbeat, halftime, boombap, shuffle, breakbeat, reggae, funk, dembow), variation 0-1, swing, seed: kick, snare and hat grids and a drums block to paste. kind=arpeggio: chord or progression (Am F C G), pattern (up, down, updown, broken, thirds, random), octave, octaves, rate, barsPerChord: melody text and a midi-clip. kind=bassline: progression, rhythm (16 steps), style (root, rootfifth, octave, walking), octave: melody text and a midi-clip. kind=fill: style (snare-roll, tom-run, kick-snare, hat-roll), steps: grids for the last bar of a section. Without kind it lists what exists.", Schema(("kind", "euclid, groove, arpeggio, bassline or fill (empty: a list of what exists)", false), ("style", "groove: fourfloor, backbeat, halftime, boombap, shuffle, breakbeat, reggae, funk, dembow; bassline: root, rootfifth, octave, walking; fill: snare-roll, tom-run, kick-snare, hat-roll", false), ("steps", "euclid: grid length (default 16); fill: how many of the last steps, 4, 8 or 16", false), ("hits", "euclid: number of hits", false), ("rotate", "euclid: rotate the grid by this many steps", false), ("variation", "groove: 0-1, how many ghost notes and extra kicks", false), ("swing", "groove: 0-60 percent (the style has a default)", false), ("chord", "arpeggio or bassline: one chord, for example Am or F#m7", false), ("progression", "arpeggio or bassline: chords separated by spaces, one bar each (Am F C G)", false), ("pattern", "arpeggio: up, down, updown, broken, thirds or random", false), ("octave", "root octave (arpeggio 3, bassline 2)", false), ("octaves", "arpeggio: 1-3 octaves", false), ("rate", "arpeggio: notes per beat (default 2)", false), ("barsPerChord", "arpeggio: bars per chord (default 1)", false), ("rhythm", "bassline: 16 characters, x a note and . a hold", false), ("voices", "fill: the drum voices the chosen kit has, comma separated (default kick,snare,hat); tom-run uses snare when there is no tom", false), ("seed", "A whole number; the same seed gives the same result (default 1)", false))),
        ("ample_components", "The components a song of the proposal can see (its own and the imported ones): the path to write as the block type, the canonical id, every version with its seal (sealed, unsealed or invalid), its signature, deprecation and migrations, and the contract of the default version: inputs, typed properties with their defaults, outputs. Also the problems of putting them together (a conflict, a missing import). Use it before you write an instance, and before you change a component: a sealed version is immutable, make a new version instead.", Schema(("path", "The song (default: the song of the task)", false))),
        ("ample_component_library", "Searches the component library of the proposal: every component of every .amplec file of the project, also the ones the song does not import yet. Give query words (all must appear in the name, description, file or #tags), a tag, and imported=yes|no. Each match has its description, tags, kind, seal and file, whether the song imports it, the import line to write and the block type to use (an alias is proposed when another component already has that name). Use it before you write a new component (maybe one exists), to find what to import, and to explain what is imported and what is not.", Schema(("query", "Words to search for (name, description, file, #tags)", false), ("tag", "Only components with this tag (without #)", false), ("imported", "yes: only what the song imports, no: only what it does not", false), ("path", "The song (default: the song of the task)", false))),
        ("ample_arrangement_lint", "A quick structural look at the arrangement of a song of the proposal, from the block placements only (instant, no audio): which blocks play in which bars (sections), a density digit per bar, and findings such as silent bars, harmony without a bass, nothing that builds up, the same layers for 12+ bars, every loop starting at bar 1 with the same length, too many layers at once, two blocks of the same kind overlapping. The findings are advice: listen with ample_listen before changing a song for them. Without path it takes the song you changed.", Schema(("path", "The song (.amplet) in the proposal (default: the song you changed)", false))),
        ("ample_mix_hints", "Mix starting points (data, no AI): for each instrument kind you name and a genre, a first volume, pan, highpass filter, EQ, compression and reverb or delay mix as the parameters of AMPLE's own blocks (volume, pan, filter, eq, compressor, reverb, delay), plus the master (limiter ceiling, peak before the master, loudness target), a sidechain duck setting for genres that pump, and the order to mix in. instruments is a comma list (kick, bass, piano, pad, vocal-lead, electric piano, 808, vocal chop...); without instruments you get what exists. They are starting points: set them, run ample_listen, then adjust by ear.", Schema(("instruments", "Comma separated instrument kinds or names, for example kick,bass,electric piano,pad,vocal-lead (empty: list what exists)", false), ("genre", "For example techno, hiphop, lofi, rock, funk, reggae, dnb, ballad, country, jazz, ambient (optional)", false), ("overview", "true: list the instruments and genres", false))),
        ("ample_sample_set_add", "Add one sample to a sample set (an instrument made of recordings that a piano, bass or guitar block, or a notes channel of a sequencer, plays through set=). source is a project audio file, or workspace/<path> for a fetched preview (mp3, flac, ...); destination is the WAV to write, inside the set's folder, named by note (samples/campfire/c4.wav; a softer or harder layer is c4_v2_r1.wav). It never overwrites. loop:true finds loop points for that sample and writes them into the folder's set.json (a sustained note instead of a one-shot); envelope sets one ADSR for the whole set (attack, hold, decay in ms, sustain 0-100, release in ms). A drums kit folder takes the envelope too but never loops. Staged in the proposal only; run ample_check afterwards.", SampleSetAddSchema),
        ("ample_copy_file", "Copy a text file in the staged proposal to a new path, without overwriting; unlike ample_move_file the source stays in place. Use this to duplicate a sequence or module before changing the copy, so other songs that still use the original are unaffected. source and destination are project-relative; source workspace/<path under your working folder> stages a file ample_fetch_url downloaded (an image, PDF, fetched score or MP3) into the project instead. No live project changes until Accept. Run ample_check afterwards.", Schema(("source", "Existing text file, or workspace/<path> for a fetched image/PDF/text/MP3 asset", true), ("destination", "New project-relative path that must not exist", true))),
        ("ample_move", "Move or rename a project file or folder (audio, a module, a song, a folder); every path in the songs and modules that points at it follows. It is NOT made in your working copy: it is made when the owner accepts. confirm \"ask\": the owner is asked now and you wait for the answer (use it when your work depends on the move); confirm \"propose\" (default): it is listed with your proposal and the owner ticks it. The destination must not exist. Keep using the old path in your own edits.", Schema(("source", "Project-relative path of the file or folder", true), ("destination", "Project-relative new path; it must not exist", true), ("confirm", "ask or propose (default propose)", false), ("reason", "Why the move is needed, in one short sentence", false))),
        ("ample_file", "The text of a file. Choose location=project (default) or work to select which folder the path is relative to; work reads are read-only and the result names its source. A long file comes back in a window with totalLength, offset and truncated: true; call again with offset at the end of what you got to read on. When something is missing that the file needs, the answer has a hint: tell the person and recommend it.", Schema(("path", "Relative path of the file, without a folder prefix", true), ("location", "project (default) or work, the separate assistant work folder", false), ("offset", "Character offset to continue a truncated read from (default 0)", false))),
        ("ample_resolve_notes", "The notes of a midi-clip, of one channel of a sequencer or of a note pipeline, in one form (how many, how many bars, a signature, the first notes), and with a second one to compare: whether they are EXACTLY the same (every note, length, velocity and setting). Use it before replacing notes by a formula: replace only when compare.same is true. block (a midi-clip, or a sequencer with channel) or expression (a pipeline such as 1:0:0.5:60:100 |> repeat(4) |> set(pitch=\"38 50\")); compare_block / compare_channel / compare_expression is the other side. Read-only; path defaults to the song you changed.", Schema(("path", "The song in the proposal (default: the song you changed)", false), ("block", "A midi-clip or a sequencer block", false), ("channel", "The channel of a sequencer", false), ("expression", "A note pipeline to work out instead of a block", false), ("compare_block", "The block to compare with", false), ("compare_channel", "Its channel when it is a sequencer", false), ("compare_expression", "A pipeline to compare with", false))),
        ("ample_find_patterns", "Finds the mathematical shape of notes so they can be written much shorter: a rhythm that repeats every bar (or every few bars) with pitches, velocities and every other setting that cycle over it, as a note pipeline (rhythm |> bars(1..=p) |> repeat(m) |> set(pitch=\"...\", velocity=\"...\")). Every candidate was worked out and compared with the original, so verified=true means every note, length and setting is the same. For a midi-clip it gives the expression to put in its notes; for a sequencer channel it gives a formula row per pattern (a row that starts with =) and the channels that play exactly the same notes. Read-only.", Schema(("path", "The song in the proposal (default: the song you changed)", false), ("block", "A midi-clip or a sequencer block", true), ("channel", "The channel of a sequencer (without it: every channel)", false))),
        ("ample_import_score", "Turn a score file of the project into a sequence you can edit and play: a MIDI file (.mid), MusicXML (.musicxml, .xml, .mxl), a MuseScore/Guitar Pro/Capella/Overture file (.mscz, .mscx, .gp, .gp3-5, .gpx, .ptb, .ove, .cap, .capx, .bww - converted with MuseScore when it is installed), or a PDF/image (.pdf, .png, .jpg, .jpeg, .webp, .tif, .tiff) of printed music, recognized with Audiveris when it is installed (optical music recognition: check the returned note count and warnings, it can be wrong on a messy source - a machine-readable score is always more reliable). Every part becomes a channel (GM program kept) with its notes as bar:beat:length:pitch:velocity rows in one pattern per N bars, plus a playlist. path is the score, out the project path of the new .ampleseq (default next to the score), bars_per_pattern defaults to 8. Read docs: ample_docs name=score-import.", Schema(("path", "The score file of the project", true), ("out", "The new sequence, for example sequences/mozart.ampleseq", false), ("bars_per_pattern", "Bars in one pattern (default 8)", false), ("compress", "true: write a row as a formula (a rhythm that repeats with pitches and velocities that cycle) when that is much shorter and was checked to give the very same notes. Identical patterns are always played again from the first one and similar ones are variations, with or without this", false))),
        ("ample_request_access", "Ask the person for permission to write somewhere outside the project (a full path, for example C:\\temp\\findings.txt). Give why (shown to them) and minutes (1 to 120). They can allow it for that long, for the project, or deny it. After an allow use ample_write_outside; never write there another way. Remarks about AMPLE itself go to ample_findings, which needs no permission.", Schema(("path", "A full path, or a folder", true), ("why", "Why you need it, in a sentence", true), ("minutes", "How long, 1 to 120 (default 15)", false), ("kind", "write (default) or passthrough (only through ample_write_outside)", false))),
        ("ample_write_outside", "Write a text file outside the project, only where the person gave a permission (ample_request_access). It does not write anywhere else.", Schema(("path", "The full path", true), ("text", "The content", true))),
        ("ample_findings", "Write your remarks about AMPLE (what is missing, what could be better, how to fix) to notes/ai-findings/ inside the project. No permission is needed. Say where it is in your last message.", Schema(("text", "The remarks, as markdown", true), ("name", "A short name for the file", false))),
        ("ample_project_log", "The project log: what is missing for the tools of this project and how to put it right (a PDF reader, the voice worker, ffmpeg). Read it when a tool says something is missing, and recommend the hint to the person.", NoArguments),
    ];

    internal static IReadOnlyList<Command> Create()
    {
        Option<string> url = new("--url") { Description = "The address of the running AMPLE server.", Required = true };
        Command mcp = new("mcp", "Runs an MCP server on stdio with task-scoped AMPLE context and constrained actions, including approved visible-browser actions (started by an AI agent, not by hand). The token of the task comes from the environment variable AMPLE_CONTEXT_TOKEN.") { url };
        mcp.SetAction(async (p, t) => await RunAsync(p.GetValue(url)!, Environment.GetEnvironmentVariable("AMPLE_CONTEXT_TOKEN") ?? string.Empty, t));
        return [mcp];
    }

    private static async Task<int> RunAsync(string url, string token, CancellationToken ct)
    {
        using HttpClient http = new() { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.Add("X-Ample-Context", token);
        using StreamReader input = new(Console.OpenStandardInput(), new UTF8Encoding(false));
        using StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string? line;
        while ((line = await input.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; } catch (System.Text.Json.JsonException) { continue; }
            if (message is null)
            {
                continue;
            }

            string? method = message["method"]?.GetValue<string>();
            JsonNode? id = message["id"]?.DeepClone();
            if (method is null || id is null)
            {
                continue;   // a notification such as notifications/initialized needs no answer
            }

            JsonNode result;
            try
            {
                result = method switch
                {
                    "initialize" => new JsonObject { ["protocolVersion"] = message["params"]?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["serverInfo"] = new JsonObject { ["name"] = "ample", ["version"] = "0.1.0" }, ["instructions"] = "Task-scoped AMPLE context and actions for the project you are working on. Start with ample_project and ample_faer_status, read ample_docs before writing AMPLE text, and use ample_song and ample_block to see how the song is built. When the owner asks to inspect a live site, use the ample_browser_* tools; the first call asks for task or project permission and opens a visible window. Credentials remain manual. Page JavaScript can submit forms and bypass numbered-click purchase blocking; never expose credentials, tokens or cookies. ample_project lists project files and task-work files separately; on ample_file/song/block, set the separate location argument to project or work and keep path as a plain relative filename. For ACE model choices read the ACE model catalog; combine AMPLE's hardcoded model families with the live Faer model inventory, and use the exact-file field as a free-form option only for a server-offered file. Faer can remotely generate songs (ACE-Step), generate voices (ACE voice or XTTS/Qwen3/Chatterbox cloning), and separate stems; consult live readiness and the independent project routes before recommending or building remote work." },
                    "tools/list" => new JsonObject { ["tools"] = new JsonArray([.. Tools.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["description"] = x.Description, ["annotations"] = new JsonObject { ["readOnlyHint"] = x.Name is not ("ample_request_asset" or "ample_fetch_url" or "ample_fetch_sample" or "ample_install_pack" or "ample_browser_open" or "ample_browser_show" or "ample_browser_read" or "ample_browser_click" or "ample_web_search" or "ample_browser_type" or "ample_browser_mouse_move" or "ample_browser_media_mute" or "ample_browser_screenshot" or "ample_browser_network_stats" or "ample_browser_js" or "ample_edit" or "ample_move_file" or "ample_copy_file" or "ample_sample_set_add" or "ample_move" or "ample_skip_build" or "ample_memory_write"), ["openWorldHint"] = x.Name is "ample_fetch_url" or "ample_web_search" or "ample_web_read" or "ample_browser_open" or "ample_browser_show" or "ample_browser_read" or "ample_browser_click" or "ample_browser_type" or "ample_browser_mouse_move" or "ample_browser_media_mute" or "ample_browser_screenshot" or "ample_browser_network_stats" or "ample_browser_js" }, ["inputSchema"] = WithThinking(x.Schema) })]) },
                    "tools/call" => await CallAsync(http, message["params"] as JsonObject ?? [], ct).ConfigureAwait(false),
                    "ping" => new JsonObject(),
                    _ => throw new InvalidOperationException("Unknown method " + method),
                };
            }
            catch (InvalidOperationException ex)
            {
                await output.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = ex.Message } }.ToJsonString()).ConfigureAwait(false);
                continue;
            }

            await output.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString()).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Every tool takes an optional <c>thinking</c> text (a short note for the user about what the AI is thinking of).</summary>
    private static JsonObject WithThinking(JsonObject schema)
    {
        JsonObject copy = (JsonObject)schema.DeepClone();
        JsonObject properties = copy["properties"] as JsonObject ?? [];
        if (!properties.ContainsKey("thinking"))
        {
            properties["thinking"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: what you are thinking about, in one or two short sentences, so the user can follow you." };
        }

        copy["properties"] = properties;
        copy["type"] ??= "object";
        return copy;
    }

    private static async Task<JsonNode> CallAsync(HttpClient http, JsonObject call, CancellationToken ct)
    {
        string name = call["name"]?.GetValue<string>() ?? string.Empty;
        JsonObject args = call["arguments"] as JsonObject ?? [];
        string Arg(string key) => Uri.EscapeDataString(args[key] is JsonValue v ? v.ToString() : string.Empty);   // a number or a boolean from the model is text too, it must not stop the call
        if (name == "ample_random_seed")
        {
            ulong seed = NewRandomSeed(), blockSeed = NewRandomSeed();
            return new JsonObject { ["isError"] = false, ["structuredContent"] = new JsonObject { ["seed"] = seed, ["masterSeed"] = seed, ["blockSeed"] = blockSeed }, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Random project master seed: {seed}; random block seed/lm_seed: {blockSeed}" }) };
        }

        if (name == "ample_redux_search")
        {
            try
            {
                string query = $"scope={Arg("scope")}&ar={Arg("ar")}&task={Arg("task")}&session={Arg("session")}&path={Arg("path")}&block={Arg("block")}&field={Arg("field")}&from={Arg("from")}&to={Arg("to")}&q={Arg("q")}&limit={Arg("limit")}&cursor={Arg("cursor")}&includePayload={Arg("includePayload")}";
                using HttpResponseMessage response = await http.GetAsync("/api/ai/context/redux-search?" + query, ct).ConfigureAwait(false);
                return new JsonObject { ["isError"] = !response.IsSuccessStatusCode, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Visible(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), response) }) };
            }
            catch (HttpRequestException ex)
            {
                return new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "AMPLE could not be reached: " + ex.Message }) };
            }
        }

        if (name == "ample_batch")
        {
            JsonArray calls = args["calls"] as JsonArray ?? [];
            if (args["thinking"] is JsonValue batchHint && batchHint.ToString().Length > 0) await ShowThoughtAsync(http, batchHint.ToString(), ct).ConfigureAwait(false);
            StringBuilder all = new();
            JsonArray results = [];
            bool anyError = false;
            List<(string Tool, long Ms)> timings = [];
            System.Diagnostics.Stopwatch batchClock = System.Diagnostics.Stopwatch.StartNew();
            foreach ((JsonNode? node, int index) in calls.Take(12).Select((n, i) => (n, i)))
            {
                string tool = node?["tool"]?.GetValue<string>() ?? string.Empty;
                string hint = string.Empty;
                foreach (string key in new[] { "thinking", "reason", "description", "action" })
                {
                    if (node?[key] is JsonValue hintValue && hintValue.ToString().Length > 0) { hint = hintValue.ToString(); break; }
                }

                if (tool is "ample_batch" or "ample_request_asset" or "ample_fetch_url" or "ample_install_pack" or "ample_browser_open" or "ample_browser_show" or "ample_browser_read" or "ample_browser_click" or "ample_web_search" or "ample_web_read" or "ample_browser_type" or "ample_browser_mouse_move" or "ample_browser_media_mute" or "ample_browser_screenshot" or "ample_browser_network_stats" or "ample_browser_js" or "ample_edit" or "ample_move_file" or "ample_copy_file" or "ample_move" or "ample_present" or "ample_obsolete_sample" or "ample_skip_build" or "ample_memory_write" or "ample_write_outside" or "ample_findings" or "ample_request_access" or "ample_import_score" or "")
                {
                    all.Append("### ").Append(index + 1).Append(". ").Append(tool).Append("\nnot allowed in a batch: a batch is for reading. Call this tool by itself\n\n");
                    anyError = true;
                    results.Add(new JsonObject { ["tool"] = tool, ["isError"] = true, ["error"] = "not allowed in a batch: a batch is for reading. Call this tool by itself (ample_edit takes path, why and edits: [{op, old, new, ...}])." });
                    continue;
                }

                JsonObject one = new() { ["name"] = tool, ["arguments"] = node?["arguments"]?.DeepClone() ?? new JsonObject() };
                if (hint.Length > 0 && one["arguments"] is JsonObject oneArgs && oneArgs["thinking"] is null) oneArgs["thinking"] = hint;   // the single-call path shows it to the user
                long started = batchClock.ElapsedMilliseconds;
                JsonNode result = await CallAsync(http, one, ct).ConfigureAwait(false);
                long ms = batchClock.ElapsedMilliseconds - started;
                timings.Add((tool, ms));
                anyError |= result["isError"]?.GetValue<bool>() == true;
                results.Add(new JsonObject { ["tool"] = tool, ["thinking"] = hint.Length > 0 ? hint : null, ["ms"] = ms, ["result"] = result.DeepClone(), ["isError"] = result["isError"]?.DeepClone() });
                all.Append("### ").Append(index + 1).Append(". ").Append(tool).Append(hint.Length > 0 ? " (" + hint + ")" : string.Empty).Append(" - ").Append(ms).Append(" ms\n").Append(result["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty).Append("\n\n");
            }

            return new JsonObject { ["isError"] = anyError && calls.Count == 1, ["structuredContent"] = new JsonObject { ["totalMs"] = batchClock.ElapsedMilliseconds, ["results"] = results, ["requestedCalls"] = calls.Count, ["executedCalls"] = Math.Min(calls.Count, 12), ["hasErrors"] = anyError }, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = all.ToString() }) };
        }

        if (name is "ample_listen" or "ample_build" && args["edits"] is JsonArray)
        {
            try
            {
                JsonObject send = new() { ["block"] = args["block"]?.DeepClone(), ["with"] = name == "ample_build" ? "none" : args["with"]?.DeepClone(), ["path"] = args["path"]?.DeepClone(), ["edits"] = args["edits"]!.DeepClone(), ["forceRender"] = args["forceRender"]?.DeepClone() };
                using StringContent content = new(send.ToJsonString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync("/api/ai/context/listen", content, ct).ConfigureAwait(false);
                return new JsonObject { ["isError"] = !response.IsSuccessStatusCode, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Visible(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), response) }) };
            }
            catch (HttpRequestException ex)
            {
                return new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "AMPLE could not be reached: " + ex.Message }) };
            }
        }

        if (name is "ample_edit" or "ample_move_file" or "ample_copy_file" or "ample_sample_set_add" or "ample_move" or "ample_present" or "ample_obsolete_sample" or "ample_memory_write" or "ample_write_outside" or "ample_findings" or "ample_import_score" or "ample_browser_js" or "ample_resolve_notes" or "ample_find_patterns")
        {
            try
            {
                using StringContent content = new(args.ToJsonString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync(name == "ample_edit" ? "/api/ai/context/edit" : name == "ample_present" ? "/api/ai/context/present" : name == "ample_obsolete_sample" ? "/api/ai/context/obsolete" : name == "ample_move" ? "/api/ai/context/move" : name == "ample_memory_write" ? "/api/ai/context/memory" : name == "ample_write_outside" ? "/api/ai/context/write-outside" : name == "ample_findings" ? "/api/ai/context/findings" : name == "ample_resolve_notes" ? "/api/ai/context/resolve-notes" : name == "ample_find_patterns" ? "/api/ai/context/find-patterns" : name == "ample_import_score" ? "/api/ai/context/import-score" : name == "ample_browser_js" ? "/api/ai/context/browser-js" : name == "ample_copy_file" ? "/api/ai/context/copy-file" : name == "ample_sample_set_add" ? "/api/ai/context/sample-set-add" : "/api/ai/context/move-file", content, ct).ConfigureAwait(false);
                string answer = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new JsonObject { ["isError"] = !response.IsSuccessStatusCode, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Visible(answer, response) }) };
            }
            catch (HttpRequestException ex)
            {
                return new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "AMPLE could not be reached: " + ex.Message }) };
            }
        }

        if (name is "ample_skip_build" or "ample_offer_variants" or "ample_answer")
        {
            try
            {
                using StringContent content = new(args.ToJsonString(), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync(name == "ample_skip_build" ? "/api/ai/context/skip-build" : name == "ample_answer" ? "/api/ai/context/answer" : "/api/ai/context/variants", content, ct).ConfigureAwait(false);
                return new JsonObject { ["isError"] = !response.IsSuccessStatusCode, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Visible(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), response) }) };
            }
            catch (HttpRequestException ex)
            {
                return new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "AMPLE could not be reached: " + ex.Message }) };
            }
        }

        string? route = name switch
        {
            "ample_project" => $"project?filesQuery={Arg("filesQuery")}&filesKind={Arg("filesKind")}&filesExt={Arg("filesExt")}&filesOffset={Arg("filesOffset")}&filesLimit={Arg("filesLimit")}",
            "ample_faer_status" => "faer",
            "ample_docs" => $"docs?name={Arg("name")}&query={Arg("query")}",
            "ample_block_types" => $"blocktypes?type={Arg("type")}",
            "ample_song" => $"song?path={Arg("path")}&location={Arg("location")}",
            "ample_block" => $"block?path={Arg("path")}&name={Arg("name")}&location={Arg("location")}",
            "ample_file" => $"file?path={Arg("path")}&location={Arg("location")}&offset={Arg("offset")}",
            "ample_request_access" => $"access?path={Arg("path")}&why={Arg("why")}&minutes={Arg("minutes")}&kind={Arg("kind")}",
            "ample_project_log" => "project-log",
            "ample_memory_read" => $"memory?scope={Arg("scope")}",
            "ample_thinking" => $"thought?text={Arg("thinking")}",
            "ample_check" => $"check?path={Arg("path")}",
            "ample_find_samples" => $"find-samples?text={Arg("text")}&source={Arg("source")}&limit={Arg("limit")}",
            "ample_fetch_sample" => $"fetch-sample?page={Arg("page")}&file={Arg("file")}&title={Arg("title")}&browser={Arg("browser")}",
            "ample_sample_sources" => $"sample-sources?text={Arg("text")}",
            "ample_beats" => "beats?" + string.Join("&", BeatsKeys.Select(k => $"{k}={Arg(k)}")) + "&queries=" + (args["queries"] is JsonArray queries ? Uri.EscapeDataString(queries.ToJsonString()) : Arg("queries")),
            "ample_analyze_audio" => $"analyze-audio?source={Arg("source")}",
            "ample_generate" => "generate?" + string.Join("&", GenerateKeys.Select(k => $"{k}={Arg(k)}")),
            "ample_arrangement_lint" => $"arrangement-lint?path={Arg("path")}",
            "ample_components" => $"components?path={Arg("path")}",
            "ample_component_library" => $"component-library?path={Arg("path")}&query={Arg("query")}&tag={Arg("tag")}&imported={Arg("imported")}",
            "ample_mix_hints" => $"mix-hints?instruments={Arg("instruments")}&genre={Arg("genre")}&overview={Arg("overview")}",
            "ample_listen" => $"listen?block={Arg("block")}&with={Arg("with")}&path={Arg("path")}",
            "ample_build" => $"listen?block={Arg("block")}&with=none&path={Arg("path")}&forceRender={Arg("forceRender")}",
            "ample_offered" => "offered",
            "ample_present" => $"present?block={Arg("block")}&from={Arg("from")}&to={Arg("to")}&unit={Arg("unit")}&label={Arg("label")}&why={Arg("why")}&source={Arg("source")}",
            "ample_changes_since" => $"changes-since?path={Arg("path")}&import={Arg("import")}",
            "ample_revision" => $"revision?id={Arg("id")}&path={Arg("path")}",
            "ample_history" => $"history?id={Arg("id")}&song={Arg("song")}&query={Arg("query")}&limit={Arg("limit")}",
            "ample_hum" => $"hum?id={Arg("id")}&tempo={Arg("tempo")}&grid={Arg("grid")}&key={Arg("key")}&bar={Arg("bar")}",
            "ample_arrangement" => $"arrangement?measure={Arg("measure")}&path={Arg("path")}",
            "ample_lookup" => $"lookup?topic={Arg("topic")}&name={Arg("name")}&query={Arg("query")}",
            "ample_fetch_url" => $"fetch?url={Arg("url")}&reason={Arg("reason")}",
            "ample_install_pack" => $"install-pack?url={Arg("url")}&reason={Arg("reason")}",
            "ample_browser_open" => $"browser-open?url={Arg("url")}&reason={Arg("reason")}",
            "ample_browser_show" => $"browser-show?reason={Arg("reason")}",
            "ample_browser_read" => "browser-read",
            "ample_browser_click" => $"browser-click?index={Arg("index")}",
            "ample_web_search" => $"web-search?query={Arg("query")}&limit={Arg("limit")}&offset={Arg("offset")}&provider={Arg("provider")}",
            "ample_web_read" => $"web-read?url={Arg("url")}&mode={Arg("mode")}",
            "ample_browser_type" => $"browser-type?index={Arg("index")}&text={Arg("text")}",
            "ample_browser_mouse_move" => $"browser-mouse-move?x={Arg("x")}&y={Arg("y")}",
            "ample_browser_media_mute" => $"browser-media-mute?muted={Arg("muted")}",
            "ample_browser_screenshot" => "browser-screenshot",
            "ample_browser_network_stats" => "browser-network-stats",
            "ample_request_asset" => $"request-asset?path={Arg("path")}&reason={Arg("reason")}",
            "ample_analyze" => $"analyze?path={Arg("path")}&block={Arg("block")}&with={Arg("with")}",
            _ => null,
        };
        if (route is null)
        {
            return new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Unknown tool " + name }) };
        }

        try
        {
            if (name != "ample_thinking" && args["thinking"] is JsonValue thought && thought.ToString().Length > 0)
            {
                using HttpResponseMessage _ = await http.GetAsync("/api/ai/context/thought?text=" + Arg("thinking"), ct).ConfigureAwait(false);
            }

            using HttpResponseMessage response = await http.GetAsync("/api/ai/context/" + route, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (name == "ample_browser_screenshot" && response.IsSuccessStatusCode)
            {
                JsonObject screenshot = JsonNode.Parse(text) as JsonObject ?? [];
                return new JsonObject { ["isError"] = false, ["structuredContent"] = new JsonObject { ["mimeType"] = "image/png", ["note"] = screenshot["note"]?.DeepClone() }, ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = screenshot["data"]?.DeepClone() }) };
            }
            return new JsonObject { ["isError"] = !response.IsSuccessStatusCode, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Visible(text, response) }) };
        }
        catch (HttpRequestException ex)
        {
            return new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "AMPLE could not be reached: " + ex.Message }) };
        }
    }

    private static ulong NewRandomSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
