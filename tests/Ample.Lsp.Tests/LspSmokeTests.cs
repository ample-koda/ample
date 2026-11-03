namespace Ample.Lsp.Tests;

public sealed class LspSmokeTests
{
    [Fact]
    public void Assembly_is_loadable() => Assert.Equal("Ample.Lsp", typeof(LspAssembly).Assembly.GetName().Name);
}
