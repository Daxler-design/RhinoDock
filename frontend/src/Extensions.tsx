import { useState } from 'react';
import { ChevronDown, FolderOpen, Plus, Puzzle, RotateCw } from 'lucide-react';
import type { Action, Extensions } from './types';
import { DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuTrigger } from './components/ui/dropdown-menu';

// The Extensions tab: the Extension Repo, its Extensions, and their generation state.
export function ExtensionsView({ extensions: ext, send, hidden }: { extensions?: Extensions; send: (a: Action) => void; hidden: boolean }) {
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const valid = !!name.trim() && !/[\\/:*?"<>|]/.test(name);
  const create = () => { send({ action: 'extNewRepo', value: name.trim() }); setCreating(false); setName(''); };
  return <main className="insp" hidden={hidden}>
    {ext?.error && <div className="banner">{ext.error}</div>}
    {!ext?.repository ? <div className="empty"><Puzzle size={30}/><h2>Choose an Extension Repo</h2>
      <p>Extensions add windows and Rhino capabilities to rhDock while Rhino runs, without a build or a restart. Each folder in the repo with a .csproj is one Extension.</p>
      <button className="btn primary" onClick={() => send({ action: 'extOpen' })}><FolderOpen size={14}/> Choose existing repo…</button>
      <button className="btn secondary" onClick={() => setCreating(true)}><Plus size={14}/> Create new repo…</button></div>
    : <section className="sec">
      <div className="ext-repo" title={ext.repository}>
        <DropdownMenu><DropdownMenuTrigger asChild><button className="repo" aria-label="Choose Extension Repo"><span className="truncate">{ext.repository.split(/[\\/]/).pop()}</span><ChevronDown size={12}/></button></DropdownMenuTrigger>
          <DropdownMenuContent align="start"><DropdownMenuGroup>
            <DropdownMenuItem onSelect={() => send({ action: 'extOpen' })}>Choose existing repo…</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => setCreating(true)}>Create new repo…</DropdownMenuItem>
          </DropdownMenuGroup></DropdownMenuContent></DropdownMenu><span className="grow"/>
        <button className="icon-btn" title="Show in Finder or Explorer" onClick={() => send({ action: 'extReveal' })}><FolderOpen size={13}/></button></div>
      <div className="sec-head"><span className="sec-title">Extensions</span><span className="sec-count">{ext.items.length}</span></div>
      {ext.items.length ? <div className="ext-grid">
        <div className="ext-cols"><span title="Enable for this session">Enable</span><span/><span>Status</span><span title="Load at startup">Startup</span><span/></div>
        {ext.items.map(item => <div className="ext-row" key={item.name}>
          <button className="bool-toggle" aria-pressed={item.enabled} aria-label={`Enable ${item.name}`} title={item.enabled ? 'Disable for this session' : 'Enable for this session'} onClick={() => send({ action: 'extEnable', name: item.name, value: !item.enabled })}><span className={`switch ${item.enabled ? 'on' : ''}`}><span className="knob"/></span></button>
          <span className="ext-name truncate">{item.name}</span>
          {(() => { const state = (!item.enabled ? 'Off' : item.generation === 0 ? 'Not loaded' : `Generation ${item.generation}`) + (item.leaked > 0 ? ` · ${item.leaked} still loaded` : '');
            return <span className={`ext-state truncate ${item.error || item.leaked ? 'warn' : ''}`} title={state}>{state}</span>; })()}
          <label className="check" title="Load at startup"><input type="checkbox" aria-label={`Load ${item.name} at startup`} checked={item.startup} onChange={e => send({ action: 'extStartup', name: item.name, value: e.target.checked })}/></label>
          <button className="icon-btn" title="Reload code and pages" disabled={!item.enabled} onClick={() => send({ action: 'extReload', name: item.name })}><RotateCw size={13}/></button>
          {item.enabled && item.error && <pre className="ext-err">{item.error}</pre>}
        </div>)}
      </div> : <div className="section-empty">No Extensions yet. Add a folder with a .csproj to the repo, yourself or with an agent.</div>}
    </section>}
    {creating && <><div className="scrim" onClick={() => setCreating(false)}/><div className="pal create-form"><h2>Create new Extension Repo</h2>
      <label>Repo name<input autoFocus value={name} onChange={e => setName(e.target.value)} placeholder="rhDock Extensions" onKeyDown={e => { if (e.key === 'Escape') setCreating(false); if (e.key === 'Enter' && valid) create(); }}/></label>
      <p>Choose a parent folder in the next dialog. The repo starts with AGENTS.md and the ViewportSliders example. Its README explains how to set up the companion Script.</p>
      <div className="form-actions"><button className="btn secondary" onClick={() => setCreating(false)}>Cancel</button><button className="btn primary" disabled={!valid} onClick={create}>Choose location</button></div></div></>}
  </main>;
}
