import { useEffect, useRef, useState } from 'react';
import { Pipette, X } from 'lucide-react';
import { StyleNumber } from './NumberField';
import type { Family, PreviewStyle, StyleEdit } from './types';

const swatches = ['#9AA0F2', '#56B98B', '#D9A54C', '#EC6F67', '#52B9C5', '#CC8DC4', '#DDD2BD', '#8F9BA5'];

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
  if (family === 'surface' || family === 'mixed') return <svg width="16" height="16" viewBox="0 0 16 16"><rect x="3" y="3" width="10" height="10" rx="2" fill={c} fillOpacity={style.shading / 100} stroke={c} strokeWidth={Math.min(style.weight, 3)}/></svg>;
  return <span className="dot" style={{ background: c }}/>;
}

export function StylePopover({ title, family, style, onChange, onSample, onClose, alpha, heading = 'Preview style', note = 'Changes Preview only. Bake keeps the target layer’s attributes.' }: {
  title: string; family?: Family | null; style: PreviewStyle; onChange: (edit: StyleEdit) => void; onSample: () => void; onClose: () => void; alpha?: boolean; heading?: string; note?: string;
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
  const edit = (patch: Partial<StyleEdit>) => onChange({ color: style.custom ? style.color : null, weight: style.weight, size: style.size, shading: style.shading, ...patch });
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
  const sizes = family === 'curve' ? ['weight'] : family === 'point' ? ['size'] : family === 'surface' ? ['weight', 'shading'] : family === 'mixed' ? ['weight', 'size', 'shading'] : [];
  return <div className="style-pop" ref={ref} role="dialog" aria-label={`${heading} for ${title}`}>
    <div className="style-head"><strong>{title}</strong><small>{heading}</small><span className="grow"/><button className="icon-btn" aria-label="Close" onClick={onClose}><X size={13}/></button></div>
    <div className="sv" style={{ background: `linear-gradient(to top, #000, rgba(0,0,0,0)), linear-gradient(to right, #fff, rgba(255,255,255,0)), hsl(${Math.round(hsv[0])}, 100%, 50%)` }} onPointerDown={start('sv')} onPointerMove={move} onPointerUp={end} onPointerCancel={end}>
      <span className="thumb" style={{ left: `${hsv[1] * 100}%`, top: `${(1 - hsv[2]) * 100}%`, background: hex }}/>
    </div>
    <div className="hue" onPointerDown={start('hue')} onPointerMove={move} onPointerUp={end} onPointerCancel={end}><span className="thumb" style={{ left: `${hsv[0] / 360 * 100}%` }}/></div>
    {alpha && <div className="hue alpha" style={{ '--c': toHex(hsv) } as React.CSSProperties} onPointerDown={start('alpha')} onPointerMove={move} onPointerUp={end} onPointerCancel={end}><span className="thumb" style={{ left: `${a / 255 * 100}%` }}/></div>}
    <div className="hex-row">
      <button className="tool" title="Sample color from a Rhino object" aria-label="Sample color from a Rhino object" onClick={onSample}><Pipette size={14}/></button>
      <input className="hex" aria-label="Hex color" value={hexDraft ?? (style.custom ? hex : `${hex} · default`)} onFocus={() => setHexDraft(hex)} onChange={e => setHexDraft(e.target.value)} onBlur={commitHex} onKeyDown={e => { if (e.key === 'Enter') e.currentTarget.blur(); if (e.key === 'Escape') setHexDraft(undefined); }}/>
    </div>
    <div className="swatches">
      <button className={`sw-none ${style.custom ? '' : 'sel'}`} title="Default color" aria-label="Use default color" onClick={() => edit({ color: null })}/>
      {swatches.map(c => <button key={c} className={style.custom && style.color.toUpperCase() === c ? 'sel' : ''} style={{ background: c }} title={c} aria-label={c} onClick={() => edit({ color: c })}/>)}
    </div>
    {sizes.length > 0 && <div className="style-div"/>}
    {sizes.includes('weight') && <StyleNumber label={family === 'curve' ? 'Line weight' : 'Edge weight'} value={style.weight} soft={{ min: 0, max: 8, step: 1 }} limits={{ min: 0, max: 32 }} suffix="px" onCommit={weight => edit({ weight })}/>}
    {sizes.includes('size') && <StyleNumber label="Point size" value={style.size} soft={{ min: 0, max: 12, step: 1 }} limits={{ min: 0, max: 64 }} suffix="px" onCommit={size => edit({ size })}/>}
    {sizes.includes('shading') && <StyleNumber label="Shading" value={style.shading} soft={{ min: 0, max: 100, step: 5 }} limits={{ min: 0, max: 100 }} suffix="%" onCommit={shading => edit({ shading })}/>}
    <div className="style-note">{note}</div>
  </div>;
}
