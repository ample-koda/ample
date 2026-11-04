# MIDI event node parity scenario

1. Open a converted module and select a track's virtual event node.
2. Filter retained generated events by kind, tick substring and one-based channel, then paginate.
3. Inspect a row's channel, data values and bounded payload preview; open the JSON editor and save a valid non-note event.
4. Edit an unmatched note event, and confirm matched Note On/Off pairs open the structured clip notes editor.
5. Add an event, remove one and undo; reopen source and regenerate MIDI to confirm untouched records and orphan notes remain in order.

Generated Node and Core fixtures cover event comment parsing, note pairing, field bounds, 50-row paging, supported-event banner facts and orphan-note export. Browser interaction remains unrun by owner instruction.
