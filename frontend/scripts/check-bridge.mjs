import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../src/bridge.ts', import.meta.url), 'utf8')
  .replace(/import \{ ([^}]+) \} from '\.\/mock';/, (_, names) => `const ${names.split(',').map(name => `${name.trim()} = {}`).join(', ')};`)
  .replaceAll('import.meta.env.DEV', 'false');
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;

function panel() {
  const timers = new Map();
  const messages = [];
  let nextId = 0;
  const window = { setInterval: callback => { timers.set(++nextId, callback); return nextId; }, clearInterval: id => timers.delete(id) };
  const exports = {};
  vm.runInNewContext(code, { window, exports });
  return { window, timers, messages, connect: exports.connect, tick: () => [...timers.values()].forEach(callback => callback()) };
}

const first = panel(), second = panel();
const received = [];
const closeFirst = first.connect(snapshot => received.push(snapshot), () => {});
const closeSecond = second.connect(() => {}, () => {});
first.tick(); // React mounted before Eto injected its bridge.
assert.equal(first.messages.length, 0);
first.window.eto = { postMessage: message => first.messages.push(JSON.parse(message)) };
first.tick(); first.tick(); // Readiness retries until acknowledged, including a lost initial reply.
assert.deepEqual(first.messages, [{ action: 'ready' }, { action: 'ready' }]);
assert.equal(second.messages.length, 0);
first.window.rhDockSnapshot({ revision: 1 });
assert.equal(received.length, 1);
assert.equal(first.timers.size, 0);
assert.equal(second.timers.size, 1); // Each panel owns its handshake.
second.window.eto = { postMessage: message => second.messages.push(JSON.parse(message)) };
second.tick();
assert.deepEqual(second.messages, [{ action: 'ready' }]);
closeFirst(); closeSecond();
assert.equal(second.timers.size, 0);
assert.equal(second.window.rhDockSnapshot, undefined);
console.log('Native bridge delayed readiness, acknowledgment, per-panel isolation, and cleanup passed');
