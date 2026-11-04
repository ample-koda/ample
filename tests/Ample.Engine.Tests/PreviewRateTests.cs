using System.Text.RegularExpressions;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>The preview rate lives in two places (the server and the browser preview); they must agree.</summary>
public sealed class PreviewRateTests
{
    [Fact]
    public void The_browser_preview_renders_at_the_rate_of_the_server_preview()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "web", "lib", "preview-rate.mjs")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        Assert.NotNull(folder);
        string text = File.ReadAllText(Path.Combine(folder, "web", "lib", "preview-rate.mjs"));
        Match match = Regex.Match(text, @"PREVIEW_RATE = (\d+);");
        Assert.True(match.Success, "web/lib/preview-rate.mjs must export PREVIEW_RATE = <number>;");
        Assert.Equal(BuildEngine.PreviewRate, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
