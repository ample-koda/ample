namespace Ample.Docs.Tests;

public sealed class DocsSmokeTests
{
    [Fact]
    public void Assembly_is_loadable() => Assert.Equal("Ample.Docs", typeof(DocsAssembly).Assembly.GetName().Name);
}
