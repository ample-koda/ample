namespace Ample.Audio.Engine;

/// <summary>Integer-cent pitch path for a voice that stays active across slide notes.</summary>
internal static class NoteGlide
{
    internal static int Cents(int pitch, int sample, IReadOnlyList<(int Start, int End, int Pitch)> path)
    {
        int previous = pitch;
        foreach ((int slideStart, int slideEnd, int target) in path)
        {
            if (sample < slideStart) break;
            if (sample < slideEnd) return (previous - pitch) * 100 + (int)((long)(target - previous) * 100 * (sample - slideStart) / Math.Max(1, slideEnd - slideStart));
            previous = target;
        }
        return (previous - pitch) * 100;
    }
}
