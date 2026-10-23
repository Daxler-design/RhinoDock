import { useEffect, useState } from 'react';
import { Puzzle } from 'lucide-react';
import { fitRange, NumberField } from './NumberField';
import { ColorInput } from './PickInput';
import { ListRows } from './ListRows';
import type { Action, PickItem, Picking, Port } from './types';

export function ListDetails({ port, picking, act }: { port: Port; picking?: Picking | null; act: (a: Action) => void }) {
  return <ListRows port={port} busy={!!picking} act={act} heading="Value"
    render={(item, i) => <ItemEditor port={port} item={item} index={i} picking={picking} act={act}/>}/>;
}

function ItemEditor({ port, item, index, picking, act }: { port: Port; item: PickItem; index: number; picking?: Picking | null; act: (a: Action) => void }) {
  const [draft, setDraft] = useState<string>();
  useEffect(() => setDraft(undefined), [item.value]);
  const kind = port.itemKind, text = draft ?? item.value ?? item.label;
  const invalid = kind === 'number' ? !text.trim() || !Number.isFinite(Number(text)) : kind === 'int' ? !/^[+-]?\d+$/.test(text.trim()) : false;
  const commit = (value: string) => {
    const valid = kind === 'number' ? !!value.trim() && Number.isFinite(Number(value)) : kind === 'int' ? /^[+-]?\d+$/.test(value.trim()) : true;
    if (valid) { act({ action: 'inputItem', name: port.name, index, value }); setDraft(undefined); }
  };
  const input = (action: Action) => {
    if (action.action === 'input' && action.value !== null) act({ ...action, action: 'inputItem', name: port.name, index });
    else if (action.action === 'pick') act({ ...action, name: port.name, index });
  };
  return <div className="pt-editor" onClick={e => e.stopPropagation()} onPointerDownCapture={() => act({ action: 'activeItem', name: port.name, index })}>
    {kind === 'number' || kind === 'int' ? <NumberField label={`${port.name} item ${index}`} text={text} invalid={invalid} int={kind === 'int'} range={fitRange(Number(item.value) || 0, kind === 'int')}
      onText={setDraft} onBlur={() => { if (draft !== undefined) commit(draft); }} onScrub={commit}/>
    : kind === 'boolean' ? <button className="switch-btn" aria-label={`${port.name} item ${index}`} aria-pressed={text === 'true'} onClick={() => act({ action: 'inputItem', name: port.name, index, value: text !== 'true' })}><span className={`switch ${text === 'true' ? 'on' : ''}`}><span className="knob"/></span>{text === 'true' ? 'True' : 'False'}</button>
    : kind === 'color' ? <ColorInput port={{ ...port, access: 'Item', value: text }} picking={picking} act={input} allowDefault={false}/>
    : kind === 'extension' ? <button className="pick" onClick={() => act({ action: 'extEdit', name: port.name, index })}><Puzzle size={12}/><span className="truncate">{text}</span></button>
    : kind === 'text' ? <div className="field"><input aria-label={`${port.name} item ${index}`} value={text} onChange={e => setDraft(e.target.value)} onBlur={() => { if (draft !== undefined) commit(draft); }} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); }}/></div>
    : <span className="pt-el">{item.label}</span>}
  </div>;
}
