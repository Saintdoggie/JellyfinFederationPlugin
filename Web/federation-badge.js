(function () {
  'use strict';

  // Idempotent: index.html injection only happens once per server, but this
  // guards against the script somehow landing on the page twice anyway.
  if (window.__federationBadgeInit) {
    return;
  }

  window.__federationBadgeInit = true;

  var STYLE_ID = 'federation-badge-style';

  // A solid cloud reads at any size as "this content lives elsewhere and is
  // streamed" - the previous outlined cloud-with-an-up-arrow read more like
  // "upload" than "streamed from somewhere else", which was the opposite of
  // the fact being conveyed. Filled shape rather than stroked so the corner
  // badge (12px) doesn't come out as a few faint hairlines over poster art.
  var ICON_SVG =
    '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true" focusable="false">' +
    '<path d="M6.5 19h11a4.5 4.5 0 0 0 .5-8.97 6 6 0 0 0-11.66-1.5A3.75 3.75 0 0 0 6.5 19z"></path>' +
    '</svg>';

  function injectStyle() {
    if (document.getElementById(STYLE_ID)) {
      return;
    }

    var style = document.createElement('style');
    style.id = STYLE_ID;
    // Presentation matches jellyfin-web's own detail-page vocabulary rather
    // than drawing custom chrome: the source tag renders like one more muted
    // entry in the itemMiscInfo line (year / runtime / resolution), and
    // Download / Hide are entries in the native "..." action sheet, built
    // from the exact same markup/classes jellyfin-web uses for its own
    // entries (Refresh metadata, Delete, ...) so they're indistinguishable
    // in shape, font, and hover/focus state.
    style.textContent = [
      // Corner overlay for gallery/grid cards. Top-left, since Jellyfin's own
      // played-checkmark and unwatched-count badges (which use the theme
      // accent color) live top-right/bottom-right. Deliberately NOT the theme
      // accent - a matching-color blue disc top-left read as another "state"
      // badge like the played checkmark rather than "info about where this
      // item comes from". Dark chip with a light cloud is neutral, always
      // legible over any poster art, and doesn't compete visually with
      // Jellyfin's own colored state badges.
      '.federation-badge-corner{position:absolute;top:6px;left:6px;width:22px;height:22px;border-radius:6px;',
      'background:rgba(0,0,0,.72);color:#fff;',
      'display:flex;align-items:center;justify-content:center;',
      'z-index:3;pointer-events:none;',
      'box-shadow:0 1px 3px rgba(0,0,0,.55);}',
      '.federation-badge-corner svg{width:13px;height:13px;opacity:.95;}',

      // Progress ring shown centered over a card/poster while an item is
      // actively being downloaded. Keeps its own dark backing plate since it
      // overlays arbitrary poster art, not page chrome.
      '.federation-download-ring{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);',
      'z-index:4;pointer-events:none;filter:drop-shadow(0 1px 4px rgba(0,0,0,.6));}',
      '.federation-ring-bg{fill:rgba(10,12,16,.66);stroke:rgba(255,255,255,.2);stroke-width:1;}',
      '.federation-ring-fg{fill:none;stroke:var(--theme-primary-color,#00a4dc);stroke-width:3;stroke-linecap:round;}',
      '.federation-ring-text{fill:#fff;font-size:9px;font-weight:700;font-family:inherit;}',

      // Source tag in the detail page's itemMiscInfo line: same muted text
      // treatment as the year/runtime entries around it - no border, no fill,
      // no uppercase. Inline svg sized off the surrounding font so it stays
      // proportional.
      '.federation-source-tag{display:inline-flex;align-items:center;gap:.3em;opacity:.7;margin-left:.5em;vertical-align:middle;}',
      '.federation-source-tag svg{width:1em;height:1em;flex-shrink:0;}',

      // Download/Hide entries injected into the native "..." action sheet
      // (.actionSheetMenuItem) - no rules of our own needed beyond a disabled
      // look while busy, since every other visual (icon, label, hover, focus)
      // already comes from that class.
      '.federation-actionsheet-item[data-fed-state="busy"]{opacity:.65;pointer-events:none;}'
    ].join('');
    document.head.appendChild(style);
  }

  function normalizeId(id) {
    return (id || '').replace(/-/g, '').toLowerCase();
  }

  // id -> source server name ('' when unknown). A map rather than a set so the
  // badge can name the server instead of just asserting "not local".
  var federatedIds = new Map();

  function refreshFederatedIds() {
    fetch('/Plugins/Federation/FederatedIds', { credentials: 'same-origin' })
      .then(function (res) {
        return res.ok ? res.json() : {};
      })
      .then(function (data) {
        var next = new Map();
        if (Array.isArray(data)) {
          // Older servers returned a bare id list; still honour it so a
          // half-upgraded setup degrades to an unlabelled badge rather than none.
          data.forEach(function (id) { next.set(normalizeId(id), ''); });
        } else {
          Object.keys(data || {}).forEach(function (id) {
            next.set(normalizeId(id), data[id] || '');
          });
        }

        federatedIds = next;
      })
      .catch(function () {
        // Leave the previous set in place; try again on the next interval.
      });
  }

  function badgeCard(el) {
    var id = normalizeId(el.getAttribute('data-id'));
    if (!id || !federatedIds.has(id)) {
      // Not federated (or not known yet) - skip this element for good so we
      // don't keep re-checking it on every scan.
      el.setAttribute('data-federation-badge', '0');
      return;
    }

    if (!el.querySelector('.federation-badge-corner')) {
      // Absolutely positioned relative to the card itself rather than a
      // specific inner image wrapper - jellyfin-web card markup varies by
      // layout, but the poster is consistently the first thing in the
      // card, so a top-left corner badge on the outer element lands on the
      // poster either way, without depending on internal class names.
      if (window.getComputedStyle(el).position === 'static') {
        el.style.position = 'relative';
      }

      var srv = federatedIds.get(id);
      var badge = document.createElement('div');
      badge.className = 'federation-badge-corner';
      badge.title = srv ? ('Streamed from ' + srv) : 'Streamed from another server';
      badge.innerHTML = ICON_SVG;
      el.appendChild(badge);
    }

    el.setAttribute('data-federation-badge', '1');
    updateDownloadRing(el, id);
  }

  // -------------------------------------------------------------------------
  // Download progress ring
  //
  // A circular progress ring drawn centered over an item's existing cover art
  // (the federated item already has real artwork - it's synced with full
  // metadata, only its Path is a remote URL - so there's no separate "add to
  // Jellyfin with cover art" step needed here, just an overlay on the card/
  // poster that's already showing it) for every card matching an in-progress
  // download, wherever that card currently appears (grid, search, home rows,
  // the detail page's own poster) - not just the item whose detail page
  // happens to be open, which is why this polls independently of
  // pollDownloadProgress's own per-item loop.
  var GUID_RE = /[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i;

  // localItemId (normalized) -> download record, active downloads only.
  var activeDownloadsByItem = new Map();

  function formatSpeed(bytesPerSecond) {
    if (!bytesPerSecond || bytesPerSecond <= 0) {
      return '';
    }

    var mbPerSec = bytesPerSecond / (1024 * 1024);
    if (mbPerSec >= 0.1) {
      return mbPerSec.toFixed(1) + ' MB/s';
    }

    return Math.round(bytesPerSecond / 1024) + ' KB/s';
  }

  function ringSvg(pct, sizePx) {
    var clamped = Math.max(0, Math.min(100, pct || 0));
    return '<svg viewBox="0 0 36 36" width="' + sizePx + '" height="' + sizePx + '">'
      + '<circle cx="18" cy="18" r="16" class="federation-ring-bg"></circle>'
      + '<circle cx="18" cy="18" r="15.9155" class="federation-ring-fg" stroke-dasharray="' + clamped + ' 100" transform="rotate(-90 18 18)"></circle>'
      + '<text x="18" y="20.5" text-anchor="middle" class="federation-ring-text">' + Math.round(clamped) + '%</text>'
      + '</svg>';
  }

  function applyDownloadRing(container, pct, sizePx) {
    if (window.getComputedStyle(container).position === 'static') {
      container.style.position = 'relative';
    }

    var ring = container.querySelector(':scope > .federation-download-ring');
    if (!ring) {
      ring = document.createElement('div');
      ring.className = 'federation-download-ring';
      container.appendChild(ring);
    }

    ring.innerHTML = ringSvg(pct, sizePx);
  }

  function removeDownloadRing(container) {
    var ring = container.querySelector(':scope > .federation-download-ring');
    if (ring) {
      ring.remove();
    }
  }

  // Applies/removes the ring on one grid/list card, from whatever is
  // currently known (either the periodic list poll below, or a live percent
  // passed straight from the detail page's own per-item poll).
  function updateDownloadRing(el, id, pctOverride) {
    if (typeof pctOverride === 'number') {
      applyDownloadRing(el, pctOverride, 46);
      return;
    }

    var dl = activeDownloadsByItem.get(id);
    if (dl) {
      applyDownloadRing(el, dl.percentComplete, 46);
    } else {
      removeDownloadRing(el);
    }
  }

  // Re-applies ring state to every already-badged card on screen (badgeCard()
  // itself only runs once per card, on first sight - see CARD_SELECTOR's
  // :not([data-federation-badge]) - so a percentage that changes after that
  // needs this separate sweep instead).
  function refreshDownloadRingsOnCards() {
    document.querySelectorAll('.card[data-federation-badge="1"],.listItem[data-federation-badge="1"]').forEach(function (el) {
      updateDownloadRing(el, normalizeId(el.getAttribute('data-id')));
    });
  }

  var DETAIL_POSTER_SELECTORS = [
    '.detailImageContainer .cardImageContainer',
    '.detailImageContainer img',
    '.itemDetailImage',
    '.detailPagePrimaryContainer .cardImageContainer',
    '.detailPageImageContainer .cardImageContainer',
    '.detailPageImageContainer'
  ];

  function findDetailPoster() {
    for (var i = 0; i < DETAIL_POSTER_SELECTORS.length; i++) {
      var el = document.querySelector(DETAIL_POSTER_SELECTORS[i]);
      if (el) {
        return el;
      }
    }

    return null;
  }

  // Called two ways: with no args, from the periodic list poll/scan cycle
  // (looks up whatever item the current URL is for); with (itemId, pct),
  // directly from pollDownloadProgress's own tighter per-item loop, which
  // already knows the exact percent without waiting for the next list poll.
  function updateDetailPageRing(itemId, pct) {
    var match = location.href.match(GUID_RE);
    if (!match) {
      return;
    }

    var poster = findDetailPoster();
    if (!poster) {
      return;
    }

    if (typeof pct === 'number') {
      if (normalizeId(match[0]) !== normalizeId(itemId)) {
        return;
      }

      applyDownloadRing(poster, pct, 96);
      return;
    }

    var dl = activeDownloadsByItem.get(normalizeId(match[0]));
    if (dl) {
      applyDownloadRing(poster, dl.percentComplete, 96);
    } else {
      removeDownloadRing(poster);
    }
  }

  function refreshDownloadsList() {
    var token = getToken();
    fetch('/Plugins/Federation/Downloads', {
      credentials: 'same-origin',
      headers: token ? { 'X-Emby-Token': token } : {}
    })
      .then(function (res) { return res.ok ? res.json() : []; })
      .then(function (list) {
        var next = new Map();
        (list || []).forEach(function (d) {
          if (!d.isComplete && d.localItemId) {
            next.set(normalizeId(d.localItemId), d);
          }
        });

        activeDownloadsByItem = next;
        refreshDownloadRingsOnCards();
        updateDetailPageRing();
      })
      .catch(function () {
        // Leave the previous set in place; try again on the next interval.
      });
  }

  // Same token-resolution strategy as the admin config page: prefer the SPA's
  // own ApiClient (works for whoever is actually logged in), fall back to the
  // credentials Jellyfin's web client keeps in localStorage for this origin.
  function getToken() {
    try {
      if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
        var t = window.ApiClient.accessToken();
        if (t) {
          return t;
        }
      }
    } catch (e) { /* fall through */ }

    try {
      var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
      var servers = creds.Servers || [];
      var origin = window.location.origin.replace(/\/+$/, '');
      for (var i = 0; i < servers.length; i++) {
        var s = servers[i];
        if (s && s.AccessToken && s.Url && String(s.Url).replace(/\/+$/, '') === origin) {
          return s.AccessToken;
        }
      }
    } catch (e) { /* no credentials available */ }

    return null;
  }

  // A floating bottom-of-screen toast used to live here for download
  // progress/completion. Removed per feedback: it read as this script
  // editing jellyfin-web's own chrome, which wasn't wanted. Progress is
  // already visible via the button state next to the native action buttons,
  // the progress ring on the cover art, and the Downloads section on the
  // settings page - these are now no-ops so every existing call site doesn't
  // need to change.
  function showToast() { /* intentionally no-op - see comment above */ }

  function hideToastAfter() { /* intentionally no-op - see comment above */ }

  // Persists in-flight downloads (itemId -> {operationId, itemName,
  // startedAt}) so a page refresh or navigating away mid-download doesn't
  // lose track of it - reloading the item's detail page (or just reloading
  // the current page, via resumeActiveDownloads at startup) picks the poll
  // back up and restores the button/ring state instead of silently going
  // quiet, which otherwise looks identical to "did this actually start?".
  var ACTIVE_DOWNLOADS_KEY = 'federationActiveDownloads';

  function loadActiveDownloads() {
    try {
      var raw = JSON.parse(localStorage.getItem(ACTIVE_DOWNLOADS_KEY) || '{}');
      return raw && typeof raw === 'object' ? raw : {};
    } catch (e) {
      return {};
    }
  }

  function saveActiveDownloads(map) {
    try {
      localStorage.setItem(ACTIVE_DOWNLOADS_KEY, JSON.stringify(map));
    } catch (e) { /* localStorage unavailable - download still works this session */ }
  }

  function setActiveDownload(itemId, operationId, itemName) {
    var map = loadActiveDownloads();
    map[itemId] = { operationId: operationId, itemName: itemName || '', startedAt: Date.now() };
    saveActiveDownloads(map);
  }

  function clearActiveDownload(itemId) {
    var map = loadActiveDownloads();
    delete map[itemId];
    saveActiveDownloads(map);
  }

  // Only one poll loop per operation, even if badgeDetailPage() re-runs (it
  // runs on every scan) and tries to resume the same in-progress download
  // again while it's already being polled.
  var pollingOperations = {};

  function pollDownloadProgress(itemId, operationId, button) {
    if (pollingOperations[operationId]) {
      return;
    }

    pollingOperations[operationId] = true;

    var poll = function () {
      var token = getToken();
      fetch('/Plugins/Federation/Download/Progress/' + operationId, {
        credentials: 'same-origin',
        headers: token ? { 'X-Emby-Token': token } : {}
      })
        .then(function (res) { return res.ok ? res.json() : null; })
        .then(function (data) {
          if (!data) {
            return;
          }

          // The button only exists for as long as the "..." action sheet it
          // was clicked from stays open (the sheet closes itself right after
          // any click inside it) - null once that's gone, same as the
          // resumeActiveDownloads() case, which never had a button to begin
          // with. setButtonState() and the two callers below already guard
          // against a null button; the poll keeps running regardless, since
          // updateDetailPageRing() below is what actually shows progress.
          var liveButton = button;

          if (!data.isComplete) {
            var pct = Math.round(data.percentComplete || 0);
            var speed = formatSpeed(data.bytesPerSecond);
            if (liveButton) {
              setButtonState(liveButton, 'busy', pct + '%' + (speed ? ' ' + speed : ''), 'Downloading to this server');
            }

            updateDetailPageRing(itemId, pct);
            setTimeout(poll, 1500);
            return;
          }

          delete pollingOperations[operationId];
          clearActiveDownload(itemId);
          activeDownloadsByItem.delete(normalizeId(itemId));
          refreshDownloadRingsOnCards();
          var finishedPoster = findDetailPoster();
          if (finishedPoster) {
            removeDownloadRing(finishedPoster);
          }

          if (data.success) {
            if (liveButton) {
              setButtonState(liveButton, 'done', 'Saved', 'A local copy now exists on this server');
            }
          } else {
            var cancelled = /cancel/i.test(data.status || '');
            if (liveButton) {
              setButtonState(liveButton, 'error', cancelled ? 'Download' : 'Download failed', data.status || 'Download failed');
            }
          }
        })
        .catch(function () {
          setTimeout(poll, 3000);
        });
    };

    poll();
  }

  function cancelDownload(button) {
    var operationId = button.getAttribute('data-operation-id');
    if (!operationId) {
      return;
    }

    setButtonState(button, 'busy', 'Cancelling', 'Cancelling download');

    var token = getToken();
    fetch('/Plugins/Federation/Download/Cancel/' + operationId, {
      method: 'POST',
      credentials: 'same-origin',
      headers: token ? { 'X-Emby-Token': token } : {}
    }).catch(function () { /* the next progress poll tick reconciles state regardless */ });
  }

  function startDownload(button, itemId) {
    setButtonState(button, 'busy', 'Starting', 'Starting download');

    var token = getToken();
    fetch('/Plugins/Federation/Download', {
      method: 'POST',
      credentials: 'same-origin',
      headers: Object.assign({ 'Content-Type': 'application/json' }, token ? { 'X-Emby-Token': token } : {}),
      body: JSON.stringify({ ItemId: itemId })
    })
      .then(function (res) { return res.json().then(function (data) { return { ok: res.ok, data: data }; }); })
      .then(function (result) {
        if (!result.ok || !result.data || !result.data.operationId) {
          var msg = (result.data && result.data.message) || 'Could not start download';
          setButtonState(button, 'error', 'Download failed', msg);
          return;
        }

        button.setAttribute('data-operation-id', result.data.operationId);
        setActiveDownload(itemId, result.data.operationId, '');
        pollDownloadProgress(itemId, result.data.operationId, button);
      })
      .catch(function () {
        setButtonState(button, 'error', 'Download failed', 'Could not start download');
      });
  }

  // Called once at script init: resumes polling for any download that was
  // still in progress when the page was last unloaded, so a refresh
  // mid-download doesn't just silently drop it.
  function resumeActiveDownloads() {
    var map = loadActiveDownloads();
    var now = Date.now();
    var changed = false;
    Object.keys(map).forEach(function (itemId) {
      var entry = map[itemId];
      // Drop anything implausibly old rather than polling forever if the
      // server-side tracker entry is long gone (server restarted, etc.).
      if (!entry || now - (entry.startedAt || 0) > 6 * 60 * 60 * 1000) {
        delete map[itemId];
        changed = true;
        return;
      }

      pollDownloadProgress(itemId, entry.operationId, null);
    });

    if (changed) {
      saveActiveDownloads(map);
    }
  }

  // Hides this item from the admin's own local library going forward: a
  // purely local, receiving-side "don't show me this" choice (a low-quality
  // rip already owned in better quality, unwanted clutter, ...) - it never
  // reaches the friend server, which keeps sharing normally. See
  // Configuration/FederationPluginController.cs's "Hidden Items" region and
  // PluginConfiguration.HiddenFederatedItemIds for the backend half.
  function startHide(button, itemId) {
    setButtonState(button, 'busy', 'Hiding', 'Hiding this item');

    var token = getToken();
    fetch('/Plugins/Federation/HiddenItems/Hide', {
      method: 'POST',
      credentials: 'same-origin',
      headers: Object.assign({ 'Content-Type': 'application/json' }, token ? { 'X-Emby-Token': token } : {}),
      body: JSON.stringify({ ItemId: itemId })
    })
      .then(function (res) { return res.json().then(function (data) { return { ok: res.ok, data: data }; }); })
      .then(function (result) {
        if (!result.ok || !result.data || !result.data.success) {
          setButtonState(button, 'error', 'Hide failed', (result.data && result.data.message) || 'Could not hide this item');
          return;
        }

        setButtonState(button, 'done', 'Hidden', 'Hidden from this library');

        // The server already deleted the local item, so this page has
        // nothing left to show - stepping back is the same recovery the
        // browser's own Back button gives after any item disappears out
        // from under a detail page. A short delay lets the "Hidden" state
        // actually register before the page navigates away.
        setTimeout(function () {
          if (history.length > 1) {
            history.back();
          }
        }, 700);
      })
      .catch(function () {
        setButtonState(button, 'error', 'Hide failed', 'Could not hide this item');
      });
  }

  // -------------------------------------------------------------------------
  // Detail page: native-looking chrome only.
  //
  //   - The source server renders as a muted entry appended to the page's own
  //     itemMiscInfo line (the "2023 - TV-MA - 1080p" row), with the cloud
  //     glyph - same visual weight as the metadata around it, not a chip.
  //   - Download and Hide are entries in the native "..." action sheet
  //     (jellyfin-web's own more-commands menu: Refresh metadata, Delete,
  //     ...), built from that menu's own .actionSheetMenuItem markup so they
  //     read as two more native commands rather than custom chrome.
  // -------------------------------------------------------------------------

  function setButtonState(button, state, label, title) {
    if (!button) {
      return;
    }

    button.setAttribute('data-fed-state', state);
    button.title = title || '';
    var text = button.querySelector('.federation-btn-label');
    if (text) {
      text.textContent = label;
    }
  }

  function injectSourceTag(rawId, srv) {
    var label = srv ? ('Streamed from ' + srv) : 'Streamed from another server';
    var name = srv || 'Another server';
    var info = document.querySelector('.itemMiscInfo-primary') || document.querySelector('.itemMiscInfo');
    if (!info) {
      return;
    }

    // Remove a tag left over from a previous item before adding the current
    // one (SPA navigation can keep the surrounding DOM).
    var old = info.querySelector('.federation-source-tag');
    if (old) {
      old.remove();
    }

    var tag = document.createElement('span');
    tag.className = 'federation-source-tag';
    tag.title = label;
    tag.innerHTML = ICON_SVG;
    var text = document.createElement('span');
    text.textContent = name;
    tag.appendChild(text);
    info.appendChild(tag);
  }

  // Builds one entry for jellyfin-web's own "..." action sheet, matching the
  // exact markup its actionsheet component renders for its own commands
  // (Refresh metadata, Delete, ...) - see actionSheetMenuItem/listItemBody/
  // listItemBodyText in jellyfin-web's bundled actionsheet component. The
  // sheet's own delegated click handler (bound on the sheet root, not on each
  // item) walks up to the nearest .actionSheetMenuItem on any click inside it
  // and closes the sheet right after - so this listener only has to run the
  // command; closing is already handled for us.
  function makeActionSheetItem(iconName, label, dataId, onClick) {
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'listItem listItem-button actionSheetMenuItem federation-actionsheet-item';
    button.setAttribute('data-id', dataId);
    button.innerHTML =
      '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ' + iconName + '" aria-hidden="true"></span>' +
      '<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText federation-btn-label"></div></div>';
    button.querySelector('.federation-btn-label').textContent = label;
    button.addEventListener('click', function () { onClick(button); });
    return button;
  }

  // Waits for the actionsheet the "..." button just opened to render (there's
  // no synchronous hook: jellyfin-web looks up the item and the current
  // user's permissions, itself async, before calling into the actionsheet
  // module), then adds Download/Hide to the top of it. Guards against
  // double-injecting into the same open sheet since the more-commands click
  // handler and this can both run more than once in edge cases.
  function injectActionSheetItems(rawId) {
    var attempts = 0;
    function tryInject() {
      var scroller = document.querySelector('.actionSheetScroller');
      if (!scroller) {
        attempts++;
        if (attempts < 20) {
          requestAnimationFrame(tryInject);
        }

        return;
      }

      if (scroller.querySelector('.federation-actionsheet-item')) {
        return;
      }

      var active = loadActiveDownloads()[rawId];
      var downloadItem = makeActionSheetItem(
        active ? 'cloud_off' : 'cloud_download',
        active ? 'Cancel download' : 'Download',
        'federation-download',
        function (btn) {
          if (active) {
            cancelDownload(btn);
          } else {
            startDownload(btn, rawId);
          }
        });
      if (active) {
        downloadItem.setAttribute('data-operation-id', active.operationId);
      }

      var hideItem = makeActionSheetItem(
        'visibility_off',
        'Hide',
        'federation-hide',
        function (btn) { startHide(btn, rawId); });

      scroller.insertBefore(hideItem, scroller.firstChild);
      scroller.insertBefore(downloadItem, scroller.firstChild);
    }

    requestAnimationFrame(tryInject);
  }

  // jellyfin-web reuses the same itemDetails view instance (and so the same
  // .btnMoreCommands element) across navigations between different items'
  // detail pages, rather than recreating it per item - so the click listener
  // below is bound exactly once and must read the CURRENT item id at click
  // time, never one captured in its own closure, or the menu would keep
  // acting on whichever federated item's page happened to bind it first.
  var currentFederatedItemId = null;

  function bindMoreCommandsMenu() {
    var btn = document.querySelector('.btnMoreCommands');
    if (!btn || btn.dataset.federationBound) {
      return;
    }

    btn.dataset.federationBound = 'true';
    btn.addEventListener('click', function () {
      if (currentFederatedItemId) {
        injectActionSheetItems(currentFederatedItemId);
      }
    });
  }

  function badgeDetailPage() {
    var match = location.href.match(/[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i);
    var rawId = match ? match[0] : null;
    var id = rawId ? normalizeId(rawId) : null;

    if (!id || !federatedIds.has(id)) {
      // Leaving a federated item's detail page (or never on one) - clear
      // this so a stale id can't leak into the next click on a page whose
      // item isn't federated at all.
      currentFederatedItemId = null;
      return;
    }

    currentFederatedItemId = rawId;
    injectSourceTag(rawId, federatedIds.get(id));
    bindMoreCommandsMenu();
  }

  // Only real poster cards. "[data-id]" alone also matches the action buttons
  // inside a card's hover overlay (played, favourite, context menu) and the
  // card's own text/footer container - all of which carry the same data-id -
  // so every federated title sprouted the icon three or four times over,
  // including in the middle of the hover controls. Restricting to the card
  // element itself gives exactly one badge per title.
  var CARD_SELECTOR = [
    '.card[data-id]:not([data-federation-badge])',
    '.listItem[data-id]:not([data-federation-badge])'
  ].join(',');

  function scan() {
    injectStyle();
    document.querySelectorAll(CARD_SELECTOR).forEach(badgeCard);
    badgeDetailPage();
    refreshDownloadRingsOnCards();
    updateDetailPageRing();
  }

  var scheduled = false;
  function scheduleScan() {
    if (scheduled) {
      return;
    }

    scheduled = true;
    requestAnimationFrame(function () {
      scheduled = false;
      scan();
    });
  }

  refreshFederatedIds();
  setInterval(refreshFederatedIds, 5 * 60 * 1000);
  resumeActiveDownloads();

  refreshDownloadsList();
  setInterval(refreshDownloadsList, 3000);

  // jellyfin-web is a client-routed SPA (pushState navigation), which does
  // not reliably fire DOM mutations synchronously with a route change - this
  // catches detail-page navigation the MutationObserver below might miss.
  setInterval(scheduleScan, 1500);

  var observer = new MutationObserver(scheduleScan);
  observer.observe(document.body, { childList: true, subtree: true });

  scheduleScan();
})();
