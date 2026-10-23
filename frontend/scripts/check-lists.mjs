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
const { ListDetails } = load('ListDetails');
const port = { name: 'Values', type: 'List<Double>', access: 'List', optional: false, kind: 'list', itemKind: 'number', count: 3,
  items: [2, 4, 6].map(value => ({ label: String(value), value: String(value), visible: true })) };
const html = renderToStaticMarkup(React.createElement(ListDetails, { port, act() {} }));
assert.match(html, /aria-label="Values item 1"/);
assert.match(html, /value="4"/);
assert.match(html, /Move Values item 1 up/);
assert.match(html, /Move Values item 1 down/);
for (const [kind, value, expected] of [['boolean', 'true', /aria-pressed="true"/], ['text', 'a,b', /value="a,b"/], ['color', '#9AA0F2', /Color for Values/], ['extension', '2026-10-14', /2026-10-14/]]) {
  const rendered = renderToStaticMarkup(React.createElement(ListDetails, { port: { ...port, itemKind: kind, items: [{ label: value, value, visible: true }], count: 1 }, act() {} }));
  assert.match(rendered, expected);
}

const hooks = { ...React, useState: initial => [initial, () => {}], useEffect() {} };
const { ListRows } = load('ListRows', hooks);
const actions = [];
const tree = ListRows({ port, busy: false, act: action => actions.push(action), heading: 'Value', render: item => item.label });
const row = tree.props.children[1][1];
row.props.onClick();
row.props.onFocusCapture();
row.props.children.at(-1).props.children[0].props.onClick();
assert.deepEqual(JSON.parse(JSON.stringify(actions)),  [{ action: 'activeItem', name: 'Values', index: 1 }, { action: 'activeItem', name: 'Values', index: 1 }, { action: 'reorder', name: 'Values', from: 1, to: 0 }]);
assert.ok(actions.every(action => action.action !== 'selectPicked'));
const details = load('ListDetails', hooks).ListDetails({ port, act: action => actions.push(action) });
const itemNode = details.props.render(port.items[1], 1);
const itemTree = itemNode.type(itemNode.props);
itemTree.props.onPointerDownCapture();
const field = itemTree.props.children;
field.props.onScrub('7.25');
field.props.onScrub('NaN');
assert.deepEqual(JSON.parse(JSON.stringify(actions.slice(3))), [
  { action: 'activeItem', name: 'Values', index: 1 },
  { action: 'inputItem', name: 'Values', index: 1, value: '7.25' },
]);
const colorHooks = { ...hooks, useRef: initial => ({ current: initial }), useState: initial => [initial ?? 0, () => {}] };
const picker = load('PickInput', colorHooks).ColorInput({ port: { ...port, kind: 'color', access: 'Item', value: '#9AA0F2' }, act() {} });
const pickerView = picker.type(picker.props);
const [anchor, popup] = pickerView.props.children;
assert.equal(anchor.type.name, 'PopoverAnchor');
assert.equal(popup.type.name, 'PopoverContent');
assert.equal(popup.props.children.props.title, 'Values');
assert.equal(pickerView.props.open, true);
console.log('Color editor popup is separate from its scrolling anchor');
console.log('Shared list rows, scalar editors, activation without geometry selection, and keyboard reorder passed');

const repoActions = [];
const stateChanges = [];
const repoHooks = { ...hooks, useState: initial => [initial, value => stateChanges.push(value)] };
const { ExtensionsView } = load('Extensions', repoHooks);
function nodes(value) {
  if (Array.isArray(value)) return value.flatMap(nodes);
  if (!value || typeof value !== 'object' || !value.props) return [];
  return [value, ...nodes(value.props.children)];
}
const repoTree = ExtensionsView({ extensions: { repository: '/example/Extensions', items: [] }, send: action => repoActions.push(action), hidden: false });
const repoNodes = nodes(repoTree);
const menuItems = repoNodes.filter(node => node.type.name === 'DropdownMenuItem');
assert.equal(menuItems.length, 2);
menuItems[0].props.onSelect();
menuItems[1].props.onSelect();
repoNodes.find(node => node.props.title === 'Show in Finder or Explorer').props.onClick();
assert.deepEqual(JSON.parse(JSON.stringify(repoActions)), [{ action: 'extOpen' }, { action: 'extReveal' }]);
assert.deepEqual(stateChanges, [true]);
assert.ok(!repoNodes.some(node => node.props.children === 'New Extension'));
const emptyNodes = nodes(ExtensionsView({ send: action => repoActions.push(action), hidden: false }));
assert.ok(emptyNodes.some(node => node.type === 'button' && node.props.children.includes(' Choose existing repo…')));
console.log('Extension repo selection, creation, reveal, and empty state passed');
const populatedRepo = nodes(ExtensionsView({ extensions: { repository: '/example/Extensions', items: [{ name: 'CameraView', enabled: true, startup: false, generation: 1 }] }, send() {}, hidden: false }));
assert.equal(populatedRepo.filter(node => node.props.title === 'Show in Finder or Explorer').length, 1, 'Only the repo has a folder reveal button');
assert.equal(populatedRepo.find(node => node.props.className === 'ext-cols').props.children.length, 5, 'Extension columns match the five row controls');
console.log('Extension rows keep reload and use the single repo folder action');

const { GroupedSection } = load('Layout');
const section = (title, previewOff) => React.createElement(GroupedSection, {
  title, previewOff, names: [], onChange() {}, edit: false, fold: {}, toggle() {},
  head: null, row() {}, summary: () => ({ text: '' }), attention() {}, empty: null,
});
for (const inputsOff of [false, true]) for (const outputsOff of [false, true]) {
  const rendered = renderToStaticMarkup(React.createElement(React.Fragment, null, section('Inputs', inputsOff), section('Outputs', outputsOff)));
  const classes = [...rendered.matchAll(/<section class="([^"]*)"/g)].map(match => match[1].split(' ').includes('pv-off'));
  assert.deepEqual(classes, [inputsOff, outputsOff], 'master preview dimming is section-local');
}
console.log('Independent Inputs/Outputs Preview dimming passed in all four combinations');
