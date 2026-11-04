using System.Text.Json;

namespace Ample.Server;

/// <summary>Builds the OpenAPI 3.0 document from the recorded routes, so the document cannot drift from the code (a test compares it with the committed <c>docs/api/openapi.json</c>).</summary>
public static class OpenApi
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Builds the document as indented JSON text with a trailing newline.</summary>
    public static string Build()
    {
        Dictionary<string, Dictionary<string, object>> paths = [];
        foreach (ApiOperation op in Routes.Catalog.OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Method, StringComparer.Ordinal))
        {
            if (!paths.TryGetValue(op.Path, out Dictionary<string, object>? methods))
            {
                paths[op.Path] = methods = [];
            }

            IReadOnlyList<Dictionary<string, string[]>> security = op.RequiresToken ? [new Dictionary<string, string[]> { ["sessionToken"] = [] }] : [];
            methods[op.Method.ToLowerInvariant()] = new
            {
                tags = new[] { op.Tag },
                summary = op.Summary,
                description = op.Mutating ? "Changes state: journaled in AMPLE Redux as a command." : "Read only.",
                security,
                responses = new Dictionary<string, object>
                {
                    ["200"] = new { description = "Success (JSON, or the raw data for /api/data)." },
                    ["default"] = new { description = "Problem details (RFC 9457) with the AMP code in 'code'.", content = new Dictionary<string, object> { ["application/problem+json"] = new { } } },
                },
            };
        }

        object doc = new
        {
            openapi = "3.0.3",
            info = new { title = "AMPLE local API", version = "0.1", description = "The API of 'ample serve'. It listens on 127.0.0.1 only; most routes need the session token in the header X-Ample-Token." },
            servers = new[] { new { url = "http://127.0.0.1:{port}", variables = new Dictionary<string, object> { ["port"] = new { @default = "0" } } } },
            paths,
            components = new { securitySchemes = new { sessionToken = new { type = "apiKey", @in = "header", name = "X-Ample-Token" } } },
        };
        return JsonSerializer.Serialize(doc, Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
