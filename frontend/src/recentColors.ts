import { send } from './bridge';

/**
 * Colors the user picked recently, newest first, shared by every color picker. The plug-in keeps them in its per-user settings
 * (the panel page has no lasting storage of its own); the browser preview falls back to local storage.
 */
const key = 'rhdock.recent-colors';
const hex6 = /^#[0-9A-F]{6}$/;
const clean = (list: unknown) => Array.isArray(list) ? list.filter((c): c is string => typeof c === 'string' && hex6.test(c)).slice(0, 12) : [];

let colors: string[] = (() => { try { return clean(JSON.parse(localStorage.getItem(key) ?? '[]')); } catch { return []; } })();

export const recentColors = () => colors;

/** Takes the plug-in's saved list from a snapshot. */
export function loadRecentColors(text: string | undefined) {
  if (!text) return;
  try { colors = clean(JSON.parse(text)); } catch { /* keep what the page has */ }
}

/** Remembers a picked color (alpha dropped); the same color moves to the front instead of repeating. */
export function rememberColor(color: string) {
  const value = color.slice(0, 7).toUpperCase();
  if (!hex6.test(value) || colors[0] === value) return;
  colors = [value, ...colors.filter(c => c !== value)].slice(0, 12);
  const text = JSON.stringify(colors);
  try { localStorage.setItem(key, text); } catch { /* storage unavailable: the plug-in still keeps them */ }
  send({ action: 'recentColors', value: text });
}
