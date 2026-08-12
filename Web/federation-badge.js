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
    '<svg class="federation-badge-icon" viewBox="0 0 24 24" width="14" height="14" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' +
    '<circle cx="12" cy="5" r="2.5"></circle>' +
    '<circle cx="5" cy="19" r="2.5"></circle>' +
    '<circle cx="19" cy="19" r="2.5"></circle>' +
    '<path d="M12 7.5 L5 16.5 M12 7.5 L19 16.5"></path>' +
    '</svg>';

  function injectStyle() {
    if (document.getElementById(STYLE_ID)) {
      return;
    }

    var style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent =
      '.federation-badge-icon{display:inline-block;vertical-align:-2px;margin-right:.35em;opacity:.75;flex-shrink:0;}';
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

    var title = el.querySelector('bdi');
    if (!title) {
      // Card shell rendered before its title text; try again next scan.
      return;
    }

    if (!title.querySelector('.federation-badge-icon')) {
      title.insertAdjacentHTML('afterbegin', ICON_SVG);
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
        title.insertAdjacentHTML('afterbegin', ICON_SVG);
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
