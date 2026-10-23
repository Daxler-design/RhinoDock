import { useEffect, useRef, useState } from 'react';
import { CornerDownLeft, MessageSquare, MessageSquareQuote, X } from 'lucide-react';

/** Where a comment's passage is: the message it is in and character offsets inside that message's text. */
export type Anchor = { msg: string; start: number; end: number };
/** A comment waiting in the composer: the selected text, the user's note, and where it came from. */
export type Note = { id: number; quote: string; note: string; anchor?: Anchor };
/** What the composer offers the transcript: add a comment (optionally sending it at once) and change a waiting comment's note. */
export type Annotator = { add: (quote: string, note: string, sendNow: boolean, anchor?: Anchor) => void; update: (id: number, note: string) => void };

const messageOf = (node: Node | null) => (node instanceof Element ? node : node?.parentElement)?.closest<HTMLElement>('[data-msg]') ?? null;
const textNodes = (root: Node) => { const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT); const found: Text[] = []; while (walker.nextNode()) found.push(walker.currentNode as Text); return found; };
const offsetIn = (root: HTMLElement, node: Node, offset: number) => { const r = document.createRange(); r.selectNodeContents(root); r.setEnd(node, offset); return r.toString().length; };

/** The anchor of a selection inside one message, so a comment can find its passage again (partial words included). */
function anchorOf(range: Range): Anchor | undefined {
  const root = messageOf(range.commonAncestorContainer);
  if (!root?.dataset.msg) return undefined;
  return { msg: root.dataset.msg, start: offsetIn(root, range.startContainer, range.startOffset), end: offsetIn(root, range.endContainer, range.endOffset) };
}

/** The passage an anchor names, or nothing when the message is gone or its text is shorter now. */
function rangeOf(anchor: Anchor): Range | undefined {
  const root = document.querySelector<HTMLElement>(`[data-msg="${CSS.escape(anchor.msg)}"]`);
  if (!root) return undefined;
  const range = document.createRange();
  let seen = 0, started = false;
  for (const node of textNodes(root)) {
    const length = node.data.length;
    if (!started && anchor.start <= seen + length) { range.setStart(node, anchor.start - seen); started = true; }
    if (started && anchor.end <= seen + length) { range.setEnd(node, anchor.end - seen); return range; }
    seen += length;
  }
  return undefined;
}

/** Scrolls to a comment's passage in the transcript. */
export function revealNote(note: Note) {
  const range = note.anchor && rangeOf(note.anchor);
  range?.startContainer.parentElement?.scrollIntoView({ block: 'center', behavior: 'smooth' });
}

/**
 * Comment on conversation text, as in Claude Code. Selecting text only shows a small toolbar, so the highlight and the usual copy shortcuts keep working;
 * Add comment opens the editor. A waiting comment stays highlighted in the transcript at its exact passage; clicking the passage edits it in place.
 * Enter saves (adds the quote and note to the prompt) and Ctrl/Cmd+Enter sends it at once. Shift+Enter adds a line; Esc closes.
 */
export function Annotate({ scope, api, onSide }: { scope: React.RefObject<HTMLElement | null>; api: React.RefObject<Annotator | undefined>; onSide?: (quote: string, anchor?: Anchor) => void }) {
  const [bar, setBar] = useState<{ x: number; y: number; quote: string; anchor?: Anchor }>();
  const [editor, setEditor] = useState<{ x: number; y: number; quote: string; anchor?: Anchor; id?: number; note: string }>();
  const [notes, setNotes] = useState<Note[]>([]);
  const holder = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const listen = (e: Event) => setNotes((e as CustomEvent<Note[]>).detail);
    window.addEventListener('rhdock:notes', listen);
    return () => window.removeEventListener('rhdock:notes', listen);
  }, []);

  // Highlight every waiting comment's passage; rebuilt whenever the transcript changes. Where the browser lacks the highlight API the passages are not marked.
  useEffect(() => {
    const highlights = (CSS as unknown as { highlights?: Map<string, unknown> }).highlights;
    const Highlight_ = (window as unknown as { Highlight?: new (...ranges: Range[]) => unknown }).Highlight;
    if (!highlights || !Highlight_) return;
    let frame = 0;
    const paint = () => {
      const ranges = notes.map(n => n.anchor && rangeOf(n.anchor)).filter((r): r is Range => !!r);
      if (ranges.length) highlights.set('rhdock-note', new Highlight_(...ranges)); else highlights.delete('rhdock-note');
      // WebKit (the Rhino panel on macOS) does not always repaint when a highlight goes away; nudge the transcript to repaint.
      const el = scope.current;
      if (el) { el.style.opacity = '0.999'; requestAnimationFrame(() => { el.style.opacity = ''; }); }
    };
    const later = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(paint); };
    paint();
    const watch = scope.current ? new MutationObserver(later) : undefined;
    if (scope.current) watch?.observe(scope.current, { childList: true, subtree: true, characterData: true });
    return () => { watch?.disconnect(); cancelAnimationFrame(frame); highlights.delete('rhdock-note'); };
  }, [notes, scope]);

  useEffect(() => {
    const up = (e: MouseEvent) => {
      if (holder.current?.contains(e.target as Node)) return;
      const selection = window.getSelection();
      const quote = selection?.toString().trim() ?? '';
      const node = selection?.anchorNode instanceof Element ? selection.anchorNode : selection?.anchorNode?.parentElement;
      if (!quote || !selection?.rangeCount || !node || !scope.current?.contains(node) || node.closest('.ag-foot, input, textarea')) {
        setBar(undefined);
        // A click on a highlighted passage edits that comment.
        if (!quote) for (const n of notes) {
          const range = n.anchor && rangeOf(n.anchor);
          if (range && [...range.getClientRects()].some(r => e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom)) {
            setEditor({ x: Math.max(8, Math.min(e.clientX, window.innerWidth - 288)), y: Math.min(e.clientY + 12, window.innerHeight - 140), quote: n.quote, anchor: n.anchor, id: n.id, note: n.note });
            return;
          }
        }
        return;
      }
      const range = selection.getRangeAt(0), rect = range.getBoundingClientRect();
      setBar({ x: Math.max(8, Math.min(rect.left, window.innerWidth - 160)), y: Math.min(rect.bottom + 6, window.innerHeight - 40), quote, anchor: anchorOf(range) });
    };
    document.addEventListener('mouseup', up);
    return () => document.removeEventListener('mouseup', up);
  }, [scope, notes]);

  // Clicking anywhere else closes the editor (the text typed so far is dropped).
  useEffect(() => {
    if (!editor) return;
    const away = (e: MouseEvent) => { if (!holder.current?.contains(e.target as Node)) setEditor(undefined); };
    document.addEventListener('mousedown', away);
    return () => document.removeEventListener('mousedown', away);
  }, [editor]);

  const mac = navigator.platform.startsWith('Mac');
  const finish = (sendNow: boolean) => {
    if (!editor) return;
    if (editor.id !== undefined) api.current?.update(editor.id, editor.note.trim());
    else api.current?.add(editor.quote, editor.note.trim(), sendNow, editor.anchor);
    setEditor(undefined); setBar(undefined); window.getSelection()?.removeAllRanges();
  };
  return <div ref={holder}>
    {bar && !editor && <div className="ag-annotate-bar" style={{ left: bar.x, top: bar.y }} role="toolbar" aria-label="Selection">
      <button onMouseDown={e => e.preventDefault()} onClick={() => { setEditor({ x: bar.x, y: bar.y, quote: bar.quote, anchor: bar.anchor, note: '' }); setBar(undefined); }}><MessageSquareQuote size={12}/>Add comment</button>
      {onSide && <button onMouseDown={e => e.preventDefault()} onClick={() => { onSide(bar.quote, bar.anchor); setBar(undefined); window.getSelection()?.removeAllRanges(); }}><MessageSquare size={12}/>Ask in side chat</button>}
    </div>}
    {editor && <div className="ag-annotate" style={{ left: editor.x, top: editor.y }} role="dialog" aria-label={editor.id !== undefined ? 'Edit the comment' : 'Comment on the selection'}>
      <textarea autoFocus rows={1} value={editor.note} placeholder="Comment" onChange={e => setEditor({ ...editor, note: e.target.value })}
        onKeyDown={e => {
          if (e.key === 'Escape') { e.preventDefault(); setEditor(undefined); return; }
          if (e.key !== 'Enter' || e.shiftKey || e.nativeEvent.isComposing) return;
          e.preventDefault();
          finish(editor.id === undefined && (e.metaKey || e.ctrlKey));
        }}/>
      <button className="ag-annotate-go" aria-label={editor.id !== undefined ? 'Save the comment' : 'Add the comment to the prompt'}
        title={editor.id !== undefined ? 'Save  ⏎' : `Add to prompt  ⏎\nSend now  ${mac ? '⌘' : 'Ctrl+'}⏎`} onClick={() => finish(false)}><CornerDownLeft size={13}/></button>
    </div>}
  </div>;
}

/** The quote and note as prompt text: the selection as a Markdown quote, then the note. */
export function quoted(quote: string, note: string) {
  return quote.split('\n').map(line => '> ' + line).join('\n') + (note ? '\n\n' + note : '');
}

/** The prompt sent with comments: each comment, then the user's text. */
export function withNotes(notes: Note[], text: string) {
  return [...notes.map(n => quoted(n.quote, n.note)), text.trim()].filter(Boolean).join('\n\n');
}

/** A comment as a card above the prompt, as in Claude Code: the quote with a bar, the note under it, and remove. */
export function NoteCard({ note, remove }: { note: Note; remove: () => void }) {
  return <div className="ag-note" title={note.anchor ? 'Show this passage in the conversation' : note.quote}>
    <blockquote onClick={() => revealNote(note)} role={note.anchor ? 'button' : undefined}>{note.quote}</blockquote>
    {note.note && <p>{note.note}</p>}
    <button className="icon-btn sm" aria-label="Remove comment" onClick={remove}><X size={10}/></button>
  </div>;
}
