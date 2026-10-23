import { useState, type ReactNode } from 'react';
import { ArrowDown, ArrowUp, GripVertical } from 'lucide-react';
import type { Action, PickItem, Port } from './types';

/** Shared rows; activating a value is independent of selecting Rhino geometry. */
export function ListRows({ port, busy, act, heading, columns, activate, render }: {
  port: Port; busy: boolean; act: (a: Action) => void; heading: string; columns?: ReactNode;
  activate?: (index: number) => void; render: (item: PickItem, index: number) => ReactNode;
}) {
  const [dragging, setDragging] = useState<number>();
  const [over, setOver] = useState<number>();
  const items = port.items ?? [], list = port.access === 'List';
  const choose = (index: number) => activate ? activate(index) : act({ action: 'activeItem', name: port.name, index });
  const move = (from: number, to: number) => act({ action: 'reorder', name: port.name, from, to });
  if (!items.length) return null;
  return <div className="pick-table">
    <div className="pt-row head"><span className="grip"/><span className="pt-i">#</span><span className="pt-el">{heading}</span>{columns}<span className="pt-moves"/></div>
    {items.map((item, i) => <div key={i} tabIndex={0} role="group" aria-label={`${port.name} item ${i}`}
      className={`pt-row ${port.activeIndex === i ? 'active' : ''} ${item.visible && port.preview?.visible !== false ? '' : 'off'} ${over === i && dragging !== undefined && dragging !== i ? 'over' : ''} ${dragging === i ? 'dragging' : ''}`}
      onClick={() => { if (!busy) choose(i); }} onFocusCapture={() => { if (!busy) act({ action: 'activeItem', name: port.name, index: list ? i : undefined }); }}
      onKeyDown={e => { if (e.target === e.currentTarget && e.key === 'Enter' && !busy) { e.preventDefault(); choose(i); } }}
      onDragOver={e => { if (dragging === undefined) return; e.preventDefault(); setOver(i); }}
      onDrop={e => { e.preventDefault(); if (dragging !== undefined && dragging !== i) move(dragging, i); setDragging(undefined); setOver(undefined); }}>
      <span className="grip" draggable={list && items.length > 1 && !busy}
        onDragStart={e => { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(i)); setDragging(i); }}
        onDragEnd={() => { setDragging(undefined); setOver(undefined); }}>
        {list && items.length > 1 && <GripVertical size={12}/>}
      </span><span className="pt-i">{i}</span>{render(item, i)}
      {list && <span className="pt-moves" onClick={e => e.stopPropagation()}>
        <button className="icon-btn sm" disabled={busy || i === 0} title="Move up" aria-label={`Move ${port.name} item ${i} up`} onClick={() => move(i, i - 1)}><ArrowUp size={11}/></button>
        <button className="icon-btn sm" disabled={busy || i === (port.count ?? items.length) - 1} title="Move down" aria-label={`Move ${port.name} item ${i} down`} onClick={() => move(i, i + 1)}><ArrowDown size={11}/></button>
      </span>}
    </div>)}
    {(port.count ?? 0) > items.length && <div className="value-more">Showing first {items.length} of {port.count}</div>}
  </div>;
}
