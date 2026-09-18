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
    requests,
    intervals,
    setCloudBadge: (enabled) => { badgeEnabled = enabled; }
  };
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
  const federated = requests.find((r) => String(r.url).includes('/FederatedIds'));
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

test('Plex share requests can be accepted from the Companion tab without pasting a code', () => {
  assert.match(configPage, /fedPlexOffersIncoming/);
  assert.match(configPage, /PlexOffers\/' \+ encodeURIComponent\(id\) \+ '\/Accept/);
  assert.match(configPage, /accept-plex-offer/);
  assert.match(configPage, /this<\/em> server's public address/);
  assert.match(configPage, /Funnel opens Plex/);
  assert.match(configPage, /explicitly choose Plex Remote Access/);
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
    'onBrowseServerChange', 'onBrowseLibraryChange', 'openBrowseSeries', 'closeBrowseSeries',
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
    var downloadsPollInterval, watchingPollInterval, healthPollInterval;
    var TAB_NAMES = ['friends','catalog','browse'], TAB_STORAGE_KEY = 'test', localStorage = {setItem:()=>{}};
    function qa() {return [];} function stopWatchdog() {} function loadDownloads() {}
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
test('refresh now reads camelCase success and message so failures are visible', () => {
  const refresh = configPage.match(/case 'refresh-cache':[\s\S]*?break;/);
  assert.ok(refresh, 'refresh-cache handler not found');
  assert.match(refresh[0], /res\.success/);
  assert.match(refresh[0], /res\.message/);
  assert.match(refresh[0], /success === false/);
  assert.match(refresh[0], /setSaveMsg\([^,]+,\s*success === false\)/);
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

test('accepting a friend does not wipe offline auto mappings on the follow-up save', async () => {
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

  h.api.load(['new']);
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

  assert.equal(h.api.saveCalls, 1);
  const sources = h.api.savedAuto.flatMap((m) => m.RemoteLibrarySources);
  assert.ok(sources.some((s) => s.ServerId === 'new' && s.RemoteLibraryId === 'lib-c'));
  assert.ok(sources.some((s) => s.ServerId === 'offline' && s.RemoteLibraryId === 'lib-b'));
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

  h.api.load(['new']);
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
