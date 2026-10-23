import { WindowedTranscript } from './WindowedTranscript';
import { AgentActivity, groupActivity as group } from './AgentActivity';
import { Fragment, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ArrowLeft, ArrowUp, AtSign, Download, Brain, Camera, Check, ChevronDown, ChevronRight, Circle, CircleHelp, FileCode2, FileText, FolderOpen, Hand, History, ImageIcon, ListChecks, Loader2, MessageSquare, MousePointerClick, Paperclip, Pencil, Plus, Search, Star, ShieldAlert, Slash, Sparkles, Maximize2, Minimize2, Square, Globe, Terminal, TriangleAlert, Undo2, X } from 'lucide-react';
import { Streamdown } from 'streamdown';
import { MessageScroller, MessageScrollerButton, MessageScrollerContent, MessageScrollerItem, MessageScrollerProvider, MessageScrollerViewport, useMessageScroller } from '@/components/ui/message-scroller';
import { Message, MessageContent } from '@/components/ui/message';
import { Bubble, BubbleContent } from '@/components/ui/bubble';
import { Boundary } from './Boundary';
import { Marker, MarkerContent, MarkerIcon } from '@/components/ui/marker';
import { Attachment, AttachmentAction, AttachmentActions, AttachmentContent, AttachmentGroup, AttachmentMedia, AttachmentTitle } from '@/components/ui/attachment';
import { defaultFilter } from 'cmdk';
import { DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { image, imagePath, send as sendHost } from './bridge';
import { Annotate, NoteCard, withNotes, type Annotator, type Note } from './Annotate';
import type { Action, Agent, AgentChange, AgentItem, AgentMode, AgentOption, AgentQuestion, Mention, QuestionField, Snapshot, Turn } from './types';

type Send = (action: Action) => void;

const baseModes: { value: AgentMode; label: string; detail: string }[] = [
  { value: 'ask', label: 'Ask', detail: 'Ask before edits and commands' },
  { value: 'edits', label: 'Accept edits', detail: 'Edit files freely, ask before commands' },
  { value: 'plan', label: 'Plan', detail: 'Plan first; nothing is changed' },
  { value: 'all', label: 'Allow all', detail: 'Never ask; you review edits afterwards' },
];
// The agent's own automatic review (Claude Code Auto, Codex Auto-review) appears only where the agent has it; it is not Allow all.
const modesFor = (agent?: Agent) => agent?.features?.autoMode
  ? [...baseModes.slice(0, 3), { value: 'auto' as AgentMode, label: agent.features.autoMode, detail: `${agentName(agent)} reviews each action and approves or blocks it; not Allow all` }, baseModes[3]]
  : baseModes;
// An Enter that confirms an input method's candidate (Chinese, Japanese, Korean) must not send. WebKit ends the composition before the
// keydown, so isComposing is already false there; keyCode 229 still marks it (as in MonoCode's keyboard helper).
const composing = (e: React.KeyboardEvent) => e.nativeEvent.isComposing || e.keyCode === 229;
const isBusy = (agent?: Agent) => agent?.status === 'working' || agent?.status === 'starting';
const agentName = (agent?: Agent) => agent?.agentName ?? agent?.agent ?? 'The agent';

/** Second header row on the Agent tab: the open conversation and New. */
export function AgentHeader({ agent, send, openList }: { agent?: Agent; send: Send; openList: () => void }) {
  return <div className="ag-head">
    <button className="script-btn" onClick={openList} disabled={!agent?.conversations.length && !agent?.sessionId}>
      <MessageSquare size={14} className="file-ic"/><span className="script-name truncate">{agent?.title || (agent?.items.length ? 'Conversation' : 'New conversation')}</span>
      <ChevronDown size={13} className="ud"/>
    </button>
    <AgentPicker agent={agent} send={send}/>
    <button className="icon-btn ag-new" title="New conversation" aria-label="New conversation" disabled={agent?.status === 'missing'} onClick={() => send({ action: 'agentNew' })}><Plus size={15}/></button>
  </div>;
}

function SessionStatus({ activity, label }: { activity?: string | null; label?: string }) {
  if (!activity) return null;
  const name = activity === 'input' ? 'Needs input' : activity === 'running' ? 'Running' : activity === 'done' ? 'Done' : 'Error';
  return <span className={`ag-session-status ${activity}`} role="img" aria-label={label ? `${label}: ${name}` : name} title={label ? `${label}: ${name}` : name}>
    {/* Menu items mute and resize icons without text- and size- classes; the status keeps one color and size everywhere. */}
    {activity === 'input' ? <Hand className="size-[13px] text-current"/> : activity === 'running' ? <Loader2 className="spin size-[13px] text-current"/> : activity === 'done' ? <Circle fill="currentColor" className="size-[7px] text-current"/> : <TriangleAlert className="size-[13px] text-current"/>}
  </span>;
}

function Activities({ activities = [], label }: { activities?: (string | null)[]; label?: string }) {
  return <>{[...new Set(activities)].map(activity => <SessionStatus key={activity} activity={activity} label={label}/>)}</>;
}

function AgentPicker({ agent, send }: { agent?: Agent; send: Send }) {
  return <DropdownMenu>
    <DropdownMenuTrigger asChild><button className="ag-pick" title={agent?.agent ?? 'Choose harness'} aria-label="Choose harness"><span className="truncate">{agentName(agent)}</span><Activities activities={agent?.agents?.flatMap(a => a.activities ?? [])}/><ChevronDown size={11}/></button></DropdownMenuTrigger>
    <DropdownMenuContent align="end" className="ag-dd">
      <DropdownMenuLabel>Harness</DropdownMenuLabel>
      <DropdownMenuGroup>{agent?.agents?.filter(a => a.installed).map(a => <DropdownMenuItem key={a.id} onSelect={() => send({ action: 'agentSelect', value: a.id })}>
        <span className="grow">{a.name}</span><Activities activities={a.activities} label={a.name}/>{agent.agentId === a.id && <Check/>}
      </DropdownMenuItem>)}</DropdownMenuGroup>
    </DropdownMenuContent>
  </DropdownMenu>;
}

/** The Agent tab body. It stays mounted while hidden, so the draft, attachments, and scroll position survive tab switches. */
export function AgentView({ snap, send, listOpen, closeList, hidden }: { snap: Snapshot; send: Send; listOpen: boolean; closeList: () => void; hidden: boolean }) {
  const agent = snap.agent;
  planMode.current = agent?.mode ?? 'ask';
  if (planMode.current !== 'plan') planMode.before = planMode.current;
  const root = useRef<HTMLDivElement>(null);
  const annotate = useRef<Annotator>(undefined);
  const side = agent?.side ?? undefined;
  // "Ask in side chat": the host forks this conversation where the agent can, in Plan. The message around the passage goes along only
  // for agents that cannot fork, as hidden context of the first prompt.
  const startSide = (quote: string, anchor?: { msg: string }) => {
    const source = agent?.items.find(i => String(i.id) === anchor?.msg);
    send({ action: 'agentSide', values: { quote, context: source && source.text.trim() !== quote.trim() ? source.text.slice(0, 1500) : '' } });
  };
  // The passage waits as a quote card above a new side chat's message box; the question is typed after it.
  useEffect(() => {
    if (!side || !agent?.viewId || agent.items.length > 0 || seededSides.has(agent.viewId)) return;
    seededSides.add(agent.viewId);
    annotate.current?.add(side.quote, '', false);
  }, [agent?.viewId, side?.quote]);
  if (!snap.repository) return <main className="insp" hidden={hidden}><div className="empty"><FolderOpen size={30}/><h2>Open a repository first</h2><p>The agent works in the Loaded Repository. Open or create one on the Script tab.</p></div></main>;
  return <div className="ag" hidden={hidden} ref={root}>
    {side && <SideBanner side={side} agent={agent} send={send}/>}
    {agent?.status === 'missing' ? <main className="insp"><Setup agent={agent} send={send}/></main> : <Conversation key={'conversation:' + (agent?.viewId ?? agent?.sessionId ?? 'new')} snap={snap} agent={agent} send={send} hidden={hidden}/>}
    {!!agent?.sideChats?.length && <SideChats chats={agent.sideChats} send={send}/>}
    {agent && agent.changes.length > 0 && <Changes changes={agent.changes} worktree={agent.worktree} repository={agent.repository} send={send}/>}
    <Composer key={'composer:' + (agent?.viewId ?? agent?.sessionId ?? 'new')} snap={snap} agent={agent} send={send} annotator={annotate}/>
    {agent?.status !== 'missing' && <Annotate scope={root} api={annotate} onSide={agent?.features?.permissions === false || !snap.repository ? undefined : startSide}/>}
    {listOpen && agent && <Conversations agent={agent} send={send} close={closeList}/>}
  </div>;
}

function Conversation({ snap, agent, send, hidden }: { snap: Snapshot; agent?: Agent; send: Send; hidden: boolean }) {
  const groups = group(agent?.items ?? []);
  const requested = useRef<number | undefined>(undefined);
  const earlier = () => {
    const history = agent?.history;
    if (!history?.hasMore || requested.current === history.before) return;
    requested.current = history.before;
    send({ action: 'agentHistory', index: history.before });
  };
  // The newest message the user sent while this view was open: sending returns the view to following the bottom.
  const mounted = useRef<number | undefined>(undefined);
  const users = (agent?.items ?? []).filter(i => i.kind === 'user');
  if (mounted.current === undefined && agent?.items.length) mounted.current = users.at(-1)?.id ?? -1;
  const sent = users.length && mounted.current !== undefined && users.at(-1)!.id > mounted.current ? users.at(-1)!.id : undefined;
  return <MessageScrollerProvider autoScroll>
    <ConversationTools agent={agent} groups={groups} hidden={hidden}/>
    <MessageScroller className="ag-scroll">
      <MessageScrollerViewport onScroll={e => { if (e.currentTarget.scrollTop < 80) earlier(); }}>
        {agent?.history?.hasMore && <div><button className="btn-sm" onClick={earlier}>Load earlier messages</button></div>}
        <MessageScrollerContent className="ag-content">
          {agent?.status === 'error' && <div className="ag-card err"><TriangleAlert size={14}/><div className="grow"><strong>Agent error</strong><p>{agent.error}</p><small>Log: {agent.log}</small></div><button className="btn-sm" onClick={() => send({ action: 'agentConnect' })}>Retry</button></div>}
          {!groups.length && agent?.status !== 'error' && <div className="ag-hello">
            {agent?.status === 'ready' ? <><MessageSquare size={22}/><h2>Ask {agentName(agent)}</h2><p>It works in <b>{snap.repository?.split(/[\\/]/).pop()}</b> and edits Script files; rhDock reloads them. Type <kbd>@</kbd> to add Scripts, files, or the Rhino selection, and <kbd>/</kbd> for commands.</p></>
            : <><Loader2 size={18} className="spin"/><p>Starting {agentName(agent)}…</p></>}
          </div>}
          <WindowedTranscript>{groups.map(g => <MessageScrollerItem key={g[0].id} messageId={String(g[0].id)}>
            {/* One item that cannot render shows as plain text; it must not take the whole panel down. */}
            <div data-msg={g[0].id} style={{ display: 'contents' }}><Boundary resetKey={`${agent?.status}:${g.length}:${g.at(-1)?.text.length}:${g.at(-1)?.status}`}
              fallback={error => <div><small className="ag-render-error">Could not render this message ({error.message}); showing the text.</small><pre className="ag-md ag-raw">{g.map(i => i.text || i.title || '').join('\n')}</pre></div>}>
              {g[0].kind === 'user' ? <UserMessage item={g[0]} busy={isBusy(agent)} send={send}/>
              : (g[0].kind === 'tool' || g[0].kind === 'thought') ? <Activity items={g} all={agent?.items ?? []}/>
              : <Entry item={g[0]} send={send} streaming={agent?.status === 'working' && g === groups.at(-1)}
                  busy={isBusy(agent)} answered={!!agent?.items.some(i => i.kind === 'user' && i.id > g[0].id)}/>}
            </Boundary></div>
          </MessageScrollerItem>)}</WindowedTranscript>
          {agent?.permission && <MessageScrollerItem messageId="permission"><div className="ag-card perm"><ShieldAlert size={14}/><div className="grow">
            <strong>Permission needed</strong>
            <p>{agentName(agent)} wants to {agent.permission.kind ? agent.permission.kind.toLowerCase() : 'use'} <b>{agent.permission.title}</b>.</p>
            {agent.permission.detail && <ToolDetail kind={agent.permission.kind} detail={agent.permission.detail}/>}
            <div className="ag-perm-actions">{agent.permission.options.map(o => <button key={o.id} className={`btn-sm ${o.kind === 'AllowOnce' ? 'primary' : o.kind.startsWith('Reject') ? 'danger' : ''}`} onClick={() => send({ action: 'agentPermission', value: o.id })}>{o.name}</button>)}</div>
          </div></div></MessageScrollerItem>}
          {agent?.question && <MessageScrollerItem messageId={'q' + agent.question.id}><Question key={agent.question.id} question={agent.question} send={send}/></MessageScrollerItem>}
          {agent?.status === 'working' && !agent.permission && !agent.question && <MessageScrollerItem messageId="working"><Marker><MarkerIcon><span className="pulse"/></MarkerIcon><MarkerContent>Working…</MarkerContent></Marker></MessageScrollerItem>}
        </MessageScrollerContent>
      </MessageScrollerViewport>
      <MessageScrollerButton/>
      <FollowOnSend sent={sent}/>
    </MessageScroller>
  </MessageScrollerProvider>;
}

function ConversationTools({ agent, groups, hidden }: { agent?: Agent; groups: AgentItem[][]; hidden: boolean }) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const { scrollToMessage } = useMessageScroller();
  const list = (agent?.items ?? []).filter(i => query ? `${i.text} ${i.title ?? ''} ${i.detail ?? ''} ${i.files.join(' ')}`.toLowerCase().includes(query.toLowerCase()) : i.kind === 'user');
  useEffect(() => {
    if (hidden) return;
    const key = (e: KeyboardEvent) => { if ((e.metaKey || e.ctrlKey) && e.key === 'f') { e.preventDefault(); setOpen(true); } };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [hidden]);
  const jump = (item: AgentItem) => {
    const parent = item.parent ? agent?.items.find(i => i.callId === item.parent) ?? item : item;
    const group = groups.find(g => g.some(i => i.id === parent.id));
    if (group) scrollToMessage(String(group[0].id), { align: 'start', behavior: 'smooth' });
    setOpen(false);
  };
  return <div className="ag-nav">
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild><button className="ag-pick" title="Find in conversation (Ctrl/Cmd+F)"><Search size={12}/>Find / prompts</button></PopoverTrigger>
      <PopoverContent className="ag-pop" align="start"><Command shouldFilter={false}>
        <CommandInput placeholder="Find in conversation" value={query} onValueChange={setQuery}/>
        <CommandList><CommandEmpty>No matches in the loaded conversation</CommandEmpty><CommandGroup heading={query ? `${list.length} matches` : 'Prompt outline'}>
          {list.map(i => <CommandItem key={i.id} value={String(i.id)} onSelect={() => jump(i)}><span className="truncate">{i.text || i.title || i.detail || 'Message'}</span></CommandItem>)}
        </CommandGroup></CommandList>
      </Command></PopoverContent>
    </Popover>
    {!!agent?.plan?.length && <Popover><PopoverTrigger asChild><button className="ag-pick"><ListChecks size={12}/>Plan {agent.plan.filter(p => p.status === 'Completed').length}/{agent.plan.length}</button></PopoverTrigger>
      <PopoverContent className="ag-plan-panel" align="end"><strong>Plan</strong>{agent.plan.map((p, i) => <div key={i} className={`ag-plan-row ${p.status.toLowerCase()}`}>
        {p.status === 'Completed' ? <Check size={12}/> : p.status === 'InProgress' ? <Loader2 size={12} className="spin"/> : <span className="ag-plan-dot"/>}<span>{p.content}</span>
      </div>)}</PopoverContent></Popover>}
  </div>;
}

const duration = (ms: number) => ms < 1000 ? `${Math.round(ms)} ms` : ms < 60000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.floor(ms / 60000)}m ${Math.round(ms % 60000 / 1000)}s`;

function UserMessage({ item, busy, send }: { item: AgentItem; busy: boolean; send: Send }) {
  const [confirm, setConfirm] = useState(false);
  useEffect(() => { if (!confirm) return; const t = setTimeout(() => setConfirm(false), 4000); return () => clearTimeout(t); }, [confirm]);
  return <Message align="end" className="ag-user">
    <MessageContent>
      <Bubble variant="secondary" align="end"><BubbleContent>
        {item.images.length > 0 && <span className="ag-msg-images">{item.images.map(id => <Picture key={id} id={id}/>)}</span>}
        {item.chips.length > 0 && <span className="ag-msg-chips">{item.chips.map((c, i) => <span key={i} className="chip">@ {c}</span>)}</span>}
        <div className="ag-user-text ag-md"><Streamdown mode="static" controls={{ code: { copy: true, download: false }, table: { copy: true, download: false, fullscreen: false }, mermaid: false }} linkSafety={{ enabled: false }}
          components={{ a: ({ href, children }) => <a href={href} onClick={e => { e.preventDefault(); if (href) send({ action: 'agentOpenUrl', value: href }); }}>{children}</a> }}>{hardBreaks(item.text)}</Streamdown></div>
      </BubbleContent></Bubble>
      {item.elapsedMs != null && <small className="ag-duration">Turn took {duration(item.elapsedMs)}</small>}
      {item.checkpoint && <button className={`ag-restore ${confirm ? 'on' : ''}`} disabled={busy} title="Remove this prompt and everything after it, and put the files back as they were before it. You can undo this until you send again."
        onClick={() => { if (confirm) { send({ action: 'agentRestore', index: item.id }); setConfirm(false); } else setConfirm(true); }}><History size={11}/>{confirm ? 'Click again to rewind' : 'Rewind to here'}</button>}
    </MessageContent>
  </Message>;
}

/**
 * The message box's light Markdown marks, drawn in a layer behind the transparent textarea text: code (inline and fenced),
 * quote lines, and bold. The symbols stay, and only color and background change, never width, so the caret stays aligned.
 */
function markdownMarks(text: string) {
  let fenced = false;
  return <>{text.split('\n').map((line, n) => {
    const fence = /^\s*(```|~~~)/.test(line);
    const block = fenced || fence;
    if (fence) fenced = !fenced;
    const parts: React.ReactNode[] = [];
    if (block) parts.push(<span key="c" className="md-code">{line}</span>);
    else {
      const pattern = /`[^`\n]+`|\*\*[^*\n]+\*\*|__[^_\n]+__/g;
      let last = 0, m: RegExpExecArray | null;
      while ((m = pattern.exec(line))) {
        parts.push(line.slice(last, m.index));
        parts.push(<span key={m.index} className={m[0][0] === '`' ? 'md-code' : 'md-strong'}>{m[0]}</span>);
        last = m.index + m[0].length;
      }
      parts.push(line.slice(last));
    }
    return <Fragment key={n}>{n > 0 && '\n'}{/^\s*>/.test(line) && !block ? <span className="md-quote">{parts}</span> : parts}</Fragment>;
  })}{' '}</>;
}

/** The user's own text is rendered as Markdown like a reply, but a single line break they typed stays a line break (outside code fences). */
function hardBreaks(text: string) {
  let fenced = false;
  return text.split('\n').map(line => { if (/^\s*(```|~~~)/.test(line)) fenced = !fenced; return fenced || !line.trim() || /^\s*(```|~~~)/.test(line) ? line : line + '  '; }).join('\n');
}

/**
 * Sending returns the view to following the bottom, even when the user had scrolled away, so the new message and its reply are in view.
 * (The scroller's pin-the-message-to-the-top anchors were tried: in a short panel they never handed back to following, so tool calls ran
 * below the fold, and answering a permission card re-pinned the message, which looked like a jump back.)
 */
function FollowOnSend({ sent }: { sent?: number }) {
  const { scrollToEnd } = useMessageScroller();
  useLayoutEffect(() => { if (sent !== undefined) scrollToEnd({ behavior: 'auto' }); }, [sent, scrollToEnd]);
  return null;
}

/** Splits reply text at Markdown images of local files, leaving code fences alone. */
function splitLocalImages(text: string): (string | { alt: string; src: string })[] {
  if (!text.includes('![')) return [text];
  const parts: (string | { alt: string; src: string })[] = [];
  let buffer = '', fenced = false;
  for (const line of text.split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) fenced = !fenced;
    // A path with spaces is written in angle brackets, as CommonMark allows: ![Viewport](</Users/me/Library/Application Support/x.jpg>).
    const pattern = /!\[([^\]]*)\]\((?:<([^>\n]+)>|([^)\s]+))\)/g;
    let last = 0, match: RegExpExecArray | null, rest = line;
    if (!fenced) while ((match = pattern.exec(line))) {
      const src = match[2] ?? (() => { try { return decodeURI(match![3]); } catch { return match![3]; } })();
      if (/^(https?:|data:|blob:)/i.test(src)) continue;
      buffer += line.slice(last, match.index);
      if (buffer.trim()) parts.push(buffer);
      buffer = '';
      parts.push({ alt: match[1], src });
      last = match.index + match[0].length;
      rest = line.slice(last);
    }
    buffer += (last ? rest : line) + '\n';
  }
  if (buffer.trim()) parts.push(buffer.replace(/\n$/, ''));
  return parts;
}

/** Plan mode only proposes; building needs the mode the user had before it. */
function leavePlan(send: Send) { if (planMode.current === 'plan') send({ action: 'agentMode', value: planMode.before }); }

/** Shown in a side chat: what it is about, the way back to the main conversation (the side chat stays in its tray), and close (discard). */
function SideBanner({ side, send }: { side: NonNullable<Agent['side']>; agent?: Agent; send: Send }) {
  return <div className="banner ag-side" role="note">
    <button className="icon-btn sm" aria-label="Back to main" title="Back to main" onClick={() => send({ action: 'agentSideBack' })}><ArrowLeft size={13}/></button>
    <span className="grow truncate" title={side.quote}>Side chat about “{side.quote}”</span>
    <button className="icon-btn sm" aria-label="Close side chat" title="Close side chat" onClick={() => send({ action: 'agentSideClose' })}><X size={12}/></button>
  </div>;
}

// answered: a later prompt followed this item, so a proposal's actions are spent.
function Entry({ item, send, streaming, busy = false, answered = false }: { item: AgentItem; send: Send; streaming: boolean; busy?: boolean; answered?: boolean }) {
  const [open, setOpen] = useState(false);
  switch (item.kind) {
    case 'assistant': {
      // Markdown images that name a local file (not http, data, or blob) become thumbnails through the host; the Markdown renderer blocks them.
      const parts = splitLocalImages(item.text);
      return <Message><MessageContent><Bubble variant="ghost"><BubbleContent className="ag-md">
        {parts.map((part, i) => typeof part !== 'string' ? <Picture key={i} path={part.src} name={part.alt || part.src.split(/[\\/]/).pop()}/>
          : <Streamdown key={i} mode="streaming" isAnimating={streaming && i === parts.length - 1} animated={false} controls={{ code: { copy: true, download: false }, table: { copy: true, download: false, fullscreen: false }, mermaid: false }} linkSafety={{ enabled: false }}
            components={{ a: ({ href, children }) => <a href={href} onClick={e => { e.preventDefault(); if (href) send({ action: 'agentOpenUrl', value: href }); }}>{children}</a> }}>{part}</Streamdown>)}
        {item.images.length > 0 && <span className="ag-msg-images ag-tool-images">{item.images.map(id => <Picture key={id} id={id}/>)}</span>}
      </BubbleContent></Bubble></MessageContent></Message>;
    }
    case 'notice': return <Marker variant="separator"><MarkerContent>{item.text}</MarkerContent></Marker>;
    case 'thought': return <div className="ag-thought"><button onClick={() => setOpen(!open)}><ChevronRight size={12} className={open ? 'rotate' : ''}/>Thinking</button>{open && <p>{item.text}</p>}</div>;
    // A proposed plan, as MonoCode shows it: nothing to approve or decline; build it, or reply to refine it.
    case 'proposal': return <div className="ag-card proposal"><ListChecks size={14}/><div className="grow">
      <strong>Proposed plan</strong>
      <ToolDetail kind="Plan" detail={item.text}/>
      {!answered && <div className="ag-perm-actions">
        <button className="btn-sm primary" disabled={busy} title="Leave Plan mode and start building" onClick={() => { leavePlan(send); send({ action: 'agentPrompt', value: 'Implement the plan.', context: false, mentions: [] }); }}>Implement</button>
        <button className="btn-sm" disabled={busy} title="Put the plan in the message box to edit, then send it" onClick={() => { leavePlan(send); window.dispatchEvent(new CustomEvent('rhdock:compose', { detail: `Implement this plan:\n\n${item.text}` })); }}>Edit</button>
        <small>Or reply to refine it.</small>
      </div>}
    </div></div>;
    default: return null;
  }
}

function Activity({ items, all }: { items: AgentItem[]; all: AgentItem[] }) {
  return <AgentActivity items={items} all={all}
    detail={item => <ToolDetail kind={item.toolKind} detail={item.detail!} files={item.files}/>}
    pictures={item => item.images.length ? <span className="ag-msg-images ag-tool-images">{item.images.map(id => <Picture key={id} id={id} name={item.title ?? undefined}/>)}</span> : null}/>;
}
/** The preview for one kind of tool call, shared by tool rows and permission cards: a diff, a command and its output, source, Markdown for plans and subagent results, or text. */
function ToolDetail({ kind, detail, files = [] }: { kind?: string | null; detail: string; files?: string[] }) {
  if (kind === 'Edit' || kind === 'Delete' || kind === 'Move') return <Detail text={detail}/>;
  if (kind === 'Execute') {
    const [command, ...output] = detail.split('\n');
    return <div className="ag-execution"><code>{command}</code><pre>{output.join('\n')}</pre></div>;
  }
  if (kind === 'Read' || kind === 'Search') return <div className="ag-source">
    {files.length > 0 && <small>{files.join(', ')}</small>}
    <pre>{detail}</pre>
  </div>;
  // Plans and subagent results are Markdown written by the agent.
  if (kind === 'Plan' || kind === 'Think') return <div className="ag-plan-detail"><Streamdown mode="static" controls={false}>{detail}</Streamdown></div>;
  return <pre className="ag-tool-text">{detail}</pre>;
}

// A question from the agent, as in Claude Code and Codex: pick an option, type an answer, or skip.
function Question({ question, send }: { question: AgentQuestion; send: Send }) {
  const [answers, setAnswers] = useState<Record<string, unknown>>(() => Object.fromEntries(question.fields.map(f => [f.key, f.type === 'multi' ? [] : undefined])));
  const [typed, setTyped] = useState<Record<string, string>>({});
  const value = (f: QuestionField) => typed[f.key]?.trim() ? (f.type === 'number' ? Number(typed[f.key]) : f.type === 'multi' ? [...(answers[f.key] as string[]), typed[f.key].trim()] : typed[f.key].trim()) : answers[f.key];
  const answered = (f: QuestionField) => { const v = value(f); return Array.isArray(v) ? v.length > 0 : v !== undefined && v !== '' && (typeof v !== 'number' || Number.isFinite(v)); };
  const ready = question.fields.every(f => !f.required || answered(f)) && question.fields.some(answered);
  const submit = () => { if (ready) send({ action: 'agentQuestion', answers: Object.fromEntries(question.fields.filter(answered).map(f => [f.key, value(f)])) }); };
  const set = (f: QuestionField, v: unknown) => { setAnswers({ ...answers, [f.key]: v }); setTyped({ ...typed, [f.key]: '' }); };
  const single = question.fields.length === 1 && question.fields[0].type === 'choice' && !question.fields[0].custom;
  return <div className="ag-card ask" onKeyDown={e => {
    if (composing(e) || !(e.target instanceof HTMLButtonElement)) return;
    const options = e.target.closest('.ag-q-options');
    if (!options) return;
    const buttons = [...options.querySelectorAll<HTMLButtonElement>('button')];
    const at = buttons.indexOf(e.target);
    const next = e.key === 'ArrowDown' || e.key === 'ArrowRight' ? (at + 1) % buttons.length : e.key === 'ArrowUp' || e.key === 'ArrowLeft' ? (at - 1 + buttons.length) % buttons.length : e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : -1;
    if (next >= 0) { e.preventDefault(); buttons[next]?.focus(); }
  }}><CircleHelp size={14}/><div className="grow">
    {question.fields.map(f => <div key={f.key} className="ag-q">
      <strong>{f.description || f.title || question.title || 'Question'}</strong>
      {f.description && f.title && <small className="ag-q-head">{f.title}</small>}
      {(f.type === 'choice' || f.type === 'multi') && <div className="ag-q-options">{f.options.map((o, i) => {
        const on = f.type === 'multi' ? (answers[f.key] as string[]).includes(o.value) : answers[f.key] === o.value && !typed[f.key]?.trim();
        return <button key={o.value} className={on ? 'on' : ''} aria-pressed={on} onClick={() => {
          if (f.type === 'multi') set(f, on ? (answers[f.key] as string[]).filter(v => v !== o.value) : [...(answers[f.key] as string[]), o.value]);
          else if (single) send({ action: 'agentQuestion', answers: { [f.key]: o.value } });
          else set(f, o.value);
        }}><span className="ag-q-key">{f.type === 'multi' ? (on ? <Check size={11}/> : '') : i + 1}</span><span className="grow"><span>{o.label}</span>{o.description && <small>{o.description}</small>}</span></button>;
      })}</div>}
      {f.type === 'boolean' && <div className="ag-q-options row">{[true, false].map(v => <button key={String(v)} className={answers[f.key] === v ? 'on' : ''} onClick={() => set(f, v)}>{v ? 'Yes' : 'No'}</button>)}</div>}
      {(f.type === 'text' || f.type === 'number' || f.custom) && <input className="ag-q-input" type={f.type === 'number' ? 'number' : 'text'} value={typed[f.key] ?? ''}
        placeholder={f.type === 'text' || f.type === 'number' ? 'Your answer' : 'Or type your own answer'} onChange={e => setTyped({ ...typed, [f.key]: e.target.value })}
        onKeyDown={e => { if (e.key === 'Enter' && !composing(e)) { e.preventDefault(); submit(); } }}/>}
    </div>)}
    <div className="ag-perm-actions">
      {!single && <button className="btn-sm primary" disabled={!ready} onClick={submit}>Answer</button>}
      <button className="btn-sm" onClick={() => send({ action: 'agentQuestion', answers: null })}>Skip</button>
    </div>
  </div></div>;
}

function Detail({ text }: { text: string }) {
  return <div className="ag-detail">{text.split('\n').map((line, i) => <div key={i} className={/^\+(?!\+\+)/.test(line) ? 'add' : /^-(?!--)/.test(line) ? 'del' : line.startsWith('@@') ? 'hunk' : ''}>{line || ' '}</div>)}</div>;
}

/**
 * A collapsible group of rows above the composer, used for file changes and side chats: a header row (a chevron, its label, and its actions)
 * and, when open, its rows. Rows are ComposerRows, as the queue and connection rows are.
 */
function ComposerTray({ label, after, actions, children }: { label: string; after?: React.ReactNode; actions?: React.ReactNode; children: React.ReactNode }) {
  const [open, setOpen] = useState(false);
  return <div className={`ag-tray ${open ? 'open' : ''}`}>
    <ComposerRow lead={<ChevronRight size={12} className={open ? 'rotate' : ''}/>} text={label} after={after} onClick={() => setOpen(!open)} expanded={open}>{actions}</ComposerRow>
    {open && <div className="ag-tray-list">{children}</div>}
  </div>;
}

/** Edits since the review baseline, above the composer like Claude Code and Zed: keep them all, or revert per file. */
function Changes({ changes, worktree, repository, send }: { changes: AgentChange[]; worktree?: Agent['worktree']; repository?: string | null; send: Send }) {
  const [file, setFile] = useState<string>();
  const [confirm, setConfirm] = useState<string>();
  const added = changes.reduce((n, c) => n + c.added, 0), removed = changes.reduce((n, c) => n + c.removed, 0);
  return <ComposerTray label={`${changes.length} file${changes.length === 1 ? '' : 's'} changed`} after={<><span className="truncate" title={worktree?.root ?? repository ?? undefined}>{(worktree?.root ?? repository)?.split(/[\\\\/]/).pop()}{worktree ? ` · ${worktree.branch}` : ""}</span><span className="add">+{added}</span><span className="del">−{removed}</span></>}
    actions={<button className="btn-sm" title="Accept these changes and start a new review" onClick={() => send({ action: 'agentKeep' })}>Keep all</button>}>
    {changes.map(c => <div key={c.path} className="ag-change">
      <ComposerRow lead={<FileCode2 size={12}/>} text={c.path} after={<>{c.status !== 'modified' && <span className="tag">{c.status}</span>}<span className="add">+{c.added}</span><span className="del">−{c.removed}</span></>}
        onClick={() => setFile(file === c.path ? undefined : c.path)} expanded={file === c.path}>
        <button className={`icon-btn sm ${confirm === c.path ? 'danger' : ''}`} title={confirm === c.path ? 'Click again to revert' : 'Revert this file'} onClick={() => { if (confirm === c.path) { send({ action: 'agentRevert', path: c.path }); setConfirm(undefined); } else setConfirm(c.path); }}><Undo2 size={12}/></button>
      </ComposerRow>
      {file === c.path && <Detail text={c.diff}/>}
    </div>)}
  </ComposerTray>;
}

/** Side chats opened from this conversation and still open: open one to continue it, or close it, which discards it. */
function SideChats({ chats, send }: { chats: NonNullable<Agent['sideChats']>; send: Send }) {
  return <ComposerTray label={`${chats.length} side chat${chats.length === 1 ? '' : 's'}`}>
    {chats.map(c => <ComposerRow key={c.viewId} lead={c.activity ? <SessionStatus activity={c.activity}/> : <MessageSquare size={12}/>} text={c.title || 'Side chat'}
      onClick={() => send({ action: 'agentSideOpen', value: c.viewId })}>
      <button className="icon-btn sm" aria-label={`Close side chat ${c.title || ''}`.trim()} title="Close side chat" onClick={() => send({ action: 'agentSideClose', value: c.viewId })}><X size={11}/></button>
    </ComposerRow>)}
  </ComposerTray>;
}

type Menu = { kind: '/' | '@'; query: string; start: number };

/** Drops entries that do not match what was typed after / or @ and keeps sections together, the best-matching section first and best matches first inside it. */
function rank(choices: Choice[], query: string) {
  if (!query) return choices;
  // cmdk's command-score fuzzy match on the name (camelCase split into words): letters in order, rewarding word starts, prefixes and
  // acronyms ("nc" finds "New conversation") and penalizing gaps. The description or path counts at half weight, so it finds an entry
  // whose name does not match but never outranks one whose name does; matches far below the best one are dropped as noise.
  const q = query.replace(/^[/@]/, '');
  const all = choices.map((c, i) => ({ c, i, s: Math.max(defaultFilter(c.label.replace(/^[/@]/, '').replace(/([a-z])([A-Z])/g, '$1 $2'), q), c.detail ? defaultFilter(c.detail, q) * 0.5 : 0) }));
  const top = Math.max(0, ...all.map(x => x.s));
  const scored = all.filter(x => x.s > 0 && x.s >= top * 0.3);
  const best = new Map<string, number>();
  for (const x of scored) best.set(x.c.group, Math.max(best.get(x.c.group) ?? 0, x.s));
  const groups = [...new Set(scored.map(x => x.c.group))];
  return scored.sort((a, b) => best.get(b.c.group)! - best.get(a.c.group)! || groups.indexOf(a.c.group) - groups.indexOf(b.c.group) || b.s - a.s || a.i - b.i).map(x => x.c);
}
type Choice = { key: string; label: string; detail?: string; group: string; icon: React.ReactNode; pick: () => void };
// Side chats whose quote card was added, by view, so reopening one does not add it twice.
const seededSides = new Set<string>();
// The mode the user was in before Plan mode, so Implement can leave it. Updated on every render of the Agent view.
const planMode = { current: 'ask', before: 'ask' };
const drafts = new Map<string, { text: string; context: boolean; mentions: Mention[]; notes: Note[]; history: string[] }>();

function Composer({ snap, agent, send, annotator }: { snap: Snapshot; agent?: Agent; send: Send; annotator: React.RefObject<Annotator | undefined> }) {
  const key = agent?.viewId ?? agent?.sessionId ?? 'new';
  const draft = drafts.get(key);
  const [text, setText] = useState(draft?.text ?? '');
  const [expanded, setExpanded] = useState(false);
  const modes = modesFor(agent);
  // The active Script is offered as a chip, not attached, until the user adds it.
  const [context, setContext] = useState(draft?.context ?? false);
  const [mentions, setMentions] = useState<Mention[]>(draft?.mentions ?? []);
  const [notes, setNotes] = useState<Note[]>(draft?.notes ?? []);
  const [menu, setMenu] = useState<Menu>();
  const [highlight, setHighlight] = useState(0);
  const [history, setHistory] = useState<string[]>(draft?.history ?? []);
  const box = useRef<HTMLTextAreaElement>(null);
  const mirror = useRef<HTMLDivElement>(null);
  const script = snap.scripts.find(s => s.path === snap.selected);
  const hostConnected = agent?.hostTools?.connected === true;
  const busy = isBusy(agent);
  const usable = !!agent && agent.status !== 'missing';
  useEffect(() => {
    drafts.set(key, { text, context, mentions, notes, history });
    if (drafts.size > 50) drafts.delete(drafts.keys().next().value!);
  }, [key, text, context, mentions, notes, history]);

  useEffect(() => { send({ action: 'agentFiles', value: menu?.kind === '@' }); }, [menu?.kind]);
  // After a rewind the removed prompt comes back for editing, as in Claude Code; Undo takes it back unless it was edited.
  const restored = useRef<string | undefined>(undefined);
  useEffect(() => {
    const back = agent?.rewound?.text;
    if (back) setText(t => { if (t) return t; restored.current = back; return back; });
    else if (restored.current !== undefined) { const back = restored.current; restored.current = undefined; setText(t => t === back ? '' : t); }
  }, [agent?.rewound?.id]);

  // While the agent works, Enter adds the message to the running turn after its current tool call, as in Claude Code
  // (pending until then, so it can still be removed); Ctrl/Cmd+Enter queues it for after the turn.
  const submit = (later = false, pending = notes) => {
    const value = withNotes(pending, text);
    if (!value || !usable) return;
    if (value.trim() === '/rhdock-connect') { send({ action: 'agentHostConnect' }); setText(''); setMenu(undefined); return; }
    // The draft travels with the prompt so a queued message can be edited back into the composer as it was.
    const draft = JSON.stringify({ text, notes: pending, mentions, context });
    send({ action: 'agentPrompt', value, context: context && hostConnected && !!script, mentions: mentions.filter(m => m.kind !== 'selection' || hostConnected), steer: !later && agent?.features?.steer !== false && agent?.status === 'working', draft });
    setHistory(h => [...h, text.trim()].filter(Boolean).slice(-20));
    setText(''); setMentions([]); setNotes([]); setMenu(undefined); setExpanded(false);
  };
  /** Puts a queued message back in the composer to edit it: its text, comment cards, mentions and context switch; the host returns its attachments. */
  const editQueued = (q: { id: number; text: string; draft?: string | null }) => {
    let d: { text?: string; notes?: Note[]; mentions?: Mention[]; context?: boolean } = { text: q.text };
    try { if (q.draft) d = JSON.parse(q.draft); } catch { /* an unreadable draft edits the plain text */ }
    send({ action: 'agentEditQueued', index: q.id });
    setText(d.text ?? q.text); setNotes(d.notes ?? []); setMentions(d.mentions ?? []); if (d.context !== undefined) setContext(d.context);
    requestAnimationFrame(() => box.current?.focus());
  };
  // A comment on selected conversation text waits as a card above the prompt; Ctrl/Cmd+Enter in the comment sends the prompt with it.
  annotator.current = {
    add: (quote, note, sendNow, anchor) => {
      const added = { id: Date.now() + notes.length, quote, note, anchor };
      if (sendNow) submit(false, [...notes, added]);
      else { setNotes(old => [...old, { ...added, id: Date.now() + old.length }]); requestAnimationFrame(() => box.current?.focus()); }
    },
    update: (id, note) => setNotes(notes.map(n => n.id === id ? { ...n, note } : n)),
  };
  // The transcript highlights waiting comments at their passages.
  useEffect(() => { window.dispatchEvent(new CustomEvent('rhdock:notes', { detail: notes })); return () => { window.dispatchEvent(new CustomEvent('rhdock:notes', { detail: [] })); }; }, [notes]);
  const replaceToken = (value: string) => {
    if (!menu) return;
    const end = box.current?.selectionStart ?? text.length;
    setText(text.slice(0, menu.start) + value + text.slice(end));
    setMenu(undefined);
    requestAnimationFrame(() => box.current?.focus());
  };
  const mention = (m: Mention) => { if (!mentions.some(x => x.kind === m.kind && x.path === m.path)) setMentions([...mentions, m]); replaceToken(''); };

  // The plan card's Edit puts text here; a long text opens the larger box.
  useEffect(() => {
    const compose = (e: Event) => { setText((e as CustomEvent<string>).detail); setExpanded(true); requestAnimationFrame(() => box.current?.focus()); };
    window.addEventListener('rhdock:compose', compose);
    return () => window.removeEventListener('rhdock:compose', compose);
  }, []);

  const q = menu?.query.toLowerCase() ?? '';
  // One role per trigger: "/" lists things to run in two sections, rhDock's own actions and then the agent's commands (its built-in
  // commands, skills, and plugin commands together: most harnesses do not say which is which); "@" lists context to attach.
  const choices: Choice[] = !menu ? [] : rank(menu.kind === '/'
    ? [{ key: 'rhdock-connect', group: 'rhDock', label: 'rhDockConnect', detail: 'Let this agent use rhDock and Rhino tools', icon: <Terminal size={13}/>, pick: () => { replaceToken(''); send({ action: 'agentHostConnect' }); } },
       { key: 'new', group: 'rhDock', label: 'New conversation', detail: '', icon: <Plus size={13}/>, pick: () => { replaceToken(''); send({ action: 'agentNew' }); } },
       ...(agent?.features?.permissions === false ? [] : modes).map(m => ({ key: 'm' + m.value, group: 'rhDock', label: `Mode: ${m.label}`, detail: m.detail, icon: <ShieldAlert size={13}/>, pick: () => { replaceToken(''); send({ action: 'agentMode', value: m.value }); } })),
       ...(agent?.commands ?? []).map(c => ({ key: 'c' + c.name, group: 'Commands', label: '/' + c.name, detail: c.description, icon: <Slash size={13}/>, pick: () => replaceToken(`/${c.name} `) })),
       // OpenCode compacts through its own route, not a command.
       ...(agent?.features?.compact === false || agent?.commands.some(c => c.name === 'compact') ? [] : [{ key: 'compact', group: 'Commands', label: '/compact', detail: 'Summarize the conversation to free context', icon: <Minimize2 size={13}/>, pick: () => replaceToken('/compact') }]),
       ...(agent?.skills ?? []).filter(k => !agent?.commands.some(c => c.name === k.id)).map(k => ({ key: 'k' + k.id, group: 'Commands', label: '/' + k.id, detail: k.description || k.name, icon: <Sparkles size={13}/>, pick: () => replaceToken(`/${k.id} `) }))]
    : [...(hostConnected ? [{ key: 'sel', group: 'Context', label: 'RhinoSelection', detail: '', icon: <MousePointerClick size={13}/>, pick: () => mention({ kind: 'selection', label: 'Rhino selection' }) }] : []),
       ...(hostConnected && script ? [{ key: 'active', group: 'Context', label: 'ActiveScript', detail: script.name, icon: <FileCode2 size={13}/>, pick: () => { setContext(true); replaceToken(''); } }] : []),
       ...snap.scripts.map(s => ({ key: 's' + s.path, group: 'Scripts', label: s.name, detail: s.path, icon: <FileCode2 size={13}/>, pick: () => mention({ kind: 'script', path: s.path, label: s.name }) })),
       ...(agent?.files ?? []).filter(f => !snap.scripts.some(s => s.path === f)).map(f => ({ key: 'f' + f, group: 'Files', label: f.split('/').pop()!, detail: f, icon: <FileText size={13}/>, pick: () => mention({ kind: 'file', path: f, label: f.split('/').pop()! }) }))], q).slice(0, 50);

  const onChange = (value: string, caret: number) => {
    setText(value);
    const before = value.slice(0, caret);
    const slash = /^\/(\S*)$/.exec(before);
    const at = /(?:^|\s)@(\S*)$/.exec(before);
    if (slash) setMenu({ kind: '/', query: slash[1], start: 0 });
    else if (at) setMenu({ kind: '@', query: at[1], start: caret - at[1].length - 1 });
    else setMenu(undefined);
    setHighlight(0);
  };
  const onKey = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (composing(e)) return;
    if (menu && choices.length) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setHighlight(Math.min(highlight + 1, choices.length - 1)); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight(Math.max(highlight - 1, 0)); return; }
      if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); choices[highlight]?.pick(); return; }
    }
    if (e.key === 'Escape') { if (menu) setMenu(undefined); else if (busy) send({ action: 'agentCancel' }); return; }
    if (e.key === 'Tab' && e.shiftKey && agent?.features?.permissions !== false) { e.preventDefault(); const i = modes.findIndex(m => m.value === agent?.mode); send({ action: 'agentMode', value: modes[(i + 1) % modes.length].value }); return; }
    if (e.key === 'ArrowUp' && !text && history.length) { e.preventDefault(); setText(history.at(-1)!); return; }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(e.ctrlKey || e.metaKey); }
  };
  const attachImages = (files: FileList | File[]) => {
    for (const file of Array.from(files)) {
      if (!file.type.startsWith('image/')) continue;
      const reader = new FileReader();
      reader.onload = () => {
        const url = String(reader.result);
        const image = new Image();
        image.onload = () => {
          const canvas = document.createElement('canvas');
          canvas.width = 160; canvas.height = Math.max(1, Math.round(160 * image.height / image.width));
          canvas.getContext('2d')?.drawImage(image, 0, 0, canvas.width, canvas.height);
          send({ action: 'agentAttach', name: file.name || 'Pasted image', mimeType: file.type, value: url.slice(url.indexOf(',') + 1), thumb: canvas.toDataURL('image/jpeg', .7) });
        };
        image.src = url;
      };
      reader.readAsDataURL(file);
    }
  };

  const model = agent?.config.find(o => o.category === 'model' || o.id === 'model');
  const effort = agent?.config.find(o => o.category === 'thought_level' || o.id === 'effort');
  const mode = modes.find(m => m.value === agent?.mode) ?? modes[0];
  return <footer className="ag-foot">
    {agent?.rewound && <div className="ag-queue"><ComposerRow lead={<History size={12}/>} text={`Rewound to before “${agent.rewound.text || 'this prompt'}”; ${agent.rewound.filesRestored ? 'files are restored' : 'files were not restored'}.`}>
      <button className="btn-sm" disabled={busy} onClick={() => send({ action: 'agentUndoRewind' })}>Undo</button></ComposerRow></div>}
    {agent && agent.queued.length > 0 && <div className="ag-queue">{agent.queued.map(q => <ComposerRow key={q.id} lead={<span className="tag" title={q.steer ? 'Joins the running turn when its current tool call finishes' : 'Runs after this turn'}>{q.steer ? 'Pending' : 'Queued'}</span>} text={q.text}>{!q.steer && agent.features?.steer !== false && agent.status === 'working' && !q.text.startsWith('/') && <button className="btn-sm" title="Add it to the running turn after its current tool call (Enter when typing)" onClick={() => send({ action: 'agentSteer', index: q.id })}>Send now</button>}<button className="icon-btn sm" aria-label="Edit" title="Edit" onClick={() => editQueued(q)}><Pencil size={11}/></button><button className="icon-btn sm" aria-label="Remove" onClick={() => send({ action: 'agentUnqueue', index: q.id })}><X size={11}/></button></ComposerRow>)}</div>}
    {hostConnected && <ComposerRow lead={<Terminal size={12}/>} text="rhDock · Connected"><button className="icon-btn sm" aria-label="Disconnect rhDock" onClick={() => send({ action: 'agentHostConnect' })}><X size={11}/></button></ComposerRow>}
    {agent?.hostTools?.error && <ComposerRow lead={<TriangleAlert size={12}/>} text={agent.hostTools.error}/>}
    <div className={`ag-box ${expanded ? 'big' : ''}`} onDragOver={e => e.preventDefault()} onDrop={e => { e.preventDefault(); attachImages(e.dataTransfer.files); }}>
      {menu && choices.length > 0 && <div className="ag-menu" role="listbox">{choices.map((c, i) => <Fragment key={c.key}>
        {c.group !== choices[i - 1]?.group && <div className="ag-menu-group" role="presentation">{c.group}</div>}
        <button role="option" aria-selected={i === highlight} className={i === highlight ? 'hi' : ''} title={c.detail ? `${c.label}\n${c.detail}` : undefined} onMouseEnter={() => setHighlight(i)} onMouseDown={e => { e.preventDefault(); c.pick(); }}>
          {c.icon}<span className="truncate">{c.label}</span>{c.detail && <small className="truncate">{c.detail}</small>}</button></Fragment>)}</div>}
      {notes.length > 0 && <div className="ag-notes">{notes.map(n => <NoteCard key={n.id} note={n} remove={() => setNotes(notes.filter(x => x !== n))}/>)}</div>}
      {(script || mentions.length > 0 || (agent?.attachments.length ?? 0) > 0) && <div className="ag-chips">
        {script && hostConnected && (context
          ? <span className="chip" title="The active Script's file, inputs, and last result are attached">@ {script.name}<button aria-label="Remove Script context" onClick={() => setContext(false)}><X size={10}/></button></span>
          : <button className="chip off" onClick={() => setContext(true)}>+ {script.name}</button>)}
        {mentions.map(m => <span key={m.kind + m.path} className="chip">@ {m.label}<button aria-label={`Remove ${m.label}`} onClick={() => setMentions(mentions.filter(x => x !== m))}><X size={10}/></button></span>)}
        {agent && agent.attachments.length > 0 && <AttachmentGroup>{agent.attachments.map(a => <Attachment key={a.id} size="xs">
          <AttachmentMedia variant={a.kind === 'image' ? 'image' : 'icon'}>{a.kind === 'image' ? <Picture id={a.id} thumb={a.thumb} name={a.name}/> : <FileText/>}</AttachmentMedia>
          <AttachmentContent><AttachmentTitle>{a.name}</AttachmentTitle></AttachmentContent>
          <AttachmentActions><AttachmentAction aria-label={`Remove ${a.name}`} onClick={() => send({ action: 'agentDetach', index: a.id })}><X/></AttachmentAction></AttachmentActions>
        </Attachment>)}</AttachmentGroup>}
      </div>}
      <label htmlFor="ag-prompt" className="sr">Message the agent</label>
      <div className="ag-edit">
      <div className="ag-mirror" aria-hidden="true" ref={mirror}>{markdownMarks(text)}</div>
      <textarea id="ag-prompt" ref={box} rows={2} value={text} disabled={!usable} onScroll={e => { if (mirror.current) mirror.current.scrollTop = e.currentTarget.scrollTop; }}
        placeholder={busy ? (agent?.features?.steer === false ? 'Enter queues for after this turn' : 'Enter adds to this turn; Ctrl+Enter queues for after it') : 'Ask the agent. @ for context, / for commands'}
        onChange={e => onChange(e.target.value, e.target.selectionStart)} onKeyDown={onKey} onBlur={() => setMenu(undefined)}
        onPaste={e => { if ([...e.clipboardData.files].some(f => f.type.startsWith('image/'))) { e.preventDefault(); attachImages(e.clipboardData.files); } }}/>
      </div>
      <div className="ag-bar">
        <DropdownMenu>
          <DropdownMenuTrigger asChild><button className="ag-pick icon" aria-label="Attach" title="Attach" disabled={!usable}><Paperclip size={13}/></button></DropdownMenuTrigger>
          <DropdownMenuContent align="start" side="top" className="ag-dd">
            <DropdownMenuGroup><DropdownMenuItem disabled={!hostConnected} onSelect={() => send({ action: 'agentCapture' })}><Camera/>Capture viewport</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => send({ action: 'agentAttachFile' })}><Paperclip/>Choose file…</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => { setText(t => t + (t && !t.endsWith(' ') ? ' @' : '@')); setMenu({ kind: '@', query: '', start: text.length + (text && !text.endsWith(' ') ? 1 : 0) }); box.current?.focus(); }}><AtSign/>Mention…</DropdownMenuItem></DropdownMenuGroup>
          </DropdownMenuContent>
        </DropdownMenu>
        {agent?.features?.permissions !== false ? <DropdownMenu>
          <DropdownMenuTrigger asChild><button className={`ag-pick mode-${mode.value}`} title={`${mode.detail} (Shift+Tab)`} disabled={!usable}><ShieldAlert size={12}/><span className="truncate">{mode.label}</span><ChevronDown size={11}/></button></DropdownMenuTrigger>
          <DropdownMenuContent align="start" side="top" className="ag-dd">
            <DropdownMenuLabel>Permission mode · Shift+Tab</DropdownMenuLabel>
            <DropdownMenuGroup>{modes.map(m => <DropdownMenuItem key={m.value} onSelect={() => send({ action: 'agentMode', value: m.value })} className="ag-dd-mode">
              <span className="grow"><span className="ag-dd-title">{m.label}</span><small>{m.detail}</small></span>{m.value === agent?.mode && <Check/>}
            </DropdownMenuItem>)}</DropdownMenuGroup>
          </DropdownMenuContent>
        </DropdownMenu> : <span className="ag-pick mode-all"><ShieldAlert size={12}/><span className="truncate">Allow all</span></span>}
        {model && <OptionPicker option={model} send={send} favorites={agent?.favorites} disabled={busy} wide/>}
        {effort && <OptionPicker option={effort} send={send} disabled={busy} icon={<Brain size={12}/>}/>}
        <span className="grow"/>
        <button className="ag-pick icon" aria-label={expanded ? 'Shrink the message box' : 'Enlarge the message box'} title={expanded ? 'Shrink the message box' : 'Enlarge the message box for a long message'} aria-pressed={expanded} onClick={() => { setExpanded(!expanded); box.current?.focus(); }}>{expanded ? <Minimize2 size={13}/> : <Maximize2 size={13}/>}</button>
        {agent?.usage?.used != null && agent.usage.size ? <ContextMeter used={agent.usage.used} size={agent.usage.size} cost={agent.usage.cost} turn={agent.turn} model={model}/> : null}
        {busy && !(text.trim() || notes.length) ? <button className="ag-send stop" aria-label="Stop" title="Stop (Esc)" onClick={() => send({ action: 'agentCancel' })} disabled={agent?.status !== 'working'}><Square size={10} fill="currentColor"/></button>
        : <button className="ag-send" aria-label={busy ? (agent?.features?.steer === false ? 'Queue' : 'Add to this turn') : 'Send'} title={busy ? (agent?.features?.steer === false ? 'Queue (Enter)' : 'Add to this turn (Enter); Ctrl+Enter queues for after it') : 'Send (Enter)'} disabled={!(text.trim() || notes.length) || !usable} onClick={() => submit()}><ArrowUp size={14}/></button>}
      </div>
    </div>
  </footer>;
}

// One line above the composer: a lead icon or tag, text that truncates (full text in its tooltip), and actions that always stay visible.
/** One row above the composer: a lead icon or tag, its text (with optional stats after it), and actions. With onClick, the lead and text are one button. */
function ComposerRow({ lead, text, after, onClick, expanded, children }: { lead: React.ReactNode; text: string; after?: React.ReactNode; onClick?: () => void; expanded?: boolean; children?: React.ReactNode }) {
  const body = <>{lead}<span className="truncate grow" title={text}>{text}</span>{after}</>;
  return <div className="ag-row">{onClick ? <button className="ag-row-main grow" aria-expanded={expanded} onClick={onClick}>{body}</button> : body}{children}</div>;
}

function OptionPicker({ option, send, disabled, wide, icon, favorites = [] }: { option: AgentOption; send: Send; disabled?: boolean; wide?: boolean; icon?: React.ReactNode; favorites?: string[] }) {
  const [open, setOpen] = useState(false);
  const current = option.options.find(o => o.value === option.current);
  const label = current?.name ?? option.current ?? option.name ?? option.id;
  const groups = new Map<string, AgentOption['options']>();
  for (const choice of option.options) {
    const key = wide ? favorites.includes(choice.value) ? 'Favorites' : choice.recent ? 'Recent' : choice.group ?? 'Models' : option.name ?? option.id;
    groups.set(key, [...(groups.get(key) ?? []), choice]);
  }
  return <Popover open={open} onOpenChange={setOpen}>
    <PopoverTrigger asChild><button className={`ag-pick ${wide ? 'wide' : ''}`} disabled={disabled} title={`${option.name ?? option.id}: ${label}`}>
      {icon}<span className="truncate">{wide ? label.split('/').pop() : label[0].toUpperCase() + label.slice(1)}</span><ChevronDown size={11}/>
    </button></PopoverTrigger>
    <PopoverContent side="top" align="start" className="ag-pop">
      <Command>
        {option.options.length > 8 && <CommandInput placeholder={`Search ${option.options.length} ${(option.name ?? 'option').toLowerCase()}s`}/>}
        <CommandList>
          <CommandEmpty>No matches</CommandEmpty>
          {[...groups].sort(([a], [b]) => (a === 'Favorites' ? -2 : a === 'Recent' ? -1 : 0) - (b === 'Favorites' ? -2 : b === 'Recent' ? -1 : 0)).map(([heading, choices]) => <CommandGroup key={heading} heading={heading}>{choices.map(o => <CommandItem key={o.value} value={`${o.name ?? ''} ${o.value}`} onSelect={() => { send({ action: 'agentOption', name: option.id, value: o.value }); setOpen(false); }}>
            <span className="grow truncate">{o.name ?? o.value}</span>{o.value === option.current && <Check/>}
            {wide && <button className="ag-star" aria-label={`${favorites.includes(o.value) ? 'Remove' : 'Add'} ${o.name ?? o.value} ${favorites.includes(o.value) ? 'from' : 'to'} favorites`} aria-pressed={favorites.includes(o.value)} onClick={e => { e.stopPropagation(); send({ action: 'agentFavorite', value: o.value }); }} onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); e.stopPropagation(); send({ action: 'agentFavorite', value: o.value }); } }}><Star fill={favorites.includes(o.value) ? 'currentColor' : 'none'}/></button>}
          </CommandItem>)}</CommandGroup>)}
        </CommandList>
      </Command>
    </PopoverContent>
  </Popover>;
}

// Token counts as Claude Code shows them: 950, 15.2k, 1M.
export function tokens(n: number) {
  const f = (v: number, unit: string) => `${v >= 100 ? Math.round(v) : Number(v.toFixed(1))}${unit}`;
  return n >= 1e6 ? f(n / 1e6, 'M') : n >= 1e3 ? f(n / 1e3, 'k') : String(Math.round(n));
}

function ContextMeter({ used, size, cost, turn, model }: { used: number; size: number; cost?: number | null; turn?: Turn | null; model?: AgentOption }) {
  const share = Math.min(1, used / size), r = 6, length = 2 * Math.PI * r, percent = Math.round(share * 100);
  const rows: [string, number | null | undefined][] = [['Input', turn?.input], ['Output', turn?.output], ['Reasoning', turn?.reasoning], ['Cache read', turn?.cacheRead], ['Cache write', turn?.cacheWrite]];
  const modelName = model?.options.find(o => o.value === model.current)?.name ?? model?.current;
  return <Popover>
    <PopoverTrigger asChild><button className={`ag-meter ${share > .8 ? 'high' : ''}`} aria-label={`Context ${percent}% used`} title={`${tokens(used)} / ${tokens(size)} tokens`}>
      <svg width="16" height="16" viewBox="0 0 16 16"><circle cx="8" cy="8" r={r} className="track"/><circle cx="8" cy="8" r={r} className="fill" strokeDasharray={`${share * length} ${length}`} transform="rotate(-90 8 8)"/></svg>
      <span>{percent}%</span>
    </button></PopoverTrigger>
    <PopoverContent side="top" align="end" className="ag-meter-card">
      <div className="ag-meter-head"><strong>Context window</strong><span>{tokens(used)} / {tokens(size)} ({percent}%)</span></div>
      <div className="ag-meter-bar"><span style={{ width: `${share * 100}%` }}/></div>
      {cost != null && <div><span>Cost so far</span><span>${cost < 0.01 ? cost.toFixed(4) : cost.toFixed(2)}</span></div>}
      {modelName && <div><span>Model</span><span className="truncate" title={modelName}>{modelName}</span></div>}
      {turn && rows.some(([, v]) => v) && <details>
        <summary>Last turn</summary>
        {rows.filter(([, v]) => v).map(([label, v]) => <div key={label}><span>{label}</span><span>{tokens(v!)}</span></div>)}
      </details>}
      <small>The agent reports these totals; it compacts the conversation when it runs out of room.</small>
    </PopoverContent>
  </Popover>;
}

// A thumbnail that opens the full image. The host sends the image the first time it is shown.
function Picture({ id, path, thumb, name }: { id?: number; path?: string; thumb?: string | null; name?: string }) {
  const [url, setUrl] = useState<string | undefined>(thumb ?? undefined);
  const [full, setFull] = useState<string>();
  const [open, setOpen] = useState(false);
  const [gone, setGone] = useState(false);
  // The conversation shows the host's small thumbnail; the full image is loaded only when the preview opens.
  useEffect(() => {
    let live = true;
    if (!thumb) void (path !== undefined ? imagePath(path, true) : image(id!, true)).then(u => { if (!live) return; if (u) setUrl(u); else setGone(true); });
    return () => { live = false; };
  }, [id, path, thumb]);
  useEffect(() => {
    if (!open || full) return;
    let live = true;
    void (path !== undefined ? imagePath(path) : image(id!)).then(u => { if (live && u) setFull(u); });
    return () => { live = false; };
  }, [open, full, id, path]);
  if (gone) return <span className="muted" title={path}>{name ?? 'Image'} (not available)</span>;
  return <>
    <button className="ag-thumb" aria-label={`Preview ${name ?? 'image'}`} onClick={() => setOpen(true)}>{url ? <img src={url} alt=""/> : <ImageIcon size={14}/>}</button>
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogContent className="ag-preview"><DialogTitle className="sr">{name ?? 'Image'}</DialogTitle>{full ? <ZoomImage src={full} alt={name ?? ''}/> : <Loader2 className="spin"/>}
        {/* In Rhino the host asks where to save; a browser (the Mirror or the preview) downloads the image itself. */}
        {full && (window.eto
          ? <button className="ag-save" aria-label="Save image" title="Save image" onClick={() => sendHost({ action: 'saveImage', values: { ...(path !== undefined ? { path } : { id: String(id) }), name: name ?? '' } })}><Download size={12}/></button>
          : <a className="ag-save" aria-label="Save image" title="Save image" href={full} download={name || 'image'}><Download size={12}/></a>)}
      </DialogContent>
    </Dialog>
  </>;
}

/**
 * The full-size image in the preview: the wheel or a trackpad pinch zooms around the pointer (1× to 8×), dragging pans when zoomed,
 * and a double-click switches between fit and 2×. Every image preview (the user's, the agent's, and tool images) uses it.
 */
function ZoomImage({ src, alt }: { src: string; alt: string }) {
  // The view lives in a ref and reaches the image once per frame, without a React render per wheel step: the Rhino panel's WebKit
  // left trailing frames when every event re-rendered the image. Only the zoom label is React state.
  const view = useRef({ scale: 1, x: 0, y: 0 });
  const [label, setLabel] = useState(1);
  const drag = useRef<{ x: number; y: number } | undefined>(undefined);
  const box = useRef<HTMLDivElement>(null), img = useRef<HTMLImageElement>(null), frame = useRef(0);
  const apply = (next: { scale: number; x: number; y: number }) => {
    view.current = next;
    cancelAnimationFrame(frame.current);
    frame.current = requestAnimationFrame(() => {
      const v = view.current;
      if (img.current) img.current.style.transform = `translate3d(${v.x}px, ${v.y}px, 0) scale(${v.scale})`;
      box.current?.classList.toggle('zoomed', v.scale > 1);
      setLabel(v.scale);
    });
  };
  const zoomTo = (scale: number, cx: number, cy: number) => {
    const v = view.current, next = Math.min(8, Math.max(1, scale));
    if (next === 1) { apply({ scale: 1, x: 0, y: 0 }); return; }
    const r = box.current!.getBoundingClientRect(), px = cx - r.left - r.width / 2, py = cy - r.top - r.height / 2;
    // Keep the point under the pointer still while scaling.
    apply({ scale: next, x: px - (px - v.x) * next / v.scale, y: py - (py - v.y) * next / v.scale });
  };
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    // Not passive, so the page itself does not scroll or zoom.
    const wheel = (e: WheelEvent) => { e.preventDefault(); zoomTo(view.current.scale * Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.002)), e.clientX, e.clientY); };
    el.addEventListener('wheel', wheel, { passive: false });
    return () => { el.removeEventListener('wheel', wheel); cancelAnimationFrame(frame.current); };
  }, []);
  return <div ref={box} className="ag-zoom"
    onDoubleClick={e => zoomTo(view.current.scale > 1 ? 1 : 2, e.clientX, e.clientY)}
    onPointerDown={e => { const v = view.current; if (v.scale > 1) { drag.current = { x: e.clientX - v.x, y: e.clientY - v.y }; e.currentTarget.setPointerCapture?.(e.pointerId); } }}
    onPointerMove={e => { const from = drag.current; if (from) apply({ ...view.current, x: e.clientX - from.x, y: e.clientY - from.y }); }}
    onPointerUp={() => { drag.current = undefined; }}>
    <img ref={img} src={src} alt={alt} draggable={false}/>
    {label > 1 && <span className="ag-zoom-level">{Math.round(label * 100)}%</span>}
  </div>;
}

function Setup({ agent, send }: { agent: Agent; send: Send }) {
  return <div className="empty">
    <TriangleAlert size={26}/><h2>{agentName(agent)} was not found</h2>
    <p>{agent.error}</p>
    <code className="ag-code">{agent.agents?.find(a => a.id === agent.agentId)?.install}</code>
    <button className="link-btn" onClick={() => send({ action: 'agentOpenUrl', value: agent.agents?.find(a => a.id === agent.agentId)?.docs })}>Installation guide</button>
    <div className="form-actions"><button className="btn primary" onClick={() => send({ action: 'agentConnect' })}>Check again</button><button className="btn secondary" onClick={() => send({ action: 'agentBrowse' })}>Choose executable…</button></div>
    {agent.userPath && <p className="muted">Using {agent.userPath}. <button className="link-btn" onClick={() => send({ action: 'agentPath', value: null })}>Search PATH instead</button></p>}
  </div>;
}

function Conversations({ agent, send, close }: { agent: Agent; send: Send; close: () => void }) {
  const [query, setQuery] = useState('');
  const list = agent.conversations.filter(c => (c.title ?? '').toLowerCase().includes(query.toLowerCase()));
  return <><div className="scrim" onClick={close}/><div className="pal">
    <div className="pal-search"><Search size={15}/><input autoFocus value={query} onChange={e => setQuery(e.target.value)} onKeyDown={e => { if (e.key === 'Escape') close(); }} placeholder="Search conversations"/><kbd>Esc</kbd></div>
    <div className="pal-list ag-conv">
      <button className="ag-new-row" onClick={() => { send({ action: 'agentNew' }); close(); }}><Plus size={14}/><span>New conversation</span></button>
      <div className="pal-group">This repository</div>
      {list.map(c => <button key={c.id} disabled={!c.live && agent.canLoad === false} className={c.open || c.id === agent.sessionId ? 'hi' : ''} onClick={() => { if (!c.open && c.id !== agent.sessionId) send({ action: 'agentLoad', value: c.id }); close(); }}>
        <SessionStatus activity={c.activity}/>{!c.activity && <MessageSquare size={14}/>}<span className="ag-conv-label"><strong className="truncate" title={c.title || 'Untitled'}>{c.title || 'Untitled'}</strong>{c.updatedAt && <small>{new Date(c.updatedAt).toLocaleString()}</small>}</span>{(c.open || c.id === agent.sessionId) && <span className="current">Open</span>}</button>)}
      {!list.length && <div className="pal-empty">No conversations yet</div>}
    </div>
    <div className="pal-foot">Stored by {agentName(agent)}, not in the Workspace</div>
  </div></>;
}
