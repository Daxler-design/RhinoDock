import React from 'react';
import { send } from './bridge';

// The host writes these to rhdock-agent.log, so a page error can be diagnosed without the web inspector.
export function report(error: unknown) {
  const e = error instanceof Error ? error : new Error(String(error));
  send({ action: 'clientError', value: `${e.name}: ${e.message}\n${(e.stack ?? '').split('\n').slice(0, 8).join('\n')}`.slice(0, 3000) });
}

export function watchPage() {
  window.addEventListener('error', e => report(e.error ?? e.message));
  window.addEventListener('unhandledrejection', e => report(e.reason));
}

// A changed resetKey renders the children again, so a failure on one snapshot does not stick once the content changes.
type Props = { children: React.ReactNode; fallback: (error: Error, reset: () => void) => React.ReactNode; resetKey?: unknown };

export class Boundary extends React.Component<Props, { error: Error | null }> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) { return { error }; }
  componentDidCatch(error: Error) { report(error); }
  componentDidUpdate(previous: Props) { if (this.state.error && previous.resetKey !== this.props.resetKey) this.setState({ error: null }); }
  render() { return this.state.error ? this.props.fallback(this.state.error, () => this.setState({ error: null })) : this.props.children; }
}
