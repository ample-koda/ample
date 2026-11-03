using Ample.Language;

namespace Ample.Docs;

/// <summary>Checks executable examples with the real language library: <c>run</c> examples must have no errors, <c>invalid CODE</c> examples must produce that code.</summary>
/// <param name="spec">The language data.</param>
/// <param name="knownCodes">All diagnostic codes of the catalogue.</param>
public sealed class AnalyzerExampleChecker(SpecData spec, IReadOnlySet<string> knownCodes) : IExampleChecker
{
    /// <inheritdoc />
    public string? Check(string kind, string? expectedCode, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "example is empty";
        }

        if (kind == "snippet")
        {
            return expectedCode is not null && knownCodes.Contains(expectedCode) ? null : $"snippet names an unknown code '{expectedCode}'";
        }

        AmpleDocument doc = Parser.Parse(text, spec);
        if (kind == "run")
        {
            List<string> errors = [.. doc.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} at {d.Line}:{d.Col}")];
            return errors.Count == 0 ? null : "run example has errors: " + string.Join(", ", errors);
        }

        if (kind == "invalid")
        {
            return expectedCode is not null && doc.Diagnostics.Any(d => d.Code == expectedCode) ? null : $"invalid example does not produce {expectedCode}";
        }

        return null;
    }
}
