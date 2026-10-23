import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

const require = createRequire(import.meta.url);
function load(name, hooks = React, cache = new Map()) {
  // Rendering checks do not exercise the native/Vite transport.
  if (name === 'bridge') return { send() {}, mirrorMode: false };
  if (cache.has(name)) return cache.get(name);
  const tsx = new URL(`../src/${name}.tsx`, import.meta.url);
  const source = readFileSync(existsSync(tsx) ? tsx : new URL(`../src/${name}.ts`, import.meta.url), 'utf8');
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 } }).outputText;
  const exports = {};
  cache.set(name, exports);
  vm.runInNewContext(code, { exports, require: id => id === 'react' ? hooks : id.startsWith('./') ? load(id.slice(2), hooks, cache) : id.startsWith('@/') ? load(id.slice(2), hooks, cache) : require(id) });
  return exports;
}
const { AgentActivity, groupActivity } = load('AgentActivity');
const item = (id, kind = 'tool', extra = {}) => ({ id, kind, text: '', title: `Call ${id}`, status: 'Completed', files: [], images: [], chips: [], checkpoint: false, ...extra });
const sequence = [item(1, 'user'), item(2), item(3, 'thought', { text: 'Reasoning' }), item(4), item(5, 'assistant'), item(6)];
assert.deepEqual(Array.from(groupActivity(sequence), g => g.map(i => i.id).join(',')), ['1', '2,3,4', '5', '6']);
const render = items => renderToStaticMarkup(React.createElement(AgentActivity, { items, all: items,
  detail: i => React.createElement('pre', null, i.detail), pictures: i => i.images.length ? React.createElement('span', null, 'Image preview') : null }));
let html = render([item(2), item(3)]);
assert.match(html, /2 tool calls/);
assert.match(html, /aria-expanded="false"/);
assert.doesNotMatch(html, /Call 2/);
html = render([item(2, 'tool', { status: 'InProgress', detail: 'Streaming output' }), item(3)]);
assert.match(html, /aria-expanded="true"/);
assert.match(html, /Streaming output/);
html = render([item(2, 'tool', { status: 'Failed', detail: 'Failure detail', images: [1] }), item(3)]);
assert.match(html, /1 failed/);
assert.match(html, /Failure detail/);
assert.match(html, /Image preview/);
console.log('PASS: Trace grouping boundaries, completed folding, active/error details, and image rendering.');