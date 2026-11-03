namespace Ample.Plugins.Tests;

public sealed class PluginsSmokeTests
{
    [Fact]
    public void Assembly_is_loadable() => Assert.Equal("Ample.Plugins", typeof(PluginsAssembly).Assembly.GetName().Name);
}
