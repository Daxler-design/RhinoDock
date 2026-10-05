import { useEffect, useRef, useState } from 'react';
import { MessageSquareQuote, X } from 'lucide-react';

/** Adds or sends a quote of the selected text with the user's note; the composer owns the text and the send. */
export type Annotator = (quote: string, note: string, sendNow: boolean) => void;

/**
 * Comment on selected conversation text, as in Claude Code: select text in the transcript, type a note, then Enter adds the quote
 * and note to the prompt and Ctrl/Cmd+Enter sends them. Shift+Enter adds a line; Esc closes. Selections in the composer are ignored.
 */
export function Annotate({ scope, annotate }: { scope: React.RefObject<HTMLElement | null>; annotate: Annotator }) {
  const [at, setAt] = useState<{ x: number; y: number; quote: string }>();
  const [note, setNote] = useState('');
  const card = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const up = (e: MouseEvent) => {
      if (card.current?.contains(e.target as Node)) return;
      const selection = window.getSelection();
      const quote = selection?.toString().trim() ?? '';
      const node = selection?.anchorNode instanceof Element ? selection.anchorNode : selection?.anchorNode?.parentElement;
      if (!quote || !selection?.rangeCount || !node || !scope.current?.contains(node) || node.closest('.ag-foot, input, textarea')) { setAt(undefined); return; }
      const rect = selection.getRangeAt(0).getBoundingClientRect();
      setNote('');
      setAt({ x: Math.max(8, Math.min(rect.left, window.innerWidth - 288)), y: Math.min(rect.bottom + 6, window.innerHeight - 120), quote });
    };
    document.addEventListener('mouseup', up);
    return () => document.removeEventListener('mouseup', up);
  }, [scope]);

  if (!at) return null;
  const finish = (sendNow: boolean) => { annotate(at.quote, note.trim(), sendNow); setAt(undefined); window.getSelection()?.removeAllRanges(); };
  return <div ref={card} className="ag-annotate" style={{ left: at.x, top: at.y }} role="dialog" aria-label="Comment on the selection">
    <MessageSquareQuote size={13}/>
    <textarea autoFocus rows={2} value={note} placeholder="Comment on the selection" onChange={e => setNote(e.target.value)}
      onKeyDown={e => {
        if (e.key === 'Escape') { e.preventDefault(); setAt(undefined); return; }
        if (e.key !== 'Enter' || e.shiftKey || e.nativeEvent.isComposing) return;
        e.preventDefault();
        finish(e.metaKey || e.ctrlKey);
      }}/>
    <small>Enter adds to the prompt · {navigator.platform.startsWith('Mac') ? '⌘' : 'Ctrl+'}Enter sends</small>
  </div>;
}

/** A comment waiting in the composer: the selected text and the user's note. */
export type Note = { id: number; quote: string; note: string };

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
  return <div className="ag-note" title={note.quote}>
    <blockquote>{note.quote}</blockquote>
    {note.note && <p>{note.note}</p>}
    <button className="icon-btn sm" aria-label="Remove comment" onClick={remove}><X size={10}/></button>
  </div>;
}

/** Message text with its Markdown quote paragraphs shown as quotes, the other paragraphs as written. */
export function QuotedText({ text }: { text: string }) {
  return <>{text.split(/\n{2,}/).map((part, i) => part.split('\n').every(line => line.startsWith('>'))
    ? <blockquote key={i} className="ag-quote">{part.split('\n').map(line => line.replace(/^> ?/, '')).join('\n')}</blockquote>
    : <span key={i} className="ag-para">{part}</span>)}</>;
}
