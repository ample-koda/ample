namespace Ample.Setup
{
    /// <summary>What a pack download that was run by the wizard amounts to.</summary>
    internal static class PackOutcome
    {
        /// <summary>
        /// Null when the pack was installed, else the reason. A download that had finished (exit code 0) before Stop arrived worked, also when Stop was pressed;
        /// only a download that did not finish is reported as cancelled.
        /// </summary>
        internal static string Describe(bool stopRequested, int exitCode, string lastLine)
        {
            if (stopRequested && exitCode != 0) return "Cancelled by you.";
            return exitCode == 0 ? null : (lastLine ?? "ample stopped with exit code " + exitCode + ".");
        }
    }
}
