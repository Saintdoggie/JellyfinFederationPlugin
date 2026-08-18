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

  // Small icon inline with text, used only on the detail page (a fixed,
  // always-visible spot, unlike a gallery card that may be scrolled past in
  // a fraction of a second with its text never even shown).
  var INLINE_ICON_HTML = '<span class="federation-badge-icon">' + ICON_SVG + '</span>';

  var DOWNLOAD_ICON_SVG =
    '<svg viewBox="0 0 24 24" fill="none" ' +
    'stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' +
    '<path d="M12 4v11"></path>' +
    '<path d="M7 11l5 5 5-5"></path>' +
    '<path d="M5 20h14"></path>' +
    '</svg>';

  // Eye-with-a-slash: the standard "hide this" glyph, distinct enough from the
  // cloud (source) and download-tray (save-a-copy) icons beside it that all
  // three read as different actions at a glance.
  var HIDE_ICON_SVG =
    '<svg viewBox="0 0 24 24" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' +
    '<path d="M3 12s3.6-7 9-7c1.7 0 3.2.5 4.5 1.3M21 12s-3.6 7-9 7c-1.7 0-3.2-.5-4.5-1.3"></path>' +
    '<path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"></path>' +
    '<path d="M3 3l18 18"></path>' +
    '</svg>';

  function injectStyle() {
    if (document.getElementById(STYLE_ID)) {
      return;
    }

    var style = document.createElement('style');
    style.id = STYLE_ID;
    // Whole visual language reworked (0.0.64):
    //   - font-size on the action pills was .5em relative to the surrounding
    //     H1, which came out at about 10 px on a 20 px title - tiny, cramped,
    //     and the reason the row read as "willy nilly" additions to the page
    //     rather than part of it. Fixed to a real pixel size like every other
    //     Jellyfin control.
    //   - Chips now use Jellyfin's own theme variables (--theme-primary-color,
    //     etc.) with sensible fallbacks, so they inherit whatever theme the
    //     user is running instead of being a hard-coded blue/brown/red palette
    //     that clashes with everything but the default dark theme.
    //   - Neutral outlined chips for source-name and Hide (this is metadata,
    //     not a call to action); a filled accent chip only for Download, which
    //     is the actual action on the row.
    //   - Corner badge: solid disc using the theme accent color, so the
    //     "federated" mark reads at a glance even on a light-background poster
    //     that swallowed the previous translucent-black disc.
    style.textContent = [
      // Inline icon next to text - sizes off the surrounding font-size so
      // it stays proportional whatever container it lands in.
      '.federation-badge-icon{display:inline-flex;width:1em;height:1em;margin-right:.3em;flex-shrink:0;align-items:center;justify-content:center;}',
      '.federation-badge-icon svg{width:100%;height:100%;display:block;}',

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

      // Row of chips sits below the title. Fixed pixel font-size instead of
      // .5em: the previous relative sizing shrank against big H1 titles into
      // something that looked pasted on. gap is a bit larger so chips aren't
      // touching each other.
      '.federation-badge-row{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin:10px 0 14px;font-size:12px;line-height:1;}',

      // Shared chip shape. Neutral outlined by default - reads as metadata,
      // not a shouty call to action. Individual chip classes below tint it.
      '.federation-badge-row > span{display:inline-flex;align-items:center;gap:6px;vertical-align:middle;',
      'padding:6px 12px;border-radius:999px;font-weight:600;letter-spacing:.03em;',
      'text-transform:uppercase;white-space:nowrap;',
      'background:rgba(255,255,255,.06);color:inherit;border:1px solid rgba(255,255,255,.16);',
      'transition:background-color .15s ease,border-color .15s ease;}',

      // Source pill - names the server. Slightly more prominent than pure
      // outlined so it reads as the first thing in the row, but not clickable
      // colored so nobody tries to press it. Uses the theme accent for its
      // border/icon tint.
      '.federation-badge-pill{color:var(--theme-primary-color,#00a4dc);',
      'border-color:color-mix(in srgb,var(--theme-primary-color,#00a4dc) 45%,transparent) !important;',
      'background:color-mix(in srgb,var(--theme-primary-color,#00a4dc) 10%,transparent) !important;}',

      // Download - the one action chip on the row, filled in the theme accent
      // so it visually invites a click.
      '.federation-badge-download{cursor:pointer;color:#fff !important;',
      'background:var(--theme-primary-color,#00a4dc) !important;border-color:transparent !important;}',
      '.federation-badge-download:hover{filter:brightness(1.1);}',
      '.federation-badge-download[data-state="busy"]{cursor:default;opacity:.85;}',
      '.federation-badge-download[data-state="done"]{background:#2e8b57 !important;cursor:default;}',
      '.federation-badge-download[data-state="error"]{background:#a83232 !important;}',

      // Hide - the metadata-y neutral outlined chip, no fill, no accent -
      // matches the default styling above.
      '.federation-badge-hide{cursor:pointer;}',
      '.federation-badge-hide:hover{background:rgba(255,255,255,.12) !important;border-color:rgba(255,255,255,.28) !important;}',
      '.federation-badge-hide[data-state="busy"]{cursor:default;opacity:.85;}',
      '.federation-badge-hide[data-state="done"]{background:#2e8b57 !important;border-color:transparent !important;color:#fff !important;cursor:default;}',
      '.federation-badge-hide[data-state="error"]{background:#a83232 !important;border-color:transparent !important;color:#fff !important;}',

      // Light-theme override - the outlined-on-transparent look above assumes
      // a dark page background. Under Jellyfin's built-in light theme the
      // white-alpha border/background disappear against a white page.
      '@media (prefers-color-scheme:light){',
      '.federation-badge-row > span{background:rgba(0,0,0,.04);border-color:rgba(0,0,0,.14);}',
      '.federation-badge-hide:hover{background:rgba(0,0,0,.08) !important;border-color:rgba(0,0,0,.24) !important;}',
      '}'
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
  // already visible via the pill next to the title, the progress ring on
  // the cover art, and the Downloads section on the settings page - these
  // are now no-ops so every existing call site doesn't need to change.
  function showToast() { /* intentionally no-op - see comment above */ }

  function hideToastAfter() { /* intentionally no-op - see comment above */ }

  // Persists in-flight downloads (itemId -> {operationId, itemName,
  // startedAt}) so a page refresh or navigating away mid-download doesn't
  // lose track of it - reloading the item's detail page (or just reloading
  // the current page, via resumeActiveDownloads at startup) picks the poll
  // back up and restores the pill/toast state instead of silently going
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

          // The button only exists while this exact item's detail page is
          // still open - re-find it each tick rather than trusting the
          // reference passed in, since badgeDetailPage() may have rebuilt it.
          var liveButton = button;

          if (!data.isComplete) {
            var pct = Math.round(data.percentComplete || 0);
            var speed = formatSpeed(data.bytesPerSecond);
            showToast('Downloading ' + (data.itemName ? data.itemName + ' - ' : '') + pct + '%' + (speed ? ' (' + speed + ')' : ''), 'busy');
            if (liveButton) {
              liveButton.setAttribute('data-state', 'busy');
              liveButton.setAttribute('data-operation-id', operationId);
              liveButton.querySelector('.federation-badge-label').textContent = pct + '%' + (speed ? ' - ' + speed : '');
              liveButton.title = 'Click to cancel';
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
            showToast('Downloaded ' + (data.itemName || 'item') + ' to this server', 'done');
            if (liveButton) {
              liveButton.setAttribute('data-state', 'done');
              liveButton.removeAttribute('data-operation-id');
              liveButton.querySelector('.federation-badge-label').textContent = 'Downloaded';
            }
          } else {
            var cancelled = /cancel/i.test(data.status || '');
            showToast(data.status || 'Download failed', cancelled ? 'busy' : 'error');
            if (liveButton) {
              liveButton.setAttribute('data-state', 'error');
              liveButton.removeAttribute('data-operation-id');
              liveButton.querySelector('.federation-badge-label').textContent = cancelled ? 'Download to server' : 'Failed - retry?';
              liveButton.title = data.status || 'Download failed';
            }
          }

          hideToastAfter(6000);
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

    button.querySelector('.federation-badge-label').textContent = 'Cancelling...';

    var token = getToken();
    fetch('/Plugins/Federation/Download/Cancel/' + operationId, {
      method: 'POST',
      credentials: 'same-origin',
      headers: token ? { 'X-Emby-Token': token } : {}
    }).catch(function () { /* the next progress poll tick reconciles state regardless */ });
  }

  function startDownload(button, itemId) {
    button.setAttribute('data-state', 'busy');
    button.querySelector('.federation-badge-label').textContent = 'Starting...';
    showToast('Starting download...', 'busy');

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
          button.setAttribute('data-state', 'error');
          button.querySelector('.federation-badge-label').textContent = 'Failed - retry?';
          button.title = msg;
          showToast(msg, 'error');
          hideToastAfter(6000);
          return;
        }

        button.setAttribute('data-operation-id', result.data.operationId);
        setActiveDownload(itemId, result.data.operationId, '');
        pollDownloadProgress(itemId, result.data.operationId, button);
      })
      .catch(function () {
        button.setAttribute('data-state', 'error');
        button.querySelector('.federation-badge-label').textContent = 'Failed - retry?';
        showToast('Could not start download', 'error');
        hideToastAfter(6000);
      });
  }

  // Called once at script init: resumes polling (and re-shows the toast)
  // for any download that was still in progress when the page was last
  // unloaded, so a refresh mid-download doesn't just silently drop it.
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
    button.setAttribute('data-state', 'busy');
    button.querySelector('.federation-badge-label').textContent = 'Hiding...';

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
          button.setAttribute('data-state', 'error');
          button.querySelector('.federation-badge-label').textContent = 'Failed';
          button.title = (result.data && result.data.message) || 'Could not hide this item';
          return;
        }

        button.setAttribute('data-state', 'done');
        button.querySelector('.federation-badge-label').textContent = 'Hidden';

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
        button.setAttribute('data-state', 'error');
        button.querySelector('.federation-badge-label').textContent = 'Failed';
      });
  }

  function badgeDetailPage() {
    var match = location.href.match(/[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i);
    if (!match) {
      return;
    }

    var rawId = match[0];
    var id = normalizeId(rawId);
    if (!federatedIds.has(id)) {
      return;
    }

    var srv = federatedIds.get(id);
    var label = srv ? ('Streamed from ' + srv) : 'Streamed from another server';
    // Rendered as its own row below the title (see insertAdjacentHTML
    // 'afterend' on the title's container below) instead of inline before
    // the title text - inline collided with stylized show-logo titles badly
    // enough to look broken rather than like an action bar.
    var pill = '<div class="federation-badge-row">'
      + '<span class="federation-badge-pill" title="' + label.replace(/"/g, '&quot;') + '">'
      + INLINE_ICON_HTML + '<span>' + (srv || 'Another server') + '</span></span>'
      + '<span class="federation-badge-download" data-state="idle" title="Save a local copy on this server">'
      + '<span class="federation-badge-icon">' + DOWNLOAD_ICON_SVG + '</span><span class="federation-badge-label">Download to server</span></span>'
      + '<span class="federation-badge-hide" data-state="idle" title="Hide this item from your local library (does not affect the friend sharing it)">'
      + '<span class="federation-badge-icon">' + HIDE_ICON_SVG + '</span><span class="federation-badge-label">Hide</span></span>'
      + '</div>';

    var selectors = ['.nameContainer bdi', '.itemName-primary bdi', '.detailPagePrimaryContainer h1 bdi', 'h1 bdi'];
    for (var i = 0; i < selectors.length; i++) {
      var title = document.querySelector(selectors[i]);
      if (!title) {
        continue;
      }

      var container = title.closest('.nameContainer, .itemName-primary, .detailPagePrimaryContainer') || title.parentElement;
      if (container.nextElementSibling && container.nextElementSibling.classList && container.nextElementSibling.classList.contains('federation-badge-row')) {
        return;
      }

      container.insertAdjacentHTML('afterend', pill);
      var row = container.nextElementSibling;

      var downloadBtn = row.querySelector('.federation-badge-download');
      if (downloadBtn) {
        // Already downloading (survives a refresh) - reflect that instead
        // of showing an idle button someone could click a second time.
        var active = loadActiveDownloads()[rawId];
        if (active) {
          downloadBtn.setAttribute('data-state', 'busy');
          downloadBtn.setAttribute('data-operation-id', active.operationId);
          downloadBtn.querySelector('.federation-badge-label').textContent = 'Downloading...';
          downloadBtn.title = 'Click to cancel';
          pollDownloadProgress(rawId, active.operationId, downloadBtn);
        }

        downloadBtn.addEventListener('click', function () {
          var state = this.getAttribute('data-state');
          if (state === 'idle' || state === 'error') {
            startDownload(this, rawId);
          } else if (state === 'busy') {
            cancelDownload(this);
          }
        });
      }

      var hideBtn = row.querySelector('.federation-badge-hide');
      if (hideBtn) {
        hideBtn.addEventListener('click', function () {
          if (this.getAttribute('data-state') === 'idle') {
            startHide(this, rawId);
          }
        });
      }

      return;
    }
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
