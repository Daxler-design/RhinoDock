import assert from 'node:assert/strict';
import { createServer } from 'vite';

const server = await createServer({ optimizeDeps: { noDiscovery: true }, server: { host: '127.0.0.1', preTransformRequests: false } });
try {
  await server.listen();
  const address = server.httpServer.address();
  const url = `http://127.0.0.1:${address.port}/__rhdock/mirror/`;
  const state = () => fetch(url + 'state').then(response => response.json());
  assert.equal((await state()).connected, false);

  const snapshot = { revision: 4, repository: '/example', inputs: [], outputs: [] };
  const post = (path, body, headers = {}) => fetch(url + path, {
    method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body),
  });
  assert.equal((await post('snapshot', snapshot)).status, 403);
  assert.equal((await post('snapshot', snapshot, { 'X-RhDock-Mirror': 'rhino' })).status, 200);
  assert.deepEqual((await state()).snapshot, snapshot);

  const origin = `http://127.0.0.1:${address.port}`;
  const action = { action: 'run', revision: 4 };
  assert.equal((await post('action', action, { Origin: 'http://example.com' })).status, 403);
  assert.equal((await post('action', action, { Origin: origin })).status, 200);
  const take = () => fetch(url + 'actions', { headers: { 'X-RhDock-Mirror': 'rhino' } }).then(response => response.json());
  assert.deepEqual(await take(), [action]);
  assert.deepEqual(await take(), []);
  console.log('Development mirror relay passed');
} finally {
  await server.close();
}
