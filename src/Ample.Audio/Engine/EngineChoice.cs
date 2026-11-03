using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>
/// The engine a block asks for with its <c>engine</c> parameter (plan engine-per-block): where it is rendered (<c>local</c>, <c>faer</c> or <c>faer-else-local</c>) and, for the ACE blocks,
/// which model (a family keyword such as <c>xl-sft</c>, or a GGUF file name). An empty parameter means "follow the project".
/// </summary>
/// <param name="Route">The place: <c>local</c>, <c>faer</c> or <c>faer-else-local</c>.</param>
/// <param name="SynthModel">The family keyword that sets <c>acestep.synthModel</c>, or null.</param>
/// <param name="SynthModelFile">The GGUF file name that sets <c>acestep.synthModelFile</c>, or null.</param>
public sealed record EngineChoice(string Route, string? SynthModel, string? SynthModelFile)
{
    /// <summary>The words a route can be.</summary>
    public static readonly IReadOnlyList<string> Routes = ["local", "faer", "faer-else-local"];

    /// <summary>The family keywords an ACE model can be (the <c>acestep.synthModel</c> values).</summary>
    public static readonly IReadOnlyList<string> Families = ["default", "turbo", "sft", "base", "xl-turbo", "xl-sft", "xl-base"];

    /// <summary>Gets the block types that have an <c>engine</c> parameter.</summary>
    public static bool Applies(string blockType) => blockType is "ace-music" or "ai-voice" or "voice-clone";

    /// <summary>Gets whether a model was named.</summary>
    public bool HasModel => SynthModel is not null || SynthModelFile is not null;

    /// <summary>
    /// Reads the text of an <c>engine</c> parameter (quotes allowed). Returns null for an empty text.
    /// </summary>
    /// <param name="text">The parameter value.</param>
    /// <param name="blockType">The block type (a <c>voice-clone</c> takes a route only).</param>
    /// <exception cref="AmpleException">AMP5383 when the text is not a route with an optional model.</exception>
    public static EngineChoice? Parse(string? text, string blockType)
    {
        string value = (text ?? string.Empty).Trim().Trim('"').Trim();
        if (value.Length == 0)
        {
            return null;
        }

        int colon = value.IndexOf(':', StringComparison.Ordinal);
        string route = (colon < 0 ? value : value[..colon]).Trim();
        string? model = colon < 0 ? null : value[(colon + 1)..].Trim();
        if (!Routes.Contains(route, StringComparer.Ordinal))
        {
            throw new AmpleException("AMP5383", $"The engine '{value}' must start with local, faer or faer-else-local (for example faer:xl-sft).");
        }

        if (model is null)
        {
            return new EngineChoice(route, null, null);
        }

        if (blockType == "voice-clone")
        {
            throw new AmpleException("AMP5383", $"The engine '{value}' names a model, but a voice-clone block chooses its voice model with its own model parameter: write only {route}.");
        }

        if (model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && model.IndexOfAny(['/', '\\']) < 0 && model.Length > 5)
        {
            return new EngineChoice(route, null, model);
        }

        if (Families.Contains(model, StringComparer.Ordinal))
        {
            return new EngineChoice(route, model, null);
        }

        throw new AmpleException("AMP5383", $"The engine '{value}' names the model '{model}', which is neither a family ({string.Join(", ", Families)}) nor a .gguf file name.");
    }

    /// <summary>Gets the song settings this choice sets for the ACE plugin (the names without the <c>acestep.</c> prefix).</summary>
    public IReadOnlyDictionary<string, string> Settings()
    {
        Dictionary<string, string> settings = new(StringComparer.Ordinal);
        if (SynthModel is not null)
        {
            settings["synthModel"] = SynthModel;
        }

        if (SynthModelFile is not null)
        {
            settings["synthModelFile"] = SynthModelFile;
        }

        return settings;
    }
}
