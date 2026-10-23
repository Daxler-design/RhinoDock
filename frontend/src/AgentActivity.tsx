import { type ReactNode } from 'react';
import { useTranscriptDisclosure } from './WindowedTranscript';
import { Brain, Check, FileText, Globe, Loader2, Pencil, Search, Terminal, TriangleAlert } from 'lucide-react';
import { Trace, TraceCollapsible, TraceCollapsibleContent, TraceCollapsibleTrigger, TraceDescription, TraceIcon, TraceLine, TraceTitle } from '@/components/ui/trace';
import type { AgentItem } from './types';

const activity = (item: AgentItem) => item.kind === 'tool' || item.kind === 'thought';
export function groupActivity(items: AgentItem[]) {
  const groups: AgentItem[][] = [];
  for (const item of items.filter(i => !i.parent || !items.some(p => p.callId === i.parent))) {
    if (activity(item) && groups.at(-1)?.every(activity)) groups.at(-1)!.push(item);
    else groups.push([item]);
  }
  return groups;
}
const running = (item: AgentItem) => item.status === 'Pending' || item.status === 'InProgress';
type Props = { items: AgentItem[]; all: AgentItem[]; detail: (item: AgentItem) => ReactNode; pictures: (item: AgentItem) => ReactNode; depth?: number };
export function AgentActivity({ items, all, detail, pictures, depth = 0 }: Props) {
  const [open, setOpen] = useTranscriptDisclosure(`group:${items[0]?.id}`);
  const calls = items.filter(i => i.kind === 'tool');
  const children = all.filter(i => i.parent && items.some(p => p.callId === i.parent));
  const failed = [...items, ...children].filter(i => i.status === 'Failed').length;
  const active = [...items, ...children].some(running);
  const rows = <Trace>{items.map(item => <ActivityLine key={item.id} item={item} all={all} detail={detail} pictures={pictures} depth={depth}/>)}</Trace>;
  if (items.length === 1) return rows;
  const edits = new Set(calls.filter(i => ['Edit', 'Delete', 'Move'].includes(i.toolKind ?? '')).flatMap(i => i.files)).size;
  return <Trace><TraceCollapsible open={open ?? (active || failed > 0)} onOpenChange={setOpen}>
    <TraceCollapsibleTrigger><TraceLine status={failed ? 'error' : active ? 'running' : undefined}>
      <TraceIcon>{failed ? <TriangleAlert/> : active ? <Loader2 className="animate-spin"/> : <Check/>}</TraceIcon>
      <TraceTitle>{active ? 'Working' : 'Activity'} · {calls.length} tool {calls.length === 1 ? 'call' : 'calls'}{edits ? ` · ${edits} ${edits === 1 ? 'file' : 'files'} edited` : ''}{!calls.length ? ` · ${items.length} reasoning steps` : ''}</TraceTitle>
      {!!failed && <TraceDescription>{failed} failed</TraceDescription>}
    </TraceLine></TraceCollapsibleTrigger>
    <TraceCollapsibleContent>{rows}</TraceCollapsibleContent>
  </TraceCollapsible></Trace>;
}
function ActivityLine({ item, all, detail, pictures, depth = 0 }: Omit<Props, 'items'> & { item: AgentItem }) {
  const [open, setOpen] = useTranscriptDisclosure(`item:${item.id}`);
  const children = depth < 5 ? all.filter(i => i.parent && i.parent === item.callId && i.id !== item.id) : [];
  const failed = item.status === 'Failed', active = running(item);
  const thought = item.kind === 'thought';
  const edit = ['Edit', 'Delete', 'Move'].includes(item.toolKind ?? '');
  const lines = edit ? item.detail?.split('\n') ?? [] : [];
  const added = lines.filter(l => l.startsWith('+') && !l.startsWith('+++')).length;
  const removed = lines.filter(l => l.startsWith('-') && !l.startsWith('---')).length;
  const Icon = failed ? TriangleAlert : active ? Loader2 : thought ? Brain : edit ? Pencil : item.toolKind === 'Execute' ? Terminal : item.toolKind === 'Read' ? FileText : item.toolKind === 'Search' ? Search : item.toolKind === 'Fetch' ? Globe : Check;
  const title = thought ? 'Thinking' : item.title ?? item.toolKind ?? 'Tool';
  const row = <TraceLine status={failed ? 'error' : item.status === 'Pending' ? 'pending' : active ? 'running' : undefined} title={item.files.join(', ')}>
    <TraceIcon><Icon className={active ? 'animate-spin' : undefined}/></TraceIcon>
    <TraceTitle>{failed ? 'Failed: ' : ''}{title}</TraceTitle>
    {!!(added || removed) && <TraceDescription>+{added} −{removed}</TraceDescription>}
    {!!children.length && <TraceDescription>{children.length} {children.length === 1 ? 'step' : 'steps'}</TraceDescription>}
  </TraceLine>;
  if (!item.detail && !(thought && item.text) && !children.length && !item.images.length) return row;
  return <TraceCollapsible open={open ?? (active || failed)} onOpenChange={setOpen}>
    <TraceCollapsibleTrigger>{row}</TraceCollapsibleTrigger>
    <TraceCollapsibleContent>
      {thought ? <div className="whitespace-pre-wrap">{item.text}</div> : item.detail ? detail(item) : null}
      {pictures(item)}
      {!!children.length && <AgentActivity items={children} all={all} detail={detail} pictures={pictures} depth={depth + 1}/>}
    </TraceCollapsibleContent>
  </TraceCollapsible>;
}
