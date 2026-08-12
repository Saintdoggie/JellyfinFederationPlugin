(function () {
  'use strict';

  // Idempotent: index.html injection only happens once per server, but this
  // guards against the script somehow landing on the page twice anyway.
  if (window.__federationBadgeInit) {
    return;
  }

  window.__federationBadgeInit = true;

  var STYLE_ID = 'federation-badge-style';

  // Hand-built "network of nodes" glyph (not lifted from any icon set) -
  // three small circles in a triangle, connected by lines. Kept simple and
  // stroke-based so it renders correctly via currentColor without needing
  // to embed and verify an external icon library's exact path data.
  var ICON_SVG =
    '<svg viewBox="0 0 24 24" fill="none" ' +
    'stroke="currentColor" stroke-width="2.25" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' +
    '<circle cx="12" cy="5" r="2.5"></circle>' +
    '<circle cx="5" cy="19" r="2.5"></circle>' +
    '<circle cx="19" cy="19" r="2.5"></circle>' +
    '<path d="M12 7.5 L5 16.5 M12 7.5 L19 16.5"></path>' +
    '</svg>';

  // Small icon inline with text, used only on the detail page (a fixed,
  // always-visible spot, unlike a gallery card that may be scrolled past in
  // a fraction of a second with its text never even shown).
  var INLINE_ICON_HTML = '<span class="federation-badge-icon">' + ICON_SVG + '</span>';

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
      '.federation-badge-corner{position:absolute;top:6px;left:6px;width:22px;height:22px;border-radius:50%;',
      'background:rgba(0,0,0,.65);display:flex;align-items:center;justify-content:center;color:#fff;',
      'z-index:3;pointer-events:none;box-shadow:0 1px 3px rgba(0,0,0,.5);}',
      '.federation-badge-corner svg{width:13px;height:13px;}'
    ].join('');
    document.head.appendChild(style);
  }

  function normalizeId(id) {
    return (id || '').replace(/-/g, '').toLowerCase();
  }

  var federatedIds = new Set();

  function refreshFederatedIds() {
    fetch('/Plugins/Federation/FederatedIds', { credentials: 'same-origin' })
      .then(function (res) {
        return res.ok ? res.json() : [];
      })
      .then(function (ids) {
        var next = new Set();
        (ids || []).forEach(function (id) {
          next.add(normalizeId(id));
        });
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

      var badge = document.createElement('div');
      badge.className = 'federation-badge-corner';
      badge.title = 'Streamed from another server';
      badge.innerHTML = ICON_SVG;
      el.appendChild(badge);
    }

    el.setAttribute('data-federation-badge', '1');
  }

  function badgeDetailPage() {
    var match = location.href.match(/[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i);
    if (!match) {
      return;
    }

    var id = normalizeId(match[0]);
    if (!federatedIds.has(id)) {
      return;
    }

    var selectors = ['.nameContainer bdi', '.itemName-primary bdi', '.detailPagePrimaryContainer h1 bdi', 'h1 bdi'];
    for (var i = 0; i < selectors.length; i++) {
      var title = document.querySelector(selectors[i]);
      if (title && !title.querySelector('.federation-badge-icon')) {
        title.insertAdjacentHTML('afterbegin', INLINE_ICON_HTML);
        return;
      }
    }
  }

  function scan() {
    injectStyle();
    document.querySelectorAll('[data-id]:not([data-federation-badge])').forEach(badgeCard);
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
