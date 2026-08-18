(function () {
  'use strict';

  // Idempotent: index.html injection only happens once per server, but this
  // guards against the script somehow landing on the page twice anyway.
  if (window.__federationBadgeInit) {
    return;
  }

  window.__federationBadgeInit = true;

  var STYLE_ID = 'federation-badge-style';

  // A cloud reads immediately as "this comes from somewhere else and is
  // streamed", which is exactly the fact being conveyed. The previous node-graph
  // glyph was consistently read as a generic "share" icon instead - it said
  // nothing about where the file lives, which is the whole point.
  var ICON_SVG =
    '<svg viewBox="0 0 24 24" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' +
    '<path d="M17.5 19a4.5 4.5 0 0 0 .5-8.97 6 6 0 0 0-11.66-1.5A3.75 3.75 0 0 0 6.5 19z"></path>' +
    '<path d="M12 11.5v5.5"></path>' +
    '<path d="M9.75 14.25 12 11.5l2.25 2.75"></path>' +
    '</svg>';

  // Small icon inline with text, used only on the detail page (a fixed,
  // always-visible spot, unlike a gallery card that may be scrolled past in
  // a fraction of a second with its text never even shown).
  var INLINE_ICON_HTML = '<span class="federation-badge-icon">' + ICON_SVG + '</span>';

  var DOWNLOAD_ICON_SVG =
    '<svg viewBox="0 0 24 24" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' +
    '<path d="M12 4v11"></path>' +
    '<path d="M7.5 11.5 12 16l4.5-4.5"></path>' +
    '<path d="M5 19.5h14"></path>' +
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
    style.textContent = [
      '.federation-badge-icon{display:inline-block;width:14px;height:14px;vertical-align:-2px;margin-right:.35em;opacity:.85;flex-shrink:0;}',
      '.federation-badge-icon svg{width:100%;height:100%;}',
      // Corner overlay for gallery/grid cards - stays visible while
      // scrolling regardless of whether the card shows its title text at
      // all. Top-left, since Jellyfin's own played-checkmark and
      // unwatched-count badges live top-right/bottom-right.
      '.federation-badge-corner{position:absolute;top:6px;left:6px;width:21px;height:21px;border-radius:50%;',
      'background:rgba(12,14,18,.72);backdrop-filter:blur(2px);display:flex;align-items:center;justify-content:center;',
      'color:rgba(255,255,255,.92);z-index:3;pointer-events:none;',
      'box-shadow:0 1px 4px rgba(0,0,0,.45);border:1px solid rgba(255,255,255,.16);}',
      '.federation-badge-corner svg{width:12px;height:12px;}',
      // Row of pills below the title, not inline before it - inline
      // collided badly with stylized show-logo titles (see badgeDetailPage).
      '.federation-badge-row{display:flex;flex-wrap:wrap;align-items:center;gap:.4em;margin:.4em 0 .6em;}',
      // Detail page: a labelled chip naming the server. An unlabelled glyph only
      // says "not from here", which raises the question it should be answering.
      // Solid-ish background (not just a tinted overlay) plus its own border and
      // text color, so it reads at the same contrast on a light theme's white
      // background as it does on a dark theme's near-black one - the colors here
      // are the chip's own, not inherited from the page.
      '.federation-badge-pill{display:inline-flex;align-items:center;gap:.3em;vertical-align:middle;',
      'margin-right:.55em;padding:.2em .6em .2em .45em;border-radius:1em;font-size:.5em;',
      'font-weight:600;letter-spacing:.02em;text-transform:uppercase;white-space:nowrap;',
      'background:#1c3a66;color:#bcd8ff;border:1px solid #3c6bb3;}',
      '.federation-badge-pill .federation-badge-icon{width:1.15em;height:1.15em;margin-right:0;opacity:1;}',
      '.federation-badge-download{display:inline-flex;align-items:center;gap:.3em;vertical-align:middle;',
      'margin-right:.55em;padding:.2em .6em .2em .5em;border-radius:1em;font-size:.5em;',
      'font-weight:600;letter-spacing:.02em;text-transform:uppercase;white-space:nowrap;cursor:pointer;',
      'background:#20304a;color:#dbe6f5;border:1px solid #4a5f80;}',
      '.federation-badge-download:hover{background:#2a3f5f;}',
      '.federation-badge-download[data-state="busy"]{cursor:default;opacity:.85;}',
      '.federation-badge-download[data-state="done"]{background:#1e4d2b;border-color:#3c8a55;color:#c9f0d3;cursor:default;}',
      '.federation-badge-download[data-state="error"]{background:#5a2323;border-color:#a34a4a;color:#f5cccc;}',
      '.federation-badge-download .federation-badge-icon{width:1.1em;height:1.1em;margin-right:0;opacity:1;}',
      // "Hide" chip - same shape/interaction as the download chip above (a
      // clickable pill with an idle/busy/done/error state), but a distinct,
      // neutral color so it doesn't read as another "save a copy" action.
      '.federation-badge-hide{display:inline-flex;align-items:center;gap:.3em;vertical-align:middle;',
      'margin-right:.55em;padding:.2em .6em .2em .5em;border-radius:1em;font-size:.5em;',
      'font-weight:600;letter-spacing:.02em;text-transform:uppercase;white-space:nowrap;cursor:pointer;',
      'background:#3a2a20;color:#f0ddc9;border:1px solid #806048;}',
      '.federation-badge-hide:hover{background:#4a3626;}',
      '.federation-badge-hide[data-state="busy"]{cursor:default;opacity:.85;}',
      '.federation-badge-hide[data-state="done"]{background:#1e4d2b;border-color:#3c8a55;color:#c9f0d3;cursor:default;}',
      '.federation-badge-hide[data-state="error"]{background:#5a2323;border-color:#a34a4a;color:#f5cccc;}',
      '.federation-badge-hide .federation-badge-icon{width:1.1em;height:1.1em;margin-right:0;opacity:1;}',
      // Theme-adaptive surface tokens, used only by the toast below, which
      // floats over ordinary page chrome. The corner badge and title/hide/
      // download pills above are deliberately NOT themed - they overlay
      // posters/backdrop images, not page background, so there is no "page
      // theme" to match in the first place; a solid dark chip is the same
      // reasoning Jellyfin's own played-checkmark and unwatched-count badges
      // use, and stays legible over any poster regardless of active theme.
      // The toast, by contrast, sits over the theme's own surface, so it
      // pulls the theme's own color variables (falling back to the same
      // dark palette if a theme doesn't define them).
      ':root{--fed-surface:var(--theme-body-background-color, #161a22);',
      '--fed-text:var(--theme-body-text-color, var(--primary-text-color, #e4ebf5));',
      '--fed-accent:var(--theme-primary-color, var(--primary-accent-color, #3c6bb3));}',
      // Small toast for download progress that stays correct across a page
      // refresh or navigating away mid-download (see resumeActiveDownloads) -
      // the pill itself only exists while its item's detail page is open.
      '.federation-toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);z-index:9999;',
      'padding:.6em 1.1em;border-radius:.5em;background:var(--fed-surface);border:1px solid var(--fed-accent);color:var(--fed-text);',
      'font-size:13px;font-weight:600;box-shadow:0 4px 18px rgba(0,0,0,.5);display:flex;align-items:center;gap:.5em;}',
      '.federation-toast[data-state="error"]{border-color:#a34a4a;color:#f5cccc;}',
      '.federation-toast[data-state="done"]{border-color:#3c8a55;color:#c9f0d3;}'
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

  // Single toast element reused across a download's lifetime - shows
  // progress even when the pill itself isn't on screen (navigated away
  // mid-download) or hasn't been re-created yet after a refresh.
  var toastEl = null;

  function showToast(text, state) {
    if (!toastEl) {
      toastEl = document.createElement('div');
      toastEl.className = 'federation-toast';
      document.body.appendChild(toastEl);
    }

    toastEl.setAttribute('data-state', state || 'busy');
    toastEl.textContent = text;
    toastEl.style.display = 'flex';
  }

  function hideToastAfter(ms) {
    var el = toastEl;
    setTimeout(function () {
      if (el === toastEl) {
        el.style.display = 'none';
      }
    }, ms);
  }

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
            showToast('Downloading ' + (data.itemName ? data.itemName + ' - ' : '') + pct + '%', 'busy');
            if (liveButton) {
              liveButton.setAttribute('data-state', 'busy');
              liveButton.querySelector('.federation-badge-label').textContent = pct + '%';
            }

            setTimeout(poll, 1500);
            return;
          }

          delete pollingOperations[operationId];
          clearActiveDownload(itemId);

          if (data.success) {
            showToast('Downloaded ' + (data.itemName || 'item') + ' to this server', 'done');
            if (liveButton) {
              liveButton.setAttribute('data-state', 'done');
              liveButton.querySelector('.federation-badge-label').textContent = 'Downloaded';
            }
          } else {
            showToast(data.status || 'Download failed', 'error');
            if (liveButton) {
              liveButton.setAttribute('data-state', 'error');
              liveButton.querySelector('.federation-badge-label').textContent = 'Failed';
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
          button.querySelector('.federation-badge-label').textContent = 'Failed';
          button.title = msg;
          showToast(msg, 'error');
          hideToastAfter(6000);
          return;
        }

        setActiveDownload(itemId, result.data.operationId, '');
        pollDownloadProgress(itemId, result.data.operationId, button);
      })
      .catch(function () {
        button.setAttribute('data-state', 'error');
        button.querySelector('.federation-badge-label').textContent = 'Failed';
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
          downloadBtn.querySelector('.federation-badge-label').textContent = 'Downloading...';
          pollDownloadProgress(rawId, active.operationId, downloadBtn);
        }

        downloadBtn.addEventListener('click', function () {
          if (this.getAttribute('data-state') === 'idle') {
            startDownload(this, rawId);
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

  // jellyfin-web is a client-routed SPA (pushState navigation), which does
  // not reliably fire DOM mutations synchronously with a route change - this
  // catches detail-page navigation the MutationObserver below might miss.
  setInterval(scheduleScan, 1500);

  var observer = new MutationObserver(scheduleScan);
  observer.observe(document.body, { childList: true, subtree: true });

  scheduleScan();
})();
