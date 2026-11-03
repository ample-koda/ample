# Component fixtures

`triple-notes.amplet` imports `components/triple-notes.amplec` (the demo of the brief: a typed block that repeats its notes) and uses it once. They are read by `tests/Ample.Core.Tests/ComponentFixtureTests.cs`, which checks that the song builds into 6 notes and that the component can be sealed, embedded, extracted and used again from the template without a change of the result.

These files are not part of the `valid/` corpus (no `.expected.json`): a component needs the project folder to find its import.
