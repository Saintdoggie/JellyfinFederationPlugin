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
      '.federation-badge-download .federation-badge-icon{width:1.1em;height:1.1em;margin-right:0;opacity:1;}'
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

  function pollDownloadProgress(button, operationId) {
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

        if (!data.isComplete) {
          button.querySelector('span').textContent = 'Downloading ' + Math.round(data.percentComplete || 0) + '%';
          setTimeout(function () { pollDownloadProgress(button, operationId); }, 1500);
          return;
        }

        if (data.success) {
          button.setAttribute('data-state', 'done');
          button.querySelector('span').textContent = 'Downloaded';
        } else {
          button.setAttribute('data-state', 'error');
          button.querySelector('span').textContent = 'Failed';
          button.title = data.status || 'Download failed';
        }
      })
      .catch(function () {
        setTimeout(function () { pollDownloadProgress(button, operationId); }, 3000);
      });
  }

  function startDownload(button, itemId) {
    button.setAttribute('data-state', 'busy');
    button.querySelector('span').textContent = 'Starting...';

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
          button.setAttribute('data-state', 'error');
          button.querySelector('span').textContent = 'Failed';
          button.title = (result.data && result.data.message) || 'Could not start download';
          return;
        }

        pollDownloadProgress(button, result.data.operationId);
      })
      .catch(function () {
        button.setAttribute('data-state', 'error');
        button.querySelector('span').textContent = 'Failed';
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
    var pill = '<span class="federation-badge-pill" title="' + label.replace(/"/g, '&quot;') + '">'
      + INLINE_ICON_HTML + '<span>' + (srv || 'Another server') + '</span></span>'
      + '<span class="federation-badge-download" data-state="idle" title="Save a local copy on this server">'
      + '<span class="federation-badge-icon">' + DOWNLOAD_ICON_SVG + '</span><span>Download to server</span></span>';

    var selectors = ['.nameContainer bdi', '.itemName-primary bdi', '.detailPagePrimaryContainer h1 bdi', 'h1 bdi'];
    for (var i = 0; i < selectors.length; i++) {
      var title = document.querySelector(selectors[i]);
      if (title && !title.querySelector('.federation-badge-pill')) {
        title.insertAdjacentHTML('afterbegin', pill);
        var btn = title.querySelector('.federation-badge-download');
        if (btn) {
          btn.addEventListener('click', function () {
            if (this.getAttribute('data-state') === 'idle') {
              startDownload(this, rawId);
            }
          });
        }

        return;
      }
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

  // jellyfin-web is a client-routed SPA (pushState navigation), which does
  // not reliably fire DOM mutations synchronously with a route change - this
  // catches detail-page navigation the MutationObserver below might miss.
  setInterval(scheduleScan, 1500);

  var observer = new MutationObserver(scheduleScan);
  observer.observe(document.body, { childList: true, subtree: true });

  scheduleScan();
})();
