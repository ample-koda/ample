namespace Ample.Server.Tests;

public sealed class AiSampleSourcesTests
{
    [Fact]
    public void The_sample_sources_tool_has_a_route_for_an_ai_task_like_the_other_context_tools()
    {
        Assert.Contains(Routes.Catalog, op => op.Method == "GET" && op.Path == "/api/ai/context/sample-sources");
        Assert.Contains(Routes.Catalog, op => op.Method == "GET" && op.Path == "/api/sample-sources");
    }
}
