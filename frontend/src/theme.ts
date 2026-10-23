/** The user's panel theme: light or dark, and an optional accent color. Applied as CSS variables on the page root, so every component and popover follows. */
/** `custom` holds the accents the user added with the picker, oldest first. */
export type Theme = { mode: 'dark' | 'light'; accent?: string; custom?: string[] };

export const accents = ['#5b61cf', '#b85c9e', '#c4742a'];
export const defaultAccent = accents[0];
const hex = /^#[0-9a-fA-F]{6}$/;

export function parseTheme(text?: string | null): Theme {
  try {
    const value = JSON.parse(text ?? '') as Partial<Theme>;
    const custom = Array.isArray(value.custom) ? value.custom.filter((c): c is string => typeof c === 'string' && hex.test(c)).slice(-8) : [];
    return { mode: value.mode === 'light' ? 'light' : 'dark', accent: typeof value.accent === 'string' && hex.test(value.accent) ? value.accent : undefined, custom };
  } catch { return { mode: 'dark' }; }
}

/** Relative luminance, so text on an accent fill can be black or white. */
function luminance(color: string) {
  const [r, g, b] = [1, 3, 5].map(i => parseInt(color.slice(i, i + 2), 16) / 255).map(c => c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function applyTheme(theme: Theme) {
  const root = document.documentElement;
  const light = theme.mode === 'light';
  root.dataset.theme = light ? 'light' : 'dark';
  const set = (name: string, value?: string) => value ? root.style.setProperty(name, value) : root.style.removeProperty(name);
  const a = theme.accent;
  set('--acfill', a);
  set('--ac-hov', a && `color-mix(in srgb, ${a} 88%, ${light ? 'black' : 'white'})`);
  set('--ac', a && (light ? `color-mix(in srgb, ${a} 88%, black)` : `color-mix(in srgb, ${a} 62%, white)`));
  // White text needs a dark enough fill (contrast 4.5:1 is a luminance of about 0.18 or less); otherwise use near-black.
  set('--on-ac', a && (luminance(a) > 0.18 ? '#111' : '#fff'));
}
