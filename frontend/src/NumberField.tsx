import { useEffect, useRef, useState } from 'react';
import { Pencil } from 'lucide-react';
import type { Range } from './types';

export type Limits = { min?: number; max?: number };

const clean = (n: number) => Number(n.toFixed(10));
export function decimals(step: number) {
  const [base, exponent = '0'] = Number(step.toPrecision(12)).toString().split('e');
  return Math.min(8, Math.max(0, (base.split('.')[1]?.length ?? 0) - Number(exponent)));
}
export const formatStep = (n: number, step: number) => String(Number(n.toFixed(decimals(step))));

/** A soft range for a value that has none: from zero to about twice the value, with a step of 1/100 of its magnitude. */
export function fitRange(value: number, int: boolean): Range {
  const a = Math.abs(value);
  if (!a) return int ? { min: 0, max: 10, step: 1 } : { min: 0, max: 1, step: 0.01 };
  const size = Math.pow(10, Math.floor(Math.log10(a)));
  const top = Math.max(int ? 10 : 0, Math.ceil(a * 2 / size) * size);
  return { min: value < 0 ? -top : 0, max: top, step: int ? 1 : clean(size / 100) };
}

/** Moves a range so the value sits at its center, keeping width and step, within hard limits. */
export function recenter(range: Range, value: number, limits: Limits = {}): Range {
  const width = range.max - range.min;
  let min = clean(Math.round((value - width / 2) / range.step) * range.step);
  if (limits.min !== undefined && min < limits.min) min = limits.min;
  let max = clean(min + width);
  if (limits.max !== undefined && max > limits.max) { max = limits.max; min = Math.max(limits.min ?? -Infinity, clean(max - width)); }
  return { min, max, step: range.step };
}

export const inside = (range: Range, value: number) => value >= range.min && value <= range.max;

/** The numeric field used by inputs and Preview Style sliders: fill, typed entry, and drag handle. */
export function NumberField({ label, text, range, int, invalid, placeholder, suffix, limits, onText, onBlur, onScrub }: {
  label: string; text: string; range: Range; int: boolean; invalid?: boolean; placeholder?: string; suffix?: string; limits?: Limits;
  onText: (value: string) => void; onBlur: () => void; onScrub: (value: string) => void;
}) {
  const number = Number(text);
  const pct = Number.isFinite(number) && text.trim() ? Math.max(0, Math.min(100, (number - range.min) / (range.max - range.min) * 100)) : 0;
  return <div className={`field num ${invalid ? 'invalid' : ''}`} style={{ background: `linear-gradient(to right, rgba(154,160,242,.11) ${pct}%, var(--field) 0)` }}>
    <input value={text} onChange={e => onText(e.target.value)} onBlur={onBlur} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); }} placeholder={placeholder} aria-label={label}/>
    {suffix && <span className="suffix">{suffix}</span>}
    <Scrub value={number} range={range} int={int} limits={limits} update={onText} commit={onScrub}/>
  </div>;
}

/** Drag handle: moving right or up increases the value, left or down decreases it. */
function Scrub({ value, range, int, limits, update, commit }: { value: number; range: Range; int: boolean; limits?: Limits; update: (value: string) => void; commit: (value: string) => void }) {
  const stop = useRef<((apply: boolean) => void) | undefined>(undefined);
  useEffect(() => () => stop.current?.(false), []);
  const begin = (e: React.PointerEvent<HTMLButtonElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    const handle = e.currentTarget;
    // Pointer-down is prevented for dragging; explicitly leave the text editor so history keys reach the panel.
    handle.focus();
    const origin = Number.isFinite(value) ? value : range.min;
    const low = Math.max(range.min, limits?.min ?? -Infinity), high = Math.min(range.max, limits?.max ?? Infinity);
    let travel = 0, current = String(origin);
    const move = (event: PointerEvent) => {
      const scale = event.shiftKey ? .1 : 1;
      const step = int ? 1 : range.step * scale;
      const raw = origin + (travel + (event.movementX - event.movementY) * scale) * range.step;
      const next = Math.max(low, Math.min(high, raw));
      travel = (next - origin) / range.step; // Stop accumulating at the range ends so reversing responds at once.
      current = int ? String(Math.round(next)) : formatStep(Math.round(next / step) * step, step);
      update(current);
    };
    const end = (apply: boolean) => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', cancel);
      window.removeEventListener('blur', cancel);
      stop.current = undefined;
      if (document.pointerLockElement === handle) document.exitPointerLock();
      if (apply) commit(current);
    };
    const up = () => end(true);
    const cancel = () => end(false);
    stop.current = end;
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', cancel);
    window.addEventListener('blur', cancel);
    // Pointer lock keeps the drag going past the screen edge; without it, capture keeps events on the handle.
    try {
      const locked = handle.requestPointerLock?.() as unknown as Promise<void> | undefined;
      const pointer = e.pointerId;
      locked?.catch?.(() => { try { handle.setPointerCapture(pointer); } catch { /* The drag still follows window events. */ } });
      if (!handle.requestPointerLock) handle.setPointerCapture(e.pointerId);
    } catch { handle.setPointerCapture(e.pointerId); }
  };
  return <button className="scrub" title="Drag left/right or up/down to adjust · Shift for fine control" onPointerDown={begin}>⋮⋮</button>;
}

/** Range shown read-only like the Type row; the pencil edits it in place and Apply or Cancel returns. */
export function RangeRow({ range, custom, onApply, onAuto }: { range: Range; custom: boolean; onApply: (range: Range) => void; onAuto: () => void }) {
  const [editing, setEditing] = useState(false);
  const [local, setLocal] = useState({ min: '', max: '', step: '' });
  useEffect(() => { setEditing(false); }, [range.min, range.max, range.step]);
  const parsed = { min: Number(local.min), max: Number(local.max), step: Number(local.step) };
  const valid = [local.min, local.max, local.step].every(v => v.trim() && Number.isFinite(Number(v))) && parsed.max > parsed.min && parsed.step > 0;
  const apply = () => { if (valid) { onApply(parsed); setEditing(false); } };
  if (!editing) return <><span className="adv-k">Range</span><div className="range-view">
    <span>{formatStep(range.min, range.step)} – {formatStep(range.max, range.step)} · step {range.step}</span>
    <span className={`tag ${custom ? '' : 'auto'}`}>{custom ? 'Custom' : 'Auto'}</span><span className="grow"/>
    <button className="icon-btn sm" title="Edit range" aria-label="Edit range" onClick={() => { setLocal({ min: String(range.min), max: String(range.max), step: String(range.step) }); setEditing(true); }}><Pencil size={12}/></button>
  </div></>;
  return <><span className="adv-k">Range</span><div className="range-edit">
    <div className="range-row">{(['min', 'max', 'step'] as const).map(k => <label key={k}><small>{k}</small><input inputMode="decimal" value={local[k]} onChange={e => setLocal({ ...local, [k]: e.target.value })} onKeyDown={e => { if (e.key === 'Enter') apply(); if (e.key === 'Escape') setEditing(false); }}/></label>)}</div>
    {!valid && <div className="range-err">Max must be above Min, and Step above 0.</div>}
    <div className="range-actions">{custom && <button className="link" onClick={() => { onAuto(); setEditing(false); }}>Use auto range</button>}<span className="grow"/><button className="btn-sm" onClick={() => setEditing(false)}>Cancel</button><button className="btn-sm primary" disabled={!valid} onClick={apply}>Apply</button></div>
  </div></>;
}

/** A self-contained style slider: soft range with recentering, direct entry, and hard limits. */
export function StyleNumber({ label, value, soft, limits, suffix, onCommit }: { label: string; value: number; soft: Range; limits: Limits; suffix: string; onCommit: (value: number) => void }) {
  const [text, setText] = useState<string>();
  const [range, setRange] = useState(() => inside(soft, value) ? soft : recenter(soft, value, limits));
  const commit = (raw: string) => {
    setText(undefined);
    const n = Math.round(Number(raw));
    if (!raw.trim() || !Number.isFinite(n)) return;
    const v = Math.max(limits.min ?? -Infinity, Math.min(limits.max ?? Infinity, n));
    if (!inside(range, v)) setRange(recenter(range, v, limits));
    if (v !== value) onCommit(v);
  };
  return <div className="style-num"><span className="style-lbl">{label}</span>
    <NumberField label={label} text={text ?? String(value)} range={range} int limits={limits} suffix={suffix} onText={setText} onBlur={() => { if (text !== undefined) commit(text); }} onScrub={commit}/>
  </div>;
}
