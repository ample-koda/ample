using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ample.Audio.Engine;

/// <summary>
/// The same renderer with <c>engine float</c>: it may use floating-point code that AMPLE does not prove identical on every platform (an SF3 SoundFont, whose Ogg Vorbis
/// samples are decoded with floating point), so it claims <c>machine-exact</c> (the same machine and toolprint give the same bytes) instead of <c>exact</c>. The toolprint
/// names the platform, so a take from another machine is not taken for one of this machine. The block type is internal (<c>gm.float</c>): a block reaches it only
/// through its router.
/// </summary>
internal sealed class MachineExactRenderer(IBlockRenderer inner) : IBlockRenderer
{
    public string BlockType => inner.BlockType + ".float";

    public string Id => inner.Id + "-float";

    public string Version => inner.Version;

    public string Determinism => "machine-exact";

    public string ToolprintJson => JsonSerializer.Serialize(new { inner = JsonSerializer.Deserialize<JsonElement>(inner.ToolprintJson), engine = "float", os = RuntimeInformation.OSDescription, arch = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription });

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => inner.RenderAsync(request, cancellationToken);

    /// <summary>The engine of a block: <c>integer</c> (the default, exact everywhere) or <c>float</c> (machine-exact); anything else is AMP5383.</summary>
    public static bool IsFloat(Ample.Language.BlockNode block)
    {
        string engine = block.Parameters.FirstOrDefault(p => p.Name == "engine")?.Value.Trim().Trim('"') ?? string.Empty;
        return engine switch
        {
            "" or "integer" => false,
            "float" => true,
            _ => throw new Ample.Core.Abstractions.AmpleException("AMP5383", $"The engine of a {block.Type} block is integer (the default) or float, not '{engine}'."),
        };
    }
}
