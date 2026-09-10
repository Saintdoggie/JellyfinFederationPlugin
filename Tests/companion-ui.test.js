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

test('Companion explains and requires owner setup for optional media helper', () => {
  const main = html.split('Manual mount')[0];
  assert.match(main, /Set up the media folder once/);
  assert.match(html, /Set up \/ start media folder/);
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

test('Companion generate code does not mint a Plex token unless Plex Remote Access is chosen', async () => {
  const requests = [];
  const dom = page(async (url, opts) => {
    requests.push([url, opts]);
    const path = String(url);
    if (path.includes('/api/connect/generate')) return json({ code: 'abc', expiresInMinutes: 15, mode: 'claim' });
    if (path.includes('/peers') || path.includes('/invites') || path.includes('/api/plex/servers')) return json([]);
    return json({ serverConnected: false, libraries: [] });
  }, 'http://localhost:7890/#access=owner-test-key');
  try {
    const d = dom.window.document;
    d.getElementById('generateCodeBtn').click();
    await tick();
    const claim = requests.find(([url]) => String(url).includes('/api/connect/generate'));
    assert.ok(claim, 'generate request was sent');
    assert.equal(header(claim[1], 'X-Companion-Admin'), 'owner-test-key');
    assert.equal(String(claim[0]).includes('usePlexRemoteAccess=true'), false);

    requests.length = 0;
    d.getElementById('generateDirectCodeBtn').click();
    await tick();
    const direct = requests.find(([url]) => String(url).includes('/api/connect/generate'));
    assert.ok(direct, 'Plex Remote Access generate request was sent');
    assert.match(String(direct[0]), /usePlexRemoteAccess=true/);
    assert.equal(header(direct[1], 'X-Companion-Admin'), 'owner-test-key');
    assert.match(html, /explicitly choose Plex Remote Access/);
    assert.doesNotMatch(html, /Companion uses Plex Remote Access instead so they can still Accept/);
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

test('Companion uptime format shows seconds until the first minute', () => {
  const dom = page(async () => json({ serverConnected: false, libraries: [] }));
  try {
    assert.equal(dom.window.formatUptime(12), '12s');
    assert.equal(dom.window.formatUptime(60), '1m');
    assert.equal(dom.window.formatUptime(3720), '1h 2m');
  } finally { dom.window.close(); }
});

test('Companion page explains the tray and background running', () => {
  assert.match(html, /keeps running in the background/);
  assert.match(html, /notification area \(tray\)/);
  assert.match(html, /id="autostartToggle"/);
  assert.match(html, /Start Companion when I sign in/);
  assert.match(html, /right-click the tray icon/);
  assert.match(html, /Copy owner key/);
  assert.match(html, /\.checkbox-line/);
  assert.match(html, /class="app-actions"/);
  assert.match(html, /#appCard \{ grid-column: 1 \/ -1/);
  assert.doesNotMatch(html, /printed in the Companion terminal/);
});

test('Companion app card shows live app facts and manages the sign-in setting', async () => {
  const requests = [];
  const dom = page(async (url, opts) => {
    requests.push([url, opts]);
    const path = String(url);
    if (path.includes('/api/app/info')) return json({ version: '0.0.158', port: 8123, uptimeSeconds: 3720, workingSetMb: 42, installDirectory: 'C:\\Fed', logPath: 'C:\\Fed\\companion.log', backgroundLaunch: true, windows: true, autostartSupported: true, autostartEnabled: false });
    if (path.includes('/api/app/autostart')) return json({ enabled: JSON.parse(opts.body).enabled });
    if (path.includes('/api/media-mount/stop')) return json({ running: false, stopped: true, message: 'Media folder stopped.' });
    if (path.includes('/api/media-mount/status')) return json({ ready: false, message: 'stopped', driverReady: true });
    if (path.includes('/api/status')) return json({ serverConnected: false, libraries: [] });
    return json([]);
  }, 'http://localhost:7890/#access=owner-test-key');
  try {
    const d = dom.window.document;
    await tick();
    const info = d.getElementById('appInfo');
    assert.match(info.textContent, /0\.0\.158/);
    assert.match(info.textContent, /42 MB RAM/);
    assert.match(info.textContent, /1h 2m/);
    assert.equal(d.getElementById('autostartRow').classList.contains('hidden'), false);
    assert.equal(d.getElementById('autostartToggle').checked, false);

    const toggle = d.getElementById('autostartToggle');
    toggle.checked = true;
    toggle.dispatchEvent(new dom.window.Event('change'));
    await tick();
    const post = requests.find(([url, opts]) => String(url).includes('/api/app/autostart') && opts && opts.method === 'POST');
    assert.ok(post, 'autostart POST was sent');
    assert.equal(header(post[1], 'X-Companion-Admin'), 'owner-test-key');
    assert.deepEqual(JSON.parse(post[1].body), { enabled: true });
    assert.equal(toggle.checked, true);

    d.getElementById('stopMountBtn').click();
    await tick();
    const stop = requests.find(([url]) => String(url).includes('/api/media-mount/stop'));
    assert.ok(stop, 'stop media folder request was sent');
    assert.match(d.getElementById('appStatus').textContent, /stopped/i);
  } finally { dom.window.close(); }
});

test('Companion hides the sign-in setting on platforms that do not support it', async () => {
  const dom = page(async url => {
    const path = String(url);
    if (path.includes('/api/app/info')) return json({ version: '0.0.158', port: 8123, uptimeSeconds: 60, workingSetMb: 30, windows: false, autostartSupported: false, autostartEnabled: false });
    if (path.includes('/api/status')) return json({ serverConnected: false, libraries: [] });
    return json([]);
  }, 'http://localhost:7890/#access=owner-test-key');
  try {
    const d = dom.window.document;
    await tick();
    assert.equal(d.getElementById('autostartRow').classList.contains('hidden'), true);
    assert.equal(d.getElementById('autostartToggle').disabled, true);
  } finally { dom.window.close(); }
});
