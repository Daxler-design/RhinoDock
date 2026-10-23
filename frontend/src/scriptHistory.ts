/** Leave text history to the browser; panel shortcuts edit only Script input history. */
export function scriptHistoryAction(e: { key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean; defaultPrevented: boolean }, editing: boolean) {
  if (editing || e.defaultPrevented || e.altKey || !(e.ctrlKey || e.metaKey)) return;
  const key = e.key.toLowerCase();
  if (key === 'z') return e.shiftKey ? 'redoInput' : 'undoInput';
  if (key === 'y' && !e.shiftKey) return 'redoInput';
}
