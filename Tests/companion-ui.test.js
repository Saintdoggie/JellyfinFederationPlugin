const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { JSDOM } = require('jsdom');
const html = fs.readFileSync(require('node:path').join(__dirname, '../Companion/wwwroot/index.html'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
function page(fetch, url = 'http://localhost:7890/') {
  return new JSDOM(html, { url, runScripts: 'dangerously', beforeParse(w) {
    w.Headers = Headers; w.fetch = fetch; w.confirm = () => true;
  } });
}
const json = data => ({ ok: true, json: async () => data });

test('Companion shows pool invites so Plex owners can join a Federation pool', () => {
  assert.match(html, /id="poolInviteList"/);
  assert.match(html, /\/api\/pools\/invites/);
  assert.match(html, /companionPluginVersion/);
});

test('Companion previews libraries unchecked and imports only explicit selections', async () => {
  const requests = [];
  const dom = page(async (url, opts) => { requests.push([url, opts]);
    if (url.endsWith('/preview')) return json({ libraries: [{ id: 'one', name: '<b>Movies</b>' }, { id: 'two', name: 'Shows' }] });
    if (url.endsWith('/connect')) return json({ name: 'Friend' });
    return json([]);
  });
  try {
    const d = dom.window.document; d.getElementById('importCodeInput').value = 'test-code';
    const button = d.getElementById('importConnectBtn'); button.click(); await tick();
    const picker = d.getElementById('importLibraryPicker');
    assert.equal(picker.querySelectorAll('input:checked').length, 0);
    assert.equal(picker.querySelector('b'), null);
    button.click(); await tick(); assert.equal(requests.length, 1);
    picker.querySelector('input').checked = true;
    button.click(); await tick();
    assert.deepEqual(JSON.parse(requests[1][1].body).libraryIds, ['one']);
    assert.equal(button.disabled, false);
  } finally { dom.window.close(); }
});

test('Companion local mount starts itself and does not ask Plex owners to install rclone', () => {
  const main = html.split('Manual mount')[0];
  assert.match(main, /starts the playable media folder by itself/);
  assert.match(html, /Retry media mount/);
  assert.doesNotMatch(main, /Put rclone\.exe beside Companion/);
  assert.doesNotMatch(main, /rclone\.org\/downloads/);
  assert.doesNotMatch(main, /click Start media mount/);
  assert.match(html, /Preparing the media mount\. Companion may download a helper once/);
  assert.match(html, /id="driverHint"/);
});

function header(opts, name) {
  const headers = opts && opts.headers;
  if (!headers) return undefined;
  return typeof headers.get === 'function' ? headers.get(name) : headers[name];
}

test('Companion local Plex connect and media-folder path send the owner access header', async () => {
  const requests = [];
  const dom = page(async (url, opts) => {
    requests.push([url, opts]);
    const path = String(url);
    if (path.includes('/api/plex/connect-local')) return json({ serverName: 'Home Plex' });
    if (path.includes('/api/plex-visible-root')) return json({ plexVisibleImportRoot: '/mnt/media' });
    if (path.includes('/peers') || path.includes('/invites') || path.includes('/api/plex/servers')) return json([]);
    return json({ serverConnected: false, libraries: [] });
  }, 'http://localhost:7890/#access=owner-test-key');
  try {
    const d = dom.window.document;
    d.getElementById('localPlexUrl').value = 'http://127.0.0.1:32400';
    d.getElementById('localPlexToken').value = 'plex-token';
    d.getElementById('connectLocalBtn').click();
    await tick();
    const connect = requests.find(([url]) => String(url).includes('/api/plex/connect-local'));
    assert.ok(connect, 'connect-local request was sent');
    assert.equal(header(connect[1], 'X-Companion-Admin'), 'owner-test-key');
    assert.equal(header(connect[1], 'Content-Type'), 'application/json');
    assert.deepEqual(JSON.parse(connect[1].body), { url: 'http://127.0.0.1:32400', token: 'plex-token' });

    d.getElementById('plexVisibleRootInput').value = '/mnt/media';
    d.getElementById('savePlexRootBtn').click();
    await tick();
    const root = requests.find(([url]) => String(url).includes('/api/plex-visible-root'));
    assert.ok(root, 'plex-visible-root request was sent');
    assert.equal(header(root[1], 'X-Companion-Admin'), 'owner-test-key');
    assert.equal(header(root[1], 'Content-Type'), 'application/json');
    assert.deepEqual(JSON.parse(root[1].body), { url: '/mnt/media' });
  } finally { dom.window.close(); }
});

test('Companion recovers mount button after a network failure', async () => {
  const dom = page(async () => { throw new Error('offline'); });
  try {
    const d = dom.window.document; const button = d.getElementById('startLocalMountBtn');
    button.click(); await tick();
    assert.equal(button.disabled, false);
    assert.match(d.getElementById('localMountStatus').textContent, /retry/i);
  } finally { dom.window.close(); }
});

test('Companion displays source numbering and issue text safely and recovers Add to Plex', async () => {
  const dom = page(async url => {
    if (url.endsWith('/add-to-plex')) throw new Error('offline');
    if (url.includes('/catalog?')) return json({ total: 1, items: [{ title: '<img src=x>', series: 'Source show', season: 1, episode: 7, episodeEnd: 8, issue: 'Source video size is missing.' }] });
    return json([{ id: 'peer', name: 'Friend', lastItemCount: 1, mountedItemCount: 0, importIssueCount: 1 }]);
  });
  try {
    await dom.window.loadImportPeers(); const d = dom.window.document;
    assert.doesNotMatch(d.getElementById('importPeerList').textContent, /Streaming ready/);
    d.querySelector('[data-action="catalog"]').click(); await tick();
    const result = d.querySelector('[data-catalog-results]');
    assert.match(result.textContent, /S1 E7-8/); assert.match(result.textContent, /size is missing/); assert.equal(result.querySelector('img'), null);
    const button = d.querySelector('[data-action="add-plex"]'); button.click(); await tick();
    assert.equal(button.disabled, false); assert.match(d.getElementById('importConnectStatus').textContent, /retry/i);
  } finally { dom.window.close(); }
});
