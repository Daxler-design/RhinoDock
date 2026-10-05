import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath } from 'node:url';

function rhinoMirror(): Plugin {
  let snapshot: unknown;
  let lastRhino = 0;
  const actions: unknown[] = [];
  const send = (res: import('node:http').ServerResponse, status: number, value: unknown) => {
    res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    res.end(JSON.stringify(value));
  };
  return {
    name: 'rhdock-dev-mirror',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        const path = req.url?.split('?')[0];
        if (!path?.startsWith('/__rhdock/mirror/')) return next();
        const rhino = req.headers['x-rhdock-mirror'] === 'rhino';
        try {
          if (path === '/__rhdock/mirror/state' && req.method === 'GET')
            return send(res, 200, { connected: Date.now() - lastRhino < 2000, snapshot });
          if (path === '/__rhdock/mirror/actions' && req.method === 'GET' && rhino) {
            lastRhino = Date.now();
            return send(res, 200, actions.splice(0));
          }
          if (req.method !== 'POST') return send(res, 404, { error: 'Not found' });
          let size = 0;
          const chunks: Buffer[] = [];
          for await (const chunk of req) {
            size += chunk.length;
            if (size > 12_000_000) return send(res, 413, { error: 'Message too large' });
            chunks.push(chunk);
          }
          const value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          if (path === '/__rhdock/mirror/snapshot' && rhino && value && typeof value.revision === 'number') {
            if (Date.now() - lastRhino >= 2000) actions.length = 0;
            snapshot = value;
            lastRhino = Date.now();
            return send(res, 200, { ok: true });
          }
          const sameOrigin = req.headers.origin === `http://${req.headers.host}`;
          if (path === '/__rhdock/mirror/action' && sameOrigin && Date.now() - lastRhino < 2000
              && value && typeof value.action === 'string' && (Number.isInteger(value.revision) || value.action.startsWith('agent'))
              && actions.length < 64) {
            actions.push(value);
            return send(res, 200, { ok: true });
          }
          return send(res, 403, { error: 'Mirror is unavailable' });
        } catch {
          return send(res, 400, { error: 'Invalid JSON' });
        }
      });
    },
  };
}

export default defineConfig({
  plugins: [react(), tailwindcss(), rhinoMirror()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: { host: '127.0.0.1', port: Number(process.env.PORT) || undefined },
  base: './',
  // One classic script (iife: no import.meta), because the packaged panel inlines it and loads nothing from disk or the network.
  build: { outDir: 'dist', assetsInlineLimit: 10000000, chunkSizeWarningLimit: 2000, rollupOptions: { output: { format: 'iife', inlineDynamicImports: true } } },
});
