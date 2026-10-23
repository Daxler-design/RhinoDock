import { ChoiceInput } from './ChoiceInput';
import { useEffect, useRef, useState } from 'react';
import { ArrowUpDown, Bot, ChevronDown, ChevronRight, CircleHelp, Download, Eye, EyeOff, FileCode2, FolderOpen, Play, Plus, Puzzle, Search, Settings, Settings2, Terminal, Upload, Zap } from 'lucide-react';
import { AgentHeader, AgentView } from './Agent';
import { connect, mirrorMode, send } from './bridge';
import { ExtensionsView } from './Extensions';
import { fitRange, inside, NumberField, RangeRow, recenter } from './NumberField';
import { ColorInput, PickButton, PickDetails } from './PickInput';
import { ListDetails } from './ListDetails';
import { GroupedSection, reconcile } from './Layout';
import { SolveBar } from './SolveBar';
import { scriptHistoryAction } from './scriptHistory';
import { Badge } from '@/components/ui/badge';
import { PickerPopover, StyleGlyph } from './StylePopover';
import { accents, applyTheme, defaultAccent, parseTheme, type Theme } from './theme';
import { loadRecentColors } from './recentColors';
import { Popover, PopoverAnchor, PopoverContent } from '@/components/ui/popover';
import type { Action, Output, Port, Range, Section, Snapshot } from './types';

function shortType(type: string) {
  const item = type.match(/^System\.Collections\.Generic\.(?:List|IEnumerable|IReadOnlyList)`1\[\[([^,\]]+)/)?.[1];
  return item ? `${item.split('.').pop()} list` : type.split(',')[0].split('.').pop()?.replace('[]', ' list') ?? type;
}
function invalidNumber(port: Port, value: string) { return !!value.trim() && (port.kind === 'number' && !Number.isFinite(Number(value)) || port.kind === 'int' && !/^[+-]?\d+$/.test(value.trim())); }

export default function App() {
  const [snap, setSnap] = useState<Snapshot>();
  const [tab, setTab] = useState<'script' | 'agent' | 'extensions'>('script');
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
  const agentAttention = tab !== 'agent' && (agent?.status === 'working' || !!agent?.permission || !!agent?.question);
  // Unread: conversations that finished or need input, plus the current one when it finished while the Script tab was shown.
  const [finishedAway, setFinishedAway] = useState(false);
  const wasWorking = useRef(false);
  useEffect(() => { const working = agent?.status === 'working'; if (wasWorking.current && !working && tab !== 'agent') setFinishedAway(true); wasWorking.current = working; }, [agent?.status]);
  useEffect(() => { if (tab === 'agent') setFinishedAway(false); }, [tab]);
  const unread = (agent?.agents?.flatMap(a => a.activities ?? []).filter(a => a === 'done' || a === 'input').length ?? 0) + (finishedAway ? 1 : 0);
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
  const inputPreviewAll = (snap?.inputPreviewAll ?? snap?.previewAll) !== false;
  const outputPreviewAll = (snap?.outputPreviewAll ?? snap?.previewAll) !== false;
  const foldKey = `rhdock.fold|${snap?.repository ?? ''}|${snap?.selected ?? ''}`;
  const [fold, setFold] = useState<Record<string, boolean>>({});
  const [arranging, setArranging] = useState(false);
  const [settings, setSettings] = useState(false);
  useEffect(() => {
    if (tab !== 'script' || !snap?.selected || palette || menu || settings) return;
    const history = (e: KeyboardEvent) => {
      const target = e.target;
      const editing = target instanceof HTMLElement && !!target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"])');
      const action = scriptHistoryAction(e, editing);
      if (action) { e.preventDefault(); e.stopPropagation(); act({ action }); }
    };
    window.addEventListener('keydown', history);
    return () => window.removeEventListener('keydown', history);
  }, [tab, snap?.selected, snap?.revision, palette, menu, settings]);
  const [accentOpen, setAccentOpen] = useState(false);
  const [theme, setTheme] = useState<Theme>(() => { try { return parseTheme(localStorage.getItem('rhdock.theme')); } catch { return parseTheme(); } });
  // The plug-in's saved theme wins once the host sends it; the page keeps a local copy for the browser preview.
  useEffect(() => { if (snap?.theme) setTheme(parseTheme(snap.theme)); }, [snap?.theme]);
  loadRecentColors(snap?.recentColors);
  useEffect(() => { applyTheme(theme); }, [theme]);
  const chooseTheme = (next: Theme) => {
    setTheme(next);
    try { localStorage.setItem('rhdock.theme', JSON.stringify(next)); } catch { /* storage unavailable: the plug-in still keeps it */ }
    act({ action: 'theme', value: JSON.stringify(next) });
  };
  // The custom accent slot this picker session edits; undefined until its first pick adds one.
  const adding = useRef<number | undefined>(undefined);
  const swatchRow = useRef<HTMLDivElement>(null);
  const addAccent = (color: string | undefined) => {
    if (!color) { chooseTheme({ ...theme, accent: undefined }); return; }
    let custom = [...(theme.custom ?? [])];
    if (adding.current !== undefined && adding.current < custom.length) custom[adding.current] = color;
    else if (!accents.includes(color) && !custom.includes(color)) {
      // As many custom slots as fit beside the presets and the add box in the row's current width; the oldest goes first.
      const row = swatchRow.current, size = 18, gap = row ? parseFloat(getComputedStyle(row).columnGap) || 7 : 7;
      const fit = row ? Math.max(1, Math.floor((row.clientWidth + gap) / (size + gap)) - accents.length - 1) : 4;
      custom = [...custom, color].slice(-fit);
      adding.current = custom.length - 1;
    }
    custom = custom.filter((c, i) => custom.indexOf(c) === i);
    chooseTheme({ ...theme, accent: color === defaultAccent ? undefined : color, custom });
  };
  const [pending, setPending] = useState<{ rev?: number; inputs?: Section; outputs?: Section }>();
  useEffect(() => { try { setFold(JSON.parse(localStorage.getItem(foldKey) ?? '{}')); } catch { setFold({}); } setArranging(false); setPending(undefined); }, [foldKey]);
  // A new layout revision that is not this panel's own save, arriving while arranging, came from another Rhino: say so, since it replaces what is on screen.
  const [layoutNote, setLayoutNote] = useState<string>();
  const lastLayout = useRef<string>(undefined);
  useEffect(() => {
    const seen = `${foldKey}:${snap?.layout?.rev}`, before = lastLayout.current;
    lastLayout.current = seen;
    if (arranging && !pending && before?.startsWith(foldKey + ':') && before !== seen) setLayoutNote('Another Rhino updated this layout; showing its version.');
    setPending(undefined);
  }, [snap?.layout?.rev]);
  useEffect(() => { if (!arranging) setLayoutNote(undefined); else if (layoutNote) { const t = setTimeout(() => setLayoutNote(undefined), 6000); return () => clearTimeout(t); } }, [arranging, layoutNote]);
  useEffect(() => { if (!pending) return; const t = setTimeout(() => setPending(undefined), 2500); return () => clearTimeout(t); }, [pending]);
  useEffect(() => {
    if (!arranging) return;
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setArranging(false); };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [arranging]);
  const toggleFold = (key: string) => setFold(old => { const next = { ...old, [key]: !old[key] }; try { localStorage.setItem(foldKey, JSON.stringify(next)); } catch { /* storage unavailable: folding still works in this window */ } return next; });
  const saved = (which: 'inputs' | 'outputs') => pending?.[which] ?? snap?.layout?.[which];
  // A section with no ports (outputs before the first Run) passes its saved layout through, so editing one section never prunes the other.
  const whole = (which: 'inputs' | 'outputs', names: string[]) => names.length ? reconcile(names, saved(which)) : saved(which) ?? { groups: [], ungrouped: [] };
  const saveLayout = (which: 'inputs' | 'outputs', next: Section) => {
    const inputs = which === 'inputs' ? next : whole('inputs', snap?.inputs.map(p => p.name) ?? []);
    const outputs = which === 'outputs' ? next : whole('outputs', snap?.outputs.map(o => o.name) ?? []);
    setPending({ rev: snap?.layout?.rev, inputs, outputs });
    act({ action: 'layout', layoutRevision: snap?.layout?.rev ?? 0, inputs, outputs });
  };
  const needsValue = (p: Port) => (!p.optional && (p.kind === 'pick' ? !p.count : p.kind !== 'boolean' && !(draft[p.name] ?? p.value)?.trim())) || invalidNumber(p, draft[p.name] ?? p.value ?? '');
  const inputSummary = (p: Port) => needsValue(p) ? { text: 'Required', warn: true } : { text: p.kind === 'pick' ? p.picked ?? '' : p.kind === 'boolean' ? (p.value === 'true' ? 'True' : 'False') : p.value ?? '' };
  const inputRow = (p: Port) => <div className="in-row">
            <div className="in-main"><span className="in-name" title={p.name}>{!p.optional && <span className="req-dot"/>}<span className="truncate">{p.name}</span></span>
              <div className="in-ctl">{p.choices?.length ? <ChoiceInput port={p} act={act}/> : p.kind === 'pick' ? <PickButton port={p} picking={snap!.picking} act={act}/>
              : p.kind === 'boolean' ? <button className="bool-toggle" aria-pressed={p.value === 'true'} onClick={() => act({ action: 'input', name: p.name, value: p.value !== 'true' })}><span className={`switch ${p.value === 'true' ? 'on' : ''}`}><span className="knob"/></span><span>{p.value === 'true' ? 'True' : 'False'}</span></button>
              : p.kind === 'color' ? <ColorInput port={p} picking={snap!.picking} act={act}/>
              : p.kind === 'extension' ? <button className="pick" title={`Edit with the ${p.editor} Extension`} onClick={() => act({ action: 'extEdit', name: p.name })}><Puzzle size={13}/><span className="truncate">{p.value ?? (p.optional ? 'Optional' : 'Required')}</span><span className="grow"/><span className="muted">{p.editor}</span></button>
              : p.kind === 'list' && p.itemKind === 'extension' ? <button className="pick" title={`Edit items with ${p.editor}`} onClick={() => setExpanded({ ...expanded, [p.name]: true })}><Puzzle size={13}/><span>{p.count ?? 0} values</span><span className="grow"/><span className="muted">{p.editor}</span></button>
              : p.kind === 'unsupported' ? <span className="muted">Unsupported input · {shortType(p.type)}</span>
              : p.kind === 'number' || p.kind === 'int' ? <NumberField label={p.name} text={draft[p.name] ?? p.value ?? ''} range={rangeOf(p)} int={p.kind === 'int'} invalid={invalidNumber(p, draft[p.name] ?? p.value ?? '')} placeholder={p.optional ? 'Optional' : 'Required'}
                  onText={value => setDraft(old => ({ ...old, [p.name]: value }))} onBlur={() => commit(p)} onScrub={value => { act({ action: 'input', name: p.name, value }); setDraft(old => { const next = { ...old }; delete next[p.name]; return next; }); }}/>
              : <div className="field"><input value={draft[p.name] ?? p.value ?? ''} onChange={e => setDraft({ ...draft, [p.name]: e.target.value })} onBlur={() => commit(p)} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); }} placeholder={p.optional ? 'Optional' : 'Required'} aria-label={p.name}/>{p.kind === 'list' && <span className="suffix">list</span>}</div>}
              </div><button className="icon-btn" title={`Details for ${p.name}`} onClick={() => setExpanded({ ...expanded, [p.name]: !expanded[p.name] })}><Settings2 size={13}/></button>
            </div>
            {invalidNumber(p, draft[p.name] ?? p.value ?? '') && <div className="in-msg">Enter a valid {p.kind === 'int' ? 'integer' : 'number'}.</div>}
            {expanded[p.name] && <div className="adv-body"><div className="adv-tip">{p.tooltip || shortType(p.type)}</div><span className="adv-k">Type</span><span className="adv-v">{shortType(p.type)} · {p.access}</span>
              {p.kind === 'boolean' && <><span className="adv-k">Trigger</span><button className="btn-sm" title="Run once with True, then keep the stored value" onClick={() => act({ action: 'pulse', name: p.name })}><Play size={11}/> Run once with True</button></>}
              {(p.kind === 'number' || p.kind === 'int') && <RangeRow range={rangeOf(p)} custom={!!p.range} onApply={range => act({ action: 'range', name: p.name, range })}
                onAuto={() => { act({ action: 'range', name: p.name, range: null }); setAutoRanges(old => { const next = { ...old }; delete next[p.name]; return next; }); }}/>}
              {rangeUndo[p.name] && <button className="link" onClick={() => { act({ action: 'range', name: p.name, range: rangeUndo[p.name] }); setRangeUndo(old => { const next = { ...old }; delete next[p.name]; return next; }); }}>Undo range change</button>}
              {p.kind === 'pick' ? <PickDetails port={p} picking={snap!.picking} act={act}/>
                : p.access === 'List' && p.kind !== 'unsupported' ? <ListDetails port={p} picking={snap!.picking} act={act}/> : null}
            </div>}
          </div>;
  const outputRow = (o: Output) => <OutputRow output={o} open={!!expanded[`out:${o.name}`]} toggle={() => setExpanded({ ...expanded, [`out:${o.name}`]: !expanded[`out:${o.name}`] })} selected={selectedBake.includes(o.name)} select={() => setSelectedBake(selectedBake.includes(o.name) ? selectedBake.filter(n => n !== o.name) : [...selectedBake, o.name])} styleOpen={styleOpen === o.name} toggleStyle={() => setStyleOpen(styleOpen === o.name ? undefined : o.name)} closeStyle={() => setStyleOpen(undefined)} act={act}/>;

  return <div className="rd">
    <header className="hd">
      <div className="hd-top">
        <div className="brand"><span className="brand-mark">r</span><span className="brand-name">rhDock</span></div>
        {tab !== 'extensions' && <><span className="sep">/</span><button className="repo" onClick={() => setMenu(!menu)} title={snap?.repository}><span className="repo-name">{snap?.repository?.split(/[\\/]/).pop() ?? 'No repository'}</span><ChevronDown size={12}/></button></>}
        <span className="grow"/>{snap?.pid != null && <span className="watch">PID: {snap.pid}</span>}<span className="watch source"><span className="live"/> {mirrorMode ? snap ? 'Live Rhino' : 'Waiting for Rhino' : import.meta.env.DEV && !window.eto ? 'Preview sample' : 'Local'}</span>
        <div className="tabs" role="tablist" aria-label="Panel">
          <button role="tab" aria-selected={tab === 'script'} title="Script" onClick={() => setTab('script')}><FileCode2 size={13}/><span className="tab-label">Script</span></button>
          <button role="tab" aria-selected={tab === 'agent'} title="Agent" onClick={() => setTab('agent')}>{agentAttention ? <span className={`tab-dot ${agent?.permission || agent?.question ? 'warn' : ''}`}/> : <Bot size={13}/>}<span className="tab-label">Agent</span>{unread > 0 && <span className="tab-badge" aria-label={`${unread} unread`}>{unread > 99 ? '99+' : unread}</span>}</button>
          <button role="tab" aria-selected={tab === 'extensions'} title="Extensions" onClick={() => setTab('extensions')}>{snap?.extensions?.items.some(i => i.enabled && (i.error || i.leaked)) ? <span className="tab-dot warn"/> : <Puzzle size={13}/>}<span className="tab-label">Extensions</span></button>
        </div>
      </div>
      {tab === 'agent' ? <AgentHeader agent={agent} send={send} openList={() => setConversations(true)}/> : tab === 'extensions' ? null :
      <button className={`script-btn ${!selected ? 'none' : ''}`} onClick={() => { setPalette(true); setHighlight(0); }} disabled={!snap?.scripts.length}>
        <FileCode2 size={14} className="file-ic"/><span className="script-name">{selected?.name ?? 'Select a Script'}</span>{selected?.category && <Badge variant="secondary" className="flex-none script-tag">#{selected.category}</Badge>}
        <span className="script-path">{selected?.path}</span><ChevronDown size={13} className="ud"/>
      </button>}
    </header>
    {snap && <AgentView snap={snap} send={send} listOpen={conversations} closeList={() => setConversations(false)} hidden={tab !== 'agent'}/>}
    {snap && <ExtensionsView extensions={snap.extensions} send={send} hidden={tab !== 'extensions'}/>}
    <main className="insp" ref={inspector} hidden={tab !== 'script'}>
      {!snap?.repository ? <div className="empty"><FolderOpen size={30}/><h2>Open a repository</h2><p>Choose a C# repository to discover its Scripts and continue your Workspace.</p><button className="btn primary" onClick={() => act({ action: 'open' })}><FolderOpen size={14}/> Open repository</button><button className="btn secondary" onClick={() => setCreating(true)}><Plus size={14}/> New repository</button></div> : <>
        {snap.notice && <div className="banner">{snap.notice}</div>}
        {layoutNote && <div className="banner">{layoutNote}</div>}
        {agentEdit && agentEditKey !== dismissedEdit && <div className="banner agent-edit"><span className="grow">The agent changed <b>{agentEdit.path.split('/').pop()}</b> (+{agentEdit.added} −{agentEdit.removed}); rhDock reloaded it.</span><button className="link-btn" onClick={() => setTab('agent')}>Review</button><button className="icon-btn sm" aria-label="Dismiss" onClick={() => setDismissedEdit(agentEditKey)}>×</button></div>}
        <GroupedSection previewOff={!inputPreviewAll} title="Inputs" names={snap.inputs.map(p => p.name)} saved={saved('inputs')} onChange={next => saveLayout('inputs', next)} edit={arranging} fold={fold} toggle={toggleFold}
          head={snap.inputs.length ? <span className="cols">
            <button className="icon-btn eyebtn master-eye" title={inputPreviewAll ? 'Hide input previews' : 'Show input previews'} aria-label="Preview inputs" aria-pressed={inputPreviewAll} onClick={() => act({ action: 'previewAll', section: 'inputs', visible: !inputPreviewAll })}>{inputPreviewAll ? <Eye size={14}/> : <EyeOff size={14}/>}</button>
            <button className="icon-btn" title="Arrange inputs and outputs" aria-label="Arrange inputs and outputs" onClick={() => setArranging(true)}><ArrowUpDown size={14}/></button></span> : null}
          editHead={<button className="btn-sm accent" onClick={() => setArranging(false)}>Done</button>}
          row={name => inputRow(snap.inputs.find(p => p.name === name)!)}
          summary={name => inputSummary(snap.inputs.find(p => p.name === name)!)}
          attention={name => { const p = snap.inputs.find(x => x.name === name); return p && needsValue(p) ? `${name} needs a value` : undefined; }}
          hasPreview={ports => ports.some(n => !!snap.inputs.find(p => p.name === n)?.preview)} eyeMargin={0}
          empty={<div className="section-empty">Select a Script to inspect its inputs.</div>}/>
        <GroupedSection previewOff={!outputPreviewAll} title="Outputs" names={snap.outputs.map(o => o.name)} saved={saved('outputs')} onChange={next => saveLayout('outputs', next)} edit={arranging} fold={fold} toggle={toggleFold}
          head={<span className="cols"><span className="icon-spacer sw"/><button className="icon-btn eyebtn master-eye" title={outputPreviewAll ? 'Hide output previews' : 'Show output previews'} aria-label="Preview outputs" aria-pressed={outputPreviewAll} onClick={() => act({ action: 'previewAll', section: 'outputs', visible: !outputPreviewAll })}>{outputPreviewAll ? <Eye size={14}/> : <EyeOff size={14}/>}</button>
            <BakeButton count={selectedBake.length} title={selectedBake.length ? `Bake ${selectedBake.length} selected` : 'Tick outputs below to Bake them'} onClick={() => { act({ action: 'bake', names: selectedBake }); setSelectedBake([]); }}/></span>}
          row={name => outputRow(snap.outputs.find(o => o.name === name)!)}
          summary={name => ({ text: snap.outputs.find(o => o.name === name)?.summary ?? '' })}
          attention={() => undefined}
          hasPreview={ports => ports.some(n => !!snap.outputs.find(o => o.name === n)?.preview)} eyeMargin={26}
          empty={<div className="section-empty">Run the Script to inspect outputs.</div>}/>
        <section className="sec con"><button className="con-toggle" onClick={() => setConsoleOpen(!consoleOpen)}><ChevronRight size={13} className={consoleOpen ? 'rotate' : ''}/><span className="sec-title">Console</span>{snap.error && <span className="badge err">!</span>}<span className="grow"/><Terminal size={13}/></button>
          {consoleOpen && <div className="console">{snap.error && <div className="c-line err"><b>Error</b><span>{snap.error}</span></div>}{snap.historical && <div className="c-line"><b>Past</b><span>{snap.historical}</span></div>}{snap.statusLine && <div className="c-line"><b>Info</b><span>{snap.statusLine}</span></div>}{snap.log.map((line, i) => <div className="c-line" key={i}><b>Log</b><span>{line}</span></div>)}{!snap.error && !snap.historical && !snap.statusLine && !snap.log.length && <div className="c-empty">No messages yet.</div>}</div>}
        </section>
      </>}
    </main>
    {tab !== 'script' ? null : snap?.solve ? <SolveBar snap={snap} solve={snap.solve} act={act} openConsole={() => { setConsoleOpen(true); inspector.current?.scrollTo({ top: inspector.current.scrollHeight, behavior: 'smooth' }); }}/> : <footer className="rb"><button className={`status st-${snap?.status ?? 'ready'}`} onClick={() => { setConsoleOpen(true); inspector.current?.scrollTo({ top: inspector.current.scrollHeight, behavior: 'smooth' }); }}><span className="st-dot"/><span className="st-label">{snap?.status ? snap.status[0].toUpperCase() + snap.status.slice(1) : 'Ready'}</span></button><button className="auto" aria-pressed={!!snap?.auto} onClick={() => act({ action: 'auto', value: !snap?.auto })}><span className={`switch ${snap?.auto ? 'on' : ''}`}><span className="knob"/></span><span className="auto-label">Auto</span></button><button className="run" aria-label="Run" disabled={!snap?.selected || snap.status === 'running' || !!snap.picking} onClick={run}><Play size={13} fill="currentColor"/><span className="run-label">Run</span></button></footer>}
    {settings && <><div className="scrim clear" onClick={() => setSettings(false)}/><div className="menu settings" role="dialog" aria-label="Appearance">
      <div className="menu-path">Appearance</div>
      <div className="set-row"><span>Theme</span><div className="seg" role="radiogroup" aria-label="Theme">{(['dark', 'light'] as const).map(m => <button key={m} role="radio" aria-checked={theme.mode === m} className={theme.mode === m ? 'on' : ''} onClick={() => chooseTheme({ ...theme, mode: m })}>{m[0].toUpperCase() + m.slice(1)}</button>)}</div></div>
      <div className="set-row"><span>Accent</span>
        <div className="swatches" ref={swatchRow}>
          {/* The presets, then the accents the user added, oldest first, so choosing a preset never loses a custom one. */}
          {[...accents, ...(theme.custom ?? [])].map(c => <button key={c} aria-label={`Accent ${c}`} aria-pressed={(theme.accent ?? defaultAccent).toLowerCase() === c} style={{ background: c, outline: (theme.accent ?? defaultAccent).toLowerCase() === c ? '2px solid var(--tx)' : undefined, outlineOffset: 1 }} onClick={() => chooseTheme({ ...theme, accent: c === defaultAccent ? undefined : c })}/>)}
          {/* The dashed box opens the same color picker the Color inputs and Preview styles use; the first pick adds a slot, later picks in the same session adjust it. */}
          <PickerPopover open={accentOpen} onOpenChange={open => { setAccentOpen(open); adding.current = undefined; }}
            trigger={<button className="swatch-add" aria-expanded={accentOpen} aria-label="Add an accent color" onClick={() => { adding.current = undefined; setAccentOpen(!accentOpen); }}/>}
            title="Accent" heading="Appearance" note="Used for buttons, highlights, and links." style={{ color: theme.accent ?? defaultAccent, custom: !!theme.accent, weight: 1, size: 1, shading: 0 }}
            onChange={edit => addAccent(edit.color?.slice(0, 7).toLowerCase())}/>
        </div>
      </div>
    </div></>}
    {menu && <><div className="scrim clear" onClick={() => setMenu(false)}/><div className="menu"><div className="menu-path">{snap?.repository ?? 'No repository'}</div><button onClick={() => { setMenu(false); act({ action: 'open' }); }}><FolderOpen size={14}/>Open repository</button><button onClick={() => { setMenu(false); setCreating(true); }}><Plus size={14}/>New repository</button><button onClick={() => { setMenu(false); setSettings(true); }}><Settings size={14}/>Appearance</button><button disabled={!snap?.selected} onClick={() => { setMenu(false); act({ action: 'export' }); }}><Upload size={14}/>Export Workspace</button></div></>}
    {creating && <><div className="scrim" onClick={() => setCreating(false)}/><div className="pal create-form"><h2>New repository</h2><label>Repository name<input autoFocus value={newName} onChange={e => setNewName(e.target.value)} placeholder="My Project" onKeyDown={e => { if (e.key === 'Escape') setCreating(false); }}/></label><p>Choose a parent folder in the next dialog. The starter will be created in a new folder with this name.</p><div className="form-actions"><button className="btn secondary" onClick={() => setCreating(false)}>Cancel</button><button className="btn primary" disabled={!newName.trim() || /[\\/:*?"<>|]/.test(newName)} onClick={() => { act({ action: 'new', value: newName.trim() }); setCreating(false); }}>Choose location</button></div></div></>}
    {palette && <><div className="scrim" onClick={() => setPalette(false)}/><div className="pal"><div className="pal-search"><Search size={15}/><input ref={paletteInput} value={query} onChange={e => { setQuery(e.target.value); setHighlight(0); }} onKeyDown={paletteKey} placeholder="Search Scripts by name, #category, or path"/><kbd>Esc</kbd></div><div className="pal-group">Scripts · {scripts.length}</div><div className="pal-list">{scripts.map((s, i) => <button className={i === highlight ? 'hi' : ''} onMouseEnter={() => setHighlight(i)} onClick={() => select(s.path)} key={s.path}><FileCode2 size={14}/><span><span className="pal-name"><strong>{s.name}</strong>{s.category && <Badge variant="secondary">#{s.category}</Badge>}</span><small>{s.path}</small></span>{s.path === snap?.selected && <span className="current">Current</span>}</button>)}{!scripts.length && <div className="pal-empty">No matching Scripts</div>}</div><div className="pal-foot">↑↓ Navigate · Enter Select · Esc Close</div></div></>}
    {!snap && <div className="loading">{mirrorMode ? 'Open rhDock in Rhino and run RhinoDockMirror to connect.' : 'Connecting to rhDock…'} <CircleHelp size={14}/></div>}
  </div>;
}

/** The one Bake control: an icon with an optional count badge, used by the Outputs header and each output's details. */
function BakeButton({ count, title, onClick }: { count?: number; title: string; onClick: () => void }) {
  return <button className={`icon-btn bake-btn ${count ? 'has' : ''}`} disabled={count === 0} title={title} aria-label={title} onClick={onClick}><Download size={14}/>{!!count && <b className="count">{count}</b>}</button>;
}

function OutputRow({ output: o, open, toggle, selected, select, styleOpen, toggleStyle, closeStyle, act }: { output: Output; open: boolean; toggle: () => void; selected: boolean; select: () => void; styleOpen: boolean; toggleStyle: () => void; closeStyle: () => void; act: (a: Action) => void }) {
  return <div className={`out-row ${o.historical ? 'hist' : ''}`}>
    <div className="out-main">
      <button className="out-toggle" onClick={toggle}><ChevronRight size={13} className={open ? 'rotate' : ''}/><span className="out-name">{o.name}</span><span className="out-type">{shortType(o.type)}</span><span className="out-sum">{o.summary ?? `${o.count} item${o.count === 1 ? '' : 's'}`}</span></button>
      {o.preview && o.style ? <PickerPopover open={styleOpen} onOpenChange={open => { if (!open) closeStyle(); }}
        trigger={<button className={`sw-btn ${styleOpen ? 'on' : ''}`} onClick={toggleStyle} title="Preview style" aria-label={`Preview style for ${o.name}`} aria-expanded={styleOpen}><StyleGlyph family={o.family} style={o.style}/></button>}
        title={o.name} family={o.family} style={o.style} onChange={style => act({ action: 'pstyle', name: o.name, style })} onSample={index => act({ action: 'pick', name: o.name, mode: 'color', index })}/> : <span className="icon-spacer"/>}
      {o.preview ? <button className="icon-btn eyebtn" title={o.visible ? 'Hide Preview' : 'Show Preview'} onClick={() => act({ action: 'preview', name: o.name, visible: !o.visible })}>{o.visible ? <Eye size={14}/> : <EyeOff size={14}/>}</button> : <span className="icon-spacer"/>}
      {o.bake ? <label className="check"><input type="checkbox" checked={selected} onChange={select} aria-label={`Select ${o.name} for Bake`}/></label> : <span className="icon-spacer"/>}
    </div>
    {open && <div className="out-extra"><div className="out-detail">
      {o.values?.length ? <><div className="value-table"><div className="value-head"><span>#</span><span>Element</span></div>{o.values.map((v, i) => <div key={i}><span>{i}</span><span>{v}</span></div>)}</div>{o.count > o.values.length && <div className="value-more">Showing first {o.values.length} of {o.count}</div>}</> : <p>{o.historical ? 'Historical result' : o.summary && !/^\d+ items?$/.test(o.summary) ? o.summary : 'No item details available.'}</p>}
    </div>
    {o.bake && <BakeButton title={`Bake ${o.name}`} onClick={() => act({ action: 'bake', names: [o.name] })}/>}
    </div>}
  </div>;
}
