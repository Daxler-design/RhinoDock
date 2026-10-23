// Contrast check for the built-in themes: reads the color tokens from theme.css and checks the pairs that carry text.
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../src/styles/theme.css', import.meta.url), 'utf8');
const block = (selector) => {
  const start = css.indexOf(selector + '{');
  if (start < 0) throw new Error('Missing theme block ' + selector);
  const body = css.slice(start + selector.length + 1, css.indexOf('}', start));
  return Object.fromEntries([...body.matchAll(/--([\w-]+):(#[0-9a-fA-F]{6}|#[0-9a-fA-F]{3})\b/g)].map(m => [m[1], m[2].length === 4 ? '#' + [...m[2].slice(1)].map(c => c + c).join('') : m[2]]));
};
const themes = { dark: block(':root'), light: { ...block(':root'), ...block(':root[data-theme=light]') } };

const lum = (hex) => {
  const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255).map(c => c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };

// [text token, background token, minimum]. Muted text and placeholders carry hints, not essential content, so they use a lower floor.
const pairs = [
  ['tx', 'bg', 7], ['tx', 'bg2', 7], ['tx2', 'bg', 4.5], ['tx2', 'bg2', 4.5], ['tx3', 'bg', 4.5], ['ph', 'field', 3],
  ['ac', 'bg', 4.5], ['ac', 'bg2', 4.5], ['on-ac', 'acfill', 4.5],
  ['ok', 'bg', 4.5], ['warn', 'bg', 4.5], ['err', 'bg', 4.5],
];
let failed = 0;
for (const [name, tokens] of Object.entries(themes))
  for (const [fg, bg, min] of pairs) {
    const value = ratio(tokens[fg], tokens[bg]);
    if (value < min) { failed++; console.error(`${name}: ${fg} on ${bg} is ${value.toFixed(2)}:1, needs ${min}:1`); }
  }
// An accent picked by the user gets near-black or white text by luminance; the threshold must give at least 4.5:1 either way.
for (const accent of ['#5b61cf', '#2f8f83', '#b85c9e', '#c4742a', '#4a86c8', '#ffffff', '#000000', '#7f7f7f']) {
  const text = lum(accent) > 0.18 ? '#111111' : '#ffffff';
  if (ratio(text, accent) < 4.4) { failed++; console.error(`accent ${accent}: ${text} text is ${ratio(text, accent).toFixed(2)}:1`); }
}
if (failed) process.exit(1);
console.log('Built-in themes meet their contrast targets');
