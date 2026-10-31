'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const root = path.resolve(__dirname, '../..');
const badgeScript = fs.readFileSync(path.join(root, 'Web/client/federation-badge.js'), 'utf8');
const configPage = fs.readFileSync(path.join(root, 'Web/admin/configPage.html'), 'utf8');
const plexPage = fs.readFileSync(path.join(root, 'Plex/Web/admin/plexPage.html'), 'utf8');
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
  const requests = [];
  const intervals = [];
  let badgeEnabled = showCloudBadge;
  dom.window.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: isAdmin } }),
    ...(serverAddress ? { serverAddress: () => serverAddress } : {}),
    accessToken: () => 'test-token'
  };
  dom.window.fetch = (url, options) => {
    requests.push({ url: String(url), options });
    calls.push(String(url));
    let data = {};
    if (String(url).includes('ClientCatalog')) data = { items: { [itemId]: 'Friend' }, unavailableIds: [] };
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
    requests,
    intervals,
    setCloudBadge: (enabled) => { badgeEnabled = enabled; }
  };
}

const localItemId = '22222222222222222222222222222222';

function makeLibraryWindow() {
  const calls = [];
  const requests = [];
  const intervals = [];
  const getItemsCalls = [];
  const itemId = '11111111111111111111111111111111';
  const localItemId = '22222222222222222222222222222222';
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body>'
      + '<div class="page libraryPage">'
      + '<div class="itemsViewSettingsContainer"><button class="btnFilter">Filter</button></div>'
      + '<div class="itemsContainer">'
      + '<div class="card" data-id="' + itemId + '"><div class="cardImageContainer"></div></div>'
      + '<div class="card" data-id="' + localItemId + '"><div class="cardImageContainer"></div></div>'
      + '</div></div></body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html#!/movies.html?tab=0' }
  );
  dom.window.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: false } }),
    accessToken: () => 'test-token',
    getItems: (userId, options) => {
      getItemsCalls.push(options || {});
      return Promise.resolve({ Items: [] });
    }
  };
  dom.window.fetch = (url, options) => {
    requests.push({ url: String(url), options });
    calls.push(String(url));
    let data = {};
    if (String(url).includes('ClientCatalog')) data = { items: { [itemId]: 'Friend' }, unavailableIds: [] };
    if (String(url).includes('ClientSettings')) data = { showFederatedCloudBadges: true };
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  dom.window.setInterval = (callback) => {
    intervals.push(callback);
    return intervals.length;
  };
  dom.window.requestAnimationFrame = (callback) => dom.window.setTimeout(callback, 0);
  try {
    dom.window.sessionStorage.clear();
  } catch (e) { /* jsdom storage may already be empty */ }
  dom.window.eval(badgeScript);
  return { dom, calls, requests, intervals, getItemsCalls };
}

test('badge styling is solid and crisp, with no gradients', () => {
  const styleBlock = badgeScript.match(/\.federation-badge-corner\{[^}]+\}/);
  assert.ok(styleBlock, 'federation-badge-corner rule not found');
  assert.equal(styleBlock[0].includes('linear-gradient'), false);
  assert.equal(styleBlock[0].includes('radial-gradient'), false);
  assert.equal(styleBlock[0].includes('backdrop-filter'), false);
  assert.match(styleBlock[0], /background:rgba\(\d+,\s*\d+,\s*\d+,\s*\d?\.?\d+\);/);
  assert.match(styleBlock[0], /border:1px solid rgba\(\d+,\s*\d+,\s*\d+,\s*\d?\.?\d+\);/);
  assert.equal(badgeScript.slice(0, badgeScript.indexOf('.federation-download-ring')).includes('gradient'), false);
});

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
  const { dom, calls, requests } = makeWindow(false);
  await settle();
  await settle();
  assert.equal(calls.some((url) => url.endsWith('/Downloads')), false);
  assert.equal(calls.some((url) => url.includes('Sharing/DisabledIds')), false);
  const federated = requests.find((r) => String(r.url).includes('/ClientCatalog'));
  assert.ok(federated);
  assert.equal(federated.options.headers.Authorization, 'MediaBrowser Token="test-token"');
  dom.window.close();
});

test('inventory map endpoints require a Jellyfin login', () => {
  const controller = fs.readFileSync(path.join(root, 'Configuration/FederationPluginController.cs'), 'utf8');
  const federated = controller.match(/\[HttpGet\("FederatedIds"\)\][\s\S]*?public ActionResult<object> GetFederatedIds\(/);
  assert.ok(federated, 'GetFederatedIds not found');
  assert.match(federated[0], /\[Authorize\]/);
  assert.doesNotMatch(federated[0], /AllowAnonymous/);
  assert.doesNotMatch(federated[0], /RequiresElevation/);

  const catalog = controller.match(/\[HttpGet\("ClientCatalog"\)\][\s\S]*?public ActionResult<object> GetClientCatalog\(/);
  assert.ok(catalog, 'GetClientCatalog not found');
  assert.match(catalog[0], /\[Authorize\]/);
  assert.doesNotMatch(catalog[0], /AllowAnonymous/);

  const disabled = controller.match(/\[HttpGet\("Sharing\/DisabledIds"\)\][\s\S]*?public ActionResult<object> GetGloballyDisabledIds\(/);
  assert.ok(disabled, 'GetGloballyDisabledIds not found');
  assert.match(disabled[0], /\[Authorize\(Policy = "RequiresElevation"\)\]/);
  assert.doesNotMatch(disabled[0], /AllowAnonymous/);
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
  // Device downloads are jellyfin-web's own Download entry now (CanDownload).
  assert.equal(labels.includes('Download to device'), false);
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

test('library origin tabs list this server and each federated friend', async () => {
  const { dom } = makeLibraryWindow();
  await settle();
  await settle();
  const tabs = [...dom.window.document.querySelectorAll('.federation-origin-tab')].map((tab) => tab.textContent.trim());
  assert.deepEqual(tabs, ['All', 'This server', 'Friend']);
  assert.equal(dom.window.document.querySelector('.federation-origin-filter').getAttribute('role'), 'tablist');
  assert.equal(dom.window.document.querySelector('.federation-origin-tab[aria-selected="true"]').textContent.trim(), 'All');
  dom.window.close();
});

test('this-server origin tab hides federated cards and tags item queries', async () => {
  const { dom, getItemsCalls, calls } = makeLibraryWindow();
  await settle();
  await settle();
  const friendCard = dom.window.document.querySelector('.card[data-id="' + itemId + '"]');
  const localCard = dom.window.document.querySelector('.card[data-id="' + localItemId + '"]');
  const thisServer = [...dom.window.document.querySelectorAll('.federation-origin-tab')].find((tab) => tab.textContent.trim() === 'This server');
  thisServer.click();
  await settle();
  await settle();
  assert.equal(friendCard.classList.contains('federation-origin-hidden'), true);
  assert.equal(localCard.classList.contains('federation-origin-hidden'), false);
  assert.equal(dom.window.document.querySelector('.federation-origin-tab[aria-selected="true"]').textContent.trim(), 'This server');
  await dom.window.fetch('/Users/abc/Items?ParentId=1');
  assert.ok(calls.some((url) => url.includes('ExcludeItemTags=Federated')));
  await dom.window.ApiClient.getItems('abc', { ParentId: '1' });
  assert.equal(getItemsCalls.at(-1).ExcludeItemTags, 'Federated');
  dom.window.close();
});

test('friend origin tab keeps that friend and hides local titles', async () => {
  const { dom, getItemsCalls } = makeLibraryWindow();
  await settle();
  await settle();
  const friendTab = [...dom.window.document.querySelectorAll('.federation-origin-tab')].find((tab) => tab.textContent.trim() === 'Friend');
  friendTab.click();
  await settle();
  await settle();
  assert.equal(dom.window.document.querySelector('.card[data-id="' + itemId + '"]').classList.contains('federation-origin-hidden'), false);
  assert.equal(dom.window.document.querySelector('.card[data-id="' + localItemId + '"]').classList.contains('federation-origin-hidden'), true);
  await dom.window.ApiClient.getItems('abc', {});
  assert.equal(getItemsCalls.at(-1).Tags, '🌐 Friend');
  dom.window.close();
});

test('origin filter never touches a details page, so a friend\'s show keeps its episodes', async () => {
  const { dom, getItemsCalls, calls } = makeLibraryWindow();
  await settle();
  await settle();
  [...dom.window.document.querySelectorAll('.federation-origin-tab')].find((tab) => tab.textContent.trim() === 'This server').click();
  await settle();
  dom.window.location.hash = '#/details?id=' + itemId;
  await dom.window.fetch('/Users/abc/Items?ParentId=season1');
  assert.equal(calls.at(-1).includes('ExcludeItemTags'), false);
  await dom.window.ApiClient.getItems('abc', { ParentId: 'season1' });
  assert.equal(getItemsCalls.at(-1).ExcludeItemTags, undefined);
  dom.window.document.querySelectorAll('.page').forEach((page) => page.classList.add('hide'));
  const episodes = dom.window.document.createElement('div');
  episodes.className = 'page itemDetailPage';
  episodes.innerHTML = '<div class="itemsContainer"><div class="card" data-id="' + itemId + '"></div></div>';
  dom.window.document.body.appendChild(episodes);
  await new Promise((resolve) => dom.window.setTimeout(resolve, 5));
  dom.window.eval('void 0');
  await settle();
  assert.equal(episodes.querySelector('.card').classList.contains('federation-origin-hidden'), false);
  dom.window.close();
});

test('origin query hooks are in place before the catalog finishes loading', async () => {
  const dom = new JSDOM('<!doctype html><html><body></body></html>', { runScripts: 'outside-only', url: 'http://localhost/web/index.html#/movies?topParentId=1' });
  const calls = [];
  dom.window.sessionStorage.setItem('federation-origin-filter', 'local');
  dom.window.ApiClient = { getCurrentUser: () => new Promise(() => {}), accessToken: () => 't' };
  dom.window.fetch = (url) => {
    calls.push(String(url));
    return new Promise(() => {});
  };
  dom.window.setInterval = () => 0;
  dom.window.requestAnimationFrame = (callback) => dom.window.setTimeout(callback, 0);
  dom.window.eval(badgeScript);
  dom.window.fetch('/Users/abc/Items?ParentId=1');
  assert.ok(calls.at(-1).includes('ExcludeItemTags=Federated'));
  dom.window.close();
});

test('origin filter CSS is a tab strip, not a floating overlay', () => {
  assert.match(badgeScript, /\.federation-origin-filter\{/);
  assert.match(badgeScript, /Library source/);
  assert.match(badgeScript, /This server/);
  assert.equal(badgeScript.includes('ExcludeItemTags'), true);
});

test('admin sessions initialize admin-only download and sharing state', async () => {
  const { dom, calls } = makeWindow(true);
  await settle();
  await settle();
  assert.equal(calls.some((url) => url.endsWith('/Downloads')), true);
  assert.equal(calls.some((url) => url.includes('Sharing/DisabledIds')), true);
  dom.window.close();
});

test('log tab is routable and routine errors use a quiet notice', () => {
  assert.match(configPage, /data-tab="log"/);
  assert.match(configPage, /id="fedPanelLog"/);
  assert.match(configPage, /id="fedLogList"/);
  assert.match(configPage, /function isRoutineNotice/);
  assert.match(configPage, /class="fed-notice"/);
  assert.match(configPage, /\/Plugins\/Federation\/Logs/);
  assert.match(configPage, /loadLogs\(\); \} \}, 10000\)/);
  assert.match(configPage, /fed-log-warning/);
  assert.match(configPage, /fed-log-information/);
  assert.equal(configPage.includes("Last sync skipped an unreachable friend"), true);
});

test('all rendered tabs are routable and the inline configuration script parses', () => {
  const tabNames = [...new Set([...configPage.matchAll(/data-tab="([a-z]+)"/g)].map((match) => match[1]))];
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

test('Plex add-in script parses, has unique ids, and wires every rendered action', () => {
  const scriptMatch = plexPage.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
  assert.ok(scriptMatch);
  assert.doesNotThrow(() => new Function(scriptMatch[1]));

  const dom = new JSDOM(plexPage);
  const ids = [...dom.window.document.querySelectorAll('[id]')].map((element) => element.id);
  assert.equal(new Set(ids).size, ids.length, 'Plex add-in contains duplicate element ids');
  dom.window.close();

  const actions = new Set([...plexPage.matchAll(/data-plex-action="([a-z0-9-]+)"/g)].map((match) => match[1]));
  const cases = new Set([...scriptMatch[1].matchAll(/case '([^']+)'/g)].map((match) => match[1]));
  assert.ok(actions.size > 0);
  assert.deepEqual([...actions].filter((action) => !cases.has(action)), []);
  assert.match(configPage, /data-fed-action="open-plex-addin"/);
  assert.doesNotMatch(configPage, /PlexSetup|PlexOffers|data-fed-action="connect-plex-source"/);
});

test('add-in loader authenticates the named page and initializes its injected scripts', async () => {
  const addinPage = fs.readFileSync(path.join(root, 'Web/admin/addinPage.html'), 'utf8');
  const loaderScript = addinPage.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
  assert.ok(loaderScript);
  const dom = new JSDOM('<!doctype html><html><body></body></html>', {
    runScripts: 'dangerously',
    url: 'http://localhost/web/index.html#!/configurationpage?name=FederationPlex'
  });
  const requests = [];
  const errors = [];
  dom.window.addEventListener('error', (event) => errors.push(event.error || event.message));
  dom.window.ApiClient = { accessToken: () => 'synthetic-browser-token' };
  dom.window.fetch = (url, options) => {
    const address = String(url);
    requests.push({ url: address, options });
    if (address.includes('/AddInPages/FederationPlex')) {
      return Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve(plexPage) });
    }
    let data = {};
    if (address.endsWith('/Configuration')) data = { RemoteServers: [], LibraryMappings: [] };
    if (address.endsWith('/PlexOffers') || address.endsWith('/LocalLibraries')) data = [];
    return Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve(JSON.stringify(data)) });
  };
  dom.window.document.body.innerHTML = addinPage;
  dom.window.eval(loaderScript[1]);
  for (let i = 0; i < 20; i++) { await settle(); }

  const resource = requests.find((entry) => entry.url.includes('/AddInPages/FederationPlex'));
  assert.ok(resource);
  assert.equal(resource.options.headers.Authorization, 'MediaBrowser Token="synthetic-browser-token"');
  assert.equal(resource.options.headers['X-Emby-Token'], undefined);
  assert.ok(dom.window.document.getElementById('federationPlexPage'));
  assert.equal(typeof dom.window.__federationPlexDisposePage, 'function');
  assert.ok(requests.some((entry) => entry.url.endsWith('/PlexSetup/Status')));
  assert.deepEqual(errors, []);
  dom.window.__federationPlexDisposePage();
  dom.window.close();
});

test('Plex add-in keeps credentials in authenticated request bodies and headers, not URLs', () => {
  assert.match(plexPage, /headers\.Authorization = 'MediaBrowser Token="' \+ encodeURIComponent\(token\) \+ '"'/);
  assert.match(plexPage, /response\.status === 401 \|\| response\.status === 403/);
  assert.match(plexPage, /plexMetaCall\('POST', 'Manual', \{ Url: url, Token: token \}\)/);
  assert.match(plexPage, /id="fedPlexManualToken" class="emby-input" type="password"|type="password" id="fedPlexManualToken"/);
  assert.match(plexPage, /id="fedPlexMetaToken" class="emby-input" type="password"|type="password" id="fedPlexMetaToken"/);
  assert.doesNotMatch(plexPage, /[?&](?:api_key|access_token|token)=/i);
  assert.doesNotMatch(plexPage, /X-Plex-Token=/i);
  assert.doesNotMatch(plexPage, /JSON\.parse\(atob/);
});

function configResponseParser() {
  const match = configPage.match(/function readJson\(response\) \{([\s\S]*?)\n {20}\}\n\n {20}function q/);
  assert.ok(match, 'readJson response parser not found');
  return new Function('response', match[1]);
}

test('settings response parser turns JSON validation details into a concise message', async () => {
  const readJson = configResponseParser();
  const response = {
    ok: false,
    status: 400,
    text: () => Promise.resolve('{"error":"Invalid configuration","details":["Plex receiver entries do not need a server address."]}')
  };

  await assert.rejects(
    readJson(response),
    { message: 'Plex receiver entries do not need a server address.' }
  );
});

test('settings response parser hides JSON syntax jargon for plain-text server failures', async () => {
  const readJson = configResponseParser();
  const response = {
    ok: false,
    status: 500,
    text: () => Promise.resolve('Error processing request.')
  };

  await assert.rejects(
    readJson(response),
    { message: 'Jellyfin could not complete this request. Check the server log for details.' }
  );
});

test('settings response parser accepts successful JSON and explains malformed success bodies', async () => {
  const readJson = configResponseParser();
  assert.deepEqual(
    await readJson({ ok: true, status: 200, text: () => Promise.resolve('{"success":true}') }),
    { success: true }
  );
  await assert.rejects(
    readJson({ ok: true, status: 200, text: () => Promise.resolve('not json') }),
    { message: 'Jellyfin returned an unreadable response. Reload the page and try again.' }
  );
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
  assert.match(configPage, /available to reclaim/);
  assert.match(configPage, /selected to reclaim/);
  assert.match(configPage, /Remove selected · /);
  assert.match(configPage, /formatBytes\(qualityBytes\(show\.episodes\)\)/);
  assert.match(configPage, /formatBytes\(qualityBytes\(episodes\)\)/);
  assert.match(configPage, /formatBytes\(ep\.localSizeBytes \|\| 0\)/);
  assert.match(configPage, /reclaimedBytes/);
  assert.match(configPage, /localSizeBytes/);
});

test('download activity always shows live speed and a strip while transferring', () => {
  assert.match(configPage, /fedDownloadLiveStrip/);
  assert.match(configPage, /Transfers resume automatically after a restart/);
  assert.match(configPage, /function formatSpeed\(bytesPerSecond, always\)/);
  assert.match(configPage, /if \(!always && \(bytesPerSecond == null \|\| bytesPerSecond <= 0\)\)/);
  assert.match(configPage, /return '0 KB\/s'/);
  assert.match(configPage, /d\.paused && !d\.isComplete/);
  assert.match(configPage, /setInterval\(function \(\) \{ if \(pageIsVisible\(\)\) \{ loadDownloads\(\); \} \}, 1000\)/);
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

test('Plex friends can be invited into pools; Companion receivers cannot', () => {
  assert.match(configPage, /Plex Companion \/ Federation v/);
  assert.match(configPage, /if \(server\.Kind !== 2\) \{\s*html \+= renderFriendPoolsBlock\(server\);/);
  assert.match(configPage, /\/api\/pools\/invite/);
});

test('library picker merges friend sections into existing Movies and Shows', () => {
  assert.match(configPage, /Checked libraries are added into your existing Movies and Shows folders by type/);
  assert.equal(configPage.includes('Each library you check appears on this server as a virtual library'), false);
  assert.match(configPage, /function localDestinationName\(/);
  assert.match(configPage, /function buildAutoMappings\(\) \{[\s\S]*localDestinationName\(lib\.mediaType\)/);
  assert.match(configPage, /meta\.push\('→ ' \+ localDestinationName\(lib\.mediaType\)\)/);
  assert.equal(configPage.includes("lib.libraryName.trim().toLowerCase() + '|' + lib.mediaType"), false);
});

test('Plex share requests can be accepted from the add-in without pasting a code', () => {
  assert.match(plexPage, /fedPlexOffersIncoming/);
  assert.match(plexPage, /PlexOffers\/' \+ encodeURIComponent\(id\) \+ '\/Accept/);
  assert.match(plexPage, /accept-plex-offer/);
  assert.match(plexPage, /this<\/em> server's public address/);
  assert.match(plexPage, /Funnel opens Plex/);
  assert.match(plexPage, /explicitly choose Plex Remote Access/);
});

test('Plex connect-code posts the raw code to the server-side claim endpoint', () => {
  // Regression: the add-in must not base64-decode the Companion code in the
  // browser and POST {url, token} to ExternalServers as if they were Plex
  // credentials. That saves Companion's Funnel URL + a one-time claim token,
  // which only works when both machines share a Tailscale tailnet. The plugin
  // must claim the code server-to-server instead.
  const fn = plexPage.match(/function connectPlexSource\(\) \{[\s\S]*?\n {20}\}/);
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

function browseHarness(options) {
  return pagingHarness('browse', options);
}

test('Downloads ignores an old server response after the selected server changes', async () => {
  const h = browseHarness();
  h.api.state.serverId = 'A'; h.api.load(true);
  h.api.state.serverId = 'B'; h.api.load(true);
  h.respond(1, [{id:'b',name:'Owned by B',sourceServerId:'B'}], '7');
  await settle();
  h.respond(0, [{id:'a',name:'Owned by A',sourceServerId:'A'}]);
  await settle();
  assert.equal(h.api.state.items.length, 1);
  assert.equal(h.api.state.items[0].id, 'b');
  assert.equal(h.api.state.startIndex, 7);
  h.api.select({id:'a',sourceServerId:'A'});
  assert.equal(h.api.selected().length, 0);
});

test('Downloads library picker cannot be replaced by a slow previous server', async () => {
  const h = browseHarness();
  h.q('#fedBrowseServer').value = 'A'; h.api.changeServer();
  h.q('#fedBrowseServer').value = 'B'; h.api.changeServer();
  h.respond(1, [{id:'b',name:'B library'}]); await settle();
  h.respond(0, [{id:'a',name:'A library'}]); await settle();
  assert.match(h.q('#fedBrowseLibrary').innerHTML, /B library/);
  assert.doesNotMatch(h.q('#fedBrowseLibrary').innerHTML, /A library/);
});

test('catalog and downloads load more via scroll sentinels instead of a Load more button', () => {
  assert.equal(configPage.includes('data-fed-action="catalog-more"'), false);
  assert.equal(configPage.includes('data-fed-action="browse-more"'), false);
  const dom = new JSDOM(configPage);
  for (const id of ['fedCatalogSentinel', 'fedBrowseSentinel']) {
    const sentinel = dom.window.document.getElementById(id);
    assert.ok(sentinel.classList.contains('fed-lazy-sentinel'));
    assert.equal(sentinel.getAttribute('aria-hidden'), 'true');
    assert.equal(dom.window.getComputedStyle(sentinel).height, '1px');
  }
  dom.window.close();
});

function catalogHarness(options) {
  return pagingHarness('catalog', options);
}

function pagingHarness(kind, { fallback = false } = {}) {
  const names = ['loadCatalog', 'armCatalogSentinel', 'observePagingSentinel', 'retryCatalog', 'retryBrowse',
    'loadBrowseItems', 'armBrowseSentinel', 'resetBrowseSeriesState', 'browseSelectionItems',
    'browseServerOffline', 'onBrowseServerChange', 'onBrowseLibraryChange', 'openBrowseSeries', 'closeBrowseSeries',
    'switchTab', 'setDownloadView', 'stopPagePolling', 'pageIsVisible', 'readJson'];
  const source = names.map(name => {
    const start = configPage.indexOf('                    function ' + name + '(');
    assert.notEqual(start, -1, name);
    const end = configPage.indexOf('\n                    }', start) + '\n                    }'.length;
    return configPage.slice(start, end);
  }).join('\n');
  const pending = [], observers = [], nodes = new Map(), listeners = new Map(), timers = new Map();
  let timerId = 0;
  const q = id => {
    if (!nodes.has(id)) nodes.set(id, { value: '', style: {}, innerHTML: '', isConnected: true,
      top: 2000, getClientRects() { return this.isConnected ? [1] : []; },
      getBoundingClientRect() { return { top: this.top, bottom: this.top + 1 }; } });
    return nodes.get(id);
  };
  class FakeIntersectionObserver {
    constructor(callback, options) { this.callback = callback; this.options = options; this.observing = []; observers.push(this); }
    observe(element) { this.observing.push(element); }
    disconnect() { this.disconnected = true; this.observing = []; }
    fire(entries = [{ isIntersecting: true }]) { this.callback(entries); }
  }
  const window = {
    innerHeight: 800,
    setTimeout(callback) { timers.set(++timerId, callback); return timerId; },
    clearTimeout(id) { timers.delete(id); },
    addEventListener(type, callback, capture) { if (!listeners.has(type)) listeners.set(type, new Map()); listeners.get(type).set(callback, capture); },
    removeEventListener(type, callback) { if (listeners.has(type)) listeners.get(type).delete(callback); },
    ...(fallback ? {} : { IntersectionObserver: FakeIntersectionObserver })
  };
  const api = new Function('fedFetch', 'q', 'window', 'kind', `
    var catalogItems = [], catalogQuery = '', catalogType = 'Movie', catalogStartIndex = 0;
    var catalogTotal = 0, catalogLoaded = false, catalogSelected = {}, catalogLoading = false, catalogSentinelObserver = null;
    var catalogRequestEpoch = 0, catalogHasMore = false, catalogFailed = false;
    var browseState = {serverId:'A',libraryId:'lib',mediaType:'Movie',startIndex:0,pageSize:60,items:[],seriesEpisodes:[]};
    var browseRequestEpoch = 0, browseLoading = false, browseSelected = {}, browseSentinelObserver = null, browseFailed = false;
    var activeTab = kind, downloadView = 'select', document = {hidden:false};
    var activePage = {isConnected:true,classList:{contains:()=>false}};
    var downloadsPollInterval, watchingPollInterval, healthPollInterval, logPollInterval, syncStatusPollInterval;
    var TAB_NAMES = ['friends','catalog','browse'], TAB_STORAGE_KEY = 'test', localStorage = {setItem:()=>{}};
    function qa() {return [];} function stopWatchdog() {} function startPairingPoll() {} function stopPairingPoll() {} function loadDownloads() {} function loadLogs() {} function clearLogBadge() {}
    function loadBrowseServers() {} function populateCatalogFriendPick() {}
    function renderCatalogGrid() {} function setCatalogStatus(msg) {q('#fedCatalogStatus').textContent = msg;}
    function renderBrowseList() {} function updateBrowseSelectionBar() {} function setBrowseStatus(msg) {q('#fedBrowseStatus').textContent = msg;}
    function renderSeriesHeader() {} function renderSeriesEpisodes() {}
    function escapeHtml(x) { return x; }
    ${source}
    return {
      load: kind === 'catalog' ? loadCatalog : loadBrowseItems,
      state: kind === 'catalog' ? () => ({items:catalogItems,total:catalogTotal,start:catalogStartIndex,loaded:catalogLoaded,loading:catalogLoading,failed:catalogFailed}) : browseState,
      changeServer:onBrowseServerChange, changeLibrary:onBrowseLibraryChange, selected:browseSelectionItems,
      select: item => {browseSelected[item.id] = item;}, retry:kind === 'catalog' ? retryCatalog : retryBrowse,
      query: (query,type) => {catalogQuery=query;catalogType=type;loadCatalog(true);},
      tab:switchTab, view:setDownloadView, open:openBrowseSeries, back:closeBrowseSeries,
      hide:stopPagePolling, show:() => {activePage={isConnected:true,classList:{contains:()=>false}};switchTab(kind);},
      loading:() => kind === 'catalog' ? catalogLoading : browseLoading
    };
  `)(url => new Promise((resolve, reject) => pending.push({ url, resolve, reject })), q, window, kind);
  return { api, pending, observers, nodes, q, listeners, timers,
    fire() { observers.at(-1).fire(); },
    flush() { const callbacks = [...timers.values()]; timers.clear(); callbacks.forEach(callback => callback()); },
    scroll() { for (const callback of (listeners.get('scroll') || new Map()).keys()) callback(); },
    respond(index, items, totalOrCursor = null, ok = true) {
      const data = kind === 'catalog' ? {totalRecordCount:totalOrCursor,items} : items;
      pending[index].resolve({ok,status:ok ? 200 : 503,headers:{get:()=>kind === 'browse' ? totalOrCursor : null},text:async()=>JSON.stringify(data)});
    }
  };
}

const catalogBatch = (offset, count) => Array.from({ length: count }, (_, i) => ({ id: 'id' + (offset + i), name: 'Item ' + (offset + i) }));

for (const kind of ['catalog', 'browse']) {
  test(kind + ' rearms for multiple bounded pages and stops on exhaustion', async () => {
    const h = pagingHarness(kind);
    h.api.load(true);
    for (let page = 0; page < 3; page++) {
      assert.match(h.pending[page].url, new RegExp('startIndex=' + page * 60 + '&limit=60(?:&|$)'));
      h.respond(page, catalogBatch(page * 60, 60), kind === 'catalog' ? 180 : null);
      await settle();
      if (page < 2 || kind === 'browse') {
        const observer = h.observers.at(-1);
        assert.equal(observer.options.rootMargin, '480px 0px');
        observer.fire([{isIntersecting:false}]);
        assert.equal(h.pending.length, page + 1);
        observer.fire();
        observer.fire();
        assert.equal(h.pending.length, page + 2);
        assert.equal(observer.disconnected, true);
      }
    }
    if (kind === 'browse') { h.respond(3, []); await settle(); }
    const count = h.pending.length;
    h.fire(); h.api.load(false);
    assert.equal(h.pending.length, count);
    assert.equal(h.api.loading(), false);
  });

  test(kind + ' ignores stale success and failure without unlocking the current request', async () => {
    const h = pagingHarness(kind);
    h.api.load(true);
    h.api.load(true);
    h.respond(0, catalogBatch(0, 60), kind === 'catalog' ? 300 : '60');
    await settle();
    assert.equal(h.api.loading(), true);
    assert.equal(h.observers.length, 0);
    h.api.load(false);
    assert.equal(h.pending.length, 2);
    h.api.load(true);
    h.pending[1].reject(new Error('Old failure'));
    await settle();
    assert.equal(h.api.loading(), true);
    h.respond(2, catalogBatch(120, 60), kind === 'catalog' ? 300 : '180');
    await settle();
    const items = kind === 'catalog' ? h.api.state().items : h.api.state.items;
    assert.equal(items[0].id, 'id120');
    assert.equal(items.length, 60);
    assert.equal(h.api.loading(), false);
    assert.equal(h.observers.length, 1);
  });

  test(kind + ' stops after an error until explicit retry, preserving the failed cursor', async () => {
    const h = pagingHarness(kind);
    h.api.load(true);
    h.respond(0, catalogBatch(0, 60), kind === 'catalog' ? 300 : '80');
    await settle(); h.fire();
    const failedUrl = h.pending[1].url;
    h.pending[1].reject(new Error('Offline'));
    await settle();
    h.fire(); h.api.load(false); h.api.tab('friends'); h.api.tab(kind);
    assert.equal(h.pending.length, 2);
    assert.equal(h.q(kind === 'catalog' ? '#fedCatalogRetry' : '#fedBrowseRetry').style.display, '');
    h.api.retry(); h.api.retry();
    assert.equal(h.pending.length, 3);
    assert.equal(h.pending[2].url, failedUrl);
    h.respond(2, catalogBatch(60, 60), kind === 'catalog' ? 300 : '140');
    await settle();
    assert.equal(h.observers.at(-1).disconnected, undefined);
  });

  test(kind + ' pauses on tab and page exit, ignores queued callbacks and rearms on return', async () => {
    const h = pagingHarness(kind);
    h.api.load(true);
    h.respond(0, catalogBatch(0, 60), kind === 'catalog' ? 300 : '60');
    await settle();
    const old = h.observers.at(-1);
    h.api.tab('friends'); old.fire();
    assert.equal(old.disconnected, true);
    assert.equal(h.pending.length, 1);
    h.api.tab(kind); h.fire();
    assert.equal(h.pending.length, 2);
    h.api.hide();
    h.respond(1, catalogBatch(60, 60), kind === 'catalog' ? 300 : '120');
    await settle();
    h.api.show(); h.fire();
    assert.equal(h.pending.length, 3);
    assert.equal(h.pending[2].url, h.pending[1].url);
  });

  test(kind + ' scroll fallback fills short pages, captures nested scrolls and cleans up', async () => {
    const h = pagingHarness(kind, {fallback:true});
    const sentinel = h.q(kind === 'catalog' ? '#fedCatalogSentinel' : '#fedBrowseSentinel');
    h.api.load(true);
    h.respond(0, catalogBatch(0, 60), kind === 'catalog' ? 300 : '60');
    await settle(); h.flush();
    assert.equal(h.pending.length, 1);
    assert.deepEqual([...h.listeners.get('scroll').values()], [true]);
    sentinel.top = 900;
    h.scroll(); h.scroll();
    assert.equal(h.timers.size, 1);
    h.flush();
    assert.equal(h.pending.length, 2);
    assert.equal(h.listeners.get('scroll').size, 0);
    h.respond(1, catalogBatch(60, 60), kind === 'catalog' ? 300 : '120');
    await settle(); h.flush();
    assert.equal(h.pending.length, 3, 'still-visible sentinel continues without another scroll event');
    h.pending[2].reject(new Error('Offline'));
    await settle(); h.scroll(); h.flush();
    assert.equal(h.pending.length, 3);
    h.api.retry();
    h.respond(3, catalogBatch(120, 60), kind === 'catalog' ? 300 : '180');
    await settle();
    h.api.hide(); h.flush();
    assert.equal(h.timers.size, 0);
    assert.equal(h.listeners.get('scroll').size, 0);
    assert.equal(h.listeners.get('resize').size, 0);
  });

  test(kind + ' stops repeated pages without duplicating cards', async () => {
    const h = pagingHarness(kind);
    h.api.load(true);
    h.respond(0, catalogBatch(0, 60), kind === 'catalog' ? 300 : '60');
    await settle(); h.fire();
    h.respond(1, catalogBatch(0, 60), kind === 'catalog' ? 300 : '120');
    await settle(); h.fire(); h.api.load(false);
    assert.equal(h.pending.length, 2);
    assert.equal((kind === 'catalog' ? h.api.state().items : h.api.state.items).length, 60);
  });
}

test('Downloads follows forward cursors through short and empty filtered pages, stopping on no progress', async () => {
  const h = browseHarness();
  h.api.load(true);
  h.respond(0, [{id:'first'}], '80');
  await settle(); h.fire();
  assert.match(h.pending[1].url, /startIndex=80&limit=60/);
  h.respond(1, [], '140');
  await settle(); h.fire();
  assert.match(h.pending[2].url, /startIndex=140&limit=60/);
  h.respond(2, [], '140');
  await settle(); h.fire(); h.api.load(false);
  assert.equal(h.pending.length, 3);
  assert.equal(h.api.state.startIndex, 140);
});

test('Downloads rejects invalid cursors and server mismatches without automatic retries', async () => {
  for (const [items, cursor] of [[[], 'NaN'], [[], '9007199254740992'], [[{id:'wrong',sourceServerId:'B'}], '60']]) {
    const h = browseHarness();
    h.api.load(true); h.respond(0, items, cursor);
    await settle(); h.api.load(false);
    assert.equal(h.pending.length, 1);
    assert.equal(h.api.state.items.length, 0);
    assert.equal(h.q('#fedBrowseRetry').style.display, '');
  }
});

test('Downloads activity and series drill-down disconnect and rearm the list sentinel', async () => {
  const h = browseHarness();
  h.api.load(true); h.respond(0, catalogBatch(0, 60), '60');
  await settle();
  h.api.view('activity'); h.fire();
  assert.equal(h.pending.length, 1);
  h.api.view('select'); h.fire();
  assert.equal(h.pending.length, 2);
  h.api.open(0);
  assert.match(h.pending[2].url, /\/Series\/id0\/Episodes/);
  h.respond(1, catalogBatch(60, 60), '120');
  await settle();
  assert.equal(h.api.state.items.length, 60);
  h.api.back(); h.fire();
  assert.equal(h.pending[3].url, h.pending[1].url);
  h.respond(2, [{id:'late-episode'}]);
  await settle();
  assert.equal(h.api.state.seriesEpisodes.length, 0);
});

test('catalog search and media resets replace in-flight results with the newest query', async () => {
  const h = catalogHarness();
  h.api.load(true);
  h.api.query('new title', 'Series');
  assert.match(h.pending[1].url, /startIndex=0&limit=60&type=Series&query=new%20title/);
  h.respond(1, [{id:'new'}], 1); await settle();
  h.respond(0, catalogBatch(0, 60), 300); await settle();
  assert.deepEqual(h.api.state().items, [{id:'new'}]);
  assert.equal(h.api.state().total, 1);
});

test('catalog lazily loads the next page when the sentinel scrolls into view', async () => {
  const h = catalogHarness();
  h.api.load(true);
  h.respond(0, catalogBatch(0, 60), 120);
  await settle(); await settle();
  assert.equal(h.api.state().items.length, 60);

  assert.equal(h.observers.length, 1, 'sentinel arms after the first full page');
  const sentinel = h.nodes.get('#fedCatalogSentinel');
  assert.deepEqual(h.observers[0].observing, [sentinel]);

  h.observers[0].fire([{ isIntersecting: false }]);
  assert.equal(h.pending.length, 1, 'off-screen sentinel does not fetch');

  h.observers[0].fire([{ isIntersecting: true }]);
  assert.equal(h.pending.length, 2, 'visible sentinel fetches the next page');
  assert.match(h.pending[1].url, /startIndex=60/);

  h.observers[0].fire([{ isIntersecting: true }]);
  assert.equal(h.pending.length, 2, 'in-flight page is not requested twice');

  h.respond(1, catalogBatch(60, 60), 120);
  await settle(); await settle();
  assert.equal(h.api.state().items.length, 120);
  assert.ok(h.observers[0].disconnected, 'observer releases when the list is exhausted');
});

// Jellyfin 12 disables X-Emby-Token by default. Exercise actual browser requests.
test('refresh now reports whether the background sync started and surfaces failures', () => {
  const refresh = configPage.match(/case 'refresh-cache':[\s\S]*?break;/);
  assert.ok(refresh, 'refresh-cache handler not found');
  assert.match(refresh[0], /res\.started/);
  assert.match(refresh[0], /res\.message/);
  assert.match(refresh[0], /showError\('Sync could not start: '/);
  assert.match(refresh[0], /setSaveMsg\('Sync could not start', true\)/);
  assert.match(refresh[0], /loadSyncStatus\(\)/);
});

function pickerHarness() {
  const start = configPage.indexOf('                    function friendlyType(');
  const endStart = configPage.indexOf('                    function buildAutoMappings(');
  assert.notEqual(start, -1, 'friendlyType not found');
  assert.notEqual(endStart, -1, 'buildAutoMappings not found');
  const end = configPage.indexOf('\n                    }', endStart) + '\n                    }'.length;
  const source = configPage.slice(start, end);
  assert.match(source, /retainUnavailableAutoSources/);

  const pending = [];
  const nodes = new Map();
  const q = (id) => {
    if (!nodes.has(id)) {
      nodes.set(id, { value: '', style: {}, innerHTML: '', textContent: '' });
    }
    return nodes.get(id);
  };
  const api = new Function('fedFetch', 'q', `
    var COLLECTION_TYPE_MAP = {
      movies: { mediaType: 'Movie', label: 'Movies' },
      tvshows: { mediaType: 'Series', label: 'TV Shows' }
    };
    var PICKER_TYPES = ['Movie', 'Series'];
    var pickerLibs = [];
    var pickerLoaded = false;
    var pickerUnavailableServerIds = [];
    var currentConfig = { RemoteServers: [], LibraryMappings: [] };
    var localLibrariesCache = [];
    var savedAuto = null;
    var saveCalls = 0;
    function loadLocalLibraries() { return Promise.resolve(localLibrariesCache); }
    function escapeHtml(x) { return String(x); }
    function readJson(r) { return r.json(); }
    function saveConfiguration() { saveCalls += 1; savedAuto = buildAutoMappings(); }
    ${source}
    return {
      get pickerLoaded() { return pickerLoaded; },
      get pickerLibs() { return pickerLibs; },
      get unavailable() { return pickerUnavailableServerIds.slice(); },
      get savedAuto() { return savedAuto; },
      get saveCalls() { return saveCalls; },
      setConfig: function (c) { currentConfig = c; },
      load: loadLibraryPicker,
      mappings: buildAutoMappings
    };
  `)((url) => new Promise((resolve) => pending.push({ url, resolve })), q);

  return {
    api,
    pending,
    q,
    respond(index, body) {
      pending[index].resolve({ ok: true, json: async () => body });
    }
  };
}

function movieMapping(sources) {
  return {
    LocalLibraryName: 'Movies',
    MediaType: 'Movie',
    RemoteServerIds: sources.map((s) => s.ServerId),
    RemoteLibrarySources: sources,
    Enabled: true,
    AutoProvision: true,
    AutoManaged: true
  };
}

test('save keeps auto mappings for a friend whose libraries failed to load', async () => {
  const h = pickerHarness();
  h.api.setConfig({
    RemoteServers: [
      { Id: 'online', Name: 'Online', Enabled: true },
      { Id: 'offline', Name: 'Offline', Enabled: true }
    ],
    LibraryMappings: [movieMapping([
      { ServerId: 'online', ServerName: 'Online', RemoteLibraryId: 'lib-a', RemoteLibraryName: 'Films' },
      { ServerId: 'offline', ServerName: 'Offline', RemoteLibraryId: 'lib-b', RemoteLibraryName: 'Cinema' }
    ])]
  });

  h.api.load();
  assert.equal(h.pending[0].url, '/Plugins/Federation/GetRemoteLibraries');
  h.respond(0, {
    success: true,
    servers: [
      {
        serverId: 'online',
        serverName: 'Online',
        libraries: [{ id: 'lib-a', name: 'Films', collectionType: 'movies', itemCount: 3 }]
      },
      {
        serverId: 'offline',
        serverName: 'Offline',
        error: 'Failed to connect: timeout',
        libraries: []
      }
    ]
  });
  await settle();

  assert.equal(h.api.pickerLoaded, true);
  assert.deepEqual(h.api.unavailable, ['offline']);
  assert.equal(h.api.pickerLibs.length, 1);
  assert.equal(h.api.pickerLibs[0].serverId, 'online');
  assert.equal(h.api.pickerLibs[0].selected, true);

  const sources = h.api.mappings().flatMap((m) => m.RemoteLibrarySources);
  assert.ok(sources.some((s) => s.ServerId === 'online' && s.RemoteLibraryId === 'lib-a'));
  assert.ok(sources.some((s) => s.ServerId === 'offline' && s.RemoteLibraryId === 'lib-b'));

  h.api.pickerLibs[0].selected = false;
  const afterClear = h.api.mappings().flatMap((m) => m.RemoteLibrarySources);
  assert.equal(afterClear.some((s) => s.ServerId === 'online'), false);
  assert.ok(afterClear.some((s) => s.ServerId === 'offline' && s.RemoteLibraryId === 'lib-b'));
});

test('a newly connected friend is not auto-selected; the prompt save keeps offline auto mappings', async () => {
  const h = pickerHarness();
  h.api.setConfig({
    RemoteServers: [
      { Id: 'new', Name: 'New', Enabled: true },
      { Id: 'offline', Name: 'Offline', Enabled: true }
    ],
    LibraryMappings: [movieMapping([
      { ServerId: 'offline', ServerName: 'Offline', RemoteLibraryId: 'lib-b', RemoteLibraryName: 'Cinema' }
    ])]
  });

  let prompted = 0;
  h.api.load(() => { prompted += 1; });
  h.respond(0, {
    success: true,
    servers: [
      {
        serverId: 'new',
        serverName: 'New',
        libraries: [{ id: 'lib-c', name: 'New Films', collectionType: 'movies', itemCount: 1 }]
      },
      {
        serverId: 'offline',
        serverName: 'Offline',
        error: 'Failed to connect: timeout',
        libraries: []
      }
    ]
  });
  await settle();

  // The prompt is shown, but nothing is pulled in or saved until the user chooses.
  assert.equal(prompted, 1);
  assert.equal(h.api.saveCalls, 0);
  assert.equal(h.api.pickerLibs.find((l) => l.serverId === 'new').selected, false);
  const before = h.api.mappings().flatMap((m) => m.RemoteLibrarySources);
  assert.equal(before.some((s) => s.ServerId === 'new'), false);

  // Ticking their library in the prompt and saving adds it without wiping the offline friend.
  h.api.pickerLibs.find((l) => l.serverId === 'new').selected = true;
  const after = h.api.mappings().flatMap((m) => m.RemoteLibrarySources);
  assert.ok(after.some((s) => s.ServerId === 'new' && s.RemoteLibraryId === 'lib-c'));
  assert.ok(after.some((s) => s.ServerId === 'offline' && s.RemoteLibraryId === 'lib-b'));
});

test('new-friend prompt saves what to share and what to receive', async () => {
  const start = configPage.indexOf('                    function finishOnboarding(');
  assert.notEqual(start, -1, 'finishOnboarding not found');
  const end = configPage.indexOf('\n                    }\n', start) + '\n                    }'.length;
  const source = configPage.slice(start, end);

  const checked = (attrs, on) => ({ checked: on, getAttribute: (k) => attrs[k] });
  const boxes = {
    '.fedOnbRecv': [checked({ 'data-idx': '0' }, true), checked({ 'data-idx': '1' }, false)],
    '.fedOnbShare': [checked({ 'data-lib': 'movies' }, true), checked({ 'data-lib': 'music' }, false)]
  };
  const root = { innerHTML: '<x/>', querySelectorAll: (sel) => boxes[sel] || [] };
  const calls = [];
  const state = { saved: 0, loaded: 0, next: 0 };
  const pickerLibs = [{ selected: false }, { selected: true }];
  const server = { Id: 'srv', Name: 'Friend', ExcludedItemIds: ['x'] };

  const api = new Function('q', 'fedFetch', 'readJson', 'showError', 'pickerLibs', 'currentConfig', 'state', `
    var pickerLoaded = true;
    var onboardCurrentId = 'srv';
    function saveConfiguration() { state.saved += 1; }
    function loadConfiguration() { state.loaded += 1; }
    function showNextOnboarding() { state.next += 1; }
    ${source}
    return finishOnboarding;
  `)(
    () => root,
    (url, opts) => { calls.push({ url, body: JSON.parse(opts.body) }); return Promise.resolve({ json: async () => ({ success: true }) }); },
    (r) => r.json(),
    (m) => { throw new Error(m); },
    pickerLibs,
    { RemoteServers: [server] },
    state
  );

  api(true);
  await settle();

  assert.equal(calls.length, 1);
  assert.match(calls[0].url, /Friends\/srv\/Sharing$/);
  assert.deepEqual(calls[0].body, { ShareAll: false, FolderIds: ['movies'], ExcludedItemIds: ['x'] });
  assert.equal(pickerLibs[0].selected, true);
  assert.equal(pickerLibs[1].selected, false);
  assert.equal(state.saved, 1);
  assert.equal(state.next, 1);

  // "Decide later" changes nothing and just moves on.
  api(false);
  await settle();
  assert.equal(calls.length, 1);
  assert.equal(state.saved, 1);
  assert.equal(state.next, 2);
});

test('config page asks about every new friend instead of silently adopting their libraries', () => {
  assert.equal(configPage.includes('autoAdopt'), false);
  assert.match(configPage, /id="fedOnboardRoot"/);
  assert.match(configPage, /data-fed-action="onboard-save"/);
  assert.match(configPage, /data-fed-action="onboard-skip"/);
  assert.match(configPage, /loadLibraryPicker\(function \(\) \{ startOnboarding\(newlyConnectedIds\); \}\)/);
});

test('accepting a friend does not auto-save when the new friend failed to load', async () => {
  const h = pickerHarness();
  h.api.setConfig({
    RemoteServers: [
      { Id: 'new', Name: 'New', Enabled: true },
      { Id: 'online', Name: 'Online', Enabled: true }
    ],
    LibraryMappings: [movieMapping([
      { ServerId: 'online', ServerName: 'Online', RemoteLibraryId: 'lib-a', RemoteLibraryName: 'Films' }
    ])]
  });

  h.api.load();
  h.respond(0, {
    success: true,
    servers: [
      { serverId: 'new', serverName: 'New', error: 'Failed to connect: timeout', libraries: [] },
      {
        serverId: 'online',
        serverName: 'Online',
        libraries: [{ id: 'lib-a', name: 'Films', collectionType: 'movies', itemCount: 3 }]
      }
    ]
  });
  await settle();

  assert.equal(h.api.saveCalls, 0);
  assert.deepEqual(h.api.unavailable, ['new']);
  const sources = h.api.mappings().flatMap((m) => m.RemoteLibrarySources);
  assert.ok(sources.some((s) => s.ServerId === 'online' && s.RemoteLibraryId === 'lib-a'));
  assert.equal(sources.some((s) => s.ServerId === 'new'), false);
});

test('a reachable friend with zero libraries is not treated as a failed fetch', async () => {
  const h = pickerHarness();
  h.api.setConfig({
    RemoteServers: [{ Id: 'empty', Name: 'Empty', Enabled: true }],
    LibraryMappings: [movieMapping([
      { ServerId: 'empty', ServerName: 'Empty', RemoteLibraryId: 'lib-old', RemoteLibraryName: 'Gone' }
    ])]
  });

  h.api.load();
  h.respond(0, {
    success: true,
    servers: [{ serverId: 'empty', serverName: 'Empty', libraries: [] }]
  });
  await settle();

  assert.deepEqual(h.api.unavailable, []);
  assert.equal(h.api.pickerLibs.length, 0);
  assert.equal(h.api.mappings().length, 0);
});

function extractConfigFunction(name) {
  const start = configPage.indexOf('                    function ' + name + '(');
  assert.notEqual(start, -1, name + ' not found');
  const end = configPage.indexOf('\n                    }', start) + '\n                    }'.length;
  return configPage.slice(start, end);
}

function catalogHideUserHarness(options) {
  const source = ['existingUserRule', 'catalogSelectedIds', 'mergeItemIds', 'applyCatalogHideUser']
    .map((name) => extractConfigFunction(name))
    .join('\n');
  const posts = [];
  const nodes = new Map();
  const q = (id) => {
    if (!nodes.has(id)) {
      nodes.set(id, { value: '', style: {}, innerHTML: '', disabled: false, textContent: '' });
    }
    return nodes.get(id);
  };
  q('#fedCatalogFriendPick').value = 'friend-1';
  q('#fedCatalogUserPick').value = 'kid';
  const selected = {};
  (options.selectedIds || ['new-id']).forEach((id) => { selected[id] = true; });
  const api = new Function('fedFetch', 'q', `
    var catalogSelected = ${JSON.stringify(selected)};
    var currentConfig = ${JSON.stringify(options.currentConfig)};
    var remoteUsersCache = { 'friend-1': { users: [{ id: 'kid', name: 'Kiddo' }] } };
    var catalogBlockedItemIdsByUser = ${JSON.stringify(options.cache || {})};
    function setCatalogStatus() {}
    function clearCatalogSelection() { catalogSelected = {}; }
    function loadCatalog() {}
    function loadConfiguration() { return Promise.resolve(); }
    function readJson(r) { return r.json(); }
    ${source}
    return {
      hide: applyCatalogHideUser,
      merge: mergeItemIds,
      cache: function () { return catalogBlockedItemIdsByUser; },
      select: function (ids) {
        catalogSelected = {};
        ids.forEach(function (id) { catalogSelected[id] = true; });
      }
    };
  `)((url, request) => {
    posts.push({ url, body: JSON.parse(request.body) });
    return Promise.resolve({ ok: true, json: async () => ({ success: true, message: 'Saved.' }) });
  }, q);
  return { api, posts };
}

test('catalog hide-from-user unions new ids with GET BlockedItemIds', async () => {
  const h = catalogHideUserHarness({
    selectedIds: ['item-new'],
    currentConfig: {
      RemoteServers: [{
        Id: 'friend-1',
        RemoteUserAccessRules: [{
          RemoteUserId: 'kid',
          Mode: 0,
          LibraryFolderIds: [],
          ItemIds: [],
          MaxAllowedRating: '',
          AllowDownload: true,
          BlockedItemIds: ['item-old']
        }]
      }]
    }
  });
  h.api.hide();
  await settle();
  assert.equal(h.posts.length, 1);
  assert.deepEqual(h.posts[0].body.BlockedItemIds, ['item-old', 'item-new']);
  assert.match(h.posts[0].url, /Friends\/friend-1\/RemoteUserRule/);
});

test('catalog hide-from-user keeps prior ids when GET omits BlockedItemIds', async () => {
  const h = catalogHideUserHarness({
    selectedIds: ['item-new'],
    cache: { 'friend-1::kid': ['item-old'] },
    currentConfig: {
      RemoteServers: [{
        Id: 'friend-1',
        RemoteUserAccessRules: [{
          RemoteUserId: 'kid',
          Mode: 0,
          LibraryFolderIds: [],
          ItemIds: [],
          AllowDownload: true
        }]
      }]
    }
  });
  h.api.hide();
  await settle();
  assert.deepEqual(h.posts[0].body.BlockedItemIds, ['item-old', 'item-new']);

  h.api.select(['item-newer']);
  h.api.hide();
  await settle();
  assert.deepEqual(h.posts[1].body.BlockedItemIds, ['item-old', 'item-new', 'item-newer']);
});

function poolMembershipHarness(success) {
  const source = ['addFriendToPool', 'invitePool'].map(extractConfigFunction).join('\n');
  const fetches = [];
  const nodes = new Map();
  const q = (sel) => {
    if (!nodes.has(sel)) {
      nodes.set(sel, { value: '', style: {}, innerHTML: '', textContent: '' });
    }
    return nodes.get(sel);
  };
  q('.fedPoolPick[data-friend="friend-1"]').value = 'pool-1';
  q('#fedPoolInvite-pool-1').value = 'https://friend.example';
  const api = new Function('fedFetch', 'q', `
    function cssEscape(value) { return value; }
    function setFriendPoolStatus() {}
    function setPoolStatus() {}
    function readJson(r) { return r.json(); }
    var loadPoolsCalls = 0;
    var loadInvitesCalls = 0;
    function loadPools() { loadPoolsCalls += 1; }
    function loadPoolInvites() { loadInvitesCalls += 1; }
    ${source}
    return {
      add: addFriendToPool,
      invite: invitePool,
      counts: function () { return { pools: loadPoolsCalls, invites: loadInvitesCalls }; }
    };
  `)((url, request) => {
    fetches.push({ url, body: request && request.body ? JSON.parse(request.body) : null });
    return Promise.resolve({ ok: true, json: async () => ({ success, message: 'ok' }) });
  }, q);
  return { api, fetches, q };
}

test('adding a friend to a pool and sending an invite refresh pool lists', async () => {
  const ok = poolMembershipHarness(true);
  ok.api.add('friend-1');
  ok.api.invite('pool-1');
  await settle();
  assert.equal(ok.fetches.length, 2);
  assert.match(ok.fetches[0].url, /Pools\/pool-1\/AddFriend/);
  assert.match(ok.fetches[1].url, /Pools\/pool-1\/Invite/);
  assert.deepEqual(ok.api.counts(), { pools: 2, invites: 2 });
  assert.equal(ok.q('#fedPoolInvite-pool-1').value, '');

  const fail = poolMembershipHarness(false);
  fail.api.add('friend-1');
  fail.api.invite('pool-1');
  await settle();
  assert.deepEqual(fail.api.counts(), { pools: 0, invites: 0 });
  assert.equal(fail.q('#fedPoolInvite-pool-1').value, 'https://friend.example');
});

test('badge requests authenticate with the supported Jellyfin 12 header', async () => {
  const { dom, requests } = makeWindow(true);
  await settle();
  await settle();
  assert.ok(requests.length > 0);
  for (const { url, options } of requests) {
    assert.equal(options.headers.Authorization, 'MediaBrowser Token="test-token"');
    assert.equal(options.headers['X-Emby-Token'], undefined);
    assert.ok(!url.includes('test-token'));
  }
  dom.window.close();
});

// ---------------------------------------------------------------------------
// Loading bar for slow federated starts
// ---------------------------------------------------------------------------

function makeLoadingWindow(options = {}) {
  const opts = Object.assign({
    overlay: true,
    estimate: { federated: true, seconds: 70, reason: 'This file is 93 Mbps but the connection to Friend runs at about 8 Mbps.', serverName: 'Friend' },
    fact: { text: 'Peter Falk had a glass eye.' },
    federatedIds: { [itemId]: 'Friend' }
  }, options);
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body></body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html#!/details?id=' + itemId }
  );
  const w = dom.window;
  let now = 1_000_000;
  const timeouts = [];
  const intervals = [];
  let nextId = 1;
  const fetched = [];
  const playbackInfoCalls = [];

  w.Date.now = () => now;
  w.setTimeout = (fn, ms) => { const id = nextId++; timeouts.push({ id, fn, at: now + (ms || 0) }); return id; };
  w.clearTimeout = (id) => { const i = timeouts.findIndex((t) => t.id === id); if (i >= 0) timeouts.splice(i, 1); };
  w.setInterval = (fn) => { const id = nextId++; intervals.push({ id, fn }); return id; };
  w.clearInterval = (id) => { const i = intervals.findIndex((t) => t.id === id); if (i >= 0) intervals.splice(i, 1); };
  w.requestAnimationFrame = (cb) => { cb(); return 1; };
  w.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: false } }),
    accessToken: () => 'test-token',
    getPlaybackInfo: (...args) => { playbackInfoCalls.push(args); return Promise.resolve({ MediaSources: [] }); }
  };
  w.fetch = (url) => {
    const u = String(url);
    fetched.push(u);
    let data = {};
    if (u.includes('ClientCatalog')) data = { items: opts.federatedIds, unavailableIds: [] };
    if (u.includes('ClientSettings')) data = { showFederatedCloudBadges: false, loadingOverlay: opts.overlay };
    if (u.includes('StartEstimate')) data = opts.estimate;
    if (u.includes('FunFact')) data = opts.fact;
    if (u.includes('DisabledIds') || u.endsWith('/Downloads')) data = [];
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  w.eval(badgeScript);

  // Moves the virtual clock forward one second at a time, running due timers and the
  // once-a-second tick, exactly as the browser would.
  function advance(ms) {
    let left = ms;
    while (left > 0) {
      const step = Math.min(1000, left);
      now += step;
      left -= step;
      timeouts.filter((t) => t.at <= now).forEach((t) => {
        timeouts.splice(timeouts.indexOf(t), 1);
        t.fn();
      });
      intervals.slice().forEach((t) => t.fn());
    }
  }

  return {
    dom,
    fetched,
    playbackInfoCalls,
    advance,
    overlay: () => w.document.getElementById('federation-loading-overlay'),
    text: (selector) => { const n = w.document.querySelector('#federation-loading-overlay ' + selector); return n ? n.textContent : null; },
    press: (id = itemId) => w.ApiClient.getPlaybackInfo(id, { UserId: 'u' }),
    addVideo: (state = {}) => {
      const v = w.document.createElement('video');
      Object.defineProperty(v, 'paused', { value: state.paused !== false, configurable: true });
      Object.defineProperty(v, 'currentTime', { value: state.currentTime || 0, configurable: true });
      Object.defineProperty(v, 'readyState', { value: state.readyState || 0, configurable: true });
      w.document.body.appendChild(v);
      return v;
    }
  };
}

async function ready(h) {
  await settle(); await settle(); await settle(); await settle();
  return h;
}

test('loading bar stays hidden for the first 1.5 s, then shows the projection, reason and a fun fact', async () => {
  const h = await ready(makeLoadingWindow());
  h.press();
  await settle(); await settle();
  assert.equal(h.overlay(), null);

  h.advance(1500);
  await settle(); await settle();

  assert.ok(h.overlay());
  assert.match(h.text(".fl-time"), /^Starting in about 1 min \d+ s$/);
  assert.match(h.text('.fl-reason'), /93 Mbps/);
  assert.equal(h.text('.fl-fact'), 'Fun fact: Peter Falk had a glass eye.');
});

test('loading bar counts down, then admits it is taking longer than projected', async () => {
  const h = await ready(makeLoadingWindow({ estimate: { federated: true, seconds: 30, reason: 'r', serverName: 'Friend' } }));
  h.press();
  h.advance(1500); await settle(); await settle();
  h.addVideo();
  assert.match(h.text('.fl-time'), /^Starting in about 2\d s$/);

  h.advance(10000);
  assert.match(h.text('.fl-time'), /^Starting in about 1\d s$/);

  h.advance(25000);
  assert.match(h.text('.fl-time'), /Almost there/);
  const fill = h.dom.window.document.querySelector('#federation-loading-overlay .fl-fill');
  assert.ok(parseFloat(fill.style.width) <= 95);
});

test('loading bar formats long waits in minutes', async () => {
  const h = await ready(makeLoadingWindow({ estimate: { federated: true, seconds: 130, reason: 'r', serverName: 'Friend' } }));
  h.press();
  h.advance(1500); await settle(); await settle();
  assert.match(h.text('.fl-time'), /^Starting in about 2 min \d+ s$/);
});

test('loading bar disappears as soon as the video is actually playing', async () => {
  const h = await ready(makeLoadingWindow());
  h.press();
  const video = h.addVideo();
  h.advance(1500); await settle(); await settle();
  assert.ok(h.overlay());

  video.dispatchEvent(new h.dom.window.Event('playing'));

  assert.equal(h.overlay(), null);
});

test('loading bar is never shown when video is already playing at the 1.5 s mark', async () => {
  const h = await ready(makeLoadingWindow());
  h.press();
  h.addVideo({ paused: false, currentTime: 3, readyState: 4 });
  h.advance(1500); await settle(); await settle();
  assert.equal(h.overlay(), null);
});

test('loading bar is removed when the player is closed before playback starts', async () => {
  const h = await ready(makeLoadingWindow());
  h.press();
  const video = h.addVideo();
  h.advance(2000); await settle(); await settle();
  assert.ok(h.overlay());

  video.remove();
  h.advance(1000);

  assert.equal(h.overlay(), null);
});

test('loading bar gives up if no player ever appears', async () => {
  const h = await ready(makeLoadingWindow());
  h.press();
  h.advance(2000); await settle(); await settle();
  assert.ok(h.overlay());

  h.advance(30000);

  assert.equal(h.overlay(), null);
});

test('loading bar ignores titles that are not federated, without asking the server for an estimate', async () => {
  const h = await ready(makeLoadingWindow());
  h.press('22222222222222222222222222222222');
  h.advance(3000); await settle(); await settle();
  assert.equal(h.overlay(), null);
  assert.equal(h.fetched.filter((u) => u.includes('StartEstimate')).length, 0);
});

test('loading bar can be switched off by the server setting', async () => {
  const h = await ready(makeLoadingWindow({ overlay: false }));
  h.press();
  h.advance(3000); await settle(); await settle();
  assert.equal(h.overlay(), null);
  assert.equal(h.fetched.filter((u) => u.includes('StartEstimate')).length, 0);
});

test('loading bar without a measurement shows an indeterminate bar, not a made-up number', async () => {
  const h = await ready(makeLoadingWindow({ estimate: { federated: true, seconds: null, reason: 'Speed not measured yet.', serverName: 'Friend' } }));
  h.press();
  h.advance(1500); await settle(); await settle();
  h.addVideo();
  assert.match(h.text('.fl-time'), /^Getting things ready/);
  assert.ok(h.dom.window.document.querySelector('#federation-loading-overlay .fl-fill').classList.contains('fl-indeterminate'));
});

test('loading bar shows server text as text, never as markup', async () => {
  const hostile = '<img src=x onerror="window.__pwned=1">';
  const h = await ready(makeLoadingWindow({
    estimate: { federated: true, seconds: 20, reason: hostile, serverName: 'Friend' },
    fact: { text: hostile }
  }));
  h.press();
  h.advance(1500); await settle(); await settle();
  assert.equal(h.dom.window.document.querySelectorAll('#federation-loading-overlay img').length, 0);
  assert.equal(h.text('.fl-reason'), hostile);
  assert.equal(h.text('.fl-fact'), 'Fun fact: ' + hostile);
});

test('repeated playback-info requests for one play keep a single bar and a single estimate request', async () => {
  const h = await ready(makeLoadingWindow());
  h.press(); h.press(); h.press();
  h.advance(1500); await settle(); await settle();
  assert.equal(h.dom.window.document.querySelectorAll('#federation-loading-overlay').length, 1);
  assert.equal(h.fetched.filter((u) => u.includes('StartEstimate')).length, 1);
});

test('loading bar rotates the fun fact every 12 seconds', async () => {
  const h = await ready(makeLoadingWindow());
  h.press();
  h.addVideo();
  h.advance(1500); await settle(); await settle();
  const before = h.fetched.filter((u) => u.includes('FunFact')).length;
  h.advance(12000); await settle();
  assert.ok(h.fetched.filter((u) => u.includes('FunFact')).length > before);
});

test('loading bar never blocks the player and leaves the original playback request untouched', async () => {
  const h = await ready(makeLoadingWindow());
  const result = h.press();
  assert.equal(h.playbackInfoCalls.length, 1);
  assert.equal(h.playbackInfoCalls[0][0], itemId);
  assert.deepEqual(await result, { MediaSources: [] });

  h.advance(1500); await settle(); await settle();
  const style = h.dom.window.document.getElementById('federation-loading-style').textContent;
  assert.match(style, /#federation-loading-overlay\{[^}]*pointer-events:none/);
});

test('a failing estimate or fun-fact request still leaves a working bar', async () => {
  const h = await ready(makeLoadingWindow());
  h.dom.window.fetch = () => Promise.reject(new Error('offline'));
  h.press();
  h.addVideo();
  h.advance(1500); await settle(); await settle();
  assert.ok(h.overlay());
  assert.match(h.text('.fl-time'), /^Getting things ready/);
});

test('settings page exposes the loading bar and fast-start switches and wires them to load and save', () => {
  for (const [id, property] of [
    ['fedChkLoadingOverlay', 'LoadingOverlay'],
    ['fedChkLoadingFunFacts', 'LoadingFunFacts'],
    ['fedChkFastStart', 'FastStartProbing']
  ]) {
    assert.match(configPage, new RegExp('<input type="checkbox" id="' + id + '"'), id + ' checkbox is on the page');
    assert.match(configPage, new RegExp("q\\('#" + id + "'\\)\\.checked = currentConfig\\." + property + ' !== false'), property + ' is loaded, defaulting on');
    assert.match(configPage, new RegExp(property + ": q\\('#" + id + "'\\)\\.checked"), property + ' is saved');
  }
});

test('the fun-fact setting says plainly that nothing about the viewer or their media is sent', () => {
  assert.match(configPage, /sends nothing about you or your media/);
});

test('admin page and web scripts use solid colours only (owner preference: no gradients)', () => {
  assert.doesNotMatch(configPage, /gradient\(/i);
  assert.doesNotMatch(badgeScript, /gradient\(/i);
});

test('admin tab row is one scrollable row and switchTab keeps the active tab in view', () => {
  const tabRules = configPage.match(/#federationConfigPage \.fed-tabs \{[^}]*\}/g);
  assert.equal(tabRules.length, 1, 'exactly one .fed-tabs rule');
  assert.match(tabRules[0], /flex-wrap:\s*nowrap/);
  assert.match(tabRules[0], /overflow-x:\s*auto/);
  assert.equal((configPage.match(/\.fed-tab-btn\.fed-tab-btn-active \{ opacity/g) || []).length, 1, 'one active-tab rule outside forced-colors');
  assert.ok(!configPage.includes('gradient('));

  const start = configPage.indexOf('                    function switchTab(');
  const end = configPage.indexOf('\n                    }', start) + '\n                    }'.length;
  const dom = new JSDOM('<div id="p"><button class="fed-tab-btn fed-tab-btn-active" data-tab="friends"></button>' +
    '<button class="fed-tab-btn" data-tab="log"></button><div class="fed-tabpanel" data-tab-panel="log"></div></div>');
  const doc = dom.window.document;
  const calls = [];
  doc.querySelectorAll('.fed-tab-btn').forEach(btn => {
    btn.scrollIntoView = function (opts) { calls.push([this.getAttribute('data-tab'), opts]); };
  });
  const switchTab = new Function('document', `
    var TAB_NAMES = ['friends','log'], activeTab = 'friends', catalogLoaded = true, catalogLoading = false, catalogFailed = false;
    var downloadView = 'select', TAB_STORAGE_KEY = 'k', localStorage = {setItem(){}};
    function q(s) { return document.querySelector(s); }
    function qa(s) { return Array.prototype.slice.call(document.querySelectorAll(s)); }
    function armCatalogSentinel() {} function armBrowseSentinel() {} function noop() {}
    var loadDownloads = noop, loadLogs = noop, clearLogBadge = noop, loadBrowseServers = noop, populateCatalogFriendPick = noop,
      loadCatalog = noop, stopWatchdog = noop, startPairingPoll = noop, stopPairingPoll = noop, setDownloadView = noop, loadBrowseItems = noop;
    ${configPage.slice(start, end)}
    return switchTab;
  `)(doc);
  try { switchTab('log'); } catch (e) { /* later lazy-load steps may need more globals */ }
  assert.equal(doc.querySelector('.fed-tab-btn-active').getAttribute('data-tab'), 'log');
  assert.deepEqual(calls, [['log', { block: 'nearest', inline: 'nearest' }]]);
  dom.window.close();
});

function plexGuidedImport() {
  const block = plexPage.match(/(var plexStateText = \{[\s\S]*?)\n {20}function loadConfiguration/);
  assert.ok(block, 'guided Plex import script not found');
  const dom = new JSDOM(plexPage);
  const doc = dom.window.document;
  const posts = [];
  const helpers = {
    q: (sel) => doc.querySelector(sel),
    qa: (sel) => Array.from(doc.querySelectorAll(sel)),
    escapeHtml: (v) => String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'),
    setFedStatus: () => {},
    setPlexStep: () => {},
    loadPlexSetup: () => {},
    plexMetaCall: (method, apiPath, body) => { posts.push({ method, apiPath, body }); return Promise.resolve({ Results: [] }); }
  };
  const state = 'var plexImportPlanData = null; var plexMapRows = [];';
  const api = new Function(...Object.keys(helpers), state + block[1] + '\nreturn { renderPlexPlan, plexImport, renderPlexMapRows, setRows: (r) => { plexMapRows = r; }, updatePlexImportButton };');
  return { doc, posts, ...api(...Object.values(helpers)) };
}

test('guided Plex import lists each library, escapes Plex names, and only offers usable ones', () => {
  const ui = plexGuidedImport();
  ui.renderPlexPlan({ Sections: [
    { Key: '1', Title: '<img src=x onerror=alert(1)>', Type: 'movie', State: 'Ready', Folders: [{ PlexPath: 'D:\\Movies', LocalPath: '/mnt/movies', Found: true }] },
    { Key: '2', Title: 'TV', Type: 'show', State: 'Missing', Folders: [{ PlexPath: '/tv', LocalPath: '/tv', Found: false }] },
    { Key: '3', Title: 'Kids', Type: 'movie', State: 'Linked', LibraryName: 'Family', Folders: [{ PlexPath: '/kids', LocalPath: '/kids', Found: true }] }
  ] });

  assert.equal(ui.doc.querySelector('#fedPlexImportList img'), null);
  const picks = [...ui.doc.querySelectorAll('.fedPlexImportPick')];
  assert.deepEqual(picks.map((c) => [c.getAttribute('data-value'), c.checked, c.disabled]), [['1', true, false], ['2', false, true], ['3', true, false]]);
  assert.equal(ui.doc.querySelector('#fedPlexImportBtn').textContent, 'Bring 2 libraries into Jellyfin');
  assert.ok(ui.doc.querySelector('#fedPlexMapBox').open, 'folder rewrites open when a library is missing');
  assert.match(ui.doc.querySelector('#fedPlexImportList').textContent, /D:\\Movies → \/mnt\/movies/);
  assert.match(ui.doc.querySelector('#fedPlexImportIntro').textContent, /1 of your Plex libraries/);

  picks[0].checked = false;
  ui.updatePlexImportButton();
  assert.equal(ui.doc.querySelector('#fedPlexImportBtn').textContent, 'Bring 1 library into Jellyfin');
});

test('guided Plex import posts the chosen libraries with only complete folder rewrites', () => {
  const ui = plexGuidedImport();
  ui.setRows([{ From: 'D:\\Media', To: '/mnt/media' }, { From: '/half', To: '' }]);
  ui.renderPlexMapRows();
  ui.renderPlexPlan({ Sections: [{ Key: '7', Title: 'Movies', Type: 'movie', State: 'Ready', Folders: [] }] });
  ui.plexImport();

  const post = ui.posts.find((p) => p.apiPath === 'Import');
  assert.deepEqual(post.body, { SectionKeys: ['7'], PathMappings: [{ From: 'D:\\Media', To: '/mnt/media' }] });
});

function syncStatusFunctions() {
  const block = configPage.match(/\/\* sync-status:start \*\/([\s\S]*?)\/\* sync-status:end \*\//);
  assert.ok(block, 'sync status block not found');
  const helpers = ['escapeHtml', 'formatBytes'].map((name) => {
    const fn = configPage.match(new RegExp('function ' + name + '\\([\\s\\S]*?\\n {20}\\}\\n'));
    assert.ok(fn, name + ' not found');
    return fn[0];
  }).join('\n');
  return new Function('document', helpers + block[1] + '\nreturn { describeLibrarySync, renderSyncStatusHtml, syncRunSummary };')(new JSDOM('').window.document);
}

test('sync status shows per-library plan, progress, outcome and plain-language failures', () => {
  const { describeLibrarySync, renderSyncStatusHtml, syncRunSummary } = syncStatusFunctions();
  const now = Date.parse('2026-10-16T12:00:00Z');
  const base = { key: 'k', serverName: 'Anna', remoteLibraryName: 'Movies', localLibraryName: 'Movies', toRemove: 0, bytesReceived: 0 };

  const downloading = describeLibrarySync({ ...base, state: 'downloading', mode: 'changes', total: 12400, toDownload: 230, downloaded: 115, toRemove: 3 }, now);
  assert.equal(downloading.label, 'Downloading 115 of 230');
  assert.equal(downloading.progress, 0.5);
  assert.deepEqual(downloading.facts, ['12,400 items', '230 to download', '3 to remove']);

  const done = describeLibrarySync({ ...base, state: 'done', mode: 'changes', total: 12400, toDownload: 0, downloaded: 0, bytesReceived: 2048, lastSuccessUtc: '2026-10-16T11:55:00Z' }, now);
  assert.equal(done.label, 'Up to date');
  assert.equal(done.tone, 'ok');
  assert.deepEqual(done.facts, ['12,400 items', 'no changes', '2.0 KB received', 'last synced 5 min ago']);
  const doneWithWork = describeLibrarySync({ ...base, state: 'done', mode: 'changes', total: 50, toDownload: 7, downloaded: 7, toRemove: 2, removed: 2 }, now);
  assert.deepEqual(doneWithWork.facts, ['50 items', '7 downloaded', '2 removed']);

  const failed = describeLibrarySync({ ...base, state: 'failed', error: "Couldn't reach Anna, or it didn't answer as a Federation server. Titles already here were kept." }, now);
  assert.equal(failed.tone, 'bad');
  assert.match(failed.error, /Couldn't reach Anna/);
  assert.ok(failed.facts.includes('never synced successfully'));

  const legacy = describeLibrarySync({ ...base, state: 'downloading', mode: 'legacy', downloaded: 400, toDownload: 0 }, now);
  assert.equal(legacy.label, 'Downloading all titles · 400 so far');
  assert.match(legacy.note, /older Federation version/);

  const checking = describeLibrarySync({ ...base, state: 'checking', toDownload: 0, downloaded: 0 }, now);
  assert.equal(checking.label, 'Checking what Anna has…');

  const ready = describeLibrarySync({ ...base, state: 'ready', mode: 'changes', total: 50, toDownload: 4, downloaded: 0 }, now);
  assert.equal(ready.label, 'Checked · queued');
  assert.deepEqual(ready.facts, ['50 items', '4 to download']);
  assert.equal(describeLibrarySync({ ...base, state: 'ready', mode: 'changes', total: 50, toDownload: 0, downloaded: 0 }, now).label, 'Checked · no changes');

  const waiting = describeLibrarySync({ ...base, state: 'done', mode: 'changes', total: 50, toDownload: 0, downloaded: 0, waitingRemovals: 3 }, now);
  assert.match(waiting.note, /^3 titles are missing on Anna's server and will be removed at the next sync/);
  const heldLib = { ...base, state: 'done', mode: 'changes', total: 500, toDownload: 0, downloaded: 0, heldRemovals: 120 };
  assert.match(describeLibrarySync(heldLib, now).held, /^120 titles are no longer on Anna's server/);
  const heldHtml = renderSyncStatusHtml({ libraries: [heldLib] }, now);
  assert.match(heldHtml, /data-fed-action="confirm-removal" data-sync-key="k" data-count="120">Remove 120 titles<\/button>/);

  const html = renderSyncStatusHtml({ libraries: [{ ...base, serverName: '<b>Anna</b>', state: 'done', mode: 'changes', total: 1, toDownload: 0, downloaded: 0 }] }, now);
  assert.ok(html.includes('&lt;b&gt;Anna&lt;/b&gt;'), 'friend names are escaped');
  assert.match(renderSyncStatusHtml({ libraries: [] }, now), /No friend libraries are selected yet/);

  // One full resync button per local library, even when several friends feed it.
  const merged = renderSyncStatusHtml({ libraries: [
    { ...base, key: 'a', state: 'done', mode: 'changes', total: 1, toDownload: 0, downloaded: 0 },
    { ...base, key: 'b', serverName: 'Ben', state: 'done', mode: 'changes', total: 1, toDownload: 0, downloaded: 0 },
    { ...base, key: 'c', localLibraryName: 'Music', state: 'done', mode: 'changes', total: 1, toDownload: 0, downloaded: 0 }
  ] }, now);
  assert.equal((merged.match(/data-fed-action="full-resync"/g) || []).length, 2);
  assert.match(merged, /data-fed-action="full-resync" data-library="Movies">Full resync of Movies<\/button>/);
  assert.match(merged, /data-library="Music">Full resync of Music</);
  assert.match(renderSyncStatusHtml({ running: true, libraries: [{ ...base, state: 'downloading', mode: 'changes', toDownload: 2, downloaded: 1 }] }, now), /data-fed-action="full-resync" data-library="Movies" disabled/);

  assert.equal(
    syncRunSummary({ libraries: [
      { ...base, state: 'done', toDownload: 10, downloaded: 10 },
      { ...base, state: 'downloading', toDownload: 30, downloaded: 5 }
    ] }),
    'Syncing · 1 of 2 libraries finished · 15 of 40 titles downloaded');
});

test('refresh starts a background sync and the page polls per-library status', () => {
  assert.match(configPage, /\/Plugins\/Federation\/Refresh\?background=true/);
  assert.match(configPage, /\/Plugins\/Federation\/Sync\/Status/);
  assert.match(configPage, /id="fedSyncStatusList"/);
  assert.match(configPage, /clearInterval\(syncStatusPollInterval\)/);
});

test('friend requests choose shared libraries before sending or accepting', () => {
  assert.match(configPage, /data-fed-action="new-friend-open"[^>]*>Create new friend request</);
  assert.match(configPage, /id="fedNewFriend" class="fed-subcard" style="display:none;"/);
  const send = configPage.match(/function sendFriendRequest\(\) \{[\s\S]*?\n {20}\}/);
  assert.ok(send, 'sendFriendRequest not found');
  assert.match(send[0], /readShareChooser\('new'\)/);
  assert.match(send[0], /ShareAll: sharing\.ShareAll, FolderIds: sharing\.FolderIds/);
  const accept = configPage.match(/function acceptFriendRequest\(id\) \{[\s\S]*?\n {20}\}/);
  assert.ok(accept, 'acceptFriendRequest not found');
  assert.match(accept[0], /readShareChooser\('accept-' \+ id\)/);
  assert.match(configPage, /data-fed-action="accept-friend-choose"/);
  assert.match(configPage, /function addDiscoveredFriend\(url\) \{[\s\S]*?showNewFriendDialog\(true, url\)/);

  const chooser = configPage.match(/function renderShareChooser\(group\) \{[\s\S]*?\n {20}\}/);
  const reader = configPage.match(/function readShareChooser\(group\) \{[\s\S]*?\n {20}\}/);
  const escape = configPage.match(/function escapeHtml\([\s\S]*?\n {20}\}\n/);
  const fns = new Function('document', 'var shareChoices = {}; var localLibrariesCache = [{ id: "m", name: "Movies" }, { id: "s", name: "Shows" }];\n'
    + escape[0] + chooser[0] + reader[0] + '\nreturn { renderShareChooser, readShareChooser, shareChoices };')(new JSDOM('').window.document);
  const html = fns.renderShareChooser('new');
  assert.equal((html.match(/ checked/g) || []).length, 0, 'nothing is shared by default');
  assert.match(html, /Share all my libraries, including ones added later/);
  assert.match(html, /data-lib="m"[^>]*> Movies/);
  assert.deepEqual(fns.readShareChooser('new'), { ShareAll: false, FolderIds: [] });
  fns.shareChoices['new'].FolderIds.push('s');
  assert.deepEqual(fns.readShareChooser('new'), { ShareAll: false, FolderIds: ['s'] });
});

test('custom library types have readable names, and music is recognisable', () => {
  assert.match(configPage, /MusicAlbum: 'Music \(albums with songs\)'/);
  assert.match(configPage, /Audio: 'Music \(songs, with their albums\)'/);
  assert.match(configPage, /escapeHtml\(CUSTOM_TYPE_LABELS\[t\] \|\| t\)/);
});

function makeFavoritesWindow(sections, items, userId = 'user-1') {
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body>'
      + '<div id="favoritesTab" class="tabContent pageTabContent is-active"><div class="sections"><div class="verticalSection">native</div></div></div>'
      + '</body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html#/home?tab=1' }
  );
  const calls = [];
  const itemQueries = [];
  let currentUser = userId;
  dom.window.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: false } }),
    getCurrentUserId: () => currentUser,
    serverId: () => 'srv',
    accessToken: () => 'test-token',
    getScaledImageUrl: (id, opts) => '/Items/' + id + '/Images/Primary?tag=' + opts.tag,
    getItems: (uid, query) => {
      itemQueries.push({ uid, query });
      return Promise.resolve({ Items: items });
    }
  };
  dom.window.fetch = (url) => {
    calls.push(String(url));
    let data = {};
    if (String(url).includes('ClientCatalog')) data = { items: {}, unavailableIds: [] };
    if (String(url).includes('ClientSettings')) data = { showFederatedCloudBadges: false };
    if (String(url).includes('FriendFavorites')) data = { enabled: true, sections };
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  dom.window.setInterval = () => 0;
  dom.window.requestAnimationFrame = (callback) => dom.window.setTimeout(callback, 0);
  dom.window.eval(badgeScript);
  return { dom, calls, itemQueries, setUser: (id) => { currentUser = id; } };
}

async function settleMany(times = 8) {
  for (let i = 0; i < times; i++) { await settle(); }
}

test("friends' favorites appear as one row per friend below the native favorites", async () => {
  const items = [
    { Id: 'aaa', Name: 'Dune <Part Two>', Type: 'Movie', ProductionYear: 2024, ImageTags: { Primary: 't1' } },
    { Id: 'bbb', Name: 'Pilot', Type: 'Episode', SeriesName: 'Show', ImageTags: {} },
    { Id: 'ccc', Name: 'Song', Type: 'Audio', AlbumArtist: 'Band', ImageTags: { Primary: 't3' } }
  ];
  const { dom, calls, itemQueries } = makeFavoritesWindow([
    { serverId: 'f1', name: 'friend-a', itemIds: ['bbb', 'aaa'] },
    { serverId: 'f2', name: 'slow-peer', itemIds: ['ccc', 'gone'] },
    { serverId: 'f3', name: 'Empty', itemIds: ['gone'] }
  ], items);
  await settleMany();

  assert.ok(calls.some((url) => url.endsWith('/Plugins/Federation/FriendFavorites')));
  assert.equal(itemQueries.length, 1);
  assert.equal(itemQueries[0].uid, 'user-1');
  const doc = dom.window.document;
  const tab = doc.getElementById('favoritesTab');
  assert.equal(tab.querySelector('.sections').textContent, 'native');
  const sections = [...tab.querySelectorAll('.federation-favorites-section')];
  assert.deepEqual(sections.map((s) => s.querySelector('.sectionTitle').textContent), ['Favorites on friend-a', 'Favorites on slow-peer']);
  const first = [...sections[0].querySelectorAll('.federation-fav-card')];
  assert.deepEqual(first.map((a) => a.getAttribute('data-id')), ['bbb', 'aaa']);
  assert.equal(first[1].getAttribute('href'), '#/details?id=aaa&serverId=srv');
  assert.equal(first[1].querySelector('.federation-fav-title').textContent, 'Dune <Part Two>');
  assert.equal(first[1].querySelector('img').getAttribute('src'), '/Items/aaa/Images/Primary?tag=t1');
  assert.ok(first[0].classList.contains('federation-fav-backdrop'));
  assert.ok(first[0].querySelector('.federation-fav-noart'));
  assert.equal(first[0].querySelector('.federation-fav-sub').textContent, 'Show');
  assert.ok(sections[1].querySelector('.federation-fav-square'));
  assert.equal(tab.querySelector('part'), null);
  dom.window.close();
});

test("friends' favorites stay empty and quiet when there is nothing to show", async () => {
  const { dom, itemQueries } = makeFavoritesWindow([], []);
  await settleMany();
  assert.equal(itemQueries.length, 0);
  assert.equal(dom.window.document.querySelectorAll('.federation-favorites-section').length, 0);
  dom.window.close();
});

test("friends' favorites rows use solid colours only", () => {
  const css = badgeScript.slice(badgeScript.indexOf('.federation-favorites-row'), badgeScript.indexOf("].join('');"));
  assert.ok(css.length > 0);
  assert.equal(/gradient/i.test(css), false);
});

test("admin page has the friends' favorites switches wired to their own settings route", () => {
  assert.ok(configPage.includes('id="fedChkShowFriendsFavorites"'));
  assert.ok(configPage.includes('id="fedChkShareFavoritesWithFriends"'));
  assert.ok(configPage.includes('data-fed-action="save-favorites"'));
  assert.match(configPage, /case 'save-favorites':\s*saveFavoritesSettings\(\);/);
  assert.match(configPage, /fedFetch\('\/Plugins\/Federation\/Settings\/Favorites', \{\s*method: 'POST'/);
  assert.match(configPage, /loadRemoteTranscodeSettings\(\);\s*loadFavoritesSettings\(\);/);
});

test('admin page has the direct link switch, per-friend mode and retry wired to their own routes', () => {
  assert.ok(configPage.includes('id="fedChkEnableDirectLink"'));
  // Opt-in and labelled beta: a direct link shows friends this home's address.
  assert.match(configPage, /<input type="checkbox" id="fedChkEnableDirectLink" class="emby-checkbox fed-check" \/>/);
  assert.match(configPage, /Direct links with friends <span class="fed-pill fed-pill-off">Beta<\/span>/);
  assert.match(configPage, /chk\.checked = res\.Enabled === true;/);
  assert.ok(configPage.indexOf('id="fedDirectLinkCard"') > configPage.indexOf('id="fedPanelBeta"'), 'the switch sits on the Beta tab');
  assert.ok(configPage.indexOf('id="fedDirectLinkCard"') < configPage.indexOf('id="fedPanelAdvanced"'), 'the card is inside the Beta panel');
  assert.match(configPage, /case 'save-direct-link':\s*saveDirectLinkSettings\(\);/);
  assert.match(configPage, /case 'direct-link-retry':\s*retryDirectLink\(t\.getAttribute\('data-id'\)\);/);
  assert.match(configPage, /fedFetch\('\/Plugins\/Federation\/Settings\/DirectLink', \{\s*method: 'POST'/);
  assert.match(configPage, /'\/Plugins\/Federation\/Friends\/' \+ encodeURIComponent\(serverId\) \+ '\/DirectLink'/);
  assert.match(configPage, /fedFetch\('\/Plugins\/Federation\/DirectLink\/Status'\)/);
  assert.match(configPage, /classList\.contains\('fedDirectLinkMode'\)/);
  // Re-rendering the Friends list keeps the last status instead of "Checking…".
  assert.match(configPage, /c\.innerHTML = html;\s*renderFriendHealth\(\);\s*applyDirectLinkStatus\(\);/);
  assert.match(configPage, /if \(s\.Id === serverId\) \{ s\.DirectLinkMode = mode; \}/);
});

function makeSyncPlayMenu(menuIds, status, groups = []) {
  const items = menuIds.map((id) => '<button class="listItem actionSheetMenuItem" data-id="' + id + '">' + id + '</button>').join('');
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body>'
      + '<div class="dialog syncPlayGroupMenu"><h1 class="actionSheetTitle">qa-admin\'s group</h1><div class="actionSheetScroller">' + items + '</div></div>'
      + '</body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html#/home' }
  );
  const posts = [];
  dom.window.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: false } }),
    getCurrentUserId: () => 'u1',
    accessToken: () => 'test-token',
    getSyncPlayGroups: () => Promise.resolve({ json: () => Promise.resolve(groups) })
  };
  dom.window.fetch = (url, options) => {
    const u = String(url);
    let data = {};
    if (u.includes('ClientCatalog')) data = { items: {}, unavailableIds: [] };
    if (u.includes('ClientSettings')) data = { showFederatedCloudBadges: false };
    if (u.includes('/Party/Status/')) data = status;
    if (options && options.method === 'POST') { posts.push({ url: u, body: JSON.parse(options.body || '{}') }); data = { success: true, message: 'Friends can join from their group list.' }; }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  dom.window.setInterval = () => 0;
  dom.window.requestAnimationFrame = (callback) => dom.window.setTimeout(callback, 0);
  dom.window.eval(badgeScript);
  return { dom, posts };
}

test('group menu offers "New group with friends" next to "New group" for viewers who may host', async () => {
  const { dom, posts } = makeSyncPlayMenu(['new-group'], { shared: false, hostedHere: false, friends: [], canHost: true });
  await settleMany();
  const item = dom.window.document.querySelector('.federation-party-item');
  assert.ok(item);
  assert.equal(item.textContent.trim(), 'New group with friends');
  // Empty data-id: the menu's own handler just closes (an unknown id would be "joined").
  assert.equal(item.getAttribute('data-id'), '');
  assert.equal(item.previousElementSibling.getAttribute('data-id'), 'new-group');
  item.click();
  await settleMany();
  assert.equal(posts.length, 1);
  assert.match(posts[0].url, /\/Plugins\/Federation\/Party$/);
  dom.window.close();
});

test('group menu hides the friends entry from viewers who may not host', async () => {
  const { dom } = makeSyncPlayMenu(['new-group'], { shared: false, hostedHere: false, friends: [], canHost: false });
  await settleMany();
  assert.equal(dom.window.document.querySelector('.federation-party-item'), null);
  dom.window.close();
});

test('inside a group the menu offers "Invite friends", or says who it is shared with', async () => {
  const groups = [{ GroupId: 'g1', GroupName: "qa-admin's group", Participants: ['qa-admin'] }];
  const invite = makeSyncPlayMenu(['settings', 'leave-group'], { shared: false, hostedHere: false, friends: [], canHost: true }, groups);
  await settleMany();
  const item = invite.dom.window.document.querySelector('.federation-party-item');
  assert.equal(item.textContent.trim(), 'Invite friends');
  assert.equal(item.nextElementSibling.getAttribute('data-id'), 'leave-group');
  item.click();
  await settleMany();
  assert.deepEqual(invite.posts[0].body, { GroupId: 'g1' });
  invite.dom.window.close();

  const shared = makeSyncPlayMenu(['leave-group'], { shared: true, hostedHere: true, friends: ['friend-a'], canHost: true }, groups);
  await settleMany();
  assert.equal(shared.dom.window.document.querySelector('.federation-party-item').textContent.trim(), 'Watching with friend-a');
  shared.dom.window.close();
});

test("admin page has the watch party switches wired to their own settings route", () => {
  assert.ok(configPage.includes('id="fedChkHostWatchParties"'));
  assert.ok(configPage.includes('id="fedChkJoinWatchParties"'));
  assert.match(configPage, /case 'save-watch-parties':\s*saveWatchPartySettings\(\);/);
  assert.match(configPage, /fedFetch\('\/Plugins\/Federation\/Settings\/WatchParties', \{\s*method: 'POST'/);
  assert.match(configPage, /loadFavoritesSettings\(\);\s*loadWatchPartySettings\(\);/);
});

function routeFetch(dom, routes) {
  const posts = [];
  dom.window.fetch = (url, options) => {
    const u = String(url);
    if (options && options.method === 'POST') { posts.push({ url: u, body: JSON.parse(options.body || '{}') }); }
    const match = Object.keys(routes).find((key) => u.includes(key));
    const data = match ? routes[match] : {};
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  return posts;
}

test('server download asks where to save, with the default preselected', async () => {
  const { dom } = makeWindow(true);
  await settleMany();
  const posts = routeFetch(dom, {
    '/Download/Destinations': {
      defaultDestination: 'auto',
      options: [{ id: 'library:Movies', name: 'Movies', path: '/media/movies' }, { id: 'library:Shows', name: 'Shows', path: '/media/tv' }]
    },
    '/Plugins/Federation/Download': { operationId: 'op1', success: true }
  });
  const doc = dom.window.document;
  doc.querySelector('.btnMoreCommands').click();
  await settleMany();
  const item = [...doc.querySelectorAll('.federation-actionsheet-item')].find((b) => b.textContent.trim() === 'Download to this server');
  item.click();
  await settleMany();

  const dialog = doc.querySelector('.federation-dest');
  assert.ok(dialog);
  const values = [...dialog.querySelectorAll('input[name="federation-dest"]')].map((r) => r.value);
  assert.deepEqual(values, ['auto', 'library:Movies', 'library:Shows', 'separate', 'folder:']);
  assert.equal(dialog.querySelector('input[name="federation-dest"]:checked').value, 'auto');
  assert.equal(posts.length, 0);

  dialog.querySelector('input[value="library:Shows"]').checked = true;
  dialog.querySelector('.federation-dest-go').click();
  await settleMany();
  assert.equal(doc.querySelector('.federation-dest'), null);
  assert.equal(posts.length, 1);
  assert.match(posts[0].url, /\/Plugins\/Federation\/Download$/);
  assert.deepEqual(posts[0].body, { ItemId: itemId, Destination: 'library:Shows' });
  dom.window.close();
});

test('server download into a new folder needs a name, and can become the default', async () => {
  const { dom } = makeWindow(true);
  await settleMany();
  const posts = routeFetch(dom, {
    '/Download/Destinations': { defaultDestination: 'library:Movies', options: [{ id: 'library:Movies', name: 'Movies', path: '/media/movies' }] },
    '/Settings/DownloadDestination': { success: true },
    '/Plugins/Federation/Download': { operationId: 'op1', success: true }
  });
  const doc = dom.window.document;
  doc.querySelector('.btnMoreCommands').click();
  await settleMany();
  [...doc.querySelectorAll('.federation-actionsheet-item')].find((b) => b.textContent.trim() === 'Download to this server').click();
  await settleMany();

  const dialog = doc.querySelector('.federation-dest');
  assert.equal(dialog.querySelector('input[name="federation-dest"]:checked').value, 'library:Movies');
  dialog.querySelector('input[value="folder:"]').checked = true;
  dialog.querySelector('.federation-dest-go').click();
  await settleMany();
  assert.ok(doc.querySelector('.federation-dest'));
  assert.match(dialog.querySelector('.federation-dest-error').textContent, /folder name/);

  dialog.querySelector('.federation-dest-new').value = 'From friends';
  dialog.querySelector('.federation-dest-remember input').checked = true;
  dialog.querySelector('.federation-dest-go').click();
  await settleMany();
  assert.deepEqual(posts.map((p) => p.url.replace(/^.*\/Plugins\/Federation\//, '')), ['Settings/DownloadDestination', 'Download']);
  assert.deepEqual(posts[0].body, { Destination: 'folder:From friends' });
  assert.equal(posts[1].body.Destination, 'folder:From friends');
  dom.window.close();
});

test('cancelling the destination dialog starts nothing', async () => {
  const { dom } = makeWindow(true);
  await settleMany();
  const posts = routeFetch(dom, { '/Download/Destinations': { defaultDestination: 'auto', options: [] } });
  const doc = dom.window.document;
  doc.querySelector('.btnMoreCommands').click();
  await settleMany();
  [...doc.querySelectorAll('.federation-actionsheet-item')].find((b) => b.textContent.trim() === 'Download to this server').click();
  await settleMany();
  doc.querySelector('.federation-dest-actions button').click();
  await settleMany();
  assert.equal(doc.querySelector('.federation-dest'), null);
  assert.equal(posts.length, 0);
  dom.window.close();
});

test('dashboard downloads have a "Save to" choice sent with the batch', () => {
  assert.ok(configPage.includes('id="fedDownloadDest"'));
  assert.ok(configPage.includes('id="fedDownloadDestFolder"'));
  assert.match(configPage, /case 'save-download-dest':\s*saveDownloadDestination\(\);/);
  assert.match(configPage, /Destination: destination,\s*Items: items\.map/);
  assert.match(configPage, /Season: item\.parentIndexNumber != null/);
  assert.match(configPage, /loadWatchPartySettings\(\);\s*loadDownloadDestinations\(\);/);
});

function makePlayerStats(connection) {
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body>'
      + '<div class="playerStats"><div class="playerStats-content"><div class="playerStats-stats">'
      + '<div class="playerStats-stat playerStats-stat-header"><div class="playerStats-stat-label">Playback Info</div><div class="playerStats-stat-value"></div></div>'
      + '</div></div></div></body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html#/video' }
  );
  const calls = [];
  dom.window.ApiClient = { getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: false } }), accessToken: () => 'test-token' };
  dom.window.fetch = (url) => {
    const u = String(url);
    calls.push(u);
    let data = {};
    if (u.includes('ClientCatalog')) data = { items: {}, unavailableIds: [] };
    if (u.includes('ClientSettings')) data = { showFederatedCloudBadges: false };
    if (u.includes('/Playback/Connection')) data = connection;
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  dom.window.setInterval = () => 0;
  dom.window.requestAnimationFrame = (callback) => dom.window.setTimeout(callback, 0);
  dom.window.eval(badgeScript);
  return { dom, calls };
}

test('Playback Info gets a Federation block for friends\' titles, and keeps it across re-renders', async () => {
  const { dom, calls } = makePlayerStats({
    federated: true,
    friend: 'friend-a',
    route: 'friend-converts',
    routeText: 'friend-a converts it and sends it straight to this player',
    playMethod: 'Transcode',
    conversion: 'friend-a',
    originalContainer: 'mkv',
    originalVideoCodec: 'hevc',
    originalAudioCodec: 'eac3',
    originalBitrate: 59000000,
    deliveredContainer: 'ts',
    deliveredVideoCodec: 'h264',
    deliveredAudioCodec: 'aac',
    deliveredBitrate: 12000000,
    transcodeReasons: 'VideoCodecNotSupported',
    placement: 'Friend converts (full hardware conversion)',
    latencyMs: 42,
    linkMbps: 87.4,
    capMbps: 20,
    friendHardware: 'RX 6600 + Ryzen 5 (12 threads); hardware encodes H264, HEVC',
    alsoOn: ['slow-peer']
  });
  await settleMany();
  const doc = dom.window.document;
  assert.ok(calls.some((u) => u.endsWith('/Plugins/Federation/Playback/Connection')));
  const block = doc.querySelector('.playerStats-stats .federation-stats');
  assert.ok(block);
  const rows = [...block.querySelectorAll('.playerStats-stat')].map((r) => [r.querySelector('.playerStats-stat-label').textContent, r.querySelector('.playerStats-stat-value').textContent]);
  assert.deepEqual(rows, [
    ['Federation', ''],
    ['Friend', 'friend-a'],
    ['Playback', 'Transcode'],
    ['Route', 'friend-a converts it and sends it straight to this player'],
    ['Conversion', 'friend-a'],
    ['Original', 'mkv / hevc / eac3 · 59 Mbps'],
    ['Delivered', 'ts / h264 / aac · 12 Mbps'],
    ['Conversion reason', 'VideoCodecNotSupported'],
    ['Placement reason', 'Friend converts (full hardware conversion)'],
    ['Round trip', '42 ms'],
    ['Server route speed', '87 Mbps'],
    ['Bitrate cap', '20 Mbps'],
    ['friend-a converts with', 'RX 6600 + Ryzen 5 (12 threads); hardware encodes H264, HEVC'],
    ['Also on', 'slow-peer']
  ]);
  assert.ok(block.querySelector('.playerStats-stat-header'));

  // jellyfin-web replaces the panel's contents every second.
  doc.querySelector('.playerStats-stats').innerHTML = '<div class="playerStats-stat"><div class="playerStats-stat-label">Player</div><div class="playerStats-stat-value">Html Video Player</div></div>';
  await settleMany();
  assert.equal(doc.querySelectorAll('.federation-stats').length, 1);
  assert.equal(calls.filter((u) => u.includes('/Playback/Connection')).length, 1);
  dom.window.close();
});

test('route telemetry sends bounded numeric observations without media URLs or tokens', async () => {
  const { dom } = makePlayerStats({
    federated: true,
    itemId: '11111111111111111111111111111111',
    serverId: 'friend-a',
    remoteItemId: '22222222222222222222222222222222',
    route: 'friend-converts',
    routeText: 'friend-a converts it'
  });
  await settleMany();
  const originalFetch = dom.window.fetch;
  const bodies = [];
  dom.window.fetch = (url, options) => {
    if (String(url).includes('/Playback/Metrics')) bodies.push(options.body);
    return originalFetch(url, options);
  };
  dom.window.__federationRouteTelemetry.observe([
    { name: 'https://owner.example/Plugins/Federation/Peer/Play/secret-token/videos/22222222222222222222222222222222/main.m3u8?api_key=secret', encodedBodySize: 1000000, duration: 500 },
    { name: 'https://owner.example/Plugins/Federation/Peer/Play/other-secret/videos/33333333333333333333333333333333/main.m3u8', encodedBodySize: 9000000, duration: 500 }
  ]);
  await dom.window.__federationRouteTelemetry.flush();
  assert.equal(bodies.length, 1);
  const body = JSON.parse(bodies[0]);
  assert.equal(body.bytes, 1000000);
  assert.equal(body.durationMs, 500);
  assert.equal(body.itemId, '11111111111111111111111111111111');
  assert.equal(body.serverId, 'friend-a');
  assert.doesNotMatch(bodies[0], /owner\.example|secret|m3u8|api_key/);
  assert.equal(dom.window.__federationRouteTelemetry.matches('https://owner.example/Plugins/Federation/DirectStream/33333333333333333333333333333333?token=secret', { serverId: 'friend-a', remoteItemId: '22222222222222222222222222222222', route: 'direct' }), false);
  dom.window.close();
});

test('Playback Info stays untouched for this server\'s own titles and when the panel is closed', async () => {
  const own = makePlayerStats({ federated: false });
  await settleMany();
  assert.equal(own.dom.window.document.querySelector('.federation-stats'), null);
  own.dom.window.close();

  const closed = makePlayerStats({ federated: true, friend: 'x', routeText: 'y' });
  closed.dom.window.document.querySelector('.playerStats').classList.add('hide');
  await settleMany();
  assert.equal(closed.calls.some((u) => u.includes('/Playback/Connection')), false);
  closed.dom.window.close();
});

test('destination dialog and Playback Info block use solid colours only', () => {
  const css = badgeScript.slice(badgeScript.indexOf('.federation-dest-backdrop'), badgeScript.indexOf("].join('');"));
  assert.ok(css.length > 0);
  assert.equal(/gradient/i.test(css), false);
});

test('admin page has the viewer and hardware cards wired to their own settings routes', () => {
  assert.ok(configPage.includes('id="fedChkShowFriendsViewers"'));
  assert.ok(configPage.includes('id="fedChkShareViewersWithFriends"'));
  assert.ok(configPage.includes('id="fedChkSmartPlacement"'));
  assert.ok(configPage.includes('id="fedSelHardwareMode"'));
  assert.match(configPage, /case 'save-viewers':\s*saveViewerSettings\(\);/);
  assert.match(configPage, /case 'save-hardware':\s*saveHardwareSettings\(\);/);
  assert.match(configPage, /fedFetch\('\/Plugins\/Federation\/Settings\/Hardware', \{\s*method: 'POST'/);
  assert.match(configPage, /loadViewerSettings\(\);\s*loadHardwareSettings\(\);/);
});

test('friend-to-friend viewer and hardware routes check the friend; panel route needs a login', () => {
  const controller = fs.readFileSync(path.join(root, 'Configuration/FederationPluginController.cs'), 'utf8');
  for (const [route, method] of [['Peer/Viewers', 'PeerViewers'], ['Peer/Hardware', 'PeerHardware']]) {
    const block = controller.match(new RegExp('\\[Http(?:Get|Post)\\("' + route.replace('/', '\\/') + '"\\)\\][\\s\\S]*?public [^(]*' + method + '\\([\\s\\S]*?ResolveCaller'));
    assert.ok(block, route + ' not found or does not resolve the calling friend');
  }

  const panel = controller.match(/\[HttpGet\("Playback\/Connection"\)\][\s\S]*?public async Task<IActionResult> GetPlaybackConnection\(/);
  assert.ok(panel);
  assert.match(panel[0], /\[Authorize\]/);
  assert.doesNotMatch(panel[0], /AllowAnonymous/);

  for (const route of ['Settings/Viewers', 'Settings/Hardware', 'Settings/DownloadDestination', 'Download/Destinations']) {
    const block = controller.match(new RegExp('\\[Http(?:Get|Post)\\("' + route.replace('/', '\\/') + '"\\)\\]\\s*\\[Authorize\\(Policy = "RequiresElevation"\\)\\]'));
    assert.ok(block, route + ' must require an administrator');
  }
});

test('download ring skips rewriting when the shown value is unchanged', () => {
  const fn = badgeScript.match(/function applyDownloadRing\(container, pct, sizePx\) \{[\s\S]*?\n  \}\n/);
  assert.ok(fn, 'applyDownloadRing not found');
  assert.match(fn[0], /data-ring-state/);
  assert.ok(fn[0].indexOf("getAttribute('data-ring-state') === shown") < fn[0].indexOf('ring.innerHTML'));
});

test('download progress poll retries non-OK replies with a cap instead of dying', () => {
  const fn = badgeScript.match(/function pollDownloadProgress\([\s\S]*?\n  \}\n/);
  assert.ok(fn, 'pollDownloadProgress not found');
  assert.match(fn[0], /res\.ok \? res\.json\(\) : \{ retry: true \}/);
  assert.match(fn[0], /failures > 8/);
  assert.match(fn[0], /delete pollingOperations\[operationId\]/);
  assert.match(fn[0], /\.catch\(retryOrStop\)/);
});

function makePopularWindow(sections, items, fail = false) {
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body>'
      + '<div id="homeTab" class="tabContent pageTabContent is-active"><div class="sections"><div class="verticalSection">native</div></div></div>'
      + '</body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html#/home' }
  );
  const calls = [];
  const itemQueries = [];
  dom.window.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: false } }),
    getCurrentUserId: () => 'user-1',
    serverId: () => 'srv',
    accessToken: () => 'test-token',
    getScaledImageUrl: (id, opts) => '/Items/' + id + '/Images/Primary?tag=' + opts.tag,
    getItems: (uid, query) => {
      itemQueries.push({ uid, query });
      return Promise.resolve({ Items: items });
    }
  };
  dom.window.fetch = (url) => {
    calls.push(String(url));
    let data = {};
    if (String(url).includes('ClientCatalog')) data = { items: {}, unavailableIds: [] };
    if (String(url).includes('ClientSettings')) data = { showFederatedCloudBadges: false };
    if (String(url).includes('FriendPopular')) {
      if (fail) return Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) });
      data = { enabled: true, sections };
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(data) });
  };
  dom.window.setInterval = () => 0;
  dom.window.requestAnimationFrame = (callback) => dom.window.setTimeout(callback, 0);
  dom.window.eval(badgeScript);
  return { dom, calls, itemQueries };
}

test("popular titles appear on the Home tab as one row per friend, most popular first", async () => {
  const items = [
    { Id: 'aaa', Name: 'Dune', Type: 'Movie', ProductionYear: 2024, ImageTags: { Primary: 't1' } },
    { Id: 'bbb', Name: 'Show', Type: 'Series', ImageTags: {} }
  ];
  const { dom, calls } = makePopularWindow([
    { serverId: 'f1', name: 'friend-a', itemIds: ['bbb', 'aaa'] },
    { serverId: 'f2', name: 'Empty', itemIds: ['gone'] }
  ], items);
  await settleMany();

  assert.ok(calls.some((url) => url.endsWith('/Plugins/Federation/FriendPopular')));
  const tab = dom.window.document.getElementById('homeTab');
  assert.equal(tab.querySelector('.sections').textContent, 'native');
  const sections = [...tab.querySelectorAll('.federation-popular-section')];
  assert.deepEqual(sections.map((s) => s.querySelector('.sectionTitle').textContent), ['Popular on friend-a']);
  assert.deepEqual([...sections[0].querySelectorAll('.federation-fav-card')].map((a) => a.getAttribute('data-id')), ['bbb', 'aaa']);
  dom.window.close();
});

test("a failed popular load is not retried on every page change", async () => {
  const { dom, calls } = makePopularWindow([], [], true);
  await settleMany();
  for (let i = 0; i < 4; i++) {
    dom.window.document.body.appendChild(dom.window.document.createElement('div'));
    await settleMany();
  }
  assert.equal(calls.filter((url) => url.endsWith('/Plugins/Federation/FriendPopular')).length, 1);
  dom.window.close();
});

test("popular rows stay absent and quiet when there is nothing to show", async () => {
  const { dom, itemQueries } = makePopularWindow([], []);
  await settleMany();
  assert.equal(itemQueries.length, 0);
  assert.equal(dom.window.document.querySelectorAll('.federation-popular-section').length, 0);
  assert.equal(dom.window.document.querySelectorAll('.federation-friend-popular').length, 0);
  dom.window.close();
});

test("admin page has the popular switches saved with the favorites settings", () => {
  assert.ok(configPage.includes('id="fedChkShowFriendsPopular"'));
  assert.ok(configPage.includes('id="fedChkSharePopularWithFriends"'));
  assert.match(configPage, /SharePopularWithFriends: q\('#fedChkSharePopularWithFriends'\)\.checked/);
});

function friendSharingApi(config) {
  const block = configPage.match(/\n {20}(\/\/ Other friends this friend could be told about[\s\S]*?)\n {20}function setSharingStatus/);
  assert.ok(block, 'friend sharing script not found');
  const posts = [];
  const helpers = {
    q: () => null,
    escapeHtml: (v) => (v === null || v === undefined ? '' : String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')),
    loadConfiguration: () => Promise.resolve(),
    readJson: (r) => r.json(),
    currentConfig: config,
    fedFetch: (url, options) => {
      posts.push({ url, body: JSON.parse(options.body) });
      return Promise.resolve({ json: () => Promise.resolve({ success: true, message: 'Saved.' }) });
    }
  };
  const api = new Function(...Object.keys(helpers),
    'var pendingFriendSharing = {};\n' + block[1] + '\nreturn { renderFriendSharingBlock, saveFriendSharing, pending: () => pendingFriendSharing };')(...Object.values(helpers));
  return { api, posts };
}

test('friends they can see: all by default, lists only friends that can be introduced', () => {
  const servers = [
    { Id: 'a', Name: 'Asker', Kind: 0 },
    { Id: 'b', Name: 'Bea <i>', Kind: 0 },
    { Id: 'c', Name: 'Off', Kind: 0, Enabled: false },
    { Id: 'd', Name: 'Paired', Kind: 0, Paired: true },
    { Id: 'p', Name: 'Plex', Kind: 1 },
    { Id: 'm', Name: 'Companion', Kind: 2 }
  ];
  const { api } = friendSharingApi({ AllowFriendsOfFriends: true, RemoteServers: servers });
  const host = new JSDOM('<div id="x"></div>').window.document.getElementById('x');
  host.innerHTML = api.renderFriendSharingBlock(servers[0]);
  assert.equal(host.querySelector('.fedFriendSharingAll').checked, true);
  assert.equal(host.querySelector('.fedFriendSharingList').style.display, 'none');
  assert.deepEqual([...host.querySelectorAll('.fedFriendSharingFriend')].map((c) => c.getAttribute('data-friend')), ['b']);
  assert.equal(host.querySelector('i'), null);
  assert.match(host.textContent, /Bea <i>/);
  assert.doesNotMatch(host.textContent, /is off/);

  host.innerHTML = api.renderFriendSharingBlock({ Id: 'b', ShareAllFriends: false, SharedFriendIds: ['a'] });
  assert.equal(host.querySelector('.fedFriendSharingAll').checked, false);
  assert.deepEqual([...host.querySelectorAll('.fedFriendSharingFriend:checked')].map((c) => c.getAttribute('data-friend')), ['a']);
});

test('friends they can see says when friends-of-friends is off, and saves the selection', async () => {
  const { api, posts } = friendSharingApi({ AllowFriendsOfFriends: false, RemoteServers: [{ Id: 'a', Kind: 0 }] });
  const html = api.renderFriendSharingBlock({ Id: 'a', ShareAllFriends: false, SharedFriendIds: [] });
  assert.match(html, /Friends-of-friends is off/);
  assert.match(html, /no other friends to show/);
  api.saveFriendSharing('a');
  await settle();
  assert.equal(posts[0].url, '/Plugins/Federation/Friends/a/FriendSharing');
  assert.deepEqual(posts[0].body, { ShareAll: false, FriendIds: [] });
  assert.equal(api.pending().a, undefined);
});

test('Beta tab sits before Advanced and holds the pairing card, off by default', () => {
  assert.ok(configPage.indexOf('id="fedTabBeta"') > 0);
  assert.ok(configPage.indexOf('id="fedTabBeta"') < configPage.indexOf('id="fedTabAdvanced"'));
  assert.match(configPage, /id="fedTabBeta" aria-controls="fedPanelBeta" tabindex="-1"[^>]*data-tab="beta" role="tab" aria-selected="false"/);
  assert.match(configPage, /id="fedPanelBeta" aria-labelledby="fedTabBeta" class="fed-tabpanel" data-tab-panel="beta" role="tabpanel"/);
  assert.match(configPage, /Pairing codes \(Beta\) <span class="fed-pill fed-pill-off">Beta<\/span>/);
  assert.match(configPage, /<input type="checkbox" id="fedChkEnablePairing" class="emby-checkbox fed-check" disabled title="Pairing codes are disabled in this build while the feature is under review\." \/>/);
  assert.ok(configPage.includes('Direct links and pairing codes are on the'));
  const dom = new JSDOM(configPage);
  const d = dom.window.document;
  assert.equal(d.getElementById('fedChkEnablePairing').checked, false);
  assert.ok(d.getElementById('fedPanelBeta').contains(d.getElementById('fedChkAdaptivePlayback')));
  assert.ok(d.getElementById('fedPanelBeta').contains(d.getElementById('fedPairPortFirst')));
  assert.ok(d.getElementById('fedPanelBeta').contains(d.getElementById('fedPairPortLast')));
});

function shareDatesApi(responses) {
  const helpersSrc = configPage.match(/\n {20}(\/\/ Date inputs hold local days[\s\S]*?)\n {20}function showRemoteTranscodeStatus/);
  const saveSrc = configPage.match(/\n {20}(function saveSharing\(serverId\) \{[\s\S]*?)\n {20}\/\/ Date inputs hold local days/);
  assert.ok(helpersSrc && saveSrc, 'end date / preview script not found');
  const posts = [];
  const statuses = [];
  const helpers = {
    q: () => null,
    escapeHtml: (v) => (v === null || v === undefined ? '' : String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')),
    renderFriends: () => {},
    loadConfiguration: () => Promise.resolve(),
    readJson: (r) => r.json(),
    setSharingStatus: (id, msg, kind) => statuses.push([msg, kind]),
    fedFetch: (url, options) => {
      posts.push({ url, body: options && options.body ? JSON.parse(options.body) : null });
      const key = Object.keys(responses || {}).find((k) => url.endsWith(k));
      return Promise.resolve({ json: () => Promise.resolve(key ? responses[key] : { success: true, message: 'Sharing saved.' }) });
    }
  };
  const api = new Function(...Object.keys(helpers),
    'var pendingSharing = {}; var friendPreviews = {};\n' + saveSrc[1] + '\n' + helpersSrc[1]
    + '\nreturn { saveSharing, isoToDateInput, dateInputToIso, renderFriendPreview, togglePreview, pendingSharing, friendPreviews };')(...Object.values(helpers));
  return { api, posts, statuses };
}

test('share-until dates: a picked day ends at its last second, and reads back as the same day', () => {
  const { api } = shareDatesApi();
  const iso = api.dateInputToIso('2030-05-01');
  const d = new Date(iso);
  assert.equal(d.getFullYear(), 2030);
  assert.equal(d.getMonth(), 4);
  assert.equal(d.getDate(), 1);
  assert.equal(d.getHours(), 23);
  assert.equal(d.getMinutes(), 59);
  assert.equal(api.isoToDateInput(iso), '2030-05-01');
  assert.equal(api.dateInputToIso(''), null);
  assert.equal(api.isoToDateInput(null), '');
  assert.equal(api.isoToDateInput('garbage'), '');
});

test('saving sharing also saves the end dates, and a refused end date is reported', async () => {
  const { api, posts, statuses } = shareDatesApi();
  api.pendingSharing.f1 = { ShareAll: false, FolderIds: ['m'], ExcludedItems: [], Until: '2030-05-01', LibraryUntil: { m: '2030-04-01', t: '' } };
  api.friendPreviews.f1 = { success: true };
  api.saveSharing('f1');
  await settle(); await settle();
  assert.deepEqual(posts.map((p) => p.url), ['/Plugins/Federation/Friends/f1/Sharing', '/Plugins/Federation/Friends/f1/Expiry']);
  assert.equal(posts[1].body.ExpiresUtc, api.dateInputToIso('2030-05-01'));
  assert.deepEqual(posts[1].body.LibraryExpiries, [{ FolderId: 'm', ExpiresUtc: api.dateInputToIso('2030-04-01') }]);
  assert.deepEqual(statuses.at(-1), ['Sharing saved.', 'ok']);
  assert.equal(api.friendPreviews.f1, undefined, 'a stale preview is dropped after saving');

  const refused = shareDatesApi({ '/Expiry': { success: false, message: 'Friend not found.' } });
  refused.api.pendingSharing.f1 = { ShareAll: true, FolderIds: [], ExcludedItems: [], Until: '', LibraryUntil: {} };
  refused.api.saveSharing('f1');
  await settle(); await settle();
  assert.deepEqual(refused.posts[1].body, { ExpiresUtc: null, LibraryExpiries: [] });
  assert.deepEqual(refused.statuses.at(-1), ['Friend not found.', 'err']);
});

test('preview shows what a friend gets, escaped, and says when sharing ended', () => {
  const { api } = shareDatesApi();
  const host = new JSDOM('<div id="x"></div>').window.document.getElementById('x');
  host.innerHTML = api.renderFriendPreview({
    success: true, name: 'Pal <b>', expired: false, expiresUtc: '2030-05-01T21:59:59Z',
    libraries: [{ name: 'Movies <i>', visible: true, titles: 3, expiresUtc: null }, { name: 'Kids', visible: false, expired: true, expiresUtc: '2020-01-01T00:00:00Z' }, { name: 'Music', visible: false }],
    excludedTitles: ['Secret (2001)'], globallyExcludedCount: 2,
    users: ['anna', 'ben'], friendsOfFriendsEnabled: false, friends: [],
    userRules: [{ name: 'Kid', mode: 'Blocked', maxRating: null, allowDownload: false }],
    allowDownloads: true, allowBulkDownloads: false, allowRemoteTranscode: true, maxConcurrentStreams: 2, monthlyDataCapGb: 0
  });
  assert.equal(host.querySelector('b'), null);
  assert.equal(host.querySelector('i'), null);
  const text = host.textContent;
  assert.match(text, /What Pal <b> sees right now/);
  assert.match(text, /Libraries \(1 of 3\)/);
  assert.match(text, /Movies <i> — 3 titles/);
  assert.match(text, /Kids — ended/);
  assert.match(text, /Music — not shared/);
  assert.match(text, /Secret \(2001\); 2 hidden from every friend/);
  assert.match(text, /anna, ben/);
  assert.match(text, /None \(friends-of-friends is off\)/);
  assert.match(text, /Kid — blocked, no downloads/);
  assert.match(text, /Single downloads allowed · you convert video for them · up to 2 streams at once · no data limit/);
  assert.match(text, /stay shared until/);

  const ended = api.renderFriendPreview({ success: true, name: 'Pal', expired: true, expiresUtc: '2020-01-01T00:00:00Z', libraries: [] });
  assert.match(ended, /Sharing ended on .*: they see none of your libraries/);
  assert.match(api.renderFriendPreview({ loading: true }), /Checking what they see/);
  assert.match(api.renderFriendPreview({ error: '<x>' }), /Could not build the preview: &lt;x&gt;/);
});

test('pairing actions and routes are wired', () => {
  for (const a of ['save-pairing', 'pairing-make', 'pairing-answer', 'pairing-complete', 'pairing-confirm', 'pairing-cancel']) {
    assert.ok(configPage.includes("case '" + a + "':"), a);
  }
  assert.ok(configPage.includes('data-fed-action="save-pairing"'));
  assert.ok(configPage.includes("'/Plugins/Federation/Settings/Pairing'"));
  assert.ok(configPage.includes("'/Plugins/Federation/Pairing/Offer'"));
  assert.ok(configPage.includes("'/Plugins/Federation/Pairing/Answer'"));
  assert.ok(configPage.includes("'/Plugins/Federation/Pairing/Complete'"));
  assert.ok(configPage.includes("'/Plugins/Federation/Pairing/Status'"));
  assert.ok(configPage.includes("'/Plugins/Federation/Pairing/' + encodeURIComponent(id) + '/Confirm'"));
  assert.ok(configPage.includes("confirmPairing(t.getAttribute('data-id'), t.getAttribute('data-safety-number'))"));
  assert.ok(configPage.includes("'POST', { SafetyNumber: safetyNumber }, '#fedPairingFriendStatus'"));
  assert.ok(configPage.includes("'/Plugins/Federation/Pairing/Offer/' + encodeURIComponent(id), 'DELETE'"));
  assert.match(configPage, /if \(tab === 'beta'\) \{\s*startPairingPoll\(\);\s*\} else \{\s*stopPairingPoll\(\);/);
  assert.ok(configPage.includes('escapeHtml(f.CallsItself)'));
  assert.ok(configPage.includes('escapeHtml(f.SafetyNumber)'));
  assert.equal(/gradient/i.test(configPage.slice(configPage.indexOf('fed-pair-code'), configPage.indexOf('fed-pair-code') + 600)), false);
  assert.equal(/gradient/i.test(configPage), false);
});

test('popular titles and viewer names: a default plus a choice per friend', () => {
  const src = configPage.match(/\n {20}(\/\/ Per-friend choice for a sharing setting[\s\S]*?)\n {20}function loadFavoritesSettings/);
  assert.ok(src, 'friend choice script not found');
  const dom = new JSDOM('<div id="fedPopularFriendChoices"></div><div id="fedViewerNameFriendChoices"></div>');
  const doc = dom.window.document;
  const helpers = {
    q: (sel) => doc.querySelector(sel),
    escapeHtml: (v) => (v === null || v === undefined ? '' : String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'))
  };
  const api = new Function(...Object.keys(helpers), src[1] + '\nreturn { renderFriendChoices, readFriendChoices };')(...Object.values(helpers));
  const friends = [{ Id: 'a', Name: 'Ann <b>', SharePopular: null }, { Id: 'b', Name: 'Bo', SharePopular: true }, { Id: 'c', Name: 'Cy', SharePopular: false }];
  api.renderFriendChoices('fedPopularFriendChoices', friends, 'SharePopular', false, 'Share', "Don't share");
  const host = doc.getElementById('fedPopularFriendChoices');
  assert.equal(host.querySelector('b'), null);
  assert.match(host.textContent, /Ann <b>/);
  const selects = [...host.querySelectorAll('select')];
  assert.deepEqual(selects.map((sel) => sel.value), ['default', 'on', 'off']);
  assert.equal(selects[0].options[0].textContent, "Default (Don't share)");
  assert.deepEqual(api.readFriendChoices('fedPopularFriendChoices'), [{ Id: 'a', Share: null }, { Id: 'b', Share: true }, { Id: 'c', Share: false }]);
  selects[2].value = 'default';
  assert.deepEqual(api.readFriendChoices('fedPopularFriendChoices')[2], { Id: 'c', Share: null });

  api.renderFriendChoices('fedViewerNameFriendChoices', [{ Id: 'a', Name: 'Ann', ShareViewerNames: null }], 'ShareViewerNames', true, 'With names', 'Without names');
  assert.equal(doc.querySelector('#fedViewerNameFriendChoices option').textContent, 'Default (With names)');
  api.renderFriendChoices('fedViewerNameFriendChoices', [], 'ShareViewerNames', true, 'With names', 'Without names');
  assert.equal(doc.getElementById('fedViewerNameFriendChoices').innerHTML, '');
});

test('popular and viewer-name defaults are on the page and saved with their cards', () => {
  assert.match(configPage, /id="fedChkSharePopularWithFriends"[^>]*\/> Share what's popular here with friends \(default for each friend\)/);
  assert.match(configPage, /id="fedChkShareViewerNames"[^>]*\/> Include my viewers' names and devices \(default for each friend\)/);
  assert.match(configPage, /PopularFriends: readFriendChoices\('fedPopularFriendChoices'\)/);
  assert.match(configPage, /ShareViewerNamesWithFriends: q\('#fedChkShareViewerNames'\)\.checked,\s*ViewerNameFriends: readFriendChoices\('fedViewerNameFriendChoices'\)/);
  assert.match(configPage, /q\('#fedChkSharePopularWithFriends'\)\.checked = res\.SharePopularWithFriends === true;/);
});

test('pairing status renders untrusted text safely and gates the confirm button', () => {
  const block = configPage.match(/(var pairingTimer = null;[\s\S]*?)\n {20}function loadPairingStatus/);
  assert.ok(block, 'pairing render script not found');
  const dom = new JSDOM(configPage);
  const doc = dom.window.document;
  const helpers = {
    q: (sel) => doc.querySelector(sel),
    escapeHtml: (v) => (v === null || v === undefined ? '' : String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'))
  };
  const api = new Function(...Object.keys(helpers), block[1] + '\nreturn { renderPairingStatus };')(...Object.values(helpers));
  const friend = (extra) => Object.assign({ ServerId: 'abc', Name: 'Pal', CallsItself: '<img src=x onerror=alert(1)>', State: 'connecting', Detail: '<b>d</b>', SafetyNumber: '12345 <i>6</i>', Confirmed: false, FriendConfirmed: false, Ready: false }, extra);
  api.renderPairingStatus({ Enabled: true, Available: true, PortFirst: 40000, PortLast: 40010, PendingCodes: [{ Id: 'p1', Expires: '2030-01-01T00:00:00Z', Port: 40001 }], Friends: [friend()] });
  const list = doc.getElementById('fedPairingFriends');
  assert.equal(list.querySelector('img'), null);
  assert.equal(list.querySelector('b'), null);
  assert.equal(list.querySelector('i'), null);
  assert.match(list.textContent, /calls itself <img src=x onerror=alert\(1\)>/);
  assert.match(list.textContent, /12345 <i>6<\/i>/);
  assert.ok(doc.querySelector('[data-fed-action="pairing-cancel"][data-id="p1"]'));
  assert.equal(doc.getElementById('fedChkEnablePairing').checked, true);
  const btn = list.querySelector('[data-fed-action="pairing-confirm"]');
  assert.equal(btn.disabled, true, 'confirm is disabled until the box is ticked');
  assert.equal(btn.getAttribute('data-id'), 'abc');
  assert.equal(btn.getAttribute('data-safety-number'), '12345 <i>6</i>');
  list.querySelector('.fedPairConfirmChk').checked = true;
  api.renderPairingStatus({ Available: true, Friends: [friend({ SafetyNumber: '98765 43210' })] });
  const refreshed = list.querySelector('[data-fed-action="pairing-confirm"]');
  assert.equal(refreshed.getAttribute('data-safety-number'), '98765 43210');
  assert.equal(refreshed.disabled, true, 'approval does not carry over to a changed safety number');
  assert.equal(list.querySelector('.fedPairConfirmChk').checked, false);
  api.renderPairingStatus({ Available: true, Friends: [friend({ Confirmed: true })] });
  assert.match(list.textContent, /Waiting for your friend to confirm/);
  api.renderPairingStatus({ Available: true, Friends: [friend({ Confirmed: true, FriendConfirmed: true, Ready: true })] });
  assert.match(list.textContent, /Ready\. Choose what to share/);
  api.renderPairingStatus({ Available: false, Reason: 'no UDP', Friends: [] });
  assert.match(doc.getElementById('fedPairingSummary').textContent, /Pairing isn't available on this server: no UDP/);
});

function userSharingApi(extra) {
  const block = configPage.match(/\n {20}(function loadLocalUsers\(\) \{[\s\S]*?)\n {20}function renderSharingBlock/);
  assert.ok(block, 'user sharing script not found');
  const posts = [];
  const helpers = Object.assign({
    q: () => null,
    escapeHtml: (v) => (v === null || v === undefined ? '' : String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')),
    renderFriends: () => {},
    loadConfiguration: () => Promise.resolve(),
    readJson: (r) => r.json(),
    fedFetch: (url, options) => {
      posts.push({ url, body: options && options.body ? JSON.parse(options.body) : null });
      return Promise.resolve({ json: () => Promise.resolve(url.endsWith('/LocalUsers') ? [{ id: 'u1', name: 'Anna <b>', isAdministrator: true }, { id: 'u2', name: 'Ben', isAdministrator: false }] : { success: true, message: 'Saved.' }) });
    }
  }, extra || {});
  const api = new Function(...Object.keys(helpers),
    'var localUsersCache = null; var pendingUserSharing = {};\n' + block[1]
    + '\nreturn { loadLocalUsers, renderUserSharingBlock, saveUserSharing, pending: () => pendingUserSharing };')(...Object.values(helpers));
  return { api, posts };
}

test('friends see none of my users unless I pick them, and names are escaped', async () => {
  const { api } = userSharingApi();
  await api.loadLocalUsers();
  const dom = new JSDOM('<div id="x"></div>');
  const host = dom.window.document.getElementById('x');
  host.innerHTML = api.renderUserSharingBlock({ Id: 'f1', Name: 'Pal' });
  assert.equal(host.querySelector('.fedUserSharingAll').checked, false);
  assert.equal(host.querySelectorAll('.fedUserSharingUser').length, 2);
  assert.equal(host.querySelectorAll('.fedUserSharingUser:checked').length, 0);
  assert.equal(host.querySelector('b'), null);
  assert.match(host.textContent, /Anna <b>/);
  assert.ok(host.querySelector('[data-fed-action="save-user-sharing"][data-id="f1"]'));

  host.innerHTML = api.renderUserSharingBlock({ Id: 'f2', ShareAllUsers: false, SharedUserIds: ['u2'] });
  assert.deepEqual([...host.querySelectorAll('.fedUserSharingUser:checked')].map((c) => c.getAttribute('data-user')), ['u2']);
  host.innerHTML = api.renderUserSharingBlock({ Id: 'f3', ShareAllUsers: true });
  assert.equal(host.querySelector('.fedUserSharingAll').checked, true);
  assert.equal(host.querySelector('.fedUserSharingList').style.display, 'none');
});

test('saving user sharing posts the selection to the friend endpoint', async () => {
  const { api, posts } = userSharingApi();
  api.renderUserSharingBlock({ Id: 'f 1', SharedUserIds: ['u1'] });
  api.saveUserSharing('f 1');
  await settle();
  const post = posts.find((p) => p.url.includes('/UserSharing'));
  assert.equal(post.url, '/Plugins/Federation/Friends/f%201/UserSharing');
  assert.deepEqual(post.body, { ShareAll: false, UserIds: ['u1'] });
  assert.equal(api.pending()['f 1'], undefined, 'pending selection is dropped once saved');
});

test('their people: rules stay listed when the friend stops showing that user', () => {
  const block = configPage.match(/\n {20}(function renderRemoteUserAccessBlock\(server\) \{[\s\S]*?)\n {20}function renderUserRuleEditor/);
  assert.ok(block, 'remote user block not found');
  const esc = (v) => String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;');
  const make = (users) => new Function('remoteUsersCache', 'loadRemoteUsersFor', 'escapeHtml', 'ruleKey', 'existingUserRule', 'expandedUserRuleEditors', 'ruleStatusLabel', 'renderUserRuleEditor',
    block[1] + '\nreturn renderRemoteUserAccessBlock;')(
    { f1: { loading: false, users } }, () => {}, esc, (s, u) => s + '::' + u,
    (server, id) => (server.RemoteUserAccessRules || []).filter((r) => r.RemoteUserId === id)[0] || null,
    {}, (rule) => (rule ? 'Blocked' : 'Everything shared'), () => '');
  const server = { Id: 'f1', Name: 'Pal', RemoteUserAccessRules: [{ RemoteUserId: 'gone', RemoteUserName: 'Carla', Mode: 3 }] };

  const withRule = make([])(server);
  assert.match(withRule, /Carla \(no longer shown by them\)/);
  assert.match(withRule, /data-key="f1::gone"/);

  const none = make([])({ Id: 'f1', Name: 'Pal' });
  assert.match(none, /haven't shown you any of their users/);
  const shown = make([{ id: 'u', name: 'Dora' }])(server);
  assert.match(shown, /Dora/);
  assert.match(shown, /Carla \(no longer shown by them\)/);
});

test('dynamic CSS selectors safely match hostile quote and backslash IDs', () => {
  const dom = new JSDOM('<div class="fedDirectLinkLine"></div><div class="fedDirectLinkLine" data-id="other"></div>');
  const source = configPage.match(/function cssEscape\([\s\S]*?\n {20}\}\n/);
  assert.ok(source, 'cssEscape not found');
  const escape = new Function('window', source[0] + '\nreturn cssEscape;')(dom.window.window);
  const hostile = 'friend\\"] , [data-id="other';
  const expected = dom.window.document.querySelector('.fedDirectLinkLine');
  expected.setAttribute('data-id', hostile);
  const selected = dom.window.document.querySelector('.fedDirectLinkLine[data-id="' + escape(hostile) + '"]');
  assert.equal(selected, expected);
  assert.notEqual(selected.getAttribute('data-id'), 'other');
  dom.window.close();
});

test('HTML helper escapes quoted attribute breakouts and pool icons are escaped', () => {
  const dom = new JSDOM('');
  const source = configPage.match(/function escapeHtml\([\s\S]*?\n {20}\}\n/);
  assert.ok(source, 'escapeHtml not found');
  const escape = new Function('document', source[0] + '\nreturn escapeHtml;')(dom.window.document);
  const hostile = '" onerror="globalThis.compromised=true\' data-x=';
  const html = '<img src="data:image/png;base64,' + escape(hostile) + '" alt="" />';
  const rendered = new JSDOM(html);
  const image = rendered.window.document.querySelector('img');
  assert.equal(image.getAttribute('onerror'), null);
  assert.equal(rendered.window.compromised, undefined);
  assert.match(configPage, /data:image\/png;base64,' \+ escapeHtml\(p\.IconBase64\)/);
  dom.window.close();
  rendered.window.close();
});
