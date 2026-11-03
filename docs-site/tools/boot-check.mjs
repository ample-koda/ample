// Starts the built site in Node with a stand-in for the browser and reports the errors that a browser would throw while the page boots:
// a variable used before it is declared (let/const in the temporal dead zone), a name that does not exist, a syntax error.
// It runs the scripts of site/index.html in their order, then a few animation frames and timers, then a page change.
// No browser and no network: everything the page asks of the browser answers with a harmless stand-in.
// Usage: node tools/boot-check.mjs   (exit 1 when a script fails; build.mjs runs it after every build)
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';

const here = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
const site = path.join(here, 'site');
const html = fs.readFileSync(path.join(site, 'index.html'), 'utf8');
const scripts = [...html.matchAll(/<script\s+src="([^"]+)"/g)].map((m) => m[1].split('?')[0]).filter((s) => !/^https?:/.test(s));

// anything: every property is the stand-in again, it can be called and constructed, it turns into '' or 0
const stub = new Proxy(function () {}, {
  get(t, k) {
    if (k === Symbol.toPrimitive) return (hint) => (hint === 'number' ? 0 : '');
    if (k === 'then') return undefined;
    if (k === Symbol.iterator) return function* () {};
    if (k === 'length' || k === 'size') return 0;
    if (k === 'classList') return { add() {}, remove() {}, toggle() { return false; }, contains() { return false; } };
    if (k === 'dataset' || k === 'style') return {};
    if (k === 'querySelectorAll' || k === 'getElementsByTagName' || k === 'getElementsByClassName') return () => [];
    return stub;
  },
  apply() { return stub; },
  construct() { return stub; },
  set() { return true; },
  has() { return true; },
});

const frames = [], timers = [], listeners = new Map(), problems = [];
const on = (type, fn) => { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(fn); };
const store = () => { const m = new Map(); return { getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k), clear: () => m.clear(), key: () => null, get length() { return m.size; } }; };
const location = { hash: '', pathname: '/', search: '', origin: 'https://example.invalid', href: 'https://example.invalid/', protocol: 'https:', host: 'example.invalid', replace() {}, assign() {}, reload() {} };
const element = () => stub;
const document = new Proxy({
  getElementById: element, querySelector: element, querySelectorAll: () => [], createElement: element, createElementNS: element, createTextNode: element,
  addEventListener: on, removeEventListener() {}, dispatchEvent(e) { (listeners.get(e && e.type) || []).forEach((f) => safely(() => f(e), 'event ' + e.type)); return true; },
  body: stub, head: stub, documentElement: stub, visibilityState: 'visible', hidden: false, title: '', cookie: '', fonts: { ready: Promise.resolve() },
}, { get(t, k) { return k in t ? t[k] : stub; } });

class CustomEvent { constructor(type, init) { this.type = type; this.detail = init && init.detail; } }
const context = {
  console: { log() {}, info() {}, warn() {}, error() {}, debug() {} },
  document, location, history: { pushState() {}, replaceState() {}, back() {} }, navigator: { userAgent: 'boot-check', clipboard: { writeText: () => Promise.resolve() }, mediaSession: stub },
  localStorage: store(), sessionStorage: store(), indexedDB: stub,
  requestAnimationFrame: (f) => { frames.push(f); return frames.length; }, cancelAnimationFrame() {},
  setTimeout: (f, ms) => { if (typeof f === 'function') timers.push(f); return timers.length; }, clearTimeout() {}, setInterval: (f) => { if (typeof f === 'function') timers.push(f); return timers.length; }, clearInterval() {},
  queueMicrotask: (f) => Promise.resolve().then(f),
  addEventListener: on, removeEventListener() {}, dispatchEvent: (e) => document.dispatchEvent(e),
  matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {}, addListener() {} }),
  getComputedStyle: () => stub, devicePixelRatio: 1, innerWidth: 1280, innerHeight: 800, scrollY: 0, scrollTo() {}, open() { return null; }, alert() {}, confirm() { return false; },
  performance: { now: () => 1000 }, fetch: () => Promise.reject(new Error('no network in the boot check')),
  AudioContext: function () { return stub; }, webkitAudioContext: function () { return stub; }, OfflineAudioContext: function () { return stub; }, Audio: function () { return stub; },
  Image: function () { return stub; }, ResizeObserver: function () { return { observe() {}, disconnect() {} }; }, IntersectionObserver: function () { return { observe() {}, disconnect() {} }; }, MutationObserver: function () { return { observe() {}, disconnect() {} }; },
  CustomEvent, Event: CustomEvent, KeyboardEvent: CustomEvent, URL, URLSearchParams, TextEncoder, TextDecoder, Blob: function () { return stub; }, crypto: globalThis.crypto, atob, btoa,
};
context.window = context; context.self = context; context.globalThis = context;
vm.createContext(context);

// only the errors of this kind fail the check; the stand-in can make other code take odd paths, those are reported as notes
const fatal = (e) => !!e && (e.name === 'ReferenceError' || e.name === 'SyntaxError' || /before initialization|is not defined/.test(String(e.message)));
function safely(fn, where) {
  try { fn(); } catch (e) { (fatal(e) ? problems : notes).push(where + ': ' + (e && e.message)); }
}
const notes = [];
for (const s of scripts) {
  const file = path.join(site, s);
  if (!fs.existsSync(file)) continue;
  safely(() => vm.runInContext(fs.readFileSync(file, 'utf8'), context, { filename: s }), s);
}
for (let round = 0; round < 3; round++) {
  const fs_ = frames.splice(0); fs_.forEach((f) => safely(() => f(1000 + round * 16), 'animation frame'));
  const ts = timers.splice(0, 200); ts.forEach((f) => safely(f, 'timer'));
}
for (const hash of ['#/learn/music/basic/chords', '#/blocks/synth', '']) { location.hash = hash; (listeners.get('hashchange') || []).forEach((f) => safely(() => f(new CustomEvent('hashchange')), 'page ' + (hash || '/'))); frames.splice(0).forEach((f) => safely(() => f(2000), 'animation frame')); }
await new Promise((r) => setImmediate(r));

if (problems.length) {
  console.log('boot check: ' + problems.length + ' error(s) a browser would show while the page starts:');
  for (const p of [...new Set(problems)].slice(0, 20)) console.log('  ' + p);
  process.exit(1);
}
console.log('boot check: ok (' + scripts.length + ' scripts' + (notes.length ? ', ' + new Set(notes).size + ' note(s) from the stand-in browser, not errors' : '') + ')');
