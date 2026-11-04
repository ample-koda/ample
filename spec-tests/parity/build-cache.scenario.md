# Build cache parity scenario

1. Build a graph and observe per-block state and inner-step progress.
2. Build it again unchanged and expect no renderer invocation, only cache hits.
3. Change one parameter and expect only that block and its downstream dependants to become stale.
