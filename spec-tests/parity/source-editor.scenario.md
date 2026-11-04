# Source editor parity scenario

1. Open a valid project in Source, edit `song.amplet` and choose Apply.
2. Expect the working graph to update without a history revision; choose Save and expect one revision.
3. Introduce invalid text and expect leaving to be blocked; opening an already-invalid project starts in Source.
4. After a reload with a browser recovery copy that differs from the saved song, show a large side-by-side diff with saved/recovered sizes, draft time and added/removed lines; require an explicit restore or saved-version choice.
