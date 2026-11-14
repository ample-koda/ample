// Small stub of the Web Audio objects the deck rig uses, recording every connection so a test can trace signal paths.
export class StubParam {
  constructor() { this.value = 1; this.target = null; }
  setTargetAtTime(v) { this.target = v; }
  cancelScheduledValues() {}
}
export class StubNode {
  constructor(ctx, kind) { this.ctx = ctx; this.kind = kind; this.out = new Set(); this.gain = new StubParam(); this.disconnected = 0; }
  connect(n) { this.out.add(n); return n; }
  disconnect() { this.out.clear(); this.disconnected++; }
}
export class StubSource extends StubNode {
  constructor(ctx) { super(ctx, 'source'); this.calls = []; this.onended = null; }
  start(when, offset) { this.calls.push(['start', when, offset]); }
  stop(when) { this.calls.push(['stop', when]); }
}
export class StubTrack { constructor() { this.stopped = false; } stop() { this.stopped = true; } }
export class StubCtx {
  constructor(opts = {}) {
    this.currentTime = 0; this.state = 'running'; this.nodes = []; this.destination = new StubNode(this, 'destination');
    this.closed = 0; this.resumed = 0; this.sinkId = null; this.opts = opts;
    if (opts.noSinkId) this.setSinkId = undefined; else this.setSinkId = async (id) => { if (opts.sinkRejects) throw new Error(opts.sinkRejects); this.sinkId = id; };
    if (opts.noStream) this.createMediaStreamDestination = undefined;
  }
  mk(kind, C = StubNode) { const n = new C(this, kind); this.nodes.push(n); return n; }
  createGain() { return this.mk('gain'); }
  createBufferSource() { return this.mk('source', StubSource); }
  createMediaStreamDestination() { const n = this.mk('streamdest'); n.stream = { tracks: [new StubTrack(), new StubTrack()], getTracks() { return this.tracks; } }; return n; }
  createMediaStreamSource(stream) { const n = this.mk('streamsource'); n.stream = stream; return n; }
  async resume() { this.resumed++; this.state = 'running'; }
  async close() { this.closed++; this.state = 'closed'; }
}
/** Factory that records every context it makes; optsFor(index) gives per-context options. */
export function makeFactory(optsFor = () => ({})) {
  const made = [];
  const f = () => { const c = new StubCtx(optsFor(made.length)); made.push(c); return c; };
  f.made = made;
  return f;
}
/** True when a signal can flow from node a to node b through recorded connections (not across contexts). */
export function reaches(a, b, seen = new Set()) {
  if (a === b) return true;
  if (seen.has(a)) return false;
  seen.add(a);
  for (const n of a.out) if (reaches(n, b, seen)) return true;
  return false;
}
