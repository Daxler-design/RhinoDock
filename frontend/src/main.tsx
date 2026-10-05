import React from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { Boundary, watchPage } from './Boundary';
import './styles/theme.css';

createRoot(document.getElementById('root')!).render(<React.StrictMode>
  <Boundary fallback={(error, reset) => <div style={{ padding: 16, color: '#e5e5e5', font: '12px system-ui' }}>
    <strong>rhDock hit an error</strong>
    <pre style={{ whiteSpace: 'pre-wrap', margin: '8px 0' }}>{error.message}</pre>
    <p>The details are in rhdock-agent.log.</p>
    <button onClick={reset}>Try again</button> <button onClick={() => location.reload()}>Reload panel</button>
  </div>}><App /></Boundary>
</React.StrictMode>);
watchPage();
