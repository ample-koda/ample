namespace Ample.Language.Tests;

public sealed class LanguageSmokeTests
{
    [Fact]
    public void Assembly_is_loadable() => Assert.Equal("Ample.Language", typeof(LanguageAssembly).Assembly.GetName().Name);
}
