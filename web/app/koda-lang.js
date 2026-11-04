// The language of Koda, a setting of the project (en or nl): read from the project settings, saved there, and used for every word of Koda's own interface. The model is told the same language by the server
// (a hint in the task), so Koda can speak Dutch while the project stays English. @ample:koda
import { api } from './api.js';
import { LANGUAGES, text } from '../lib/koda-text.mjs';

let lang = 'en';
const listeners = new Set();

export const kodaLanguage = () => lang;
export const tx = (key, vars) => text(lang, key, vars);
export const onKodaLanguage = (fn) => { listeners.add(fn); return () => listeners.delete(fn); };

function set(next) {
  const value = LANGUAGES.includes(next) ? next : 'en';
  if (value === lang) return;
  lang = value;
  for (const fn of listeners) fn(lang);
}

/** Reads the language of the project (quietly: English when the settings cannot be read). */
export async function loadKodaLanguage() {
  try { set((await api.get('/api/project/settings')).koda?.language); } catch { /* keep the language */ }
  return lang;
}

/** Saves the language in the project settings and uses it at once. */
export async function saveKodaLanguage(next) {
  if (!LANGUAGES.includes(next)) return lang;
  await api.post('/api/project/settings', { koda: { language: next } });
  set(next);
  return lang;
}
