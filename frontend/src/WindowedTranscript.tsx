import { Children, cloneElement, createContext, useContext, useLayoutEffect, useRef, useState, type ReactElement, type ReactNode } from 'react';
import { useMessageScrollerVisibility } from '@/components/ui/message-scroller';

const DisclosureState = createContext<Map<string, boolean> | null>(null);
/** Preserve manual expansion while a row's expensive content is unmounted. */
export function useTranscriptDisclosure(key: string) {
  const cache = useContext(DisclosureState);
  const [open, setOpen] = useState<boolean | undefined>(() => cache?.get(key));
  return [open, (value: boolean) => { cache?.set(key, value); setOpen(value); }] as const;
}

/** Keep addressable scroll anchors; mount expensive transcript bodies near the viewport only. */
export function WindowedTranscript({ children }: { children: ReactNode }) {
  const disclosure = useRef(new Map<string, boolean>());
  const { visibleMessageIds } = useMessageScrollerVisibility();
  const rows = Children.toArray(children) as ReactElement<{ messageId: string; children: ReactNode }>[];
  const visible = new Set(visibleMessageIds);
  const indices = rows.flatMap((row, index) => visible.has(row.props.messageId) ? [index] : []);
  const first = indices.length ? Math.max(0, indices[0] - 5) : Math.max(0, rows.length - 12);
  const last = indices.length ? Math.min(rows.length - 1, indices[indices.length - 1] + 5) : rows.length - 1;
  return <DisclosureState.Provider value={disclosure.current}>{rows.map((row, index) => <MeasuredRow key={row.key ?? row.props.messageId} row={row} active={index >= first && index <= last}/>)}</DisclosureState.Provider>;
}
function MeasuredRow({ row, active }: { row: ReactElement<{ messageId: string; children: ReactNode }>; active: boolean }) {
  const ref = useRef<HTMLDivElement>(null);
  const height = useRef(100);
  // Keep a focused editor or a selected passage mounted until the interaction ends.
  const pinned = !!ref.current && (ref.current.contains(document.activeElement) || (!!window.getSelection()?.anchorNode && ref.current.contains(window.getSelection()!.anchorNode)));
  const mounted = active || pinned;
  useLayoutEffect(() => {
    if (!mounted || !ref.current) return;
    const measure = () => { if (ref.current) height.current = ref.current.getBoundingClientRect().height; };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, [mounted]);
  return cloneElement(row as ReactElement<Record<string, unknown>>, {
    ref,
    'data-windowed': !mounted || undefined,
    children: mounted ? row.props.children : <div style={{ height: height.current }} aria-hidden="true"/>,
  });
}
