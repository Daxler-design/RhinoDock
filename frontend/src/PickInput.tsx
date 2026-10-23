import { useState } from 'react';
import { Crosshair, Eye, EyeOff, Link2, MousePointer2, Plus } from 'lucide-react';
import { PickerPopover, StyleGlyph } from './StylePopover';
import { ListRows } from './ListRows';
import type { Action, Picking, Port } from './types';

const pickingLabel = (picking: Picking) => picking.mode === 'add' ? 'Adding in Rhino…' : picking.mode === 'one' ? `Replacing element ${picking.index}…`
  : picking.mode === 'color' ? 'Sampling a color…' : 'Picking in Rhino…';

/** ComponentIndex ports pick sub-objects (ADR 0006); the port type name says so. */
const isSubObject = (port: Port) => port.type.includes('ComponentIndex');

/** The input-row control: the picked count, or the live pick state with Cancel. */
export function PickButton({ port, picking, act }: { port: Port; picking?: Picking | null; act: (a: Action) => void }) {
  if (picking?.name === port.name) return <div className="pick active" role="status"><span className="pulse"/><span className="truncate">{pickingLabel(picking)}</span>
    <button className="mini" onClick={() => act({ action: 'cancelPick' })}>Cancel</button></div>;
  return <button className={`pick ${port.picked ? '' : 'unpicked'}`} disabled={!!picking} onClick={() => act({ action: 'pick', name: port.name, mode: 'pick' })}
    title={isSubObject(port) ? `Pick sub-objects for ${port.name}: select vertices, edges, or faces in Rhino first (Ctrl/Cmd+Shift-click) and they are used directly, or click them at the prompt${port.access === 'List' ? ' (replaces the list)' : ''}`
      : port.access === 'List' ? `Pick objects for ${port.name} in Rhino (replaces the list)` : `Pick an object for ${port.name} in Rhino`}>
    <Crosshair size={13}/><span className="truncate">{port.picked || 'Pick in Rhino'}</span></button>;
}

/** A Color input: the Preview style picker with alpha and without size controls. A list shows one chip per color.
 *  The default swatch clears a single input (the Script default applies) or removes a list element. */
export function ColorInput({ port, picking, act, allowDefault = true }: { port: Port; picking?: Picking | null; act: (a: Action) => void; allowDefault?: boolean }) {
  const [open, setOpen] = useState<number>();
  if (picking?.name === port.name) return <PickButton port={port} picking={picking} act={act}/>;
  const list = port.access === 'List';
  const colors = port.value ? port.value.split(/\s*,\s*/) : [];
  const placeholder = port.optional ? 'Optional' : 'Required';
  const current = open === undefined ? undefined : colors[open];
  const change = (color: string | null) => {
    const next = list ? [...colors] : [];
    if (color) next[open!] = color; else { next.splice(open!, 1); setOpen(undefined); }
    act({ action: 'input', name: port.name, value: next.length ? next.join(', ') : null });
  };
  return <PickerPopover open={open !== undefined} onOpenChange={value => { if (!value) setOpen(undefined); }}
    trigger={<div className="color-in">
    {list ? <div className={`pick color-chips ${colors.length ? '' : 'unpicked'}`}>
      {colors.map((c, i) => <button key={i} className={`dot ${open === i ? 'sel' : ''}`} style={{ background: c }} title={c} aria-label={`${port.name} color ${i + 1}: ${c}`} onClick={() => setOpen(open === i ? undefined : i)}/>)}
      <button className="icon-btn sm" disabled={!!picking} title="Add a color" aria-label={`Add a color to ${port.name}`} onClick={() => setOpen(colors.length)}><Plus size={12}/></button>
      {!colors.length && <span className="truncate">{placeholder}</span>}
    </div>
    : <button className={`pick ${port.value ? '' : 'unpicked'}`} disabled={!!picking} aria-expanded={open !== undefined} aria-label={`Color for ${port.name}`} onClick={() => setOpen(open === undefined ? 0 : undefined)}>
      {port.value ? <span className="dot" style={{ background: port.value }}/> : <span className="dot none"/>}<span className="truncate">{port.value ?? placeholder}</span></button>}
    </div>}
    title={list ? `${port.name} ${(open ?? 0) + 1}` : port.name} style={{ color: current ?? '#808080', custom: !!current, weight: 1, size: 1, shading: 0 }}
    alpha allowDefault={allowDefault} heading="Color" note={!allowDefault ? 'Edits this list element.' : list ? 'Default removes this color.' : 'Default uses the Script’s own value.'}
    onChange={edit => change(edit.color)} onSample={() => act({ action: 'pick', name: port.name, mode: 'color', index: open })}/>;
}

/** Details for any picked geometry input: Preview, element list with reorder and replace, and list actions. */
export function PickDetails({ port, picking, act }: { port: Port; picking?: Picking | null; act: (a: Action) => void }) {
  const [styleOpen, setStyleOpen] = useState(false);
  const list = port.access === 'List';
  const busy = !!picking;
  const preview = port.preview;
  return <>
    {port.picked && preview && <><span className="adv-k">Preview</span><div className="pv-row">
      <button className="icon-btn sm eyebtn" title={preview.visible ? 'Hide Preview' : 'Show Preview'} aria-label={preview.visible ? `Hide Preview for ${port.name}` : `Show Preview for ${port.name}`}
        onClick={() => act({ action: 'preview', name: port.name, visible: !preview.visible })}>{preview.visible ? <Eye size={14}/> : <EyeOff size={14}/>}</button>
      <PickerPopover open={styleOpen} onOpenChange={setStyleOpen} align="start"
        trigger={<button className={`sw-btn ${styleOpen ? 'on' : ''}`} title="Preview style" aria-label={`Preview style for ${port.name}`} aria-expanded={styleOpen} onClick={() => setStyleOpen(!styleOpen)}><StyleGlyph family={port.family} style={preview.style}/></button>}
        title={port.name} family={port.family} style={preview.style} onChange={style => act({ action: 'pstyle', name: port.name, style })} onSample={index => act({ action: 'pick', name: port.name, mode: 'color', index })}/>
    </div></>}
    <ListRows port={port} busy={busy} act={act} heading="Element" columns={<><span className="pt-col">Ref</span><span className="pt-col">Show</span></>}
      activate={i => { act({ action: 'activeItem', name: port.name, index: list ? i : undefined }); act({ action: 'selectPicked', name: port.name, index: i }); }}
      render={(item, i) => <><span className="pt-el" title={item.label}>{item.label}</span>
        <button className="icon-btn sm pt-col ref" disabled={busy} title={isSubObject(port) ? 'A part of a Rhino object · click to replace this element' : 'Linked to a Rhino object · click to replace this element'} aria-label={`Replace element ${i}`}
          onClick={e => { e.stopPropagation(); act({ action: 'pick', name: port.name, mode: 'one', index: i }); }}><Link2 size={12} className="a"/><Crosshair size={13} className="b"/></button>
        <button className="icon-btn sm pt-col" title={item.visible ? 'Hide this element' : 'Show this element'} aria-label={`${item.visible ? 'Hide' : 'Show'} element ${i}`}
          onClick={e => { e.stopPropagation(); act({ action: 'element', name: port.name, index: i, visible: !item.visible }); }}>{item.visible ? <Eye size={13}/> : <EyeOff size={13}/>}</button>
      </>}/>
    <div className="pick-actions">
      {list && port.picked && <button className="btn-sm" disabled={busy} onClick={() => act({ action: 'pick', name: port.name, mode: 'add' })}><Plus size={11}/>Add</button>}
      {port.picked && <button className="btn-sm" title="Select these objects in Rhino" onClick={() => act({ action: 'selectPicked', name: port.name })}><MousePointer2 size={11}/>Select</button>}
      <span className="grow"/>
      {port.picked && <button className="btn-sm danger" disabled={busy} onClick={() => act({ action: 'input', name: port.name, value: null })}>Clear</button>}
    </div>
  </>;
}
