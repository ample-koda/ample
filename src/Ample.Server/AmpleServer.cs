using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Ample.Server;

/// <summary>A running local server.</summary>
public sealed class RunningServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly ProjectSession session;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InstanceRegistry? registry;
    private readonly string projectId;
    private readonly CancellationTokenSource refreshStop = new();
    private readonly Task refreshTask;

    internal RunningServer(WebApplication app, ProjectSession session, string url, string token, InstanceRegistry? registry, string projectId)
    {
        this.app = app;
        app.Lifetime.ApplicationStopped.Register(() => completion.TrySetResult());
        this.session = session;
        Url = url;
        Token = token;
        this.registry = registry;
        this.projectId = projectId;
        refreshTask = registry is null ? Task.CompletedTask : RefreshRegistryAsync();
    }

    /// <summary>Gets the base address (http://127.0.0.1:port).</summary>
    public string Url { get; }

    /// <summary>Gets the session token every API call must carry (header X-Ample-Token).</summary>
    public string Token { get; }

    /// <summary>Gets whether this server was written to the optional per-user instance registry.</summary>
    public bool IsRegistered => registry is not null;

    /// <summary>Gets a task that ends when the server has stopped.</summary>
    public Task Completion => completion.Task;

    /// <summary>Gets the address to open in a browser: the token travels in the fragment, which browsers never send to a server.</summary>
    public string LaunchUrl => $"{Url}/#token={Token}";

    /// <summary>Stops the server and releases the project.</summary>
    public async ValueTask DisposeAsync()
    {
        refreshStop.Cancel();
        try
        {
            await app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await refreshTask.ConfigureAwait(false);
            }
            finally
            {
                registry?.Unregister(projectId);
                await app.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                refreshStop.Dispose();
            }
        }
    }

    private async Task RefreshRegistryAsync()
    {
        if (registry is null) return;
        InstanceRegistry activeRegistry = registry;
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(refreshStop.Token).ConfigureAwait(false)) activeRegistry.Touch(projectId);
        }
        catch (OperationCanceledException) when (refreshStop.IsCancellationRequested)
        {
        }
    }
}

/// <summary>The local AMPLE server: a loopback-only web server with a session token, host and origin checks, request limits, problem details for every error, server-sent events, and the whole engine as an API.</summary>
public static class AmpleServer
{
    private const long MaxBody = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions ProblemOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Starts the server for a project.</summary>
    public static async Task<RunningServer> StartAsync(ServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxProjectUploadBytes is < 1 or > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options), "The project upload limit must be from 1 byte to 512 MiB.");
        EventBus events = new();
        ProjectSession session = await ProjectSession.OpenAsync(options, events, cancellationToken).ConfigureAwait(false);
        if (options.SeedDemo && !options.LiteOnly) await DemoSong.EnsureAsync(session, options.AmpleRoot, cancellationToken).ConfigureAwait(false);
        if (!options.LiteOnly) StorageApi.Warm(session);
        string token = options.Token ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        WebApplication? startedApp = null;
        try
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            string bind = options.BindAddress ?? (options.LiteOnly ? "127.0.0.1" : options.AllowLan || options.AllowAll ? "0.0.0.0" : "127.0.0.1");
            if (!System.Net.IPAddress.TryParse(bind, out System.Net.IPAddress? bindIp))
            {
                throw new AmpleException("AMP5940", $"--bind needs an IP address (for example 127.0.0.1), not \"{bind}\".");
            }

            builder.WebHost.UseUrls($"http://{(bindIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + bind + "]" : bind)}:{options.Port}");
            builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxBody);
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
            WebApplication app = builder.Build();
            startedApp = app;
            AuthService? auth = null;
            if (options.AllowAll && !options.Login && !options.LiteOnly)
            {
                throw new AmpleException("AMP5297", "--allow-all needs --login: without a sign-in the launch token would be the only protection.");
            }

            if (options.Login)
            {
                auth = AuthService.Load(options.LibraryHome ?? Ample.Storage.Library.Library.DefaultHome()) ?? throw new AmpleException("AMP5298", "--login needs a user and password first: run `ample auth set-password --user <name>`.");
            }

            app.Use(async (context, next) =>
            {
                try
                {
                    if (!HostAllowed(context, options.AllowLan || options.AllowAll || options.LiteOnly))
                    {
                        await Write(context, 421, Problems.Body("AMP5282", "This host name is not allowed (the server only answers on 127.0.0.1, localhost and ::1).", 421)).ConfigureAwait(false);
                        return;
                    }

                    string? origin = context.Request.Headers.Origin.FirstOrDefault();
                    if (origin is not null && !Uri.TryCreate(origin, UriKind.Absolute, out Uri? o) || (origin is not null && Uri.TryCreate(origin, UriKind.Absolute, out Uri? o2) && !string.Equals(o2.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        await Write(context, 403, Problems.Body("AMP5283", "Requests from other origins are refused.", 403)).ConfigureAwait(false);
                        return;
                    }

                    // a link to a shared song (/s/K7QF2M9X) is the same page as Lite: the page reads the id from the address (in every mode, so that the browser tests can reach it)
                    if (System.Text.RegularExpressions.Regex.IsMatch(context.Request.Path.Value ?? string.Empty, "^/(?:mix|m|s)/[0-9A-HJKMNP-TV-Z]{8}/?$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200))) context.Request.Path = "/remix/";

                    if (options.LiteOnly)
                    {
                        // the public Lite: the page of Lite is the front page, only Lite's own routes and the few files it imports exist, everything else is a 404 before anything else looks at it
                        if (string.Equals(context.Request.Path.Value, "/", StringComparison.Ordinal)) context.Request.Path = "/remix/";
                        if (string.Equals(context.Request.Path.Value, "/favicon.ico", StringComparison.Ordinal))
                        {
                            context.Response.StatusCode = 204;   // no icon: an empty answer instead of a 404 in the browser console
                            return;
                        }

                        if (!LiteOnlyPath(context.Request.Path))
                        {
                            await Write(context, 404, Problems.Body("AMP5285", "This address does not exist.", 404)).ConfigureAwait(false);
                            return;
                        }

                        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self' ws: wss:; worker-src 'self' blob:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                        context.Response.Headers["X-Frame-Options"] = "DENY";
                        context.Response.Headers["Referrer-Policy"] = "no-referrer";
                        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                    }

                    if (auth is not null && context.Request.Path.StartsWithSegments("/login", StringComparison.Ordinal) && context.Request.Method == HttpMethods.Get)
                    {
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.WriteAsync(AuthService.LoginPage).ConfigureAwait(false);
                        return;
                    }

                    if (auth is not null && context.Request.Method == HttpMethods.Post && string.Equals(context.Request.Path.Value, "/api/login", StringComparison.Ordinal))
                    {
                        using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
                        string client = context.Connection.RemoteIpAddress?.ToString() ?? "local";

                        // honeypot: a person never sees the hidden "website" field, a form-filling bot does; refused like a wrong password, but not counted towards the lockout
                        if (body.RootElement.TryGetProperty("website", out JsonElement trap) && !string.IsNullOrEmpty(trap.GetString()))
                        {
                            await Write(context, 401, Problems.Body("AMP5299", "Wrong user or password.", 401)).ConfigureAwait(false);
                            return;
                        }

                        // reverse honeypot: the page script fills the hidden "stamp" field with its load time; a bot that does not run the page script leaves it out
                        if (!body.RootElement.TryGetProperty("stamp", out JsonElement stamp) || stamp.ValueKind != JsonValueKind.Number || !stamp.TryGetInt64(out long loaded) || loaded <= 0)
                        {
                            await Write(context, 401, Problems.Body("AMP5299", "Wrong user or password.", 401)).ConfigureAwait(false);
                            return;
                        }

                        string? session = auth.TryLogin(client, body.RootElement.TryGetProperty("user", out JsonElement uu) ? uu.GetString() ?? "" : "", body.RootElement.TryGetProperty("password", out JsonElement pp) ? pp.GetString() ?? "" : "", out bool locked);
                        if (session is not null)
                        {
                            context.Response.Headers["Cache-Control"] = "no-store";
                            await context.Response.WriteAsJsonAsync(new { token = session }).ConfigureAwait(false);
                        }
                        else
                        {
                            await Write(context, locked ? 429 : 401, Problems.Body("AMP5299", locked ? "Too many wrong attempts; try again in a minute." : "Wrong user or password.", locked ? 429 : 401)).ConfigureAwait(false);
                        }

                        return;
                    }

                    bool liteOpen = options.LiteOnly && (context.Request.Path.StartsWithSegments("/api/remix", StringComparison.Ordinal) || string.Equals(context.Request.Path.Value, "/lsp/remix", StringComparison.Ordinal));
                    bool publicPing = context.Request.Method == HttpMethods.Get && string.Equals(context.Request.Path.Value, "/api/ping", StringComparison.Ordinal);
                    // every route under /api/ai/context is for the task that holds the token (a GET reads, a POST acts); a path that is not registered is a 404 anyway. No list of paths to keep in step with the routes.
                    bool contextRead = (context.Request.Method == HttpMethods.Get || context.Request.Method == HttpMethods.Post) && context.Request.Path.StartsWithSegments("/api/ai/context", StringComparison.Ordinal) && AiService.IsContextToken(context.Request.Headers["X-Ample-Context"].FirstOrDefault());
                    if (!publicPing && !liteOpen && (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal) || context.Request.Path.StartsWithSegments("/lsp", StringComparison.Ordinal)) && !TokenOk(context, token, auth) && !contextRead)
                    {
                        if (context.Request.Path.StartsWithSegments("/api/ai/context", StringComparison.Ordinal) && context.Request.Headers.ContainsKey("X-Ample-Context"))
                        {
                            if (context.Request.Method != HttpMethods.Get && context.Request.Method != HttpMethods.Post)
                            {
                                // not an unknown token: this method is not one a task may use
                                await Write(context, 405, Problems.Body("AMP5458", "A task token can only read (GET) or act (POST) on /api/ai/context routes; this method is not allowed.", 405)).ConfigureAwait(false);
                                return;
                            }

                            // a task token AMPLE does not know: the task ended, or AMPLE was restarted since it started (tokens live in memory only)
                            await Write(context, 401, Problems.Body("AMP5458", "This task is no longer running: AMPLE was restarted or the task ended, so its token is no longer known. Nothing is lost: the conversation and the unaccepted proposal in the session's working copy are kept. Stop here and tell the person to open this same session in the assistant panel and press 'Resume conversation'. Do not suggest a new task: a new task cannot see the unaccepted proposal of this one.", 401)).ConfigureAwait(false);
                            return;
                        }

                        await Write(context, 401, Problems.Body("AMP5284", "The session token is missing or wrong (header X-Ample-Token).", 401)).ConfigureAwait(false);
                        return;
                    }

                    context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    await next().ConfigureAwait(false);
                }
                catch (AmpleException ex)
                {
                    (int status, object body) = Problems.From(ex);
                    if (!context.Response.HasStarted)
                    {
                        await Write(context, status, body).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    if (!context.Response.HasStarted)
                    {
                        await Write(context, 400, Problems.Body("AMP5285", "The request is not valid: " + ex.Message, 400)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Anything unexpected is shown in the server window and told to the page, so the UI never says only "The request failed".
                    await Console.Error.WriteLineAsync($"[AMPLE] {context.Request.Method} {context.Request.Path} failed: {ex}").ConfigureAwait(false);
                    if (!context.Response.HasStarted)
                    {
                        await Write(context, 500, Problems.Body("AMP5360", $"The server hit an unexpected error ({ex.GetType().Name}: {ex.Message}). The server window has the details.", 500)).ConfigureAwait(false);
                    }
                }
            });

            string web = Path.Combine(options.AmpleRoot, "web");
            if (Directory.Exists(web))
            {
                Lite.LiteApi.UseStaticFiles(app, web);
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(web) });
                app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(web) });
            }

            // a phone that goes to sleep leaves a connection that nobody closes: a ping every 20 s and a close after 40 s without an answer free its place (and its Lite slot)
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(40) });
            app.Map("/lsp", async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    await Write(context, 426, Problems.Body("AMP5285", "The language server is a WebSocket: connect with ws://.", 426)).ConfigureAwait(false);
                    return;
                }

                using System.Net.WebSockets.WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                using WebSocketChannel channel = new(socket);
                await new Ample.Lsp.LspServer(session.Spec, "/help", session.SettingHints(), session.ReadProjectText, (blocks, lines) => Ample.Audio.Engine.DrumPreflight.Check(session.Root, blocks, lines), text => new Ample.Engine.Modules.ModuleService(session.Root, () => session.Library, session.Manifest.Id.ToString("D")).ComponentsForEditors(text, session.Spec)).RunAsync(channel, context.RequestAborted).ConfigureAwait(false);
            });
            string helpFile = Path.Combine(options.AmpleRoot, "docs", "help", "help.html");
            app.MapGet("/help", () => File.Exists(helpFile) ? Results.File(helpFile, "text/html; charset=utf-8") : Results.NotFound());
            // the guide (the learning help, built from guide/ into docs/guide/): static files, read only, nothing outside that folder
            string guideRoot = Path.GetFullPath(Path.Combine(options.AmpleRoot, "docs", "guide"));
            Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider guideTypes = new();
            app.MapGet("/guide", (HttpContext c) => c.Request.Path.Value!.EndsWith('/') ? ServeGuide("index.html") : Results.Redirect("/guide/"));
            IResult ServeGuide(string? path)
            {
                string full = Path.GetFullPath(Path.Combine(guideRoot, string.IsNullOrEmpty(path) ? "index.html" : path.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(guideRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
                return Results.File(full, guideTypes.TryGetContentType(full, out string? type) ? type : "application/octet-stream");
            }

            app.MapGet("/guide/{**path}", (string? path) => ServeGuide(path));
            if (!options.LiteOnly) Routes.Map(app, session, token, events, options.MaxProjectUploadBytes);
            Lite.LiteLimits liteLimits = options.LiteOnly ? Lite.LiteLimits.FromEnvironment(Lite.LiteLimits.Public) : Lite.LiteLimits.FromEnvironment();
            Lite.LiteGovernor liteGovernor = new(liteLimits);
            app.Lifetime.ApplicationStopped.Register(liteGovernor.Dispose);
            Lite.LiteApi.Map(app, session.Spec, options.LibraryHome ?? Ample.Storage.Library.Library.DefaultHome(), liteLimits, liteGovernor, options.LiteOnly, options.AmpleRoot, options.LiteOnly || Environment.GetEnvironmentVariable("AMPLE_LITE_ISOLATE") == "1");
            Lite.LiteLanguage.Map(app, session.Spec.WithoutTemplates(Lite.LitePolicy.IsForbidden), liteLimits, liteGovernor);   // the language help of Lite does not know the plugin blocks (ACE-Step, voice cloning, ...): not in completion; a build still refuses them with AMPL911
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
            using Process process = Process.GetCurrentProcess();
            InstanceRegistry? registry = null;
            string projectId = session.Manifest.Id.ToString("D");
            string version = typeof(AmpleServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            try
            {
                registry = new InstanceRegistry(options.LibraryHome ?? Ample.Storage.Library.Library.DefaultHome(), session.Clock, new SystemInstanceProbe());
                registry.Register(projectId, session.Manifest.Name, session.Root, Environment.ProcessId, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero), new Uri(address).Port, version, options.StartedBy, options.Agent, null, $"{address}/#token={token}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                registry = null;
            }

            RunningServer running = new(app, session, address, token, registry, projectId);
            startedApp = null;
            return running;
        }
        catch
        {
            if (startedApp is not null)
            {
                await startedApp.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await startedApp.DisposeAsync().ConfigureAwait(false);
            }

            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The paths of the public Lite: its page and files, its API and language server, and the four files of Studio's web folder that the page imports. Nothing else.</summary>
    private static bool LiteOnlyPath(PathString path)
    {
        string value = path.Value ?? string.Empty;
        if (value.Contains("/test/", StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal)) return false;
        return value is "/remix" or "/lsp/remix" or "/styles/tokens.css" or "/app/api.js" or "/app/lsp.js" or "/app/editor-touch-menu.js" or "/lib/notes-model.mjs" or "/lib/meter.mjs" or "/vendor/codemirror-ample.js"
            || path.StartsWithSegments("/remix", StringComparison.Ordinal) || path.StartsWithSegments("/api/remix", StringComparison.Ordinal);
    }

    internal static async Task Write(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, ProblemOptions)).ConfigureAwait(false);
    }

    private static bool HostAllowed(HttpContext context, bool allowLan)
    {
        if (allowLan)
        {
            return true;
        }

        string host = context.Request.Host.Host;
        return host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
    }

    private static bool TokenOk(HttpContext context, string token, AuthService? auth)
    {
        string cookieName = $"ample-token-{context.Connection.LocalPort}-{token[..Math.Min(16, token.Length)]}";
        string? given = context.Request.Headers["X-Ample-Token"].FirstOrDefault() ?? (context.Request.Method == "GET" ? context.Request.Cookies[cookieName] ?? context.Request.Cookies["ample-token"] : null);   // the plain name stays for clients that are not the page (the language server over a websocket, editors); the token itself still has to match
        if (given is not null && Matches(given))
        {
            return true;
        }

        // the page names its cookie after its own port and token: behind a proxy the port differs and a signed-in session has its own token, so any of the page's cookies may carry a valid one
        if (context.Request.Method == "GET" && context.Request.Headers["X-Ample-Token"].Count == 0)
        {
            foreach (KeyValuePair<string, string> cookie in context.Request.Cookies)
            {
                if (cookie.Key.StartsWith("ample-token-", StringComparison.Ordinal) && Matches(cookie.Value))
                {
                    return true;
                }
            }
        }

        return false;

        bool Matches(string candidate) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(token)) || (auth?.IsSession(candidate) ?? false);
    }
}
