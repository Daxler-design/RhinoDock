import { useEffect, useRef, useState } from 'react';
import { Bot, ChevronDown, ChevronRight, CircleHelp, Eye, EyeOff, FileCode2, FolderOpen, Play, Plus, Search, Settings2, Terminal, Upload, Zap } from 'lucide-react';
import { AgentHeader, AgentView } from './Agent';
import { connect, mirrorMode, send } from './bridge';
import { fitRange, inside, NumberField, RangeRow, recenter } from './NumberField';
import { ColorInput, PickButton, PickDetails } from './PickInput';
import { SolveBar } from './SolveBar';
import { Badge } from '@/components/ui/badge';
import { StyleGlyph, StylePopover } from './StylePopover';
import type { Action, Output, Port, Range, Snapshot } from './types';

function shortType(type: string) {
  const item = type.match(/^System\.Collections\.Generic\.(?:List|IEnumerable|IReadOnlyList)`1\[\[([^,\]]+)/)?.[1];
  return item ? `${item.split('.').pop()} list` : type.split(',')[0].split('.').pop()?.replace('[]', ' list') ?? type;
}
function invalidNumber(port: Port, value: string) { return !!value.trim() && (port.kind === 'number' && !Number.isFinite(Number(value)) || port.kind === 'int' && !/^[+-]?\d+$/.test(value.trim())); }

export default function App() {
  const [snap, setSnap] = useState<Snapshot>();
  const [tab, setTab] = useState<'script' | 'agent'>('script');
  const [conversations, setConversations] = useState(false);
  const [palette, setPalette] = useState(false);
  const [menu, setMenu] = useState(false);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const [query, setQuery] = useState('');
  const [highlight, setHighlight] = useState(0);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [consoleOpen, setConsoleOpen] = useState(false);
  const [selectedBake, setSelectedBake] = useState<string[]>([]);
  const [styleOpen, setStyleOpen] = useState<string>();
  const [autoRanges, setAutoRanges] = useState<Record<string, Range>>({});
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [rangeUndo, setRangeUndo] = useState<Record<string, Range>>({});
  const [holding, setHolding] = useState<string>();
  const holdTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const holdDone = useRef(false);
  const paletteInput = useRef<HTMLInputElement>(null);
  const inspector = useRef<HTMLDivElement>(null);

  useEffect(() => connect(next => { setSnap(next); if (next.error) setConsoleOpen(true); }, () => setSnap(undefined)), []);
  useEffect(() => { if (palette) paletteInput.current?.focus(); }, [palette]);
  useEffect(() => { setDraft({}); setExpanded({}); setSelectedBake([]); setStyleOpen(undefined); setAutoRanges({}); }, [snap?.selected, snap?.repository]);
  const act = (action: Action) => send({ ...action, revision: snap?.revision, viewId: action.action.startsWith('agent') ? snap?.agent?.viewId : undefined });
  const agent = snap?.agent;
  // Start the agent when its tab is shown, and start a conversation for a newly loaded repository.
  useEffect(() => {
    if (tab === 'agent' && snap?.repository && agent && (agent.status === 'idle' || agent.repository !== snap.repository)) send({ action: 'agentConnect' });
  }, [tab, snap?.repository, agent?.status, agent?.repository]);
  const agentAttention = tab === 'script' && (agent?.status === 'working' || !!agent?.permission || !!agent?.question);
  // The Script tab says when the agent changed the active Script's file; dismissed per change.
  const agentEdit = agent?.changes?.find(c => c.path === snap?.selected);
  const agentEditKey = agentEdit ? `${agentEdit.path}:${agentEdit.added}:${agentEdit.removed}` : undefined;
  const [dismissedEdit, setDismissedEdit] = useState<string>();
  const scripts = snap?.scripts.filter(s => `${s.name} ${s.path} ${s.category ? '#' + s.category : ''}`.toLowerCase().includes(query.toLowerCase())) ?? [];
  const selected = snap?.scripts.find(s => s.path === snap.selected);
  // A custom range comes from the host; otherwise the panel keeps an automatic soft range for the committed value.
  const rangeOf = (port: Port): Range => {
    const number = Number(port.value) || 0;
    if (port.range) return port.range;
    const auto = autoRanges[port.name];
    return auto && inside(auto, number) ? auto : fitRange(number, port.kind === 'int');
  };
  const commit = (port: Port) => {
    if (!(port.name in draft)) return;
    const value = draft[port.name].trim();
    let movedRange: Range | undefined;
    if ((port.kind === 'number' || port.kind === 'int') && value && Number.isFinite(Number(value))) {
      const number = Number(value), range = rangeOf(port);
      if (!inside(range, number)) {
        if (port.range) { setRangeUndo({ ...rangeUndo, [port.name]: port.range }); movedRange = recenter(port.range, number); }
        else setAutoRanges(old => ({ ...old, [port.name]: recenter(range, number) }));
      }
    }
    act({ action: 'input', name: port.name, value: value || null, range: movedRange });
    setDraft(old => { const next = { ...old }; delete next[port.name]; return next; });
  };
  const run = () => {
    const values = Object.fromEntries(Object.entries(draft).map(([name, value]) => [name, value.trim() || null]));
    setDraft({}); act({ action: 'run', values });
  };
  const select = (path: string) => { act({ action: 'select', path }); setPalette(false); setQuery(''); };
  const paletteKey = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') { setPalette(false); return; }
    if (e.key === 'ArrowDown') { e.preventDefault(); setHighlight(Math.min(highlight + 1, scripts.length - 1)); }
    if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight(Math.max(highlight - 1, 0)); }
    if (e.key === 'Enter' && scripts[highlight]) select(scripts[highlight].path);
  };
  const startHold = (name: string) => {
    holdDone.current = false;
    if (snap?.inputs.find(p => p.name === name)?.value === 'true') return;
    setHolding(name);
    holdTimer.current = setTimeout(() => { holdDone.current = true; setHolding(undefined); act({ action: 'input', name, value: true }); }, 1000);
  };
  const endHold = (name: string, cancelled = false) => {
    clearTimeout(holdTimer.current); setHolding(undefined);
    if (cancelled) { holdDone.current = true; return; }
    if (holdDone.current) return;
    if (snap?.inputs.find(p => p.name === name)?.value === 'true') act({ action: 'input', name, value: false });
    else act({ action: 'pulse', name });
  };

  return <div className="rd">
    <header className="hd">
      <div className="hd-top">
        <div className="brand"><span className="brand-mark">r</span>rhDock</div><span className="sep">/</span>
        <button className="repo" onClick={() => setMenu(!menu)} title={snap?.repository}><span className="repo-name">{snap?.repository?.split(/[\\/]/).pop() ?? 'No repository'}</span><ChevronDown size={12}/></button>
        <span className="grow"/>{snap?.pid != null && <span className="watch">PID: {snap.pid}</span>}<span className="watch source"><span className="live"/> {mirrorMode ? snap ? 'Live Rhino' : 'Waiting for Rhino' : import.meta.env.DEV && !window.eto ? 'Preview sample' : 'Local'}</span>
        <div className="tabs" role="tablist" aria-label="Panel">
          <button role="tab" aria-selected={tab === 'script'} title="Script" onClick={() => setTab('script')}><FileCode2 size={13}/><span className="tab-label">Script</span></button>
          <button role="tab" aria-selected={tab === 'agent'} title="Agent" onClick={() => setTab('agent')}>{agentAttention ? <span className={`tab-dot ${agent?.permission || agent?.question ? 'warn' : ''}`}/> : <Bot size={13}/>}<span className="tab-label">Agent</span></button>
        </div>
      </div>
      {tab === 'agent' ? <AgentHeader agent={agent} send={send} openList={() => setConversations(true)}/> :
      <button className={`script-btn ${!selected ? 'none' : ''}`} onClick={() => { setPalette(true); setHighlight(0); }} disabled={!snap?.scripts.length}>
        <FileCode2 size={14} className="file-ic"/><span className="script-name">{selected?.name ?? 'Select a Script'}</span>{selected?.category && <Badge variant="secondary" className="flex-none">#{selected.category}</Badge>}
        <span className="script-path">{selected?.path}</span><ChevronDown size={13} className="ud"/>
      </button>}
    </header>
    {snap && <AgentView snap={snap} send={send} listOpen={conversations} closeList={() => setConversations(false)} hidden={tab !== 'agent'}/>}
    <main className="insp" ref={inspector} hidden={tab === 'agent'}>
      {!snap?.repository ? <div className="empty"><FolderOpen size={30}/><h2>Open a repository</h2><p>Choose a C# repository to discover its Scripts and continue your Workspace.</p><button className="btn primary" onClick={() => act({ action: 'open' })}><FolderOpen size={14}/> Open repository</button><button className="btn secondary" onClick={() => setCreating(true)}><Plus size={14}/> New repository</button></div> : <>
        {snap.notice && <div className="banner">{snap.notice}</div>}
        {agentEdit && agentEditKey !== dismissedEdit && <div className="banner agent-edit"><span className="grow">The agent changed <b>{agentEdit.path.split('/').pop()}</b> (+{agentEdit.added} −{agentEdit.removed}); rhDock reloaded it.</span><button className="link-btn" onClick={() => setTab('agent')}>Review</button><button className="icon-btn sm" aria-label="Dismiss" onClick={() => setDismissedEdit(agentEditKey)}>×</button></div>}
        <section className="sec"><div className="sec-head"><span className="sec-title">Inputs</span><span className="sec-count">{snap.inputs.length}</span></div>
          {snap.inputs.length ? snap.inputs.map(p => <div className="in-row" key={p.name}>
            <div className="in-main"><span className="in-name" title={p.name}>{!p.optional && <span className="req-dot"/>}<span className="truncate">{p.name}</span></span>
              <div className="in-ctl">{p.kind === 'pick' ? <PickButton port={p} picking={snap.picking} act={act}/>
              : p.kind === 'boolean' ? p.boolStyle === 'switch' ? <button className="bool-toggle" aria-pressed={p.value === 'true'} onClick={() => act({ action: 'input', name: p.name, value: p.value !== 'true' })}><span className={`switch ${p.value === 'true' ? 'on' : ''}`}><span className="knob"/></span><span>{p.value === 'true' ? 'True' : 'False'}</span></button>
              : <button className={`press ${p.value === 'true' ? 'locked' : ''} ${holding === p.name ? 'holding' : ''}`} aria-pressed={p.value === 'true'} title="Press to Run once · hold to lock" onPointerDown={e => { if (e.button === 0) { e.currentTarget.setPointerCapture(e.pointerId); startHold(p.name); } }} onPointerUp={() => endHold(p.name)} onPointerLeave={() => { if (holding === p.name) endHold(p.name, true); }} onPointerCancel={() => endHold(p.name, true)} onKeyDown={e => { if (e.key === ' ') { e.preventDefault(); if (!e.repeat) startHold(p.name); } else if (e.key === 'Enter') { e.preventDefault(); if (!e.repeat) p.value === 'true' ? act({ action: 'input', name: p.name, value: false }) : act({ action: 'pulse', name: p.name }); } }} onKeyUp={e => { if (e.key === ' ') { e.preventDefault(); endHold(p.name); } }} onBlur={() => { if (holding === p.name) endHold(p.name, true); }}><span className="hold-fill"/><Zap size={13}/><span>{holding === p.name ? 'Keep holding…' : p.value === 'true' ? 'Locked' : 'Press'}</span></button>
              : p.kind === 'color' ? <ColorInput port={p} picking={snap.picking} act={act}/>
              : p.kind === 'unsupported' ? <span className="muted">Unsupported input · {shortType(p.type)}</span>
              : p.kind === 'number' || p.kind === 'int' ? <NumberField label={p.name} text={draft[p.name] ?? p.value ?? ''} range={rangeOf(p)} int={p.kind === 'int'} invalid={invalidNumber(p, draft[p.name] ?? p.value ?? '')} placeholder={p.optional ? 'Optional' : 'Required'}
                  onText={value => setDraft(old => ({ ...old, [p.name]: value }))} onBlur={() => commit(p)} onScrub={value => { act({ action: 'input', name: p.name, value }); setDraft(old => { const next = { ...old }; delete next[p.name]; return next; }); }}/>
              : <div className="field"><input value={draft[p.name] ?? p.value ?? ''} onChange={e => setDraft({ ...draft, [p.name]: e.target.value })} onBlur={() => commit(p)} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); }} placeholder={p.optional ? 'Optional' : 'Required'} aria-label={p.name}/>{p.kind === 'list' && <span className="suffix">list</span>}</div>}
              </div><button className="icon-btn" title={`Details for ${p.name}`} onClick={() => setExpanded({ ...expanded, [p.name]: !expanded[p.name] })}><Settings2 size={13}/></button>
            </div>
            {invalidNumber(p, draft[p.name] ?? p.value ?? '') && <div className="in-msg">Enter a valid {p.kind === 'int' ? 'integer' : 'number'}.</div>}
            {expanded[p.name] && <div className="adv-body"><div className="adv-tip">{p.tooltip || shortType(p.type)}</div><span className="adv-k">Type</span><span className="adv-v">{shortType(p.type)} · {p.access}</span>
              {p.kind === 'boolean' && <><span className="adv-k">Control</span><div className="seg"><button className={p.boolStyle !== 'switch' ? 'on' : ''} onClick={() => act({ action: 'style', name: p.name, style: 'press' })}>Press</button><button className={p.boolStyle === 'switch' ? 'on' : ''} onClick={() => act({ action: 'style', name: p.name, style: 'switch' })}>Switch</button></div></>}
              {(p.kind === 'number' || p.kind === 'int') && <RangeRow range={rangeOf(p)} custom={!!p.range} onApply={range => act({ action: 'range', name: p.name, range })}
                onAuto={() => { act({ action: 'range', name: p.name, range: null }); setAutoRanges(old => { const next = { ...old }; delete next[p.name]; return next; }); }}/>}
              {rangeUndo[p.name] && <button className="link" onClick={() => { act({ action: 'range', name: p.name, range: rangeUndo[p.name] }); setRangeUndo(old => { const next = { ...old }; delete next[p.name]; return next; }); }}>Undo range change</button>}
              {p.kind === 'pick' && <PickDetails port={p} picking={snap.picking} act={act}/>}
            </div>}
          </div>) : <div className="section-empty">Select a Script to inspect its inputs.</div>}
        </section>
        <section className="sec"><div className="sec-head"><span className="sec-title">Outputs</span><span className="sec-count">{snap.outputs.length}</span><span className="grow"/><button className="btn-sm" disabled={!selectedBake.length} onClick={() => { act({ action: 'bake', names: selectedBake }); setSelectedBake([]); }}>Bake selected</button></div>
          {snap.outputs.length ? snap.outputs.map(o => <OutputRow key={o.name} output={o} open={!!expanded[`out:${o.name}`]} toggle={() => setExpanded({ ...expanded, [`out:${o.name}`]: !expanded[`out:${o.name}`] })} selected={selectedBake.includes(o.name)} select={() => setSelectedBake(selectedBake.includes(o.name) ? selectedBake.filter(n => n !== o.name) : [...selectedBake, o.name])} styleOpen={styleOpen === o.name} toggleStyle={() => setStyleOpen(styleOpen === o.name ? undefined : o.name)} closeStyle={() => setStyleOpen(undefined)} act={act}/>) : <div className="section-empty">Run the Script to inspect outputs.</div>}
        </section>
        <section className="sec con"><button className="con-toggle" onClick={() => setConsoleOpen(!consoleOpen)}><ChevronRight size={13} className={consoleOpen ? 'rotate' : ''}/><span className="sec-title">Console</span>{snap.error && <span className="badge err">!</span>}<span className="grow"/><Terminal size={13}/></button>
          {consoleOpen && <div className="console">{snap.error && <div className="c-line err"><b>Error</b><span>{snap.error}</span></div>}{snap.historical && <div className="c-line"><b>Past</b><span>{snap.historical}</span></div>}{snap.statusLine && <div className="c-line"><b>Info</b><span>{snap.statusLine}</span></div>}{snap.log.map((line, i) => <div className="c-line" key={i}><b>Log</b><span>{line}</span></div>)}{!snap.error && !snap.historical && !snap.statusLine && !snap.log.length && <div className="c-empty">No messages yet.</div>}</div>}
        </section>
      </>}
    </main>
    {tab === 'agent' ? null : snap?.solve ? <SolveBar snap={snap} solve={snap.solve} act={act} openConsole={() => { setConsoleOpen(true); inspector.current?.scrollTo({ top: inspector.current.scrollHeight, behavior: 'smooth' }); }}/> : <footer className="rb"><button className={`status st-${snap?.status ?? 'ready'}`} onClick={() => { setConsoleOpen(true); inspector.current?.scrollTo({ top: inspector.current.scrollHeight, behavior: 'smooth' }); }}><span className="st-dot"/><span className="st-label">{snap?.status ? snap.status[0].toUpperCase() + snap.status.slice(1) : 'Ready'}</span></button><button className="auto" aria-pressed={!!snap?.auto} onClick={() => act({ action: 'auto', value: !snap?.auto })}><span className={`switch ${snap?.auto ? 'on' : ''}`}><span className="knob"/></span>Auto</button><button className="run" disabled={!snap?.selected || snap.status === 'running' || !!snap.picking} onClick={run}><Play size={13} fill="currentColor"/>Run</button></footer>}
    {menu && <><div className="scrim clear" onClick={() => setMenu(false)}/><div className="menu"><div className="menu-path">{snap?.repository ?? 'No repository'}</div><button onClick={() => { setMenu(false); act({ action: 'open' }); }}><FolderOpen size={14}/>Open repository</button><button onClick={() => { setMenu(false); setCreating(true); }}><Plus size={14}/>New repository</button><button disabled={!snap?.selected} onClick={() => { setMenu(false); act({ action: 'export' }); }}><Upload size={14}/>Export Workspace</button></div></>}
    {creating && <><div className="scrim" onClick={() => setCreating(false)}/><div className="pal create-form"><h2>New repository</h2><label>Repository name<input autoFocus value={newName} onChange={e => setNewName(e.target.value)} placeholder="My Project" onKeyDown={e => { if (e.key === 'Escape') setCreating(false); }}/></label><p>Choose a parent folder in the next dialog. The starter will be created in a new folder with this name.</p><div className="form-actions"><button className="btn secondary" onClick={() => setCreating(false)}>Cancel</button><button className="btn primary" disabled={!newName.trim() || /[\\/:*?"<>|]/.test(newName)} onClick={() => { act({ action: 'new', value: newName.trim() }); setCreating(false); }}>Choose location</button></div></div></>}
    {palette && <><div className="scrim" onClick={() => setPalette(false)}/><div className="pal"><div className="pal-search"><Search size={15}/><input ref={paletteInput} value={query} onChange={e => { setQuery(e.target.value); setHighlight(0); }} onKeyDown={paletteKey} placeholder="Search Scripts by name, #category, or path"/><kbd>Esc</kbd></div><div className="pal-group">Scripts · {scripts.length}</div><div className="pal-list">{scripts.map((s, i) => <button className={i === highlight ? 'hi' : ''} onMouseEnter={() => setHighlight(i)} onClick={() => select(s.path)} key={s.path}><FileCode2 size={14}/><span><span className="pal-name"><strong>{s.name}</strong>{s.category && <Badge variant="secondary">#{s.category}</Badge>}</span><small>{s.path}</small></span>{s.path === snap?.selected && <span className="current">Current</span>}</button>)}{!scripts.length && <div className="pal-empty">No matching Scripts</div>}</div><div className="pal-foot">↑↓ Navigate · Enter Select · Esc Close</div></div></>}
    {!snap && <div className="loading">{mirrorMode ? 'Open rhDock in Rhino and run RhinoDockMirror to connect.' : 'Connecting to rhDock…'} <CircleHelp size={14}/></div>}
  </div>;
}

function OutputRow({ output: o, open, toggle, selected, select, styleOpen, toggleStyle, closeStyle, act }: { output: Output; open: boolean; toggle: () => void; selected: boolean; select: () => void; styleOpen: boolean; toggleStyle: () => void; closeStyle: () => void; act: (a: Action) => void }) {
  return <div className={`out-row ${o.historical ? 'hist' : ''}`} data-style-anchor={styleOpen || undefined}>
    <div className="out-main">
      <button className="out-toggle" onClick={toggle}><ChevronRight size={13} className={open ? 'rotate' : ''}/><span className="out-name">{o.name}</span><span className="out-type">{shortType(o.type)}</span><span className="out-sum">{o.summary ?? `${o.count} item${o.count === 1 ? '' : 's'}`}</span></button>
      {o.preview ? <button className="icon-btn" title={o.visible ? 'Hide Preview' : 'Show Preview'} onClick={() => act({ action: 'preview', name: o.name, visible: !o.visible })}>{o.visible ? <Eye size={14}/> : <EyeOff size={14}/>}</button> : <span className="icon-spacer"/>}
      {o.preview && o.style ? <button className={`sw-btn ${styleOpen ? 'on' : ''}`} data-style-anchor onClick={toggleStyle} title="Preview style" aria-label={`Preview style for ${o.name}`} aria-expanded={styleOpen}><StyleGlyph family={o.family} style={o.style}/></button> : <span className="icon-spacer"/>}
      {o.bake ? <label className="check"><input type="checkbox" checked={selected} onChange={select} aria-label={`Select ${o.name} for Bake`}/></label> : <span className="icon-spacer"/>}
    </div>
    {styleOpen && o.style && <StylePopover title={o.name} family={o.family} style={o.style} onClose={closeStyle} onChange={style => act({ action: 'pstyle', name: o.name, style })} onSample={() => act({ action: 'pick', name: o.name, mode: 'color' })}/>}
    {open && <div className="out-detail">
      {o.values?.length ? <><div className="value-table"><div className="value-head"><span>#</span><span>Element</span></div>{o.values.map((v, i) => <div key={i}><span>{i}</span><span>{v}</span></div>)}</div>{o.count > o.values.length && <div className="value-more">Showing first {o.values.length} of {o.count}</div>}</> : <p>{o.historical ? 'Historical result' : o.summary && !/^\d+ items?$/.test(o.summary) ? o.summary : 'No item details available.'}</p>}
      {o.bake && <button className="btn-sm" onClick={() => act({ action: 'bake', names: [o.name] })}>Bake</button>}
    </div>}
  </div>;
}
