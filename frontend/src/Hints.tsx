import { useEffect, useState } from 'react';

/**
 * The panel's WebView does not show the browser's own tooltips, so every element's `title` is shown here instead. While an element is hovered its
 * title moves to `data-hint` (so a platform that does show tooltips does not show two) and comes back when the pointer leaves.
 */
export function Hints() {
  const [tip, setTip] = useState<{ text: string; x: number; y: number; above: boolean }>();
  useEffect(() => {
    let holder: HTMLElement | undefined, timer = 0;
    const restore = () => {
      clearTimeout(timer);
      if (holder?.dataset.hint !== undefined) { holder.setAttribute('title', holder.dataset.hint); delete holder.dataset.hint; }
      holder = undefined;
      setTip(undefined);
    };
    const over = (e: PointerEvent) => {
      const el = (e.target as Element | null)?.closest?.('[title]') as HTMLElement | null;
      if (!el || el === holder) return;
      restore();
      const text = el.getAttribute('title');
      if (!text?.trim()) return;
      holder = el;
      el.dataset.hint = text;
      el.removeAttribute('title');
      timer = window.setTimeout(() => {
        const r = el.getBoundingClientRect();
        const below = r.bottom + 48 < window.innerHeight;
        setTip({ text, x: r.left + r.width / 2, y: below ? r.bottom + 6 : r.top - 6, above: !below });
      }, 350);
    };
    const out = (e: PointerEvent) => { if (holder && !holder.contains(e.relatedTarget as Node | null)) restore(); };
    document.addEventListener('pointerover', over);
    document.addEventListener('pointerout', out);
    document.addEventListener('pointerdown', restore, true);
    window.addEventListener('blur', restore);
    return () => { restore(); document.removeEventListener('pointerover', over); document.removeEventListener('pointerout', out); document.removeEventListener('pointerdown', restore, true); window.removeEventListener('blur', restore); };
  }, []);
  if (!tip) return null;
  const above = tip.above;
  return <div className="ag-tip" role="tooltip" style={{ left: Math.max(8, Math.min(tip.x - 100, window.innerWidth - 248)), top: above ? undefined : tip.y, bottom: above ? window.innerHeight - tip.y : undefined }}>{tip.text}</div>;
}
