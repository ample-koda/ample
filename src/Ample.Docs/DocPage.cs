namespace Ample.Docs;

/// <summary>One page of the generated help.</summary>
/// <param name="Id">Stable anchor id of the page.</param>
/// <param name="Title">Page title.</param>
/// <param name="Section">Sidebar section the page belongs to.</param>
/// <param name="Order">Sort key inside the section.</param>
/// <param name="Html">Rendered body (without the page title).</param>
/// <param name="SearchText">Plain text used to build the search index.</param>
/// <param name="Since">Spec version in which the item appeared, if known.</param>
/// <param name="Deprecated">Deprecation note, if any.</param>
public sealed record DocPage(string Id, string Title, string Section, int Order, string Html, string SearchText, string? Since = null, string? Deprecated = null);

/// <summary>A source of help pages. Adding a source means one class plus a registration in <see cref="HelpGenerator"/>.</summary>
public interface IDocSource
{
    /// <summary>Gets the source name, used in problem reports.</summary>
    string Name { get; }

    /// <summary>Gets the sidebar section title and its sort order.</summary>
    (string Title, int Order) Section { get; }

    /// <summary>Produces the pages of this source.</summary>
    IEnumerable<DocPage> GetPages(DocContext context);
}

/// <summary>Shared state while a help page set is generated.</summary>
public sealed class DocContext
{
    /// <summary>Initializes a new instance of the <see cref="DocContext"/> class.</summary>
    public DocContext(SpecRoot root, MarkdownRenderer renderer, CliCommandInfo? cli)
    {
        Root = root;
        Renderer = renderer;
        Cli = cli;
    }

    /// <summary>Gets the AMPLE folder with the spec data and docs.</summary>
    public SpecRoot Root { get; }

    /// <summary>Gets the Markdown renderer.</summary>
    public MarkdownRenderer Renderer { get; }

    /// <summary>Gets the CLI command tree, when the host supplied one.</summary>
    public CliCommandInfo? Cli { get; }

    /// <summary>Gets the problems found while generating (dead links, failing examples). A non-empty list fails <c>docs check</c>.</summary>
    public IList<string> Problems => Renderer.Problems;
}

/// <summary>Describes one command of the CLI for the generated reference.</summary>
/// <param name="Name">Command name.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Options">Options and arguments as <c>name: description</c> text.</param>
/// <param name="Subcommands">Nested commands.</param>
public sealed record CliCommandInfo(string Name, string Description, IReadOnlyList<string> Options, IReadOnlyList<CliCommandInfo> Subcommands);
