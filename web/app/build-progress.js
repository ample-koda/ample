// GUI flow 11: a thin bar along the top of the page that moves while a build runs (the status line of the Build button is easy to miss).
import { S, subscribe } from './store.js';

export function initBuildProgress() {
  const bar = document.createElement('div');
  bar.id = 'build-progress'; bar.setAttribute('role', 'progressbar'); bar.setAttribute('aria-label', 'Build in progress'); bar.setAttribute('aria-hidden', 'true');
  document.body.append(bar);
  const update = () => { const running = Boolean(S.build?.running); bar.classList.toggle('on', running); bar.setAttribute('aria-hidden', String(!running)); };
  subscribe(update);
  update();
}
