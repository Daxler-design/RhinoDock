import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Pipette, X } from 'lucide-react';
import { StyleNumber } from './NumberField';
import { Popover, PopoverAnchor, PopoverContent } from '@/components/ui/popover';
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group';
import { recentColors, rememberColor } from './recentColors';
import type { Family, PreviewStyle, StyleEdit } from './types';

const swatches = ['#9AA0F2', '#56B98B', '#D9A54C', '#EC6F67'];

function toHsv(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1, 7), 16), r = (n >> 16 & 255) / 255, g = (n >> 8 & 255) / 255, b = (n & 255) / 255;
  const max = Math.max(r, g, b), d = max - Math.min(r, g, b);
  const h = !d ? 0 : max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [(h * 60 + 360) % 360, max ? d / max : 0, max];
}
function toHex([h, s, v]: [number, number, number]) {
  const f = (n: number) => { const k = (n + h / 60) % 6; return Math.round((v - v * s * Math.max(Math.min(k, 4 - k, 1), 0)) * 255); };
  return '#' + [f(5), f(3), f(1)].map(x => x.toString(16).padStart(2, '0')).join('').toUpperCase();
}

const alphaOf = (hex: string) => hex.length === 9 ? parseInt(hex.slice(7), 16) : 255;

/** Row glyph: a line for curves, a square for points, a filled tile for surfaces, a dot otherwise. */
export function StyleGlyph({ family, style }: { family?: Family | null; style: PreviewStyle }) {
  const c = style.color;
  if (family === 'curve') return <svg width="16" height="16" viewBox="0 0 16 16"><line x1="3" y1="13" x2="13" y2="3" stroke={c} strokeWidth={Math.min(style.weight, 5)} strokeLinecap="round"/></svg>;
  if (family === 'point') { const s = Math.max(3, Math.min(12, style.size + 1)); return <svg width="16" height="16" viewBox="0 0 16 16"><rect x={8 - s / 2} y={8 - s / 2} width={s} height={s} fill={c}/></svg>; }
  if (family === 'surface' || family === 'mixed') return <svg width="16" height="16" viewBox="0 0 16 16"><rect x="3" y="3" width="10" height="10" rx="2" fill={style.fill?.color ?? c} fillOpacity={(style.fill?.opacity ?? style.shading) / 100} stroke={style.edges?.color ?? c} strokeOpacity={(style.edges?.opacity ?? 100) / 100} strokeWidth={Math.min(style.weight, 3)}/></svg>;
  return <span className="dot" style={{ background: c }}/>;
}

type PickerProps = Omit<Parameters<typeof StylePopover>[0], 'onClose'>;

/**
 * The one way a color picker opens, used by every call site: the picker body anchored to its trigger in a popover that flips and stays on
 * screen. Clicking the trigger (or anything marked data-style-anchor) is left to the trigger's own toggle; closing returns focus to it.
 * Call sites differ only in the picker props and in their trigger.
 */
export function PickerPopover({ open, onOpenChange, trigger, align = 'end', ...picker }: PickerProps & { open: boolean; onOpenChange: (open: boolean) => void; trigger: React.ReactElement; align?: 'start' | 'end' }) {
  const anchor = useRef<HTMLDivElement>(null);
  return <Popover open={open} onOpenChange={onOpenChange}>
    <PopoverAnchor asChild ref={anchor} data-style-anchor="">{trigger}</PopoverAnchor>
    {open && <PopoverContent className="color-popup" align={align} sideOffset={4} collisionPadding={8} role="presentation"
      onCloseAutoFocus={e => { e.preventDefault(); (anchor.current?.matches('button') ? anchor.current : anchor.current?.querySelector<HTMLButtonElement>('button'))?.focus(); }}
      onInteractOutside={e => { if ((e.target as Element).closest?.('[data-style-anchor]')) e.preventDefault(); }}>
      <StylePopover {...picker} onClose={() => onOpenChange(false)}/>
    </PopoverContent>}
  </Popover>;
}

function ColorStylePopover({ headerControls, opacity, onOpacity, title, family, style, onChange, onSample, onClose, alpha, allowDefault = true, heading = 'Preview style', note = 'Changes Preview only. Bake keeps the target layer’s attributes.' }: {
  headerControls?: ReactNode; opacity?: number; onOpacity?: (value: number) => void; title: string; family?: Family | null; style: PreviewStyle; onChange: (edit: StyleEdit) => void; onSample?: (index?: number) => void; onClose: () => void; alpha?: boolean; allowDefault?: boolean; heading?: string; note?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [hsv, setHsvState] = useState(() => toHsv(style.color));
  const hsvRef = useRef(hsv);
  const setHsv = (next: [number, number, number]) => { hsvRef.current = next; setHsvState(next); };
  const [a, setAState] = useState(() => alphaOf(style.color));
  const aRef = useRef(a);
  const setA = (next: number) => { aRef.current = next; setAState(next); };
  const [hexDraft, setHexDraft] = useState<string>();
  const drag = useRef<'sv' | 'hue' | 'alpha' | undefined>(undefined);
  useEffect(() => { if (!drag.current) { setHsv(toHsv(style.color)); setA(alphaOf(style.color)); } }, [style.color]);
  /** #RRGGBB, with an AA suffix only when alpha is editable and not opaque. */
  const out = (value: [number, number, number], alphaValue: number) => toHex(value) + (alpha && alphaValue < 255 ? alphaValue.toString(16).padStart(2, '0').toUpperCase() : '');
  useEffect(() => {
    const down = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node) && !(e.target as Element).closest?.('[data-style-anchor]')) onClose(); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('mousedown', down); document.addEventListener('keydown', key);
    return () => { document.removeEventListener('mousedown', down); document.removeEventListener('keydown', key); };
  }, [onClose]);
  const [, refresh] = useState(0);
  const edit = (patch: Partial<StyleEdit>) => {
    if (patch.color) { rememberColor(patch.color); refresh(n => n + 1); }
    onChange({ color: style.custom ? style.color : null, weight: style.weight, size: style.size, shading: style.shading, ...patch });
  };
  const pos = (e: React.PointerEvent<HTMLDivElement>) => { const r = e.currentTarget.getBoundingClientRect(); return [Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)), Math.max(0, Math.min(1, (e.clientY - r.top) / r.height))]; };
  const move = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    const [x, y] = pos(e);
    const old = hsvRef.current;
    if (drag.current === 'alpha') setA(Math.round(x * 255));
    else setHsv(drag.current === 'sv' ? [old[0], x, 1 - y] : [x * 359.9, old[1], old[2]]);
  };
  const start = (kind: 'sv' | 'hue' | 'alpha') => (e: React.PointerEvent<HTMLDivElement>) => { e.preventDefault(); e.currentTarget.setPointerCapture(e.pointerId); drag.current = kind; move(e); };
  const end = () => { if (!drag.current) return; drag.current = undefined; edit({ color: out(hsvRef.current, aRef.current) }); };
  const hex = out(hsv, a);
  const commitHex = () => { const m = hexDraft?.trim().match(alpha ? /^#?([0-9a-f]{6}(?:[0-9a-f]{2})?)$/i : /^#?([0-9a-f]{6})$/i); setHexDraft(undefined); if (m) edit({ color: '#' + m[1].toUpperCase() }); };
  const recent = recentColors().filter(c => !swatches.includes(c)).slice(0, 4);
  const [hsvDraft, setHsvDraft] = useState<string[]>();
  const commitHsv = () => {
    const [h, sat, val] = (hsvDraft ?? []).map(Number);
    setHsvDraft(undefined);
    if (![h, sat, val].every(Number.isFinite)) return;
    const next: [number, number, number] = [Math.max(0, Math.min(359.9, h)), Math.max(0, Math.min(100, sat)) / 100, Math.max(0, Math.min(100, val)) / 100];
    setHsv(next);
    edit({ color: out(next, aRef.current) });
  };
  const sizes = family === 'curve' ? ['weight'] : family === 'point' ? ['size'] : family === 'surface' ? ['weight', 'shading'] : family === 'mixed' ? ['weight', 'size', 'shading'] : [];
  return <div className="style-pop" ref={ref} role="dialog" aria-label={`${heading} for ${title}`}>
    <div className="style-head"><strong>{title}</strong><small>{heading}</small><span className="grow"/>{headerControls}<button className="icon-btn" aria-label="Close" onClick={onClose}><X size={13}/></button></div>
    <div className="sv" style={{ background: `linear-gradient(to top, #000, rgba(0,0,0,0)), linear-gradient(to right, #fff, rgba(255,255,255,0)), hsl(${Math.round(hsv[0])}, 100%, 50%)` }} onPointerDown={start('sv')} onPointerMove={move} onPointerUp={end} onPointerCancel={end}>
      <span className="thumb" style={{ left: `${hsv[1] * 100}%`, top: `${(1 - hsv[2]) * 100}%`, background: hex }}/>
    </div>
    <div className="hue" onPointerDown={start('hue')} onPointerMove={move} onPointerUp={end} onPointerCancel={end}><span className="thumb" style={{ left: `${hsv[0] / 360 * 100}%` }}/></div>
    {alpha && <div className="hue alpha" style={{ '--c': toHex(hsv) } as React.CSSProperties} onPointerDown={start('alpha')} onPointerMove={move} onPointerUp={end} onPointerCancel={end}><span className="thumb" style={{ left: `${a / 255 * 100}%` }}/></div>}
    <div className="hex-row">
      {onSample && <button className="tool" title="Sample color from a Rhino object" aria-label="Sample color from a Rhino object" onClick={() => onSample?.()}><Pipette size={14}/></button>}
      <input className="hex" aria-label="Hex color" value={hexDraft ?? hex} onFocus={() => setHexDraft(hex)} onChange={e => setHexDraft(e.target.value)} onBlur={commitHex} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); if (e.key === 'Escape') setHexDraft(undefined); }}/>
      <span className="hsv">{(['H', 'S', 'V'] as const).map((name, i) => <label key={name}>{name}<input aria-label={['Hue', 'Saturation', 'Value'][i]} inputMode="numeric" value={hsvDraft?.[i] ?? String(Math.round(i === 0 ? hsv[0] : hsv[i] * 100))}
        onFocus={() => setHsvDraft([0, 1, 2].map(k => String(Math.round(k === 0 ? hsv[0] : hsv[k] * 100))))} onChange={e => { const value = e.target.value; setHsvDraft(d => (d ?? [0, 1, 2].map(k => String(Math.round(k === 0 ? hsv[0] : hsv[k] * 100)))).map((x, k) => k === i ? value : x)); }}
        onBlur={commitHsv} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); if (e.key === 'Escape') setHsvDraft(undefined); }}/></label>)}</span>
    </div>
    {/* No color | four recent colors | four defaults */}
    <div className="swatches three">
      {allowDefault && <><button className={`sw-none ${style.custom ? '' : 'sel'}`} title="Default color" aria-label="Use default color" onClick={() => edit({ color: null })}/><i className="sw-div"/></>}
      {Array.from({ length: 4 }, (_, k) => recent[k]
        ? <button key={recent[k]} className={style.custom && style.color.toUpperCase().slice(0, 7) === recent[k] ? 'sel' : ''} style={{ background: recent[k] }} title={`Recent ${recent[k]}`} aria-label={`Recent ${recent[k]}`} onClick={() => edit({ color: recent[k] })}/>
        : <span key={k} className="sw-empty" aria-hidden="true"/>)}
      <i className="sw-div"/>
      {swatches.map(c => <button key={c} className={style.custom && style.color.toUpperCase().slice(0, 7) === c ? 'sel' : ''} style={{ background: c }} title={c} aria-label={c} onClick={() => edit({ color: c })}/>)}
    </div>
    {sizes.length > 0 && <div className="style-div"/>}
    {sizes.includes('weight') && <StyleNumber label={family === 'curve' ? 'Line weight' : 'Edge weight'} value={style.weight} soft={{ min: 0, max: 8, step: 1 }} limits={{ min: 0, max: 32 }} suffix="px" onCommit={weight => edit({ weight })}/>}
    {sizes.includes('size') && <StyleNumber label="Point size" value={style.size} soft={{ min: 0, max: 12, step: 1 }} limits={{ min: 0, max: 64 }} suffix="px" onCommit={size => edit({ size })}/>}
    {opacity !== undefined && <StyleNumber label="Opacity (A)" value={opacity} soft={{ min: 0, max: 100, step: 5 }} limits={{ min: 0, max: 100 }} suffix="%" onCommit={value => onOpacity?.(value)}/>}
    {opacity === undefined && sizes.includes('shading') && <StyleNumber label="Shading" value={style.shading} soft={{ min: 0, max: 100, step: 5 }} limits={{ min: 0, max: 100 }} suffix="%" onCommit={shading => edit({ shading })}/>}
    <div className="style-note">{note}</div>
  </div>;
}

/** Shared picker; geometry styles select a channel without changing ordinary Color inputs. */
export function StylePopover(props: Parameters<typeof ColorStylePopover>[0]) {
  const surface = props.family === 'surface' || props.family === 'mixed';
  const geometry = !!props.family && props.heading === undefined;
  const [channel, setChannel] = useState<'fill' | 'edges'>(surface ? 'fill' : 'edges');
  if (!geometry) return <ColorStylePopover {...props}/>;
  const { style } = props;
  const edges = style.edges ?? { color: style.color, custom: style.custom, opacity: 100 };
  const fill = style.fill ?? { color: style.color, custom: style.custom, opacity: style.shading };
  const selected = channel === 'fill' ? fill : edges;
  const edit = (patch: { color?: string | null; opacity?: number; weight?: number; size?: number }) => {
    const current = { color: selected.custom ? selected.color : null, opacity: selected.opacity, ...patch };
    const edgeEdit = channel === 'edges' ? current : { color: edges.custom ? edges.color : null, opacity: edges.opacity };
    const fillEdit = channel === 'fill' ? current : { color: fill.custom ? fill.color : null, opacity: fill.opacity };
    props.onChange({ color: style.custom ? style.color : null, weight: patch.weight ?? style.weight, size: patch.size ?? style.size,
      shading: fillEdit.opacity, edges: { color: edgeEdit.color, opacity: edgeEdit.opacity }, fill: { color: fillEdit.color, opacity: fillEdit.opacity } });
  };
  return <ColorStylePopover {...props} key={channel} alpha={false}
    style={{ ...style, color: selected.color, custom: selected.custom }}
    headerControls={surface && <ToggleGroup type="single" value={channel} onValueChange={value => { if (value) setChannel(value as 'fill' | 'edges'); }} className="preview-channels" aria-label="Preview color channel">
      <ToggleGroupItem value="fill" aria-label="Fill color" title="Fill color" className="preview-channel"><span className="channel-fill" style={{ background: fill.color }}/></ToggleGroupItem>
      <ToggleGroupItem value="edges" aria-label="Edge color" title="Edge color" className="preview-channel"><span className="channel-edge" style={{ borderColor: edges.color }}/></ToggleGroupItem>
    </ToggleGroup>}
    opacity={selected.opacity} onOpacity={opacity => edit({ opacity })}
    onSample={props.onSample ? () => props.onSample?.(channel === 'fill' ? -2 : -1) : undefined}
    onChange={value => edit({ color: value.color, weight: value.weight, size: value.size })}/ >;
}
