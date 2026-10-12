// The page side of an rhDock Extension View. Load it before your own scripts:
//   <script src="rhdock.js"></script>
// rhdock.on(type, handler) receives ExtensionView.Post(type, payload); rhdock.send(type, payload) reaches ExtensionView.On(type, …).
// Types starting with "rhdock." are reserved for the host.
window.rhdock = {
  handlers: {},
  on(type, handler) { this.handlers[type] = handler; },
  receive(type, payload) { this.handlers[type]?.(payload); },
  send(type, payload) { window.eto?.postMessage(JSON.stringify({ type, payload })); },
};

// In a browser through the rhDock Mirror (RhinoDockMirror), the same messages travel over HTTP to the window in Rhino.
// rhDock serves its own copy of this file there, so a copy in an Extension folder never needs updating for it.
(() => {
  const name = new URLSearchParams(location.search).get('rhdock-window');
  if (window.eto || !name) return;
  const [, token, , extension] = location.pathname.split('/');
  const url = `/${token}/ext/${extension}/__rhdock/window?window=${encodeURIComponent(name)}`;
  window.eto = { postMessage: message => fetch(url, { method: 'POST', body: message }).catch(() => {}) };
  new EventSource(url).onmessage = event => {
    const { type, payload } = JSON.parse(event.data);
    if (type === 'rhdock.reload') location.reload();
    else if (type === 'rhdock.navigate') location.href = payload;
    else if (type === 'rhdock.closed') document.title = '(closed in Rhino) ' + document.title;
    else rhdock.receive(type, payload);
  };
})();

// Page errors go to the rhDock log (rhdock.error). Eto injects window.eto after DOMContentLoaded, so errors before that
// are kept and sent once the bridge exists, together with one environment report (rhdock.env).
(() => {
  const pending = [];
  const report = text => window.eto ? rhdock.send('rhdock.error', text) : pending.push(text);
  addEventListener('error', e => report(`${e.message} at ${e.filename}:${e.lineno}:${e.colno}`));
  addEventListener('unhandledrejection', e => report('unhandled rejection: ' + (e.reason?.stack || e.reason)));
  const etoAtLoad = !!window.eto;
  let tries = 0;
  const env = setInterval(async () => {
    if (!window.eto && ++tries < 40) return;
    clearInterval(env);
    const adapter = await navigator.gpu?.requestAdapter().catch(() => null);
    rhdock.send('rhdock.env', { eto: !!window.eto, etoAtLoad, gpu: !!navigator.gpu, adapter: !!adapter,
      secure: isSecureContext, url: location.href, userAgent: navigator.userAgent });
    pending.splice(0).forEach(text => rhdock.send('rhdock.error', text));
  }, 250);
})();

// The host bridge may arrive after the page starts: rhdock.ready() sends 'ready' every 250 ms until the first message
// arrives. Call it once after registering your rhdock.on handlers; answer 'ready' in the Extension with the page's state.
rhdock.ready = () => {
  let done = false;
  const receive = rhdock.receive.bind(rhdock);
  rhdock.receive = (type, payload) => { done = true; receive(type, payload); };
  const timer = setInterval(() => done ? clearInterval(timer) : rhdock.send('ready'), 250);
};
