import { useState } from 'react';
import { FolderOpen, Plus, Puzzle, RotateCw } from 'lucide-react';
import type { Action, Extensions } from './types';

// The Extensions tab: the Extension Repo, its Extensions, and their generation state.
export function ExtensionsView({ extensions: ext, send, hidden }: { extensions?: Extensions; send: (a: Action) => void; hidden: boolean }) {
  const [creating, setCreating] = useState<'repo' | 'extension'>();
  const [name, setName] = useState('');
  const valid = creating === 'extension' ? /^[A-Za-z_][A-Za-z0-9_]*$/.test(name.trim()) : !!name.trim() && !/[\\/:*?"<>|]/.test(name);
  const create = () => { send({ action: creating === 'repo' ? 'extNewRepo' : 'extNew', value: name.trim() }); setCreating(undefined); setName(''); };
  return <main className="insp" hidden={hidden}>
    {ext?.error && <div className="banner">{ext.error}</div>}
    {!ext?.repository ? <div className="empty"><Puzzle size={30}/><h2>Open an Extension Repo</h2>
      <p>Extensions add windows and Rhino capabilities to rhDock while Rhino runs, without a build or a restart. Each folder in the repo with a .csproj is one Extension.</p>
      <button className="btn primary" onClick={() => send({ action: 'extOpen' })}><FolderOpen size={14}/> Open Extension Repo</button>
      <button className="btn secondary" onClick={() => setCreating('repo')}><Plus size={14}/> New Extension Repo</button></div>
    : <section className="sec">
      <div className="ext-repo" title={ext.repository}><span className="truncate">{ext.repository.split(/[\\/]/).pop()}</span><span className="grow"/>
        <button className="btn-sm" onClick={() => send({ action: 'extOpen' })}>Open…</button>
        <button className="icon-btn" title="Show in folder" onClick={() => send({ action: 'extReveal' })}><FolderOpen size={13}/></button></div>
      <div className="sec-head"><span className="sec-title">Extensions</span><span className="sec-count">{ext.items.length}</span><span className="grow"/>
        <button className="btn-sm" onClick={() => setCreating('extension')}>New Extension</button></div>
      {ext.items.length ? <div className="ext-grid">
        <div className="ext-cols"><span title="Enable for this session">Enable</span><span/><span>Status</span><span title="Load at startup">Startup</span><span/><span/></div>
        {ext.items.map(item => <div className="ext-row" key={item.name}>
          <button className="bool-toggle" aria-pressed={item.enabled} aria-label={`Enable ${item.name}`} title={item.enabled ? 'Disable for this session' : 'Enable for this session'} onClick={() => send({ action: 'extEnable', name: item.name, value: !item.enabled })}><span className={`switch ${item.enabled ? 'on' : ''}`}><span className="knob"/></span></button>
          <span className="ext-name truncate">{item.name}</span>
          {(() => { const state = (!item.enabled ? 'Off' : item.generation === 0 ? 'Not loaded' : `Generation ${item.generation}`) + (item.leaked > 0 ? ` · ${item.leaked} still loaded` : '');
            return <span className={`ext-state truncate ${item.error || item.leaked ? 'warn' : ''}`} title={state}>{state}</span>; })()}
          <label className="check" title="Load at startup"><input type="checkbox" aria-label={`Load ${item.name} at startup`} checked={item.startup} onChange={e => send({ action: 'extStartup', name: item.name, value: e.target.checked })}/></label>
          <button className="icon-btn" title="Reload code and pages" disabled={!item.enabled} onClick={() => send({ action: 'extReload', name: item.name })}><RotateCw size={13}/></button>
          <button className="icon-btn" title="Show in folder" onClick={() => send({ action: 'extReveal', name: item.name })}><FolderOpen size={13}/></button>
          {item.enabled && item.error && <pre className="ext-err">{item.error}</pre>}
        </div>)}
      </div> : <div className="section-empty">No Extensions yet. Create one, or add a folder with a .csproj to the repo.</div>}
    </section>}
    {creating && <><div className="scrim" onClick={() => setCreating(undefined)}/><div className="pal create-form"><h2>{creating === 'repo' ? 'New Extension Repo' : 'New Extension'}</h2>
      <label>{creating === 'repo' ? 'Repo name' : 'Extension name'}<input autoFocus value={name} onChange={e => setName(e.target.value)} placeholder={creating === 'repo' ? 'rhDock Extensions' : 'SunStudy'} onKeyDown={e => { if (e.key === 'Escape') setCreating(undefined); if (e.key === 'Enter' && valid) create(); }}/></label>
      <p>{creating === 'repo' ? 'Choose a parent folder in the next dialog. The repo starts with AGENTS.md and one starter Extension.' : 'A C# name such as SunStudy. The Extension is created from the starter and enabled for this session.'}</p>
      <div className="form-actions"><button className="btn secondary" onClick={() => setCreating(undefined)}>Cancel</button><button className="btn primary" disabled={!valid} onClick={create}>{creating === 'repo' ? 'Choose location' : 'Create'}</button></div></div></>}
  </main>;
}
