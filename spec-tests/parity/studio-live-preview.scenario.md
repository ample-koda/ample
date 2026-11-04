# Live effect preview parity scenario

1. Open an effect connected to a built upstream take. Confirm its advanced controls shows that block's parameters; use the source audio block's arrow to navigate between its connected effects. The graph links remain the source of truth.
2. Start the primary live preview control. It changes to Stop, and the status identifies the effect and labels the local DSP approximate. Confirm playback continues through the shared player.
3. Expand Compare / render, play the selected input take, then request Play rendered effect. Confirm these comparison actions remain secondary to the primary live control.
4. Drag an EQ knob while audio is playing. Confirm the knob readout and live graph update before release, playback time continues without restarting, and no additional rendered-preview request occurs during the gesture. Release once and confirm one edit targets that effect and parameter.
5. Toggle A/B while local preview is live. Confirm the selected effect bypass changes in the running graph without restarting playback or issuing a rendered-preview request.
6. Open a source with effects on multiple branches. Search the effect picker, select one result, and confirm its own panel opens.
7. Insert + EQ on an audio source. Confirm it creates a separate neutral EQ block linked in the graph with Low, Mid and High at 0 dB.
8. Open an effect block's advanced controls when it has a downstream effect; confirm it opens only its own controls. Open a generator without an audio input; confirm Build block creates its own take, Play block plays that take, and effect-preview controls are absent.
9. Connect a convolution block, select a project or pack IR and start Live local preview from either Advanced button. Confirm that changing mix updates the running audio graph without fetching the IR again, while choosing another IR reloads the bounded preview.

The matching Playwright spec uses a fresh test server and synthetic audio fixtures. Passing those cases verifies browser behavior and request wiring, not playback or audio quality in an active project. Report any active-demo check separately.
