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
      '.federation-badge-pill{display:inline-flex;align-items:center;gap:.3em;vertical-align:middle;',
      'margin-right:.55em;padding:.2em .6em .2em .45em;border-radius:1em;font-size:.5em;',
      'font-weight:600;letter-spacing:.02em;text-transform:uppercase;white-space:nowrap;',
      'background:rgba(120,170,255,.16);color:#9fc4ff;border:1px solid rgba(120,170,255,.3);}',
      '.federation-badge-pill .federation-badge-icon{width:1.15em;height:1.15em;margin-right:0;opacity:1;}'
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

  function badgeDetailPage() {
    var match = location.href.match(/[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i);
    if (!match) {
      return;
    }

    var id = normalizeId(match[0]);
    if (!federatedIds.has(id)) {
      return;
    }

    var srv = federatedIds.get(id);
    var label = srv ? ('Streamed from ' + srv) : 'Streamed from another server';
    var pill = '<span class="federation-badge-pill" title="' + label.replace(/"/g, '&quot;') + '">'
      + INLINE_ICON_HTML + '<span>' + (srv || 'Another server') + '</span></span>';

    var selectors = ['.nameContainer bdi', '.itemName-primary bdi', '.detailPagePrimaryContainer h1 bdi', 'h1 bdi'];
    for (var i = 0; i < selectors.length; i++) {
      var title = document.querySelector(selectors[i]);
      if (title && !title.querySelector('.federation-badge-pill')) {
        title.insertAdjacentHTML('afterbegin', pill);
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
