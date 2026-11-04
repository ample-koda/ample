using System.Reflection;

namespace Ample.Server.Tests;

/// <summary>The reasoning effort of one request wins over the server setting; "default" lets the model choose.</summary>
public sealed class AiEffortTests
{
    private static string? EffortOf(string? requested, string serverSetting)
    {
        AiTask task = new() { Request = new AiTaskRequest("custom", "project", "x", "song.amplet", "song.amplet", null, null, null, null, Effort: requested), Mode = "manual" };
        AiPolicy policy = AiPolicy.Read() with { Effort = serverSetting };
        MethodInfo method = typeof(AiService).GetMethod("EffortOf", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string?)method.Invoke(null, [task, policy]);
    }

    [Fact]
    public void The_request_wins_over_the_server_setting_and_default_lets_the_model_choose()
    {
        Assert.Equal("high", EffortOf("high", "low"));
        Assert.Equal("low", EffortOf(null, "low"));
        Assert.Null(EffortOf("default", "high"));
        Assert.Null(EffortOf(null, "default"));
    }
}
