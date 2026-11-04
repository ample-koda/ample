# Assistant network budget

- Showing forty sample waveforms fetches serialized bounded summaries, not full song audio or browser AudioContext decodes.
- A stalled task-indicator read does not cause interval requests to accumulate.
- GET requests accept cancellation and have a finite timeout; closing the conversation cancels its pending poll.
- A task dialog suppresses the duplicate background indicator poll. Hidden tabs release their event stream; returning reconnects it.
- Stop clears the media source so the browser can release the streaming connection. Existing project playback/scrubbing coverage remains green.

The synthetic browser tests verify the first three behaviors without GPU generation or touching user tabs. Real browser Timing is still needed to attribute any remaining live delay to queueing versus server handling.
