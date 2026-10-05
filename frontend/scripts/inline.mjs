import { readFileSync, writeFileSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Script } from 'node:vm';

const folder = fileURLToPath(new URL('../dist/', import.meta.url));
const index = join(folder, 'index.html');
let html = readFileSync(index, 'utf8');
for (const file of readdirSync(join(folder, 'assets'))) {
  const contents = readFileSync(join(folder, 'assets', file), 'utf8');
  // A classic inline script runs as soon as it is parsed, so place it after #root.
  // The panel runs it as a classic script; module-only syntax such as import.meta would leave it blank.
  if (file.endsWith('.js')) new Script(contents, { filename: file });
  if (file.endsWith('.js')) html = html
    .replace(`<script type="module" crossorigin src="./assets/${file}"></script>`, '')
    .replace('</body>', () => `<script>${contents.replace(/<\/script/gi, '<\\/script')}</script></body>`);
  if (file.endsWith('.css')) html = html.replace(`<link rel="stylesheet" crossorigin href="./assets/${file}">`, () => `<style>${contents}</style>`);
}
if (html.includes('src="./assets/') || html.includes('href="./assets/')) throw new Error('Frontend still references external assets');
writeFileSync(index, html);
rmSync(join(folder, 'assets'), { recursive: true });
