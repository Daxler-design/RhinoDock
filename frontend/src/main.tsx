import React from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { Boundary, watchPage } from './Boundary';
import { Hints } from './Hints';
import './styles/theme.css';

// The packaged panel page is loaded from a string, so its base URL is about:blank, which cannot be a base for URLs. Vite resolves lazy chunks
// (code highlighting) against it, so give such a page a harmless absolute base. Pages served over http (the mirror, the browser preview) keep theirs.
if (!/^(https?|file):/.test(document.baseURI)) {
  const base = document.createElement('base');
  base.href = 'file:///rhdock/';
  document.head.prepend(base);
}

createRoot(document.getElementById('root')!).render(<React.StrictMode>
  <Boundary fallback={(error, reset) => <div style={{ padding: 16, color: '#e5e5e5', font: '12px system-ui' }}>
    <strong>rhDock hit an error</strong>
    <pre style={{ whiteSpace: 'pre-wrap', margin: '8px 0' }}>{error.message}</pre>
    <p>The details are in rhdock-agent.log.</p>
    <button onClick={reset}>Try again</button> <button onClick={() => location.reload()}>Reload panel</button>
  </div>}><App /><Hints /></Boundary>
</React.StrictMode>);
watchPage();
