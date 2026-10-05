import { useState } from 'react';
import { Pause, Play, RotateCcw, SkipForward } from 'lucide-react';
import type { Action, Snapshot, Solve } from './types';

const rates = [0, 30, 10, 1];
const rateLabel = (rate: number) => rate ? `${rate}/s` : 'Max';

/** Run Bar for a Solver Script: status, Auto, pace, Reset, Step, and Solve/Pause. */
export function SolveBar({ snap, solve, act, openConsole }: { snap: Snapshot; solve: Solve; act: (a: Action) => void; openConsole: () => void }) {
  const [menu, setMenu] = useState(false);
  const finished = solve.done || solve.stalled;
  const phase = snap.status === 'error' ? 'error' : solve.playing ? (solve.held ? 'held' : 'solving') : finished ? 'done' : solve.frame > 0 ? 'paused' : 'ready';
  const label = { error: 'Error', held: 'Waiting', solving: 'Solving', done: 'Converged', paused: 'Paused', ready: 'Ready' }[phase];
  const meta = phase === 'held' ? 'for Rhino' : phase === 'solving' ? `frame ${solve.frame}`
    : phase === 'done' || phase === 'paused' ? `frame ${solve.frame}` : '';
  const busy = !!snap.picking;
  return <footer className="rb solve">
    {solve.playing && <div className="solve-track"><div className="solve-line"/></div>}
    <button className={`status st-${phase === 'held' ? 'pending' : phase === 'solving' ? 'running' : phase === 'done' ? 'completed' : phase === 'paused' ? 'pending' : phase === 'error' ? 'error' : 'ready'}`} onClick={openConsole} title={solve.playing && solve.fps ? `${solve.fps} frames per second` : solve.stalled ? 'Stopped: outputs no longer change' : solve.done ? 'The Script set Done' : undefined}>
      <span className={`st-dot ${phase === 'solving' ? 'pulsing' : ''}`}/><span className="st-label">{label}</span>{meta && <span className="st-meta">{meta}</span>}
    </button>
    <button className="auto" aria-pressed={snap.auto} title="Auto: restart the Solve when an input changes" onClick={() => act({ action: 'auto', value: !snap.auto })}><span className={`switch ${snap.auto ? 'on' : ''}`}><span className="knob"/></span>Auto</button>
    <div className="rate-wrap">
      <button className="rate" title="Frame rate limit" aria-haspopup="menu" aria-expanded={menu} onClick={() => setMenu(!menu)}>{rateLabel(solve.rate)}</button>
      {menu && <><div className="scrim clear" onClick={() => setMenu(false)}/><div className="rate-menu" role="menu">
        {rates.map(rate => <button key={rate} role="menuitemradio" aria-checked={solve.rate === rate} className={solve.rate === rate ? 'on' : ''} onClick={() => { setMenu(false); act({ action: 'rate', value: rate }); }}>{rate ? `${rate} frames/s` : 'Max'}</button>)}
      </div></>}
    </div>
    <button className="tb" title="Reset: discard the Solve" aria-label="Reset the Solve" disabled={busy || (!solve.playing && solve.frame === 0)} onClick={() => act({ action: 'resetSolve' })}><RotateCcw size={15}/></button>
    <button className="tb" title="Step one frame" aria-label="Step one frame" disabled={busy || solve.playing || finished} onClick={() => act({ action: 'step' })}><SkipForward size={15}/></button>
    <button className="run" disabled={busy || !snap.selected || (!solve.playing && finished)} onClick={() => act({ action: solve.playing ? 'pause' : 'play' })}>
      {solve.playing ? <Pause size={13} fill="currentColor"/> : <Play size={13} fill="currentColor"/>}{solve.playing ? 'Pause' : finished ? 'Solved' : solve.frame > 0 ? 'Resume' : 'Solve'}
    </button>
  </footer>;
}
