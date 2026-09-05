'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const root = path.resolve(__dirname, '..');
const badgeScript = fs.readFileSync(path.join(root, 'Web/federation-badge.js'), 'utf8');
const configPage = fs.readFileSync(path.join(root, 'Configuration/configPage.html'), 'utf8');
const itemId = '11111111111111111111111111111111';

function settle() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function makeWindow(isAdmin, showCloudBadge = true, serverAddress = null) {
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body>'
      + '<div class="card" data-id="' + itemId + '"><div class="cardScalable"><div class="cardImageContainer"></div></div><div class="cardText"></div></div>'
      + '<div class="itemMiscInfo-primary"></div><button class="btnMoreCommands"></button><div class="actionSheetScroller"></div>'
      + '</body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html#!/details?id=' + itemId }
  );
  const calls = [];
  const intervals = [];
  let badgeEnabled = showCloudBadge;
  dom.window.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: isAdmin } }),
    ...(serverAddress ? { serverAddress: () => serverAddress } : {}),
    accessToken: () => 'test-token'
  };
  dom.window.fetch = (url) => {
    calls.push(String(url));
    let data = {};
    if (String(url).includes('FederatedIds')) data = { [itemId]: 'Friend' };
    if (String(url).includes('ClientSettings')) data = { showFederatedCloudBadges: badgeEnabled };
    if (String(url).includes('DisabledIds') || String(url).endsWith('/Downloads')) data = [];
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  dom.window.setInterval = (callback) => {
    intervals.push(callback);
    return intervals.length;
  };
  dom.window.requestAnimationFrame = (callback) => dom.window.setTimeout(callback, 0);
  dom.window.eval(badgeScript);
  return {
    dom,
    calls,
    intervals,
    setCloudBadge: (enabled) => { badgeEnabled = enabled; }
  };
}

test('cloud badge is anchored to artwork rather than the outer card', async () => {
  const { dom } = makeWindow(false);
  await settle();
  await settle();
  const image = dom.window.document.querySelector('.cardImageContainer');
  assert.ok(image.querySelector(':scope > .federation-badge-corner'));
  assert.equal(dom.window.document.querySelector('.card > .federation-badge-corner'), null);
  dom.window.close();
});

test('cloud badge can be disabled without disabling federated item discovery', async () => {
  const { dom } = makeWindow(false, false);
  await settle();
  await settle();
  assert.equal(dom.window.document.querySelector('.federation-badge-corner'), null);
  assert.equal(dom.window.document.querySelector('.card').getAttribute('data-federation-badge'), '0');
  dom.window.close();
});

test('cloud badge setting reconciles existing cards without a page reload', async () => {
  const { dom, intervals, setCloudBadge } = makeWindow(false, false);
  await settle();
  await settle();
  const card = dom.window.document.querySelector('.card');
  assert.equal(dom.window.document.querySelector('.federation-badge-corner'), null);

  setCloudBadge(true);
  await intervals[1]();
  await settle();
  await settle();
  assert.ok(dom.window.document.querySelector('.federation-badge-corner'));
  assert.equal(card.getAttribute('data-federation-badge'), '1');

  setCloudBadge(false);
  await intervals[1]();
  await settle();
  await settle();
  assert.equal(dom.window.document.querySelector('.federation-badge-corner'), null);
  assert.equal(card.getAttribute('data-federation-badge'), '0');
  dom.window.close();
});

test('repeated SPA mutations keep exactly one badge and one source label', async () => {
  const { dom } = makeWindow(false);
  await settle();
  await settle();
  const document = dom.window.document;
  const card = document.querySelector('.card');
  card.appendChild(document.createElement('span'));
  card.setAttribute('data-refresh', '1');
  await settle();
  await settle();

  assert.equal(document.querySelectorAll('.federation-badge-corner').length, 1);
  assert.equal(document.querySelectorAll('.federation-source-tag').length, 1);
  dom.window.close();
});

test('ordinary viewers never poll admin-only download or sharing state', async () => {
  const { dom, calls } = makeWindow(false);
  await settle();
  await settle();
  assert.equal(calls.some((url) => url.endsWith('/Downloads')), false);
  assert.equal(calls.some((url) => url.includes('Sharing/DisabledIds')), false);
  dom.window.close();
});

test('a separately hosted client sends federation requests to its configured server and base path', async () => {
  const { dom, calls } = makeWindow(true, true, 'https://media.example/jellyfin/');
  await settle();
  await settle();
  assert.ok(calls.length >= 3);
  assert.ok(calls.every(url => url.startsWith('https://media.example/jellyfin/Plugins/Federation/')));
  dom.window.close();
});

test('admin action sheet offers download to this server for federated items', async () => {
  const { dom } = makeWindow(true);
  await settle();
  await settle();
  dom.window.document.querySelector('.btnMoreCommands').click();
  await settle();
  await settle();
  const labels = [...dom.window.document.querySelectorAll('.federation-actionsheet-item')].map((btn) => btn.textContent.trim());
  assert.ok(labels.includes('Download to this server'));
  assert.ok(labels.includes('Download to device'));
  assert.equal(labels.includes('Cancel server download'), false);
  assert.ok(badgeScript.includes("federationFetch('/Plugins/Federation/Download'"));
  assert.equal(badgeScript.includes('temporarily disabled'), false);
  dom.window.close();
});

test('injected action buttons have independent SVG icons and accessible text', async () => {
  const { dom } = makeWindow(true);
  await settle();
  await settle();
  dom.window.document.querySelector('.btnMoreCommands').click();
  await settle();
  await settle();
  const buttons = [...dom.window.document.querySelectorAll('.federation-actionsheet-item')];
  assert.ok(buttons.length > 0);
  buttons.forEach(button => {
    assert.equal(button.querySelector('.federation-action-icon svg')?.getAttribute('viewBox'), '0 0 24 24');
    assert.equal(button.querySelector('.material-icons'), null);
    assert.ok(button.textContent.trim().length > 0);
  });
  dom.window.close();
});

test('admin sessions initialize admin-only download and sharing state', async () => {
  const { dom, calls } = makeWindow(true);
  await settle();
  await settle();
  assert.equal(calls.some((url) => url.endsWith('/Downloads')), true);
  assert.equal(calls.some((url) => url.includes('Sharing/DisabledIds')), true);
  dom.window.close();
});

test('all rendered tabs are routable and the inline configuration script parses', () => {
  const tabNames = [...configPage.matchAll(/data-tab="([a-z]+)"/g)].map((match) => match[1]);
  const routerMatch = configPage.match(/var TAB_NAMES = \[([^\]]+)\]/);
  assert.ok(routerMatch);
  const routed = [...routerMatch[1].matchAll(/'([a-z]+)'/g)].map((match) => match[1]);
  assert.deepEqual(routed, tabNames);

  const scriptMatch = configPage.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
  assert.ok(scriptMatch);
  assert.doesNotThrow(() => new Function(scriptMatch[1]));

  const dom = new JSDOM(configPage);
  const ids = [...dom.window.document.querySelectorAll('[id]')].map((element) => element.id);
  assert.equal(new Set(ids).size, ids.length, 'configuration page contains duplicate element ids');
  dom.window.document.querySelectorAll('[role="tab"]').forEach((tab) => {
    const panel = dom.window.document.getElementById(tab.getAttribute('aria-controls'));
    assert.ok(panel, tab.textContent.trim() + ' has no matching panel');
    assert.equal(panel.getAttribute('aria-labelledby'), tab.id);
  });
  dom.window.close();
});

test('catalog and downloads tabs expose distinct local/remote workflows', () => {
  const dom = new JSDOM(configPage);
  const document = dom.window.document;
  assert.equal(document.querySelector('#fedTabCatalog').textContent.trim(), 'Catalog');
  assert.equal(document.querySelector('#fedTabBrowse').textContent.trim(), 'Downloads');
  assert.deepEqual(
    [...document.querySelectorAll('#fedCatalogType option')].map((option) => option.textContent.trim()),
    ['Movies', 'TV shows']
  );
  assert.deepEqual(
    [...document.querySelectorAll('#fedBrowseType option')].map((option) => option.textContent.trim()),
    ['Movies', 'TV shows']
  );
  dom.window.close();
});

test('storage cleanup is delete-only, grouped by show and season, with exact bulk confirmation', () => {
  assert.equal(configPage.includes('data-fed-action="quality-select-all"'), true);
  assert.equal(configPage.includes('data-fed-action="quality-select-show"'), true);
  assert.equal(configPage.includes('data-fed-action="quality-select-season"'), true);
  assert.equal(configPage.includes('data-fed-action="quality-apply-selected"'), true);
  assert.equal(configPage.includes('data-fed-action="quality-apply-one"'), false);
  assert.match(configPage, /QualityUpgrades\/RemoveLocal/);
  assert.match(configPage, /ItemIds:\s*ids,\s*Confirm:\s*true/);
  assert.equal(configPage.includes('QualityUpgrades/Apply\', {'), false);
  assert.equal(configPage.includes('class="fed-show-folder"'), true);
  assert.equal(configPage.includes('class="fed-season-folder"'), true);
  assert.equal(configPage.includes('class="fed-episode-poster"'), true);
  assert.match(configPage, /type="checkbox" class="fed-check[^"]*" data-fed-action="quality-select"/);
  assert.match(configPage, /function selectedQualityBytes\(\)/);
  assert.match(configPage, /function qualityBytes\(/);
  assert.match(configPage, /Estimated space freed/);
  assert.equal(configPage.includes('fed-selection-toggle'), false, 'storage and downloads must use native checkboxes, not fake toggle buttons');

  const applyFn = configPage.match(/function applySelectedQualityUpgrades\(\) \{[\s\S]*?\n {20}\}/);
  assert.ok(applyFn, 'applySelectedQualityUpgrades function body not found');
  const confirmCalls = applyFn[0].match(/window\.confirm\(/g) || [];
  assert.equal(confirmCalls.length, 2, 'expected exactly two confirmations before local files are removed');
  assert.match(applyFn[0], /No replacement will be downloaded/);
});

test('storage size calculator totals eligible, selected, show, season, and episode bytes', () => {
  assert.match(configPage, /potential cleanup/);
  assert.match(configPage, /selected cleanup/);
  assert.match(configPage, /Remove selected · /);
  assert.match(configPage, /formatBytes\(qualityBytes\(show\.episodes\)\)/);
  assert.match(configPage, /formatBytes\(qualityBytes\(episodes\)\)/);
  assert.match(configPage, /formatBytes\(ep\.localSizeBytes \|\| 0\)/);
  assert.match(configPage, /reclaimedBytes/);
  assert.match(configPage, /localSizeBytes/);
});

test('downloads separate selection from activity and require bulk source consent', () => {
  const dom = new JSDOM(configPage);
  const document = dom.window.document;
  assert.equal(document.querySelector('#fedDownloadSelectView').getAttribute('role'), 'tabpanel');
  assert.equal(document.querySelector('#fedDownloadActivityView').getAttribute('role'), 'tabpanel');
  assert.ok(document.querySelector('[data-fed-action="browse-download-selected"]'));
  assert.match(configPage, /Browse\/DownloadBatch/);
  assert.match(configPage, /items\.length > 3/);
  assert.match(configPage, /Bulk permission required from the source server/);
  assert.equal(configPage.includes('fedAllowBulkDownloads'), true);
  dom.window.close();
});

test('Downloads server dropdown refreshes on every config load, not just once', () => {
  // Regression: loadBrowseServers() used to run only the first time the
  // Downloads tab was opened (guarded by a "browseLoaded" flag). If that
  // first open raced ahead of the initial config fetch, the dropdown was
  // populated from the still-empty currentConfig placeholder and then
  // never touched again for the rest of the page's life - "no servers
  // available" even though friends existed. It must now run every time
  // loadConfiguration() resolves, and there must be no once-only gate left
  // on the tab-switch call.
  assert.equal(configPage.includes('var browseLoaded'), false);
  assert.equal(/if \(tab === 'browse'[^)]*\)\s*\{\s*loadBrowseServers\(\);/.test(configPage), true);

  const configScript = configPage.match(/function loadConfiguration\(silent\) \{[\s\S]*?\n {20}\}/);
  assert.ok(configScript, 'loadConfiguration function body not found');
  assert.match(configScript[0], /loadBrowseServers\(\);/);
});

test('Plex connect-code posts the raw code to the server-side claim endpoint', () => {
  // Regression: the settings page used to base64-decode the Companion code in
  // the browser and POST {url, token} to ExternalServers as if they were Plex
  // credentials. That saved Companion's Funnel URL + a one-time claim token,
  // which only worked when both machines shared a Tailscale tailnet. The
  // plugin must claim the code server-to-server instead.
  const fn = configPage.match(/function connectPlexSource\(\) \{[\s\S]*?\n {20}\}/);
  assert.ok(fn, 'connectPlexSource not found');
  assert.equal(fn[0].includes('JSON.parse(atob'), false);
  assert.match(fn[0], /ExternalServers\/ConnectCode/);
  assert.match(fn[0], /Code:\s*raw/);
});

test('fed-check checkboxes render with a visible native box, not the unupgraded emby-checkbox style', () => {
  // Regression: every checkbox on this page is class="emby-checkbox fed-check"
  // with no is="emby-checkbox", so jellyfin-web's checkbox custom element
  // never upgrades them, and the dashboard's own .emby-checkbox rule (which
  // hides the native box expecting that element to draw a replacement) left
  // every checkbox on the page fully invisible while still toggling on click.
  assert.equal(/is="emby-checkbox"/.test(configPage), false, 'no checkbox uses the emby-checkbox upgrade');
  assert.match(configPage, /#federationConfigPage input\.fed-check\s*\{[^}]*appearance:\s*auto/);
  assert.match(configPage, /#federationConfigPage input\.fed-check\s*\{[^}]*opacity:\s*1/);
});
