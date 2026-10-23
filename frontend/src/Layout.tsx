import { Fragment, useEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowDown, ArrowUp, ChevronRight, Eye, EyeOff, GripVertical, MoreHorizontal, Plus } from 'lucide-react';
import type { Section } from './types';

/** Pure layout operations. A Section is named groups in order, then the Ungrouped ports. Group names are labels only. */
export function reconcile(names: string[], saved?: Section | null): Section {
  const known = new Set(names), seen = new Set<string>();
  const keep = (ports: string[]) => ports.filter(p => known.has(p) && !seen.has(p) && !!seen.add(p));
  const groups = (saved?.groups ?? []).map(g => ({ ...g, ports: keep(g.ports) }));
  const ungrouped = keep(saved?.ungrouped ?? []);
  for (const n of names) if (!seen.has(n)) ungrouped.push(n);
  return { groups, ungrouped };
}
const clone = (s: Section): Section => ({ groups: s.groups.map(g => ({ ...g, ports: [...g.ports] })), ungrouped: [...s.ungrouped] });
const listOf = (s: Section, group: string | null) => group === null ? s.ungrouped : s.groups.find(g => g.name === group)!.ports;
const where = (s: Section, port: string) => {
  const g = s.groups.find(g => g.ports.includes(port));
  return g ? { group: g.name as string | null, index: g.ports.indexOf(port) } : { group: null, index: s.ungrouped.indexOf(port) };
};
export function movePort(s: Section, port: string, group: string | null, index: number): Section {
  const n = clone(s), from = where(n, port);
  listOf(n, from.group).splice(from.index, 1);
  const list = listOf(n, group);
  list.splice(Math.max(0, Math.min(from.group === group && from.index < index ? index - 1 : index, list.length)), 0, port);
  return n;
}
/** One step up or down in visual order; past the end of a group the port enters the neighbouring group. */
export function nudge(s: Section, port: string, delta: -1 | 1): Section {
  const lists: { group: string | null; ports: string[] }[] = [...s.groups.map(g => ({ group: g.name as string | null, ports: g.ports })), { group: null, ports: s.ungrouped }];
  const at = where(s, port), c = lists.findIndex(l => l.group === at.group), len = lists[c].ports.length;
  if (delta < 0) return at.index > 0 ? movePort(s, port, at.group, at.index - 1) : c > 0 ? movePort(s, port, lists[c - 1].group, lists[c - 1].ports.length) : s;
  return at.index < len - 1 ? movePort(s, port, at.group, at.index + 2) : c < lists.length - 1 ? movePort(s, port, lists[c + 1].group, 0) : s;
}
export function addGroup(s: Section): [Section, string] {
  const taken = new Set(s.groups.map(g => g.name.toLowerCase()));
  let i = s.groups.length + 1;
  while (taken.has(`group ${i}`)) i++;
  const name = `Group ${i}`;
  return [{ ...clone(s), groups: [...clone(s).groups, { name, ports: [] }] }, name];
}
export function renameGroup(s: Section, from: string, to: string): Section | undefined {
  const name = to.trim();
  if (!name || name.length > 40 || s.groups.some(g => g.name !== from && g.name.toLowerCase() === name.toLowerCase())) return undefined;
  return { ...clone(s), groups: s.groups.map(g => ({ ...g, name: g.name === from ? name : g.name, ports: [...g.ports] })) };
}
export function removeGroup(s: Section, name: string): Section {
  const n = clone(s), g = n.groups.find(g => g.name === name);
  if (!g) return n;
  n.ungrouped.push(...g.ports);
  n.groups = n.groups.filter(x => x !== g);
  return n;
}
/** A group's own Preview switch. It is stored beside the group and never touches the ports' own visibility. */
export function setGroupPreview(s: Section, name: string, visible: boolean): Section {
  const n = clone(s);
  for (const g of n.groups) if (g.name === name) { if (visible) delete g.previewOff; else g.previewOff = true; }
  return n;
}
export function moveGroup(s: Section, name: string, to: number): Section {
  const n = clone(s), i = n.groups.findIndex(g => g.name === name);
  if (i < 0) return n;
  const [g] = n.groups.splice(i, 1);
  n.groups.splice(Math.max(0, Math.min(to, n.groups.length)), 0, g);
  return n;
}

type Menu = { kind: 'port' | 'group'; name: string };
type Props = {
  previewOff?: boolean; title: string; names: string[]; saved?: Section | null; onChange: (s: Section) => void;
  edit: boolean; fold: Record<string, boolean>; toggle: (key: string) => void;
  head: ReactNode; editHead?: ReactNode; row: (name: string) => ReactNode;
  summary: (name: string) => { text: string; warn?: boolean };
  attention: (name: string) => string | undefined; hasPreview?: (ports: string[]) => boolean; eyeMargin?: number; empty: ReactNode;
};

/** One panel section with optional named groups: fold, reorder by drag or keyboard, rename, move between groups, remove. */
export function GroupedSection(p: Props) {
  const base = reconcile(p.names, p.saved);
  const [work, setWork] = useState<{ before: Section; item: { kind: 'port' | 'group'; name: string } }>();
  const [menu, setMenu] = useState<Menu>();
  const [renaming, setRenaming] = useState<{ from: string; text: string }>();
  const [drag, setDrag] = useState<{ kind: 'port' | 'group'; name: string }>();
  const [dropTo, setDropTo] = useState<{ kind: 'port'; group: string | null; index: number } | { kind: 'group'; index: number }>();
  const [note, setNote] = useState<string>();
  const [undo, setUndo] = useState<{ section: Section; text: string }>();
  const focus = useRef<string | undefined>(undefined);
  const workSection = useRef<Section | undefined>(undefined);
  const view = work ? workSection.current ?? base : base;
  const key = (k: string) => `${p.title}:${k}`;
  const open = !p.fold[p.title];
  const hasGroups = view.groups.length > 0;

  useEffect(() => { if (!p.edit) { setWork(undefined); setMenu(undefined); setRenaming(undefined); } }, [p.edit]);
  useEffect(() => { if (focus.current) { document.getElementById(focus.current)?.focus(); focus.current = undefined; } });
  useEffect(() => {
    if (!menu) return;
    const close = (e: MouseEvent) => { if (!(e.target as Element).closest?.('[data-menu]')) setMenu(undefined); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [menu]);
  useEffect(() => { if (!undo) return; const t = setTimeout(() => setUndo(undefined), 8000); return () => clearTimeout(t); }, [undo]);

  const commit = (next: Section, text?: string) => { workSection.current = undefined; setWork(undefined); p.onChange(next); if (text) setNote(text); };
  const gripId = (kind: string, name: string) => `grip:${p.title}:${kind}:${name}`;
  const lift = (kind: 'port' | 'group', name: string) => { workSection.current = clone(view); setWork({ before: view, item: { kind, name } }); setNote(`${name} lifted. Arrow keys move it, Space drops, Escape cancels.`); };
  const apply = (next: Section, item: { kind: 'port' | 'group'; name: string }) => { workSection.current = next; setWork(w => w && { ...w }); focus.current = gripId(item.kind, item.name); };
  const where2 = (s: Section, item: { kind: 'port' | 'group'; name: string }) => item.kind === 'group'
    ? `position ${s.groups.findIndex(g => g.name === item.name) + 1} of ${s.groups.length}` : (() => { const w = where(s, item.name); return `${w.group ?? 'Ungrouped'}, position ${w.index + 1}`; })();
  const step = (item: { kind: 'port' | 'group'; name: string }, delta: -1 | 1) => {
    const s = workSection.current ?? view;
    return item.kind === 'port' ? nudge(s, item.name, delta) : moveGroup(s, item.name, s.groups.findIndex(g => g.name === item.name) + delta);
  };
  const gripKey = (kind: 'port' | 'group', name: string) => (e: React.KeyboardEvent) => {
    const item = { kind, name };
    if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown') && !work) {
      e.preventDefault(); e.stopPropagation();
      const next = step(item, e.key === 'ArrowUp' ? -1 : 1);
      focus.current = gripId(kind, name); commit(next, `${name} moved to ${where2(next, item)}`); return;
    }
    if (e.key === ' ' || e.key === 'Enter') {
      e.preventDefault(); e.stopPropagation();
      if (!work) lift(kind, name); else commit(workSection.current ?? view, `${name} dropped at ${where2(workSection.current ?? view, item)}`);
    } else if (work && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
      e.preventDefault(); e.stopPropagation();
      const next = step(item, e.key === 'ArrowUp' ? -1 : 1);
      apply(next, item); setNote(`${name} at ${where2(next, item)}`);
    } else if (work && e.key === 'Escape') {
      e.preventDefault(); e.stopPropagation();
      workSection.current = undefined; setWork(undefined); focus.current = gripId(kind, name); setNote(`${name} move cancelled`);
    }
  };
  const doRename = () => {
    if (!renaming) return;
    const next = renameGroup(view, renaming.from, renaming.text);
    if (next) commit(next); else setNote('Group names must be unique and at most 40 characters.');
    if (next || !renaming.text.trim()) setRenaming(undefined);
  };
  const finishDrop = () => {
    if (drag && dropTo) {
      if (drag.kind === 'port' && dropTo.kind === 'port') commit(movePort(view, drag.name, dropTo.group, dropTo.index));
      else if (drag.kind === 'group' && dropTo.kind === 'group') {
        const from = view.groups.findIndex(g => g.name === drag.name);
        commit(moveGroup(view, drag.name, dropTo.index - (from < dropTo.index ? 1 : 0)));
      }
    }
    setDrag(undefined); setDropTo(undefined);
  };
  const half = (e: React.DragEvent) => { const r = e.currentTarget.getBoundingClientRect(); return e.clientY > r.top + r.height / 2 ? 1 : 0; };
  const newGroup = (move?: string) => {
    const [next, name] = addGroup(view);
    commit(move ? movePort(next, move, name, 0) : next);
    setRenaming({ from: name, text: name }); setMenu(undefined);
  };
  const removeG = (name: string) => {
    const count = listOf(view, name).length;
    setUndo({ section: view, text: `Removed group ${name}${count ? ` · ${count} moved to Ungrouped` : ''}` });
    commit(removeGroup(view, name)); setMenu(undefined);
  };

  const attnOf = (ports: string[]) => ports.map(p.attention).find(Boolean);
  const popover = (items: ReactNode) => <div className="lay-pop" data-menu>{items}</div>;

  const editRow = (name: string, groupNames: (string | null)[]) => {
    const s = p.summary(name), id = gripId('port', name), lifted = work?.item.kind === 'port' && work.item.name === name;
    const w = where(view, name);
    return <div key={name} className={`lay-row ${lifted ? 'lifted' : ''} ${drag?.kind === 'port' && drag.name === name ? 'dragging' : ''}`} data-menu
      onDragOver={e => { if (drag?.kind === 'port') { e.preventDefault(); setDropTo({ kind: 'port', group: w.group, index: w.index + half(e) }); } }}
      onDrop={e => { e.preventDefault(); finishDrop(); }}>
      <button id={id} className="grip" draggable aria-label={`Reorder ${name}. Space to lift, arrow keys to move.`} aria-pressed={lifted}
        onDragStart={e => { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', name); setDrag({ kind: 'port', name }); }} onDragEnd={() => { setDrag(undefined); setDropTo(undefined); }}
        onKeyDown={gripKey('port', name)}><GripVertical size={13}/></button>
      <span className="lay-name truncate">{name}</span><span className={`lay-sum truncate ${s.warn ? 'warn' : ''}`}>{s.text}</span>
      <span className="pt-moves"><button className="icon-btn sm" title="Move up" aria-label={`Move ${name} up`} onClick={() => commit(nudge(view, name, -1))}><ArrowUp size={11}/></button>
        <button className="icon-btn sm" title="Move down" aria-label={`Move ${name} down`} onClick={() => commit(nudge(view, name, 1))}><ArrowDown size={11}/></button></span>
      <button className={`icon-btn ${menu?.kind === 'port' && menu.name === name ? 'on' : ''}`} aria-label={`Actions for ${name}`} aria-haspopup="menu" data-menu
        onClick={() => setMenu(menu?.kind === 'port' && menu.name === name ? undefined : { kind: 'port', name })}><MoreHorizontal size={13}/></button>
      {menu?.kind === 'port' && menu.name === name && popover(<>
        <button onClick={() => { commit(nudge(view, name, -1)); setMenu(undefined); }}>Move up<kbd>Alt ↑</kbd></button>
        <button onClick={() => { commit(nudge(view, name, 1)); setMenu(undefined); }}>Move down<kbd>Alt ↓</kbd></button>
        <div className="lay-lbl">Move to group</div>
        {groupNames.map(g => <button key={g ?? 'ungrouped'} className={w.group === g ? 'cur' : ''} onClick={() => { commit(movePort(view, name, g, 1e9)); setMenu(undefined); }}>{g ?? 'Ungrouped'}</button>)}
        <button onClick={() => newGroup(name)}><Plus size={12}/>New group…</button>
      </>)}
    </div>;
  };

  const groupHead = (g: { name: string; ports: string[] } | { name: null; ports: string[] }, i: number, isOpen: boolean) => {
    const named = g.name !== null, label = g.name ?? 'Ungrouped', k = key(label), renamingThis = named && renaming?.from === g.name;
    const attn = !isOpen ? attnOf(g.ports) : undefined, id = gripId('group', label);
    const off = named && view.groups.find(x => x.name === g.name)?.previewOff === true;
    const lifted = work?.item.kind === 'group' && work.item.name === g.name;
    return <div key={'h:' + label} className={`grp-head ${named ? '' : 'ug'} ${lifted ? 'lifted' : ''} ${drag?.kind === 'group' && drag.name === g.name ? 'dragging' : ''}`} data-menu
      onDragOver={e => { if (!p.edit || !drag) return; if (drag.kind === 'port') { e.preventDefault(); setDropTo({ kind: 'port', group: g.name, index: 0 }); } else if (named) { e.preventDefault(); setDropTo({ kind: 'group', index: view.groups.findIndex(x => x.name === g.name) + half(e) }); } }}
      onDrop={e => { e.preventDefault(); finishDrop(); }}>
      {p.edit && !named && <span className="grip-spacer"/>}
      {p.edit && named && <button id={id} className="grip" draggable aria-label={`Reorder group ${label}. Space to lift, arrow keys to move.`} aria-pressed={lifted}
        onDragStart={e => { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', label); setDrag({ kind: 'group', name: label }); }} onDragEnd={() => { setDrag(undefined); setDropTo(undefined); }}
        onKeyDown={gripKey('group', label)}><GripVertical size={13}/></button>}
      {renamingThis
        ? <span className="grp-rename"><input autoFocus value={renaming!.text} maxLength={40} aria-label="Group name" onChange={e => setRenaming({ ...renaming!, text: e.target.value })} onFocus={e => e.currentTarget.select()}
            onKeyDown={e => { if (e.key === 'Enter') doRename(); if (e.key === 'Escape') { e.stopPropagation(); setRenaming(undefined); } }} onBlur={doRename}/><span className="muted">Enter saves · Esc cancels</span></span>
        : <button className="grp-fold" aria-expanded={isOpen} title={p.edit && named ? 'Double-click or press F2 to rename' : undefined} onClick={() => p.toggle(k)} onDoubleClick={() => named && p.edit && setRenaming({ from: g.name!, text: g.name! })}
            onKeyDown={e => { if (e.key === 'F2' && named && p.edit) setRenaming({ from: g.name!, text: g.name! }); if (e.key === 'Delete' && named && p.edit) removeG(g.name!); }}>
            <ChevronRight size={13} className={isOpen ? 'rotate' : ''}/><span className="grp-name truncate">{label}</span><span className="sec-count">{g.ports.length}</span></button>}
      {attn && <span className="attn"><i/>{attn}</span>}
      <span className="grow"/>
      {!p.edit && named && p.hasPreview?.(g.ports) && <button className="icon-btn eyebtn" style={{ marginRight: p.eyeMargin }} aria-pressed={!off}
        title={off ? 'Group Preview off: show the previews in this group again' : 'Group Preview on: hide the previews in this group. Each item keeps its own eye.'} aria-label={`Preview for group ${label}`}
        onClick={() => commit(setGroupPreview(view, g.name!, off))}>{off ? <EyeOff size={14}/> : <Eye size={14}/>}</button>}
      {p.edit && named && <button className={`icon-btn ${menu?.kind === 'group' && menu.name === g.name ? 'on' : ''}`} aria-label={`Actions for group ${label}`} aria-haspopup="menu" data-menu
        onClick={() => setMenu(menu?.kind === 'group' && menu.name === g.name ? undefined : { kind: 'group', name: g.name! })}><MoreHorizontal size={13}/></button>}
      {menu?.kind === 'group' && menu.name === g.name && popover(<>
        <button onClick={() => { setRenaming({ from: g.name!, text: g.name! }); setMenu(undefined); }}>Rename<kbd>F2</kbd></button>
        <button disabled={i === 0} onClick={() => { commit(moveGroup(view, g.name!, i - 1)); setMenu(undefined); }}>Move up<kbd>Alt ↑</kbd></button>
        <button disabled={i === view.groups.length - 1} onClick={() => { commit(moveGroup(view, g.name!, i + 1)); setMenu(undefined); }}>Move down<kbd>Alt ↓</kbd></button>
        <button className="danger" onClick={() => removeG(g.name!)}>Remove group<kbd>Del</kbd></button>
      </>)}
    </div>;
  };

  const containers = [...view.groups.map(g => ({ name: g.name as string | null, ports: g.ports })), ...(view.ungrouped.length || (p.edit && hasGroups) ? [{ name: null as string | null, ports: view.ungrouped }] : [])];
  const groupNames = [...view.groups.map(g => g.name as string | null), null];
  const attention = !open ? attnOf(p.names) : undefined;

  return <section className={`sec ${p.previewOff ? 'pv-off' : ''}`}>
    <div className="sec-head">
      <button className="sec-fold" aria-expanded={open} onClick={() => p.toggle(p.title)}><ChevronRight size={13} className={open ? 'rotate' : ''}/><span className="sec-title">{p.title}</span><span className="sec-count">{p.names.length}</span>
        {attention && <span className="attn"><i/>{p.names.filter(n => p.attention(n)).length} need attention</span>}</button>
      <span className="grow"/>
      {p.edit ? <><button className="ghost-btn" onClick={() => newGroup()}><Plus size={12}/>Group</button>{p.editHead}</> : p.head}
    </div>
    {open && (p.names.length === 0 ? p.empty : <div onDragOver={e => { if (drag) e.preventDefault(); }} onDrop={e => { e.preventDefault(); finishDrop(); }}
      onDragLeave={e => { if (!e.currentTarget.contains(e.relatedTarget as Node)) setDropTo(undefined); }}>
      {containers.map((c, i) => {
        const named = c.name !== null, at = named ? i : view.groups.length;
        const mark = (group: string | null, index: number) => dropTo?.kind === 'port' && dropTo.group === group && dropTo.index === index ? <div className="lay-drop"/> : null;
        const rows = <>{c.ports.map((n, k) => <Fragment key={n}>{mark(c.name, k)}{p.edit ? editRow(n, groupNames) : <div>{p.row(n)}</div>}</Fragment>)}{mark(c.name, c.ports.length)}</>;
        if (!hasGroups) return <div key="flat" className="flat">{rows}</div>;
        const isOpen = !p.fold[key(c.name ?? 'Ungrouped')];
        return <div key={c.name ?? 'ungrouped'}>
          {dropTo?.kind === 'group' && dropTo.index === at && <div className="lay-drop"/>}
          {groupHead(c.name === null ? { name: null, ports: c.ports } : { name: c.name, ports: c.ports }, named ? i : -1, isOpen)}
          {(isOpen || dropTo?.kind === 'port' && dropTo.group === c.name) && <div className={`grp-body ${c.name !== null && view.groups.find(x => x.name === c.name)?.previewOff ? 'grp-off' : ''}`}>{rows}</div>}
        </div>;
      })}
      {p.edit && !hasGroups && <div className="lay-hint">Use + Group to organize these. Groups are saved with the repository.</div>}
    </div>)}
    <span className="sr" role="status" aria-live="polite">{note}</span>
    {work && <div className="lay-keys"><span><kbd>Space</kbd> drop</span><span><kbd>↑</kbd><kbd>↓</kbd> move across groups</span><span><kbd>Esc</kbd> cancel</span></div>}
    {undo && <div className="lay-toast" role="status"><span>{undo.text}</span><button onClick={() => { p.onChange(undo.section); setUndo(undefined); }}>Undo</button></div>}
  </section>;
}
