# Cleanup parity scenario

1. Select archived leaves and request a cleanup preview.
2. Expect referenced and reclaimable sizes, a backup suggestion and no deletion during preview.
3. Complete exact typed acceptance and expect tombstones; operation-log undo must not claim it can cross cleanup.
