using Ample.Engine;

namespace Ample.Core.Tests;

public sealed class CoreSmokeTests
{
    [Fact]
    public void Assembly_is_loadable() => Assert.Equal("Ample.Engine", typeof(CoreAssembly).Assembly.GetName().Name);
}
