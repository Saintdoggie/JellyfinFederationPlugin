const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { JSDOM } = require('jsdom');
const html = fs.readFileSync(require('node:path').join(__dirname, '../Companion/wwwroot/index.html'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
function page(fetch) {
  return new JSDOM(html, { url: 'http://localhost:7890/', runScripts: 'dangerously', beforeParse(w) {
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
