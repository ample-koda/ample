namespace Ample.Setup
{
    /// <summary>
    /// When a list that is read in the background (the sound packs of the Tune-up wizard) is started: once for a visit of its page. A list that could not be read waits
    /// for the next visit instead of being read again at once (the end of a load shows the page again, and showing it starts a load that is not marked loaded).
    /// </summary>
    internal sealed class LoadGate
    {
        private bool loaded, loading, failed;

        internal bool Loaded { get { return loaded; } }

        internal bool Loading { get { return loading; } }

        /// <summary>True when the load has to be started now. Ask it whenever the pages change; <paramref name="onPage"/> says whether the page of the list is the one shown.</summary>
        internal bool ShouldStart(bool onPage)
        {
            if (!onPage) { failed = false; return false; }   // leaving the page allows a new try on the next visit
            return !loaded && !loading && !failed;
        }

        /// <summary>The load has been started.</summary>
        internal void Started() { loading = true; }

        /// <summary>The load ended; <paramref name="ok"/> says whether the list could be read.</summary>
        internal void Finished(bool ok) { loading = false; loaded = ok; failed = !ok; }
    }
}
