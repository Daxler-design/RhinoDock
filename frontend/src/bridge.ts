import type { Action, Agent, AgentItem, PreviewStyle, Snapshot, StyleEdit } from './types';
import { mock, mockAgent } from './mock';

declare global { interface Window { eto?: { postMessage(message: string): void }; rhDockSnapshot?: (snapshot: Snapshot) => void; rhDockImage?: (id: number, url: string | null, small?: boolean) => void; rhDockImagePath?: (path: string, url: string | null, small?: boolean) => void } }

// Images stay out of snapshots: the host sends each one once, when the panel first shows it; small is a thumbnail of at most 400 px,
// used in the conversation, and the full image is asked for only when the preview opens. A local image an agent named in its reply is
// read only from the allowed folders, else the host answers null.
type Key = string;
const loaded = new Map<Key, Promise<string | null>>();
const waiting = new Map<Key, (url: string | null) => void>();
const keyOf = (kind: 'id' | 'path', ref: string | number, small: boolean) => `${kind}:${small ? 's' : 'f'}:${ref}`;
const arrive = (key: Key, url: string | null) => { waiting.get(key)?.(url); waiting.delete(key); };
window.rhDockImage = (id, url, small) => arrive(keyOf('id', id, !!small), url);
window.rhDockImagePath = (path, url, small) => arrive(keyOf('path', path, !!small), url);
// A mirrored page cannot receive the host's script calls, so it fetches images from the mirror server.
const mirrorImage = (query: string) => fetch('__rhdock/mirror/image?' + query, { cache: 'no-store' }).then(r => r.json() as Promise<{ url: string | null }>).then(r => r.url).catch(() => null);
function load(kind: 'id' | 'path', ref: string | number, small: boolean): Promise<string | null> {
  const key = keyOf(kind, ref, small);
  let url = loaded.get(key);
  if (!url) {
    url = mirrorMode ? mirrorImage(`${kind}=${encodeURIComponent(String(ref))}${small ? '&small=1' : ''}`) : new Promise(resolve => waiting.set(key, resolve));
    loaded.set(key, url);
    if (!mirrorMode) send(kind === 'id' ? { action: 'agentImage', value: Number(ref), small } : { action: 'agentImagePath', value: String(ref), small });
  }
  return url;
}
export const imagePath = (path: string, small = false) => load('path', path, small);
export const image = (id: number, small = false) => load('id', id, small);

// The rhDock Mirror in Rhino and the Vite development relay both serve the panel with ?mirror=1 and the same endpoints.
export const mirrorMode = !window.eto && /[?&]mirror(=|&|$)/.test(window.location?.search ?? '');

export function connect(receive: (snapshot: Snapshot) => void, disconnected: () => void): () => void {
  let readyTimer: number | undefined;
  window.rhDockSnapshot = snapshot => { window.clearInterval(readyTimer); receive(snapshot); };
  if (!mirrorMode && (window.eto || !import.meta.env.DEV)) {
    // Eto may inject its bridge after React mounts. Retry until the host's first snapshot acknowledges readiness.
    const ready = () => window.eto?.postMessage(JSON.stringify({ action: 'ready', viewport: {
      width: window.innerWidth, height: window.innerHeight, dpr: window.devicePixelRatio,
      scale: window.visualViewport?.scale,
    } }));
    readyTimer = window.setInterval(ready, 100);
    ready();
  }
  else if (mirrorMode) {
    let active = true;
    let polling = false;
    const poll = async () => {
      if (!active || polling) return;
      polling = true;
      try {
        const response = await fetch('__rhdock/mirror/state', { cache: 'no-store' });
        const state = await response.json() as { connected: boolean; snapshot?: Snapshot };
        if (!active) return;
        if (response.ok && state.connected && state.snapshot) receive(state.snapshot);
        else disconnected();
      } catch { if (active) disconnected(); }
      finally { polling = false; }
    };
    void poll();
    const timer = window.setInterval(poll, 350);
    return () => { active = false; window.clearInterval(timer); window.rhDockSnapshot = undefined; };
  } else if (import.meta.env.DEV) { mockReceive = receive; receive(mockState); }
  return () => { window.clearInterval(readyTimer); window.rhDockSnapshot = undefined; mockReceive = undefined; };
}

export function send(message: Action) {
  if (window.eto) window.eto.postMessage(JSON.stringify(message));
  else if (mirrorMode) void fetch('__rhdock/mirror/action', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(message),
  }).catch(() => {});
  else if (import.meta.env.DEV && message.action === 'agentImagePath') window.setTimeout(() => window.rhDockImagePath?.(String(message.value), String(message.value).includes('missing') ? null : mockImage(7), message.small), 50);
  else if (import.meta.env.DEV && message.action === 'agentImage') window.setTimeout(() => window.rhDockImage?.(Number(message.value), mockImage(Number(message.value)), message.small), 50);
  else if (import.meta.env.DEV && message.action !== 'clientError') { mockState = applyMock(mockState, message); mockReceive?.(mockState); }
}

let mockState: Snapshot = { ...mock, agent: { ...mockAgent, status: 'idle' } };
const historyFixture: AgentItem[] = import.meta.env.DEV && /[?&]history-stress=1/.test(window.location?.search ?? '')
  ? Array.from({ length: 500 }, (_, i) => ({ id: i + 1, kind: i % 2 ? 'assistant' : 'user',
    text: `History message ${i + 1}\n\n` + 'A retained passage for scrolling and selection. '.repeat(12),
    files: [], chips: [], images: [], checkpoint: false })) : [];
if (historyFixture.length) mockState = { ...mockState, agent: { ...mockAgent, status: 'ready',
  items: historyFixture.slice(-100), history: { before: 401, hasMore: true, total: 500 } } };
if (import.meta.env.DEV) window.setInterval(() => {
  const solve = mockState.solve;
  if (!solve?.playing) return;
  const frame = solve.frame + 1;
  mockState = { ...mockState, status: 'completed', outputs: mock.outputs, statusLine: `Iteration ${frame}/50`, solve: { ...solve, frame, fps: 5, playing: frame < 50, done: frame >= 50 } };
  mockReceive?.(mockState);
}, 200);
let mockReceive: ((snapshot: Snapshot) => void) | undefined;
const mockViews = new Map<string, Agent>();
let mockViewId = 0;

// A reproducible long-output layout fixture, available only in the Vite sample preview.
if (import.meta.env.DEV && new URLSearchParams(window.location.search).has('agent-stress')) {
  const reply = Array.from({ length: 60 }, (_, i) => `### Section ${i + 1}\n\nStreaming paragraph with **Markdown**, inline \`code\`, and enough words to wrap across several lines in a narrow Rhino panel. Keep the composer stable as more output arrives.\n\n`).join('');
  let end = 0;
  mockState = { ...mock, agent: { ...mockAgent, status: 'working', permission: null, question: null, changes: [],
    config: mockAgent.config.map(o => o.category === 'model' ? { ...o, options: o.options.map(c => ({ ...c, name: 'DeepSeek: DeepSeek V4 Flash Latest with a very long provider and model name' })) } : o),
    items: [{ ...mockAgent.items[0], images: [], text: 'Stream a long Markdown response for the layout check.' }, { ...mockAgent.items.find(i => i.kind === 'assistant')!, text: '' }] } };
  window.setInterval(() => {
    if (!mockState.agent || mockState.agent.status !== 'working') return;
    end = Math.min(end + 32, reply.length);
    mockState = { ...mockState, agent: { ...mockState.agent, status: end === reply.length ? 'ready' : 'working', items: [mockState.agent.items[0], { ...mockState.agent.items[1], text: reply.slice(0, end) }] } };
    mockReceive?.(mockState);
  }, 80);
}

function mockImage(id: number) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="800" height="500"><rect width="800" height="500" fill="hsl(${id * 47 % 360} 40% 35%)"/><text x="400" y="260" font-size="48" fill="white" text-anchor="middle" font-family="sans-serif">Sample image ${id}</text></svg>`;
  return 'data:image/svg+xml;base64,' + btoa(svg);
}

function applyMock(state: Snapshot, action: Action): Snapshot {
  if (action.action === 'activeItem') return { ...state, inputs: state.inputs.map(p => ({ ...p, activeIndex: p.name === action.name ? action.index ?? null : null })) };
  if (action.action === 'inputItem') {
    const inputs = state.inputs.map(p => {
      if (p.name !== action.name || !p.items) return p;
      const items = p.items.map((item, i) => i === action.index ? { ...item, label: String(action.value), value: String(action.value) } : item);
      return { ...p, items, value: items.map(item => item.value ?? item.label).join(', ') };
    });
    return { ...state, inputs, outputs: [], status: 'pending' };
  }
  const inputs = state.inputs.map(p => p.name !== action.name ? p : {
    ...p,
    ...(action.action === 'input' ? { value: action.value === null ? undefined : String(action.value) } : {}),
    ...(action.action === 'range' ? { range: action.range ?? undefined } : {}),
    ...(action.action === 'style' && typeof action.style === 'string' ? { boolStyle: action.style } : {}),
    ...(action.action === 'pstyle' && p.preview && typeof action.style === 'object' ? { preview: { ...p.preview, style: mockStyle(p.preview.style, action.style) } } : {}),
    ...(action.action === 'preview' && p.preview ? { preview: { ...p.preview, visible: !!action.visible } } : {}),
    ...(action.action === 'element' && p.items ? { items: p.items.map((item, i) => i === action.index ? { ...item, visible: !!action.visible } : item) } : {}),
    ...(action.action === 'reorder' && p.items && action.from !== undefined && action.to !== undefined ? {
      items: move(p.items, action.from, action.to),
      ...(p.kind !== 'pick' ? { value: move(p.items, action.from, action.to).map(item => item.value ?? item.label).join(', ') } : {}),
      activeIndex: p.activeIndex == null ? null : move(p.items.map((_, i) => i), action.from, action.to).indexOf(p.activeIndex),
    } : {}),
    ...(action.action === 'input' && p.kind === 'pick' && action.value === null ? { picked: undefined, items: [], count: 0 } : {}),
  });
  const agent = state.agent;
  if (agent && action.action.startsWith('agent')) switch (action.action) {
    case 'agentFavorite': { const value = String(action.value), favorites = agent.favorites ?? []; return { ...state, agent: { ...agent, favorites: favorites.includes(value) ? favorites.filter(f => f !== value) : [...favorites, value] } }; }
    case 'agentSelect': {
      const selected = agent.agents?.find(a => a.id === action.value);
      if (!selected) return state;
      mockViews.set(agent.viewId ?? agent.sessionId ?? 'new', agent);
      const previous = [...mockViews.values()].find(a => a.agentId === selected.id);
      return { ...state, agent: previous ?? { ...agent, viewId: 'sample-' + selected.id, agentId: selected.id, agentName: selected.name, agent: selected.name,
        status: selected.installed ? 'ready' : 'missing', sessionId: null, title: null, items: [], attachments: [], permission: null, question: null, changes: [], queued: [],
        mode: selected.id === 'pi' ? 'all' : 'ask', features: { steer: selected.id !== 'hermes', rewind: selected.id !== 'pi', permissions: selected.id !== 'pi' } } };
    }
    case 'agentHostConnect': return { ...state, agent: { ...agent, hostTools: { connected: !agent.hostTools?.connected } } };
    case 'agentHistory': {
      if (!historyFixture.length || action.index === undefined || action.index !== agent.history?.before) return state;
      const start = Math.max(0, action.index - 1 - 100);
      return { ...state, agent: { ...agent, items: historyFixture.slice(start), history: { before: start + 1, hasMore: start > 0, total: historyFixture.length } } };
    }
    case 'agentConnect': return { ...state, agent: { ...agent, status: 'ready' } };
    case 'agentNew': { mockViews.set(agent.viewId ?? 'new', agent); return { ...state, agent: { ...agent, viewId: 'sample-new-' + ++mockViewId, sessionId: null, hostTools: { connected: false }, status: 'ready', items: [], queued: [], title: null, permission: null, question: null, changes: [] } }; }
    case 'agentSide': { mockViews.set(agent.viewId ?? 'new', agent); return { ...state, agent: { ...agent, viewId: 'sample-side-' + ++mockViewId, mode: 'plan', items: [], queued: [], title: null, permission: null, question: null, changes: [],
      side: { parentViewId: agent.viewId ?? 'new', parentSessionId: agent.sessionId, quote: action.values?.quote ?? '', forked: true } } }; }
    case 'agentSideClose': { const parent = agent.side && mockViews.get(agent.side.parentViewId); return parent ? { ...state, agent: parent } : state; }
    case 'agentLoad': { mockViews.set(agent.viewId ?? 'new', agent); return { ...state, agent: [...mockViews.values()].find(a => a.sessionId === action.value) ?? { ...mockAgent, viewId: 'sample-' + action.value, sessionId: String(action.value) } }; }
    case 'agentPermission': return { ...state, agent: { ...agent, permission: null } };
    case 'agentQuestion': return { ...state, agent: { ...agent, question: null } };
    case 'agentCancel': return { ...state, agent: { ...agent, status: 'ready' } };
    case 'agentOption': return { ...state, agent: { ...agent, config: agent.config.map(o => o.id === action.name ? { ...o, current: String(action.value) } : o) } };
    case 'agentPrompt': { const id = agent.items.length + 10; const base = { files: [], images: [] as number[], checkpoint: true };
      if (agent.status === 'working') return { ...state, agent: { ...agent, queued: [...agent.queued, { id, text: String(action.value), steer: !!action.steer, draft: action.draft }] } };
      return { ...state, agent: { ...agent, attachments: [], items: [...agent.items,
      { ...base, id, kind: 'user', text: String(action.value), chips: [...(action.context ? ['Facade Study'] : []), ...(action.mentions ?? []).map(m => m.label), ...agent.attachments.map(a => a.name)] },
      { ...base, id: id + 1, kind: 'assistant', text: 'This is the browser preview sample; the real agent answers in Rhino.', chips: [], checkpoint: false }] } }; }
    case 'agentMode': return { ...state, agent: { ...agent, mode: action.value as Agent['mode'] } };
    case 'agentAttach': return { ...state, agent: { ...agent, attachments: [...agent.attachments, { id: Date.now(), name: action.name ?? 'image', kind: 'image', thumb: action.thumb }] } };
    case 'agentCapture': return { ...state, agent: { ...agent, attachments: [...agent.attachments, { id: Date.now(), name: 'Perspective viewport.png', kind: 'image' }] } };
    case 'agentDetach': return { ...state, agent: { ...agent, attachments: agent.attachments.filter(a => a.id !== action.index) } };
    case 'agentSteer': return { ...state, agent: { ...agent, queued: agent.queued.map(q => q.id === action.index ? { ...q, steer: true } : q) } };
    case 'agentUnqueue': return { ...state, agent: { ...agent, queued: agent.queued.filter(q => q.id !== action.index) } };
    case 'agentEditQueued': return { ...state, agent: { ...agent, queued: agent.queued.filter(q => q.id !== action.index) } };
    case 'agentKeep': return { ...state, agent: { ...agent, changes: [] } };
    case 'agentRevert': return { ...state, agent: { ...agent, changes: agent.changes.filter(c => c.path !== action.path) } };
    default: return state;
  }
  switch (action.action) {
    case 'input': return { ...state, inputs, outputs: [], status: 'pending' };
    case 'pick': return { ...state, picking: { name: action.name ?? '', mode: action.mode ?? 'pick', index: action.index ?? -1 } };
    case 'cancelPick': return { ...state, picking: null };
    case 'range': case 'style': case 'element': case 'reorder': return { ...state, inputs, ...(action.action === 'reorder' ? { outputs: [], status: 'pending' as const } : {}) };
    case 'preview': case 'pstyle': return { ...state, inputs, outputs: state.outputs.map(o => o.name === action.name
      ? { ...o, ...(action.action === 'preview' ? { visible: !!action.visible } : o.style && typeof action.style === 'object' ? { style: mockStyle(o.style, action.style) } : {}) } : o) };
    case 'layout': return action.layoutRevision !== (state.layout?.rev ?? 0) ? state : { ...state, layout: { rev: (state.layout?.rev ?? 0) + 1, inputs: action.inputs!, outputs: action.outputs! } };
    case 'previewAll': return { ...state, previewAll: !!action.visible };
    case 'auto': return { ...state, auto: !!action.value };
    case 'select': return { ...state, revision: state.revision + 1, selected: action.path, status: 'pending', outputs: [], inputs: state.inputs.map(p => ({ ...p, value: undefined })),
      solve: action.path?.includes('CurveSmooth') ? { playing: false, frame: 0, done: false, stalled: false, rate: 0, held: false } : null };
    case 'play': return state.solve ? { ...state, solve: { ...state.solve, playing: true, done: false, frame: state.solve.done ? 0 : state.solve.frame } } : state;
    case 'pause': return state.solve ? { ...state, solve: { ...state.solve, playing: false } } : state;
    case 'step': return state.solve ? { ...state, status: 'completed', outputs: mock.outputs, solve: { ...state.solve, frame: state.solve.frame + 1 } } : state;
    case 'resetSolve': return state.solve ? { ...state, status: 'pending', outputs: [], solve: { ...state.solve, playing: false, done: false, frame: 0 } } : state;
    case 'rate': return state.solve ? { ...state, solve: { ...state.solve, rate: Number(action.value) } } : state;
    case 'run': return { ...state, status: 'completed', inputs: inputs.map(p => action.values && p.name in action.values ? { ...p, value: action.values[p.name] ?? undefined } : p), outputs: mock.outputs, log: ['Run completed in 42 ms'] };
    case 'pulse': return { ...state, status: 'completed', outputs: mock.outputs, log: [`Ran once with ${action.name} = true`] };
    case 'new': return { ...state, repository: `~/Projects/${action.value}`, selected: undefined, scripts: mock.scripts, inputs: [], outputs: [], status: 'ready', revision: state.revision + 1 };
    case 'open': return { ...mock, revision: state.revision + 1 };
    case 'bake': return { ...state, log: [`Baked ${(action.names ?? []).join(', ')} in the preview sample`] };
    case 'export': return { ...state, log: ['Exported the preview sample'] };
    default: return state;
  }
}

function mockStyle(old: PreviewStyle, edit: StyleEdit): PreviewStyle {
  const channel = (value: StyleEdit['fill'], fallback: string) => value && ({ color: value.color ?? fallback, custom: value.color !== null, opacity: value.opacity });
  return { ...old, ...edit, color: edit.edges?.color ?? edit.color ?? old.color, custom: edit.edges ? edit.edges.color !== null : edit.color !== null,
    edges: channel(edit.edges, '#40E0D0') ?? old.edges, fill: channel(edit.fill, '#40E0D0') ?? old.fill };
}

function move<T>(items: T[], from: number, to: number) {
  const next = items.slice();
  next.splice(to, 0, ...next.splice(from, 1));
  return next;
}
