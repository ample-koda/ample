using Ample.Core.Abstractions;

namespace Ample.Server.Tests;

public sealed class KodaBrowserPackTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-koda-browser-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private string Program()
    {
        string program = Path.Combine(folder, "program");
        Directory.CreateDirectory(program);
        return program;
    }

    [Fact]
    public void Without_a_driver_anywhere_Koda_says_plainly_that_it_has_no_browser()
    {
        AmpleException ex = Assert.Throws<AmpleException>(() => KodaBrowserPack.DriverSearchPathFor(Path.Combine(folder, "home"), Program(), null));

        Assert.Equal("AMP5458", ex.DiagnosticCode);
        Assert.Contains("ample packs install koda-browser", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_installed_pack_is_found_through_the_driver_search_path()
    {
        string home = Path.Combine(folder, "home");
        string package = Path.Combine(KodaBrowserPack.FolderOf(home), ".playwright", "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), "{}");

        Assert.True(KodaBrowserPack.Installed(home));
        Assert.Equal(KodaBrowserPack.FolderOf(home), KodaBrowserPack.DriverSearchPathFor(home, Program(), null));
    }

    [Fact]
    public void A_pack_folder_without_the_package_is_not_an_installed_pack()
    {
        string home = Path.Combine(folder, "home");
        Directory.CreateDirectory(Path.Combine(KodaBrowserPack.FolderOf(home), ".playwright"));

        Assert.False(KodaBrowserPack.Installed(home));
        Assert.Throws<AmpleException>(() => KodaBrowserPack.DriverSearchPathFor(home, Program(), null));
    }

    [Fact]
    public void A_driver_next_to_the_program_or_a_search_path_that_is_already_set_is_left_alone()
    {
        string home = Path.Combine(folder, "home");
        string withDriver = Program();
        Directory.CreateDirectory(Path.Combine(withDriver, ".playwright"));

        Assert.Null(KodaBrowserPack.DriverSearchPathFor(home, withDriver, null));
        Assert.Null(KodaBrowserPack.DriverSearchPathFor(home, Path.Combine(folder, "elsewhere"), folder));
    }
}
