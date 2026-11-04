using System.Globalization;
using System.Reflection;

namespace Ample.Server.Tests;

public sealed class AiSampleLimitTests
{
    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void A_five_sample_request_stops_proposal_clips_at_five(int proposalClips, bool reached)
    {
        AiTask task = new() { ContinuationClipLimit = 5 };
        for (int i = 0; i < proposalClips; i++) task.Clips.Add(new AiClip { Id = i.ToString(CultureInfo.InvariantCulture), Source = "proposal" });
        task.Clips.Add(new AiClip { Id = "current", Source = "current" });

        MethodInfo guard = typeof(AiService).GetMethod("SampleLimitReached", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(reached, guard.Invoke(null, [task]));
    }
}
