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

  // Same "eye with a line through it" glyph as the native visibility_off
  // material icon already used for the receiving-side Hide action, so the two
  // opposite-direction "make this invisible" actions read as the same idea.
  // Filled rather than stroked for the same 12px-legibility reason as the
  // cloud glyph above.
  var EYE_OFF_SVG =
    '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true" focusable="false">' +
    '<path d="M2 4.27 3.28 3 21 20.72 19.73 22l-3.08-3.08c-1.44.61-2.99.93-4.65.93-5 0-9.27-3.11-11-7.5.72-1.84 1.87-3.45 3.31-4.71L2 4.27ZM12 9a3 3 0 0 1 3 3c0 .35-.06.68-.16 1L11 9.16c.32-.1.65-.16 1-.16Zm0-4.5c5 0 9.27 3.11 11 7.5a11.8 11.8 0 0 1-2.88 4.19l-1.42-1.42A9.8 9.8 0 0 0 20.82 12C19.17 8.36 15.75 6 12 6c-1.1 0-2.16.2-3.15.56L7.34 5.06A11.6 11.6 0 0 1 12 4.5ZM9.5 12a2.5 2.5 0 0 0 2.94 2.46l-2.9-2.9c-.03.14-.04.29-.04.44Z"></path>' +
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
      '.federation-badge-corner{position:absolute;top:.45em;left:.45em;width:clamp(1.65em,1.55vw,2em);height:clamp(1.65em,1.55vw,2em);border-radius:.48em;',
      'background:rgba(8,11,16,.92);color:#fff;',
      'display:flex;align-items:center;justify-content:center;',
      'z-index:3;pointer-events:none;line-height:0;',
      'border:1px solid rgba(255,255,255,.28);',
      'box-shadow:0 1px 4px rgba(0,0,0,.5);}',
      '.federation-badge-corner svg{display:block;width:56%;height:56%;opacity:1;}',

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
      '.federation-source-tag svg{display:block;width:1em;height:1em;flex-shrink:0;}',
      '.federation-download-ring svg{display:block;}',
      '.federation-actionsheet-item{display:flex;align-items:center;min-height:3em;text-align:start;}',
      '.federation-action-icon{display:flex;align-items:center;justify-content:center;flex:0 0 1.5em;width:1.5em;height:1.5em;margin-inline-end:1em;line-height:1;}',
      '.federation-action-icon svg{display:block;width:1.2em;height:1.2em;transform:none;}',
      '.federation-actionsheet-item:focus-visible{outline:2px solid var(--theme-primary-color,#00a4dc);outline-offset:-2px;}',
      '@media(forced-colors:active){.federation-badge-corner{background:Canvas;color:CanvasText;border:1px solid CanvasText;}}',

      // Library origin tabs: All / this server / each federated friend.
      '.federation-origin-filter{display:flex;flex-wrap:wrap;align-items:center;gap:.35em;width:100%;padding:.15em 0 .7em;margin:0;}',
      '.federation-origin-filter[data-empty="1"]{display:none;}',
      '.federation-origin-tab{border:0;background:transparent;color:inherit;opacity:.72;font:inherit;line-height:1.2;padding:.45em .9em;border-radius:.45em;cursor:pointer;}',
      '.federation-origin-tab[aria-selected="true"]{opacity:1;color:var(--theme-primary-color,#00a4dc);background:rgba(0,164,220,.14);}',
      '.federation-origin-tab:focus-visible{outline:2px solid var(--theme-primary-color,#00a4dc);outline-offset:2px;}',
      '.card.federation-origin-hidden,.listItem.federation-origin-hidden{display:none!important;}',
      '.federation-origin-empty{display:none;padding:1.4em .2em;opacity:.7;}',
      '.federation-origin-empty[data-show="1"]{display:block;}',

      // Download/Hide entries injected into the native "..." action sheet
      // (.actionSheetMenuItem) - no rules of our own needed beyond a disabled
      // look while busy, since every other visual (icon, label, hover, focus)
      // already comes from that class.
      '.federation-actionsheet-item[data-fed-state="busy"]{opacity:.65;pointer-events:none;}',

      // Friends' favorites rows on the Favorites tab: a sideways-scrolling row
      // of plain cards under jellyfin-web's own section heading.
      '.federation-favorites-row{display:flex;gap:1em;overflow-x:auto;overscroll-behavior-x:contain;scroll-snap-type:x proximity;padding-top:.3em;padding-bottom:1.2em;}',
      '.federation-fav-card{flex:0 0 auto;display:flex;flex-direction:column;gap:.25em;color:inherit;text-decoration:none;scroll-snap-align:start;border-radius:.3em;}',
      '.federation-fav-portrait{width:clamp(7.5em,14vw,11em);}',
      '.federation-fav-backdrop{width:clamp(13em,24vw,19em);}',
      '.federation-fav-square{width:clamp(8.5em,15vw,12em);}',
      '.federation-fav-art{display:block;position:relative;width:100%;border-radius:.3em;overflow:hidden;background:rgba(255,255,255,.07);}',
      '.federation-fav-portrait .federation-fav-art{aspect-ratio:2/3;}',
      '.federation-fav-backdrop .federation-fav-art{aspect-ratio:16/9;}',
      '.federation-fav-square .federation-fav-art{aspect-ratio:1/1;}',
      '.federation-fav-art img{display:block;width:100%;height:100%;object-fit:cover;}',
      '.federation-fav-noart{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;text-align:center;padding:.6em;opacity:.75;font-size:.9em;}',
      '.federation-fav-title,.federation-fav-sub{display:block;text-align:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}',
      '.federation-fav-sub{opacity:.6;font-size:.9em;}',
      '.federation-fav-card:hover .federation-fav-art,.federation-fav-card:focus-visible .federation-fav-art{outline:3px solid var(--theme-primary-color,#00a4dc);outline-offset:-3px;}',
      '.federation-fav-card:focus-visible{outline:none;}',

      // Watch-party status line (bottom centre, solid colours).
      '.federation-party-notice{position:fixed;left:50%;bottom:5em;transform:translateX(-50%);max-width:min(32em,calc(100vw - 2em));padding:.7em 1.1em;border-radius:.4em;background:rgba(20,22,26,.96);color:#fff;z-index:100000;opacity:0;pointer-events:none;transition:opacity .2s;}',
      '.federation-party-notice[data-show="1"]{opacity:1;}',

      // "Download to this server" destination picker (solid colours).
      '.federation-dest-backdrop{position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:100001;display:flex;align-items:center;justify-content:center;padding:1em;}',
      '.federation-dest{width:min(30em,100%);max-height:calc(100vh - 2em);overflow:auto;background:#202226;color:#fff;border-radius:.5em;padding:1.2em 1.3em;box-shadow:0 .5em 2em rgba(0,0,0,.5);}',
      '.federation-dest h3{margin:0 0 .8em;font-size:1.2em;font-weight:500;}',
      '.federation-dest label{display:flex;gap:.7em;align-items:flex-start;padding:.45em .3em;border-radius:.3em;cursor:pointer;}',
      '.federation-dest label:hover{background:rgba(255,255,255,.06);}',
      '.federation-dest input[type=radio]{margin-top:.25em;}',
      '.federation-dest-path{display:block;opacity:.6;font-size:.85em;word-break:break-all;}',
      '.federation-dest-new{width:100%;box-sizing:border-box;margin:.2em 0 0;padding:.45em .6em;border-radius:.3em;border:1px solid #555;background:#15171a;color:#fff;font:inherit;}',
      '.federation-dest-remember{margin-top:.6em;opacity:.85;}',
      '.federation-dest-error{color:#ff8a80;min-height:1.2em;margin:.4em 0 0;font-size:.9em;}',
      '.federation-dest-actions{display:flex;justify-content:flex-end;gap:.6em;margin-top:.8em;}',
      '.federation-dest-actions button{padding:.5em 1.1em;border-radius:.3em;border:0;font:inherit;cursor:pointer;background:#33363c;color:#fff;}',
      '.federation-dest-actions .federation-dest-go{background:var(--theme-primary-color,#00a4dc);}'
    ].join('');
    document.head.appendChild(style);
  }

  function federationFetch(path, options) {
    var base = window.location.origin;
    try {
      if (window.ApiClient && typeof window.ApiClient.serverAddress === 'function') {
        base = window.ApiClient.serverAddress().replace(/\/+$/, '');
      } else {
        var webAt = window.location.pathname.toLowerCase().indexOf('/web');
        if (webAt >= 0) { base += window.location.pathname.substring(0, webAt); }
      }
    } catch (e) { /* use the current server */ }
    options = options || {};
    options.headers = options.headers || {};
    var token = getToken();
    if (token) { options.headers['Authorization'] = 'MediaBrowser Token="' + encodeURIComponent(token) + '"'; }
    return fetch(base + path, options);
  }

  function normalizeId(id) {
    return (id || '').replace(/-/g, '').toLowerCase();
  }

  // id -> source server name ('' when unknown). A map rather than a set so the
  // badge can name the server instead of just asserting "not local".
  var federatedIds = new Map();
  var unavailableIds = new Set();
  var currentUserIsAdmin = false;
  var showFederatedCloudBadges = false;
  var clientSettingsLoaded = false;

  // This script runs for ordinary viewers too. Resolve elevation once before
  // enabling admin-only polling/actions, otherwise every non-admin browser
  // sends a forbidden /Downloads request every three seconds indefinitely.
  function resolveAdminState() {
    try {
      if (window.ApiClient && typeof window.ApiClient.getCurrentUser === 'function') {
        return Promise.resolve(window.ApiClient.getCurrentUser()).then(function (user) {
          currentUserIsAdmin = !!(user && user.Policy && user.Policy.IsAdministrator);
        }).catch(function () { /* a viewer remains non-admin */ });
      }
    } catch (e) { /* a viewer remains non-admin */ }

    return Promise.resolve();
  }

  function refreshFederatedIds() {
    return federationFetch('/Plugins/Federation/ClientCatalog', { credentials: 'same-origin' })
      .then(function (res) {
        if (!res.ok) { throw new Error('Catalog unavailable'); }
        return res.json();
      })
      .then(function (catalog) {
        var data = catalog.items || {};
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

        document.querySelectorAll('.card[data-id], .listItem[data-id]').forEach(function (card) {
          var id = normalizeId(card.getAttribute('data-id'));
          if (federatedIds.get(id) !== next.get(id)) {
            card.removeAttribute('data-federation-badge');
            card.querySelectorAll('.federation-badge-corner').forEach(function (badge) { badge.remove(); });
          }
        });
        federatedIds = next;
        unavailableIds = new Set((catalog.unavailableIds || []).map(normalizeId));
        scheduleScan();
      })
      .catch(function () {
        // Leave the previous set in place; try again on the next interval.
      });
  }

  // Simple loading bar shown while a federated title is still starting (see the
  // "Loading bar" section below). Switchable by the server's LoadingOverlay setting.
  var loadingOverlayEnabled = true;

  function refreshClientSettings() {
    return federationFetch('/Plugins/Federation/ClientSettings', { credentials: 'same-origin' })
      .then(function (res) { return res.ok ? res.json() : {}; })
      .then(function (settings) {
        var wasLoaded = clientSettingsLoaded;
        var previousValue = showFederatedCloudBadges;
        showFederatedCloudBadges = settings.showFederatedCloudBadges === true;
        loadingOverlayEnabled = settings.loadingOverlay !== false;
        clientSettingsLoaded = true;

        // Cards are deliberately marked as processed even when the cloud is
        // disabled, otherwise every DOM mutation would reconsider the same
        // cards. If an administrator changes the setting in another tab,
        // clear only federated card state and let the normal scanner reconcile
        // it. This makes both enabling and disabling take effect without a
        // full jellyfin-web reload.
        if (wasLoaded && previousValue !== showFederatedCloudBadges) {
          document.querySelectorAll('.card[data-id],.listItem[data-id]').forEach(function (card) {
            if (!federatedIds.has(normalizeId(card.getAttribute('data-id')))) {
              return;
            }

            card.removeAttribute('data-federation-badge');
            card.querySelectorAll('.federation-badge-corner').forEach(function (badge) {
              badge.remove();
            });
          });
          scheduleScan();
        }
      })
      .catch(function () {
        // The privacy-preserving default is also the visual default: no cloud.
        showFederatedCloudBadges = false;
      })
      .then(function () { clientSettingsLoaded = true; });
  }

  // ids of this server's own local items currently excluded from sharing with
  // every friend (Configuration/FederationPluginController.cs's
  // "Outgoing sharing" region) - the opposite direction from federatedIds
  // above, so the corner badge and detail-page action can tell an admin apart
  // "this plays from elsewhere" from "this plays here but I've stopped
  // sharing it out".
  var sharingDisabledIds = new Set();

  function refreshSharingDisabledIds() {
    if (!currentUserIsAdmin) {
      return;
    }

    federationFetch('/Plugins/Federation/Sharing/DisabledIds', { credentials: 'same-origin' })
      .then(function (res) { return res.ok ? res.json() : []; })
      .then(function (ids) {
        var next = new Set();
        (ids || []).forEach(function (id) { next.add(normalizeId(id)); });
        sharingDisabledIds = next;
      })
      .catch(function () {
        // Leave the previous set in place; try again on the next interval.
      });
  }

  function badgeCard(el) {
    var id = normalizeId(el.getAttribute('data-id'));
    var isFederated = !!id && federatedIds.has(id);
    // A federated item can never itself be re-shared onward (see
    // FederationPeerAccessService's remarks on the non-transitive guarantee),
    // so the two badges are mutually exclusive - no need to pick between them
    // on the same card.
    var isSharingDisabled = !isFederated && !!id && sharingDisabledIds.has(id);

    if (isFederated && !showFederatedCloudBadges) {
      el.setAttribute('data-federation-badge', '0');
      updateDownloadRing(el, id);
      return;
    }

    if (!isFederated && !isSharingDisabled) {
      // Neither (or not known yet) - skip this element for good so we don't
      // keep re-checking it on every scan.
      el.setAttribute('data-federation-badge', '0');
      return;
    }

    if (!el.querySelector('.federation-badge-corner')) {
      // Anchor to the artwork, not the outer card. Card footers vary in height
      // across poster, landscape and list layouts; positioning against them is
      // what made the cloud visibly drift away from the image.
      var anchor = el.querySelector('.cardImageContainer, .listItemImage') || el;
      if (window.getComputedStyle(anchor).position === 'static') {
        anchor.style.position = 'relative';
      }

      var badge = document.createElement('div');
      badge.className = 'federation-badge-corner';
      if (isFederated) {
        var srv = federatedIds.get(id);
        badge.title = srv ? ('Streamed from ' + srv) : 'Streamed from another server';
        badge.innerHTML = ICON_SVG;
      } else {
        badge.title = 'Not shared with any friend';
        badge.innerHTML = EYE_OFF_SVG;
      }

      anchor.appendChild(badge);
    }

    el.setAttribute('data-federation-badge', '1');
    if (isFederated) {
      updateDownloadRing(el, id);
    }
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
    var n = Number(bytesPerSecond) || 0;
    if (n <= 0) {
      return '0 KB/s';
    }

    var mbPerSec = n / (1024 * 1024);
    if (mbPerSec >= 0.1) {
      return mbPerSec.toFixed(1) + ' MB/s';
    }

    return Math.round(n / 1024) + ' KB/s';
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

    // Rewriting the ring is a DOM mutation, which the body-wide observer turns
    // into another scan; only touch it when the shown value actually changed.
    var shown = Math.round(pct) + '|' + sizePx;
    if (ring.getAttribute('data-ring-state') === shown) {
      return;
    }

    ring.setAttribute('data-ring-state', shown);
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
    var anchor = el.querySelector('.cardImageContainer, .listItemImage') || el;
    if (typeof pctOverride === 'number') {
      applyDownloadRing(anchor, pctOverride, 46);
      return;
    }

    var dl = activeDownloadsByItem.get(id);
    if (dl) {
      applyDownloadRing(anchor, dl.percentComplete, 46);
    } else {
      removeDownloadRing(anchor);
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
    if (!currentUserIsAdmin) {
      return;
    }

    var token = getToken();
    federationFetch('/Plugins/Federation/Downloads', {
      credentials: 'same-origin',
      headers: token ? { 'Authorization': 'MediaBrowser Token="' + encodeURIComponent(token) + '"' } : {}
    })
      .then(function (res) { return res.ok ? res.json() : []; })
      .then(function (list) {
        var next = new Map();
        (list || []).forEach(function (d) {
          if (!d.isComplete && d.localItemId) {
            next.set(normalizeId(d.localItemId), d);
            pollDownloadProgress(d.localItemId, d.operationId, null);
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

    // Consecutive failed polls (server error or network). A transient one is
    // retried with a growing delay; past the cap the poll stops and a later scan
    // may resume it, rather than leaving the operation marked as polled forever.
    var failures = 0;
    var retryOrStop = function () {
      failures += 1;
      if (failures > 8) {
        delete pollingOperations[operationId];
        return;
      }

      setTimeout(poll, Math.min(3000 * failures, 15000));
    };

    var poll = function () {
      var token = getToken();
      federationFetch('/Plugins/Federation/Download/Progress/' + operationId, {
        credentials: 'same-origin',
        headers: token ? { 'Authorization': 'MediaBrowser Token="' + encodeURIComponent(token) + '"' } : {}
      })
        .then(function (res) {
          if (res.status === 404) {
            delete pollingOperations[operationId];
            clearActiveDownload(itemId);
            return null;
          }

          return res.ok ? res.json() : { retry: true };
        })
        .then(function (data) {
          if (!data) {
            return;
          }

          if (data.retry === true) {
            retryOrStop();
            return;
          }

          failures = 0;

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
            var speed = data.paused ? 'paused' : formatSpeed(data.bytesPerSecond);
            if (liveButton) {
              setButtonState(liveButton, 'busy', pct + '% ' + speed, data.paused ? (data.status || 'Paused') : 'Downloading to this server');
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
        .catch(retryOrStop);
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
    federationFetch('/Plugins/Federation/Download/Cancel/' + operationId, {
      method: 'POST',
      credentials: 'same-origin',
      headers: token ? { 'Authorization': 'MediaBrowser Token="' + encodeURIComponent(token) + '"' } : {}
    }).catch(function () { /* the next progress poll tick reconciles state regardless */ });
  }

  // Asks where a server download should go: each library with a folder of
  // its own, the separate Federation Downloads library, or a new folder. The
  // configured default is preselected; resolves null when cancelled.
  function chooseDownloadDestination() {
    return federationFetch('/Plugins/Federation/Download/Destinations', { credentials: 'same-origin' })
      .then(function (res) { return res.ok ? res.json() : { defaultDestination: 'auto', options: [] }; })
      .catch(function () { return { defaultDestination: 'auto', options: [] }; })
      .then(function (data) {
        return new Promise(function (resolve) {
          var current = data.defaultDestination || 'auto';
          var choices = [{ id: 'auto', name: 'Matching library', path: 'Movies for films, Shows for episodes' }]
            .concat((data.options || []).map(function (o) { return { id: o.id, name: o.name, path: o.path }; }))
            .concat([{ id: 'separate', name: 'Federation Downloads', path: 'A separate library, kept apart from your own titles' }]);
          var isNewFolder = current.indexOf('folder:') === 0;
          if (!isNewFolder && !choices.some(function (c) { return c.id === current; })) { current = 'auto'; }

          var backdrop = document.createElement('div');
          backdrop.className = 'federation-dest-backdrop';
          var box = document.createElement('div');
          box.className = 'federation-dest';
          box.setAttribute('role', 'dialog');
          box.setAttribute('aria-modal', 'true');
          var title = document.createElement('h3');
          title.textContent = 'Download to this server';
          box.appendChild(title);

          function addChoice(id, name, sub) {
            var label = document.createElement('label');
            var radio = document.createElement('input');
            radio.type = 'radio';
            radio.name = 'federation-dest';
            radio.value = id;
            radio.checked = isNewFolder ? id === 'folder:' : id === current;
            var text = document.createElement('span');
            text.textContent = name;
            var path = document.createElement('span');
            path.className = 'federation-dest-path';
            path.textContent = sub || '';
            text.appendChild(path);
            label.appendChild(radio);
            label.appendChild(text);
            box.appendChild(label);
            return label;
          }

          choices.forEach(function (c) { addChoice(c.id, c.name, c.path); });
          var newLabel = addChoice('folder:', 'New folder', 'A name (made next to your Movies folder) or a full path. It becomes its own library.');
          var input = document.createElement('input');
          input.type = 'text';
          input.className = 'federation-dest-new';
          input.placeholder = 'e.g. From friends or /media/friends';
          input.value = isNewFolder ? current.substring('folder:'.length) : '';
          newLabel.querySelector('span').appendChild(input);
          input.addEventListener('focus', function () { newLabel.querySelector('input[type=radio]').checked = true; });

          var remember = document.createElement('label');
          remember.className = 'federation-dest-remember';
          var rememberBox = document.createElement('input');
          rememberBox.type = 'checkbox';
          var rememberText = document.createElement('span');
          rememberText.textContent = 'Use this for every download';
          remember.appendChild(rememberBox);
          remember.appendChild(rememberText);
          box.appendChild(remember);

          var error = document.createElement('div');
          error.className = 'federation-dest-error';
          box.appendChild(error);

          var actions = document.createElement('div');
          actions.className = 'federation-dest-actions';
          var cancel = document.createElement('button');
          cancel.type = 'button';
          cancel.textContent = 'Cancel';
          var go = document.createElement('button');
          go.type = 'button';
          go.className = 'federation-dest-go';
          go.textContent = 'Download';
          actions.appendChild(cancel);
          actions.appendChild(go);
          box.appendChild(actions);
          backdrop.appendChild(box);
          document.body.appendChild(backdrop);

          function close(value) {
            document.removeEventListener('keydown', onKey, true);
            if (backdrop.parentNode) { backdrop.parentNode.removeChild(backdrop); }
            resolve(value);
          }

          function onKey(e) {
            if (e.key === 'Escape') { e.stopPropagation(); close(null); }
          }

          document.addEventListener('keydown', onKey, true);
          backdrop.addEventListener('click', function (e) { if (e.target === backdrop) { close(null); } });
          cancel.addEventListener('click', function () { close(null); });
          go.addEventListener('click', function () {
            var picked = box.querySelector('input[name="federation-dest"]:checked');
            var value = picked ? picked.value : 'auto';
            if (value === 'folder:') {
              var folder = input.value.trim();
              if (!folder) { error.textContent = 'Type a folder name or path.'; input.focus(); return; }
              value = 'folder:' + folder;
            }

            if (!rememberBox.checked) { close(value); return; }
            go.disabled = true;
            federationFetch('/Plugins/Federation/Settings/DownloadDestination', {
              method: 'POST',
              credentials: 'same-origin',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ Destination: value })
            })
              .then(function (res) { return res.json().then(function (body) { return { ok: res.ok, body: body }; }); })
              .then(function (r) {
                if (!r.ok) { go.disabled = false; error.textContent = (r.body && r.body.message) || 'Could not save that destination.'; return; }
                close(value);
              })
              .catch(function () { go.disabled = false; error.textContent = 'Could not save that destination.'; });
          });
          setTimeout(function () { go.focus(); }, 0);
        });
      });
  }

  function startDownload(button, itemId, destination) {
    setButtonState(button, 'busy', 'Starting', 'Starting download');

    var token = getToken();
    federationFetch('/Plugins/Federation/Download', {
      method: 'POST',
      credentials: 'same-origin',
      headers: Object.assign({ 'Content-Type': 'application/json' }, token ? { 'Authorization': 'MediaBrowser Token="' + encodeURIComponent(token) + '"' } : {}),
      body: JSON.stringify({ ItemId: itemId, Destination: destination || null })
    })
      .then(function (res) { return res.json().then(function (data) { return { ok: res.ok, data: data }; }); })
      .then(function (result) {
        if (!result.ok || !result.data || !result.data.operationId) {
          var msg = (result.data && result.data.message) || 'Could not start download';
          setButtonState(button, 'error', 'Download failed', msg);
          notifyParty(msg);
          return;
        }

        notifyParty('Downloading to this server. It shows up in the library once it finishes.');
        button.setAttribute('data-operation-id', result.data.operationId);
        setActiveDownload(itemId, result.data.operationId, '');
        pollDownloadProgress(itemId, result.data.operationId, button);
      })
      .catch(function () {
        setButtonState(button, 'error', 'Download failed', 'Could not start download');
        notifyParty('Could not start the download.');
      });
  }

  // Called once at script init: resumes polling for any download that was
  // still in progress when the page was last unloaded, so a refresh
  // mid-download doesn't just silently drop it.
  function resumeActiveDownloads() {
    refreshDownloadsList();
    var map = loadActiveDownloads();
    Object.keys(map).forEach(function (itemId) {
      var entry = map[itemId];
      if (!entry || !entry.operationId) {
        delete map[itemId];
        return;
      }

      pollDownloadProgress(itemId, entry.operationId, null);
    });
    saveActiveDownloads(map);
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
    federationFetch('/Plugins/Federation/HiddenItems/Hide', {
      method: 'POST',
      credentials: 'same-origin',
      headers: Object.assign({ 'Content-Type': 'application/json' }, token ? { 'Authorization': 'MediaBrowser Token="' + encodeURIComponent(token) + '"' } : {}),
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

  // Stops sharing one of this server's OWN items with every friend - the
  // opposite direction from startHide above (that suppresses a friend's item
  // locally; this stops offering one of ours to anyone). See
  // Configuration/FederationPluginController.cs's "Outgoing sharing" region
  // and PluginConfiguration.GloballyExcludedItemIds for the backend half.
  // Per-friend/per-user narrowing still lives in the settings page's Catalog
  // picker - this quick action is deliberately the broad "everyone" case only.
  function startDisableSharing(button, itemId) {
    setButtonState(button, 'busy', 'Stopping', 'Stopping sharing');

    var token = getToken();
    federationFetch('/Plugins/Federation/Sharing/Disable', {
      method: 'POST',
      credentials: 'same-origin',
      headers: Object.assign({ 'Content-Type': 'application/json' }, token ? { 'Authorization': 'MediaBrowser Token="' + encodeURIComponent(token) + '"' } : {}),
      body: JSON.stringify({ ItemId: itemId })
    })
      .then(function (res) { return res.json().then(function (data) { return { ok: res.ok, data: data }; }); })
      .then(function (result) {
        if (!result.ok || !result.data || !result.data.success) {
          setButtonState(button, 'error', 'Failed', (result.data && result.data.message) || 'Could not stop sharing this item');
          return;
        }

        sharingDisabledIds.add(normalizeId(itemId));
        setButtonState(button, 'done', 'Stopped sharing', 'Not shared with any friend');
        refreshDownloadRingsOnCards();
      })
      .catch(function () {
        setButtonState(button, 'error', 'Failed', 'Could not stop sharing this item');
      });
  }

  function startEnableSharing(button, itemId) {
    setButtonState(button, 'busy', 'Resuming', 'Resuming sharing');

    var token = getToken();
    federationFetch('/Plugins/Federation/Sharing/Enable', {
      method: 'POST',
      credentials: 'same-origin',
      headers: Object.assign({ 'Content-Type': 'application/json' }, token ? { 'Authorization': 'MediaBrowser Token="' + encodeURIComponent(token) + '"' } : {}),
      body: JSON.stringify({ ItemId: itemId })
    })
      .then(function (res) { return res.json().then(function (data) { return { ok: res.ok, data: data }; }); })
      .then(function (result) {
        if (!result.ok || !result.data || !result.data.success) {
          setButtonState(button, 'error', 'Failed', (result.data && result.data.message) || 'Could not resume sharing this item');
          return;
        }

        sharingDisabledIds.delete(normalizeId(itemId));
        setButtonState(button, 'done', 'Sharing', 'Shared per its existing per-friend settings');
        document.querySelectorAll('.card[data-id],.listItem[data-id]').forEach(function (el) {
          if (normalizeId(el.getAttribute('data-id')) === normalizeId(itemId)) {
            var badge = el.querySelector('.federation-badge-corner');
            if (badge) {
              badge.remove();
            }

            el.setAttribute('data-federation-badge', '0');
          }
        });
      })
      .catch(function () {
        setButtonState(button, 'error', 'Failed', 'Could not resume sharing this item');
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

    var old = info.querySelector('.federation-source-tag');
    // MutationObserver calls scan() after our own DOM changes. Rebuilding the
    // same tag on every scan created a self-sustaining remove/add loop that
    // kept a browser busy even while the page was idle.
    if (old && old.getAttribute('data-item-id') === normalizeId(rawId)
        && old.getAttribute('data-server-name') === name) {
      return;
    }

    // Remove a tag left over from a previous item (the SPA reuses this DOM).
    if (old) {
      old.remove();
    }

    var tag = document.createElement('span');
    tag.className = 'federation-source-tag';
    tag.setAttribute('data-item-id', normalizeId(rawId));
    tag.setAttribute('data-server-name', name);
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
  function actionIcon(name) {
    if (name === 'visibility_off') { return EYE_OFF_SVG; }
    var paths = {
      file_download: 'M11 3h2v10l4-4 1.4 1.4L12 17l-6.4-6.6L7 9l4 4V3ZM5 19h14v2H5v-2Z',
      cloud_download: 'M19.35 10.04A7.49 7.49 0 0 0 5.3 6.25 5.5 5.5 0 0 0 6 17h13a4.5 4.5 0 0 0 .35-6.96ZM11 17v-6H8l4-5 4 5h-3v6h-2Z',
      cloud_off: 'M6 5l6 6 6-6 1 1-6 6 6 6-1 1-6-6-6 6-1-1 6-6-6-6 1-1Z',
      visibility: 'M12 5C7 5 3 8 1 12c2 4 6 7 11 7s9-3 11-7c-2-4-6-7-11-7Zm0 2c4 0 7 2 9 5-2 3-5 5-9 5s-7-2-9-5c2-3 5-5 9-5Zm0 2a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z',
      groups: 'M16 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm-8 0a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm0 2c-2.7 0-8 1.3-8 4v3h16v-3c0-2.7-5.3-4-8-4Zm8 0c-.3 0-.7 0-1.1.1 1.3.9 2.1 2.2 2.1 3.9v3h7v-3c0-2.7-5.3-4-8-4Z'
    };
    return '<svg viewBox="0 0 24 24" fill="currentColor" focusable="false" aria-hidden="true"><path d="' + (paths[name] || paths.file_download) + '"></path></svg>';
  }

  function makeActionSheetItem(iconName, label, dataId, onClick) {
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'listItem listItem-button actionSheetMenuItem federation-actionsheet-item';
    button.setAttribute('data-id', dataId);
    button.innerHTML =
      '<span class="federation-action-icon" aria-hidden="true">' + actionIcon(iconName) + '</span>' +
      '<div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText federation-btn-label"></div></div>';
    button.querySelector('.federation-btn-label').textContent = label;
    button.addEventListener('click', function () { onClick(button); });
    return button;
  }

  // Waits for the actionsheet the "..." button just opened to render (there's
  // no synchronous hook: jellyfin-web looks up the item and the current
  // user's permissions, itself async, before calling into the actionsheet
  // module), then adds Download/Hide (federated item) or a sharing toggle
  // (this server's own item) to the top of it. Guards against double-injecting
  // into the same open sheet since the more-commands click handler and this
  // can both run more than once in edge cases.
  function injectActionSheetItems(rawId, isFederated) {
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

      if (isFederated) {
        // Saving to the viewer's own device is jellyfin-web's own Download
        // entry: the server reports CanDownload for friends' titles and
        // answers /Items/{id}/Download itself, the same as for every app.
        var active = loadActiveDownloads()[rawId];

        if (currentUserIsAdmin) {
          if (active) {
            var downloadItem = makeActionSheetItem(
              'cloud_off',
              'Cancel server download',
              'federation-download',
              function (btn) { cancelDownload(btn); });
            downloadItem.setAttribute('data-operation-id', active.operationId);
            scroller.insertBefore(downloadItem, scroller.firstChild);
          } else {
            scroller.insertBefore(makeActionSheetItem(
              'cloud_download',
              'Download to this server',
              'federation-download-server',
              function (btn) {
                chooseDownloadDestination().then(function (destination) {
                  if (destination) { startDownload(btn, rawId, destination); }
                });
              }), scroller.firstChild);
          }

          var hideItem = makeActionSheetItem(
            'visibility_off',
            'Hide from this server',
            'federation-hide',
            function (btn) { startHide(btn, rawId); });

          scroller.insertBefore(hideItem, scroller.firstChild);
        }

        return;
      }

      if (!currentUserIsAdmin) {
        return;
      }

      var currentlyDisabled = sharingDisabledIds.has(normalizeId(rawId));
      var sharingItem = makeActionSheetItem(
        currentlyDisabled ? 'visibility' : 'visibility_off',
        currentlyDisabled ? 'Share again' : 'Stop sharing',
        'federation-sharing',
        function (btn) {
          if (currentlyDisabled) {
            startEnableSharing(btn, rawId);
          } else {
            startDisableSharing(btn, rawId);
          }
        });

      scroller.insertBefore(sharingItem, scroller.firstChild);
    }

    requestAnimationFrame(tryInject);
  }

  // jellyfin-web reuses the same itemDetails view instance (and so the same
  // .btnMoreCommands element) across navigations between different items'
  // detail pages, rather than recreating it per item - so the click listener
  // below is bound exactly once and must read the CURRENT item id/kind at
  // click time, never values captured in its own closure, or the menu would
  // keep acting on whichever item's page happened to bind it first.
  var currentItemId = null;
  var currentItemIsFederated = false;

  function bindMoreCommandsMenu() {
    var btn = document.querySelector('.btnMoreCommands');
    if (!btn || btn.dataset.federationBound) {
      return;
    }

    btn.dataset.federationBound = 'true';
    btn.addEventListener('click', function () {
      if (currentItemId) {
        injectActionSheetItems(currentItemId, currentItemIsFederated);
      }
    });
  }

  function badgeDetailPage() {
    var match = location.href.match(/[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i);
    var rawId = match ? match[0] : null;
    var id = rawId ? normalizeId(rawId) : null;

    if (!id) {
      currentItemId = null;
      return;
    }

    if (unavailableIds.has(id)) {
      currentItemId = null;
      document.querySelectorAll('.federation-source-tag').forEach(function (tag) { tag.remove(); });
      return;
    }

    if (federatedIds.has(id)) {
      currentItemId = rawId;
      currentItemIsFederated = true;
      injectSourceTag(rawId, federatedIds.get(id));
      bindMoreCommandsMenu();
      return;
    }

    // Not federated - only offer the sharing toggle on an actual media detail
    // page (has the runtime/year/rating info line every playable item shows),
    // not a Person/Studio/Genre page or anything else that happens to carry a
    // guid in its URL but isn't one of this server's own shareable items.
    var isMediaPage = !!(document.querySelector('.itemMiscInfo-primary') || document.querySelector('.itemMiscInfo'));
    if (!isMediaPage) {
      currentItemId = null;
      return;
    }

    currentItemId = rawId;
    currentItemIsFederated = false;
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

  var ORIGIN_STORAGE_KEY = 'federation-origin-filter';
  var ORIGIN_OTHER = 'Other servers';

  function getOriginFilter() {
    try {
      return sessionStorage.getItem(ORIGIN_STORAGE_KEY) || 'all';
    } catch (e) {
      return 'all';
    }
  }

  function setOriginFilter(mode) {
    try {
      sessionStorage.setItem(ORIGIN_STORAGE_KEY, mode);
    } catch (e) { /* sessionStorage unavailable */ }
  }

  function uniqueFriendNames() {
    var names = [];
    var seen = {};
    federatedIds.forEach(function (name) {
      var label = (name || '').trim() || ORIGIN_OTHER;
      if (!seen[label]) {
        seen[label] = true;
        names.push(label);
      }
    });
    names.sort(function (a, b) { return a.localeCompare(b); });
    return names;
  }

  function originModeForCard(id) {
    if (!federatedIds.has(id)) {
      return 'local';
    }

    var name = (federatedIds.get(id) || '').trim() || ORIGIN_OTHER;
    return 'friend:' + name;
  }

  // Library-browsing routes only. On a details page the same /Items queries
  // load a show's seasons and episodes or a collection's titles, and filtering
  // those by origin made a friend's show look empty under "This server".
  function originFilterActive() {
    return /^#!?\/(home|movies|tv|search|list|music)(\.html)?(?=[?\/#]|$)/i.test(window.location.hash || '');
  }

  function cardMatchesOrigin(el) {
    if (unavailableIds.has(normalizeId(el.getAttribute('data-id')))) {
      return false;
    }
    var mode = getOriginFilter();
    if (mode === 'all' || !originFilterActive()) {
      return true;
    }

    var id = normalizeId(el.getAttribute('data-id'));
    var cardMode = originModeForCard(id);
    if (mode === 'local') {
      return cardMode === 'local';
    }

    return cardMode === mode;
  }

  function applyOriginVisibility() {
    var mode = getOriginFilter();
    document.querySelectorAll('.card[data-id], .listItem[data-id]').forEach(function (el) {
      el.classList.toggle('federation-origin-hidden', !cardMatchesOrigin(el));
    });

    document.querySelectorAll('.itemsContainer').forEach(function (container) {
      var empty = container.querySelector(':scope > .federation-origin-empty');
      if (mode === 'all' || !originFilterActive()) {
        if (empty) {
          empty.setAttribute('data-show', '0');
        }

        return;
      }

      var visibleCards = container.querySelectorAll(':scope > .card[data-id]:not(.federation-origin-hidden), :scope > .listItem[data-id]:not(.federation-origin-hidden)');
      if (!empty) {
        empty = document.createElement('p');
        empty.className = 'federation-origin-empty';
        container.appendChild(empty);
      }

      empty.textContent = 'No titles from this source in the current view.';
      empty.setAttribute('data-show', visibleCards.length === 0 ? '1' : '0');
    });
  }

  function joinQueryTag(existing, tag) {
    if (!tag) {
      return existing;
    }

    if (Array.isArray(existing)) {
      return existing.indexOf(tag) >= 0 ? existing : existing.concat([tag]);
    }

    var current = existing ? String(existing) : '';
    if (!current) {
      return tag;
    }

    var parts = current.split(',');
    if (parts.indexOf(tag) >= 0) {
      return current;
    }

    return current + ',' + tag;
  }

  function applyOriginToOptions(options) {
    var mode = getOriginFilter();
    if (!options || mode === 'all' || !originFilterActive()) {
      return;
    }

    if (mode === 'local') {
      options.ExcludeItemTags = joinQueryTag(options.ExcludeItemTags, 'Federated');
      return;
    }

    if (mode.indexOf('friend:') === 0) {
      options.Tags = joinQueryTag(options.Tags, '🌐 ' + mode.slice(7));
    }
  }

  function isItemsListUrl(url) {
    if (!url || url.indexOf('/Plugins/') !== -1) {
      return false;
    }

    var path = String(url).split('?')[0];
    return /\/Items\/?$/.test(path) || /\/Users\/[^/]+\/Items\/?$/.test(path);
  }

  function applyOriginToUrl(url) {
    var mode = getOriginFilter();
    if (mode === 'all' || !originFilterActive() || !isItemsListUrl(url)) {
      return url;
    }

    try {
      var parsed = new URL(url, window.location.origin);
      if (mode === 'local') {
        parsed.searchParams.set('ExcludeItemTags', joinQueryTag(parsed.searchParams.get('ExcludeItemTags'), 'Federated'));
      } else if (mode.indexOf('friend:') === 0) {
        parsed.searchParams.set('Tags', joinQueryTag(parsed.searchParams.get('Tags'), '🌐 ' + mode.slice(7)));
      }

      return parsed.pathname + parsed.search + parsed.hash;
    } catch (e) {
      return url;
    }
  }

  function hookOriginQueries() {
    if (!window.__federationOriginFetchHook) {
      window.__federationOriginFetchHook = true;
      var origFetch = window.fetch;
      window.fetch = function (input, init) {
        try {
          var url = typeof input === 'string' ? input : (input && input.url);
          var next = url ? applyOriginToUrl(url) : url;
          if (next && next !== url) {
            input = typeof input === 'string' ? next : new Request(next, input);
          }
        } catch (e) { /* leave the request unchanged */ }

        return origFetch.call(this, input, init);
      };
    }

    if (!window.ApiClient || window.ApiClient.__federationOriginHook) {
      return;
    }

    window.ApiClient.__federationOriginHook = true;
    if (typeof window.ApiClient.getItems === 'function') {
      var origGetItems = window.ApiClient.getItems.bind(window.ApiClient);
      window.ApiClient.getItems = function (userId, options) {
        options = Object.assign({}, options || {});
        applyOriginToOptions(options);
        return origGetItems(userId, options);
      };
    }
  }

  function reloadLibraryItems() {
    document.querySelectorAll('.itemsContainer').forEach(function (container) {
      try {
        if (typeof container.notifyRefreshNeeded === 'function') {
          container.notifyRefreshNeeded(true);
        }
      } catch (e) { /* visibility filter still applies */ }
    });
  }

  function originHostPage() {
    var page = document.querySelector('.page:not(.hide)');
    if (!page) {
      return null;
    }

    if (page.classList.contains('type-interior') || page.id === 'federationConfigPage' || page.classList.contains('itemDetailPage')) {
      return null;
    }

    if (/#!\/details/i.test(location.hash || location.href || '')) {
      return null;
    }

    if (page.classList.contains('libraryPage') || page.classList.contains('homePage') || page.classList.contains('searchPage')) {
      return page;
    }

    return null;
  }

  function originToolbar(page) {
    return page.querySelector('.itemsViewSettingsContainer')
      || page.querySelector('.btnFilter-wrapper')
      || (page.querySelector('.btnFilter') && page.querySelector('.btnFilter').parentElement)
      || page.querySelector('.padded-left.padded-right.padded-top')
      || page.querySelector('.padded-left.padded-right');
  }

  function renderOriginFilter() {
    hookOriginQueries();
    var page = originHostPage();
    document.querySelectorAll('.federation-origin-filter').forEach(function (bar) {
      if (!page || !page.contains(bar)) {
        bar.remove();
      }
    });

    if (!page) {
      applyOriginVisibility();
      return;
    }

    var friends = uniqueFriendNames();
    var mode = getOriginFilter();
    if (mode.indexOf('friend:') === 0 && friends.indexOf(mode.slice(7)) < 0) {
      setOriginFilter('all');
      mode = 'all';
      reloadLibraryItems();
    }
    var signature = friends.join('\t') + '|' + mode;
    var bar = page.querySelector(':scope .federation-origin-filter');
    if (!bar) {
      var toolbar = originToolbar(page);
      if (!toolbar || !toolbar.parentNode) {
        applyOriginVisibility();
        return;
      }

      bar = document.createElement('div');
      bar.className = 'federation-origin-filter padded-left padded-right';
      bar.setAttribute('role', 'tablist');
      bar.setAttribute('aria-label', 'Library source');
      toolbar.parentNode.insertBefore(bar, toolbar.nextSibling);
      bar.addEventListener('click', function (event) {
        var tab = event.target.closest('.federation-origin-tab');
        if (!tab) {
          return;
        }

        event.preventDefault();
        var next = tab.getAttribute('data-origin') || 'all';
        if (next.indexOf('friend:') === 0) {
          try {
            next = 'friend:' + decodeURIComponent(next.slice(7));
          } catch (e) { /* keep encoded value */ }
        }

        setOriginFilter(next);
        renderOriginFilter();
        reloadLibraryItems();
      });
    }

    if (bar.getAttribute('data-signature') === signature) {
      applyOriginVisibility();
      return;
    }

    var tabs = [{ origin: 'all', label: 'All' }, { origin: 'local', label: 'This server' }];
    friends.forEach(function (name) {
      tabs.push({ origin: 'friend:' + encodeURIComponent(name), label: name });
    });

    bar.setAttribute('data-signature', signature);
    bar.setAttribute('data-empty', friends.length === 0 ? '1' : '0');
    bar.innerHTML = tabs.map(function (tab) {
      var selected = tab.origin === 'all' ? mode === 'all'
        : tab.origin === 'local' ? mode === 'local'
          : mode === 'friend:' + decodeURIComponent(tab.origin.slice(7));
      return '<button type="button" class="federation-origin-tab" role="tab" data-origin="'
        + tab.origin + '" aria-selected="' + (selected ? 'true' : 'false') + '">'
        + tab.label.replace(/&/g, '&amp;').replace(/</g, '&lt;') + '</button>';
    }).join('');
    applyOriginVisibility();
  }

  function scan() {
    if (!clientSettingsLoaded) {
      return;
    }

    injectStyle();
    hookPlaybackOverlay();
    document.querySelectorAll(CARD_SELECTOR).forEach(badgeCard);
    badgeDetailPage();
    refreshDownloadRingsOnCards();
    updateDetailPageRing();
    renderOriginFilter();
    renderFriendFavorites();
    renderFriendPopular();
    injectSyncPlayMenu();
    injectPlaybackConnection();
  }

  // ---------------------------------------------------------------------
  // Playback Info panel (player menu → Playback Info): a "Federation" block
  // for friends' titles with the friend, how the stream travels, who converts
  // it and why, the link, and the hardware involved. jellyfin-web re-renders
  // the panel every second (replacing its contents), which re-runs scan(), so
  // the block is simply put back each time. The server works out what this
  // device is playing; the answer is refreshed every 10 seconds.
  // ---------------------------------------------------------------------
  var connection = { data: null, fetchedAt: 0, pending: false, panel: null };
  var routeTelemetry = { samples: [], pending: false, waitingSince: 0, started: false };

  function telemetryResourceMatches(name, data) {
    if (!name || !data || !data.serverId || !data.remoteItemId) { return false; }
    var url = String(name).toLowerCase().replace(/-/g, '');
    var remote = String(data.remoteItemId).toLowerCase().replace(/-/g, '');
    var server = String(data.serverId).toLowerCase().replace(/-/g, '');
    if (url.indexOf(remote) < 0) { return false; }
    if (data.route === 'relay') {
      return url.indexOf('/plugins/federation/stream') >= 0 && url.indexOf('serverid=' + server) >= 0;
    }
    if (data.route === 'direct-link') {
      return url.indexOf('/directlink/' + server + '/') >= 0;
    }
    return url.indexOf('/plugins/federation/directstream/') >= 0 || url.indexOf('/plugins/federation/peer/play/') >= 0;
  }

  function observeRoutePerformance(entries) {
    var data = connection.data;
    if (!data || !data.federated) { return; }
    entries.forEach(function (entry) {
      if (!telemetryResourceMatches(entry.name, data)) { return; }
      var bytes = Number(entry.encodedBodySize || 0);
      var duration = Number(entry.duration || 0);
      if (bytes >= 65536 && bytes <= 1000000000 && duration >= 250 && duration <= 300000) {
        routeTelemetry.samples.push({ bytes: bytes, durationMs: duration });
      }
    });
    routeTelemetry.samples = routeTelemetry.samples.slice(-50);
  }

  function videoRouteHealth() {
    var video = document.querySelector('video');
    var buffered = null;
    if (video && isFinite(video.currentTime)) {
      for (var i = 0; i < video.buffered.length; i++) {
        if (video.buffered.start(i) <= video.currentTime + 0.05 && video.buffered.end(i) > video.currentTime) {
          buffered = Math.max(0, video.buffered.end(i) - video.currentTime);
          break;
        }
      }
    }
    var quality = video && typeof video.getVideoPlaybackQuality === 'function' ? video.getVideoPlaybackQuality() : null;
    return {
      stalled: !!routeTelemetry.waitingSince,
      bufferedSeconds: buffered,
      droppedFrames: quality && isFinite(quality.droppedVideoFrames) ? quality.droppedVideoFrames : null,
      totalFrames: quality && isFinite(quality.totalVideoFrames) ? quality.totalVideoFrames : null
    };
  }

  function flushRouteTelemetry() {
    var data = connection.data;
    if (!data || !data.federated || routeTelemetry.pending) { return Promise.resolve(); }
    var samples = routeTelemetry.samples.splice(0);
    var health = videoRouteHealth();
    if (!samples.length && !health.stalled && health.bufferedSeconds == null && health.droppedFrames == null) { return Promise.resolve(); }
    var body = {
      itemId: data.itemId, serverId: data.serverId, route: data.route,
      bytes: samples.length ? samples.reduce(function (sum, sample) { return sum + sample.bytes; }, 0) : null,
      durationMs: samples.length ? samples.reduce(function (sum, sample) { return sum + sample.durationMs; }, 0) : null,
      stalled: health.stalled, bufferedSeconds: health.bufferedSeconds,
      droppedFrames: health.droppedFrames, totalFrames: health.totalFrames
    };
    routeTelemetry.pending = true;
    return federationFetch('/Plugins/Federation/Playback/Metrics', {
      method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
    }).catch(function () {
      routeTelemetry.samples = samples.concat(routeTelemetry.samples).slice(-50);
    }).then(function () { routeTelemetry.pending = false; });
  }

  function startRouteTelemetry() {
    if (routeTelemetry.started) { return; }
    routeTelemetry.started = true;
    if (typeof window.PerformanceObserver === 'function') {
      try {
        var observer = new window.PerformanceObserver(function (list) { observeRoutePerformance(list.getEntries()); });
        observer.observe({ type: 'resource', buffered: true });
        routeTelemetry.observer = observer;
      } catch (_) { /* Browser timing is optional; no measurement is reported without it. */ }
    }
    document.addEventListener('waiting', function () { routeTelemetry.waitingSince = Date.now(); }, true);
    document.addEventListener('playing', function () { routeTelemetry.waitingSince = 0; }, true);
    window.setInterval(flushRouteTelemetry, 15000);
  }

  window.__federationRouteTelemetry = {
    flush: flushRouteTelemetry,
    matches: telemetryResourceMatches,
    observe: observeRoutePerformance,
    samples: routeTelemetry.samples
  };

  function injectPlaybackConnection() {
    startRouteTelemetry();
    var stats = document.querySelector('.playerStats:not(.hide) .playerStats-stats');
    if (!stats) {
      connection.panel = null;
      return;
    }

    if (connection.panel !== stats) {
      // Newly opened panel: forget the previous title's answer.
      connection.panel = stats;
      connection.data = null;
      connection.fetchedAt = 0;
    }

    if (!connection.pending && Date.now() - connection.fetchedAt > 10000) {
      connection.pending = true;
      federationFetch('/Plugins/Federation/Playback/Connection', { credentials: 'same-origin' })
        .then(function (res) { return res.ok ? res.json() : null; })
        .catch(function () { return null; })
        .then(function (data) {
          connection.pending = false;
          connection.fetchedAt = Date.now();
          connection.data = data;
          if (connection.panel) { renderPlaybackConnection(connection.panel); }
        });
    }

    renderPlaybackConnection(stats);
  }

  function playbackConnectionRows(data) {
    if (!data || !data.federated) {
      return null;
    }

    var rows = [['Friend', data.friend], ['Playback', data.playMethod || 'Unknown'], ['Route', data.routeText || 'Unknown'], ['Conversion', data.conversion || 'Unknown']];
    function formatFormat(container, video, audio, bitrate) {
      var codecs = [container, video, audio].filter(function (value) { return !!value; }).join(' / ');
      if (typeof bitrate === 'number' && bitrate > 0) {
        codecs += (codecs ? ' · ' : '') + (bitrate / 1000000).toFixed(bitrate < 10000000 ? 1 : 0) + ' Mbps';
      }
      return codecs;
    }
    var original = formatFormat(data.originalContainer, data.originalVideoCodec, data.originalAudioCodec, data.originalBitrate);
    var delivered = formatFormat(data.deliveredContainer, data.deliveredVideoCodec, data.deliveredAudioCodec, data.deliveredBitrate);
    if (original) { rows.push(['Original', original]); }
    if (delivered) { rows.push(['Delivered', delivered]); }
    if (data.transcodeReasons && data.transcodeReasons !== '0') { rows.push(['Conversion reason', data.transcodeReasons]); }
    if (data.placement) { rows.push(['Placement reason', data.placement]); }
    if (typeof data.latencyMs === 'number') { rows.push(['Round trip', Math.round(data.latencyMs) + ' ms']); }
    if (typeof data.linkMbps === 'number') { rows.push(['Server route speed', data.linkMbps.toFixed(data.linkMbps < 10 ? 1 : 0) + ' Mbps']); }
    if (typeof data.routeMbps === 'number') { rows.push(['Device route speed', data.routeMbps.toFixed(data.routeMbps < 10 ? 1 : 0) + ' Mbps' + (typeof data.routeMeasurementAgeSeconds === 'number' ? ' · ' + data.routeMeasurementAgeSeconds + 's old' : '')]); }
    if (typeof data.routeBufferedSeconds === 'number') { rows.push(['Buffered ahead', data.routeBufferedSeconds.toFixed(1) + 's']); }
    if (typeof data.routeDroppedFrames === 'number' && typeof data.routeTotalFrames === 'number' && data.routeTotalFrames > 0) { rows.push(['Dropped frames', data.routeDroppedFrames + ' / ' + data.routeTotalFrames]); }
    if (data.speedNote) { rows.push(['Speed test', data.speedNote]); }
    if (data.directLink) { rows.push(['Direct link', data.directLink]); }
    if (typeof data.capMbps === 'number') { rows.push(['Bitrate cap', data.capMbps + ' Mbps']); }
    if (data.friendHardware) { rows.push([data.friend + ' converts with', data.friendHardware]); }
    if (data.thisServerHardware) { rows.push(['This server converts with', data.thisServerHardware]); }
    if (data.alsoOn && data.alsoOn.length) { rows.push(['Also on', data.alsoOn.join(', ')]); }
    return rows;
  }

  function renderPlaybackConnection(stats) {
    var existing = stats.querySelector('.federation-stats');
    var rows = playbackConnectionRows(connection.data);
    if (!rows) {
      if (existing) { existing.parentNode.removeChild(existing); }
      return;
    }

    var signature = JSON.stringify(rows);
    if (existing && existing.getAttribute('data-signature') === signature) {
      return;
    }

    // Same classes as jellyfin-web's own sections, so it looks native.
    var block = document.createElement('div');
    block.className = 'federation-stats';
    block.setAttribute('data-signature', signature);
    function row(label, value, header) {
      var line = document.createElement('div');
      line.className = header ? 'playerStats-stat playerStats-stat-header' : 'playerStats-stat';
      var l = document.createElement('div');
      l.className = 'playerStats-stat-label';
      l.textContent = label;
      var v = document.createElement('div');
      v.className = 'playerStats-stat-value';
      v.textContent = value || '';
      line.appendChild(l);
      line.appendChild(v);
      block.appendChild(line);
    }

    row('Federation', '', true);
    rows.forEach(function (r) { row(r[0], r[1], false); });
    if (existing) {
      existing.parentNode.replaceChild(block, existing);
    } else {
      stats.appendChild(block);
    }
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

  // ---------------------------------------------------------------------
  // Watch parties with friends
  //
  // Extra entries in jellyfin-web's own SyncPlay group menu (the people icon at
  // the top): "New group with friends" next to "New group", and "Invite
  // friends" / "Shared with friends" once in a group. Friends' parties need no
  // entry here: the server lists them in the menu itself and joining works
  // like any group. Entries carry an empty data-id so the menu's own handler
  // just closes it (an unknown id would be treated as a group to join).
  // ---------------------------------------------------------------------
  var partyNotice = null;

  function notifyParty(text) {
    if (!partyNotice) {
      partyNotice = document.createElement('div');
      partyNotice.className = 'federation-party-notice';
      partyNotice.setAttribute('role', 'status');
      document.body.appendChild(partyNotice);
    }

    partyNotice.textContent = text;
    partyNotice.setAttribute('data-show', '1');
    clearTimeout(partyNotice.hideTimer);
    partyNotice.hideTimer = setTimeout(function () { partyNotice.setAttribute('data-show', '0'); }, 5000);
  }

  function partyRequest(path, body) {
    return federationFetch(path, {
      method: body === undefined ? 'GET' : 'POST',
      credentials: 'same-origin',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body)
    }).then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (data) { return { ok: res.ok, data: data || {} }; });
    });
  }

  function currentGroupId(title) {
    if (!window.ApiClient || typeof window.ApiClient.getSyncPlayGroups !== 'function') {
      return Promise.resolve(null);
    }

    return Promise.resolve(window.ApiClient.getSyncPlayGroups())
      .then(function (res) { return res && typeof res.json === 'function' ? res.json() : res; })
      .then(function (groups) {
        var match = (groups || []).filter(function (g) { return g.GroupName === title; });
        return match.length === 1 ? match[0].GroupId : null;
      })
      .catch(function () { return null; });
  }

  function injectSyncPlayMenu() {
    var sheet = document.querySelector('.syncPlayGroupMenu');
    var scroller = sheet && sheet.querySelector('.actionSheetScroller');
    if (!scroller || scroller.getAttribute('data-federation-party') === '1') {
      return;
    }

    scroller.setAttribute('data-federation-party', '1');
    var newGroup = scroller.querySelector('[data-id="new-group"]');
    var leave = scroller.querySelector('[data-id="leave-group"]');
    if (!newGroup && !leave) {
      return;
    }

    if (newGroup) {
      partyRequest('/Plugins/Federation/Party/Status/00000000000000000000000000000000').then(function (status) {
        if (!status.ok || !status.data.canHost || !newGroup.isConnected) {
          return;
        }

        var item = makeActionSheetItem('groups', 'New group with friends', '', function () {
          partyRequest('/Plugins/Federation/Party', {}).then(function (result) {
            notifyParty(result.data.message || (result.ok ? 'Friends can join from their group list.' : 'The watch party could not be started.'));
          });
        });
        item.classList.add('federation-party-item');
        newGroup.parentNode.insertBefore(item, newGroup.nextSibling);
      });
      return;
    }

    var titleEl = sheet.querySelector('.actionSheetTitle');
    var title = titleEl ? titleEl.textContent : '';
    currentGroupId(title).then(function (groupId) {
      if (!groupId || !leave.isConnected) {
        return null;
      }

      return partyRequest('/Plugins/Federation/Party/Status/' + encodeURIComponent(groupId)).then(function (status) {
        if (!status.ok || !leave.isConnected) {
          return;
        }

        var item;
        if (status.data.shared) {
          var friends = (status.data.friends || []).join(', ');
          item = makeActionSheetItem('groups', status.data.hostedHere
            ? (friends ? 'Watching with ' + friends : 'Shared with friends')
            : 'Hosted by ' + (friends || 'a friend'), '', function () {});
        } else if (status.data.canHost) {
          item = makeActionSheetItem('groups', 'Invite friends', '', function () {
            partyRequest('/Plugins/Federation/Party/Share', { GroupId: groupId }).then(function (result) {
              notifyParty(result.data.message || (result.ok ? 'Friends can join from their group list.' : 'Could not invite friends.'));
            });
          });
        }

        if (item) {
          item.classList.add('federation-party-item');
          leave.parentNode.insertBefore(item, leave);
        }
      });
    });
  }

  // ---------------------------------------------------------------------
  // Friends' favorites
  //
  // Below the Favorites tab's own sections, one row per friend: "Favorites on
  // <friend>", the titles that friend's users favorited (never who). The
  // server only lists titles this viewer can open here, so every card plays
  // like any other. Built from jellyfin-web's own section/heading classes;
  // the row scrolls sideways like the native ones.
  // ---------------------------------------------------------------------
  var FRIEND_FAVORITES_TTL_MS = 5 * 60 * 1000;
  var friendFavorites = { userId: '', at: 0, loading: false, html: '' };

  function escapeHtml(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function currentUserId() {
    try {
      if (window.ApiClient && typeof window.ApiClient.getCurrentUserId === 'function') {
        return window.ApiClient.getCurrentUserId() || '';
      }
    } catch (e) { /* not signed in yet */ }
    return '';
  }

  function favoriteCardShape(item) {
    if (item.Type === 'Episode' || item.Type === 'Video' || item.Type === 'MusicVideo') { return 'backdrop'; }
    if (item.Type === 'Audio' || item.Type === 'MusicAlbum') { return 'square'; }
    return 'portrait';
  }

  function favoriteImageUrl(item, shape) {
    var tag = item.ImageTags && item.ImageTags.Primary;
    if (!tag || !window.ApiClient || typeof window.ApiClient.getScaledImageUrl !== 'function') {
      return '';
    }

    return window.ApiClient.getScaledImageUrl(item.Id, {
      type: 'Primary',
      tag: tag,
      maxHeight: shape === 'portrait' ? 360 : 240,
      quality: 90
    });
  }

  function favoriteCardHtml(item, serverId) {
    var shape = favoriteCardShape(item);
    var image = favoriteImageUrl(item, shape);
    var subtitle = item.Type === 'Episode'
      ? (item.SeriesName || '')
      : (item.Type === 'Audio' || item.Type === 'MusicAlbum')
        ? (item.AlbumArtist || (item.Artists && item.Artists[0]) || '')
        : (item.ProductionYear ? String(item.ProductionYear) : '');
    var href = '#/details?id=' + encodeURIComponent(item.Id) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : '');
    return '<a class="federation-fav-card federation-fav-' + shape + '" href="' + href + '" data-id="' + escapeHtml(item.Id) + '" title="' + escapeHtml(item.Name) + '">'
      + '<span class="federation-fav-art">'
      + (image ? '<img loading="lazy" decoding="async" alt="" src="' + escapeHtml(image) + '">' : '<span class="federation-fav-noart">' + escapeHtml(item.Name) + '</span>')
      + '</span>'
      + '<span class="federation-fav-title">' + escapeHtml(item.Name) + '</span>'
      + (subtitle ? '<span class="federation-fav-sub">' + escapeHtml(subtitle) + '</span>' : '')
      + '</a>';
  }

  function friendFavoritesHtml(sections, itemsById, serverId) {
    return sections.map(function (section) {
      var cards = (section.itemIds || [])
        .map(function (id) { return itemsById[normalizeId(id)]; })
        .filter(Boolean)
        .map(function (item) { return favoriteCardHtml(item, serverId); });
      if (cards.length === 0) {
        return '';
      }

      return '<div class="verticalSection federation-favorites-section" data-server-id="' + escapeHtml(section.serverId) + '">'
        + '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left">'
        + '<h2 class="sectionTitle sectionTitle-cards">Favorites on ' + escapeHtml(section.name) + '</h2>'
        + '</div>'
        + '<div class="federation-favorites-row padded-left padded-right">' + cards.join('') + '</div>'
        + '</div>';
    }).join('');
  }

  function loadFriendFavorites(userId) {
    friendFavorites.loading = true;
    federationFetch('/Plugins/Federation/FriendFavorites', { credentials: 'same-origin' })
      .then(function (res) {
        if (!res.ok) { throw new Error('Friends\' favorites unavailable'); }
        return res.json();
      })
      .then(function (data) {
        var sections = (data && data.enabled !== false && data.sections) || [];
        var ids = [];
        sections.forEach(function (section) { (section.itemIds || []).forEach(function (id) { ids.push(id); }); });
        if (ids.length === 0 || !window.ApiClient || typeof window.ApiClient.getItems !== 'function') {
          return { sections: sections, items: [] };
        }

        return Promise.resolve(window.ApiClient.getItems(userId, {
          Ids: ids.join(','),
          Fields: 'PrimaryImageAspectRatio',
          EnableImageTypes: 'Primary',
          ImageTypeLimit: 1,
          EnableTotalRecordCount: false
        })).then(function (result) {
          return { sections: sections, items: (result && result.Items) || [] };
        });
      })
      .then(function (result) {
        var itemsById = {};
        result.items.forEach(function (item) { itemsById[normalizeId(item.Id)] = item; });
        var serverId = '';
        try { serverId = window.ApiClient.serverId(); } catch (e) { /* links still open on this server */ }
        friendFavorites.html = friendFavoritesHtml(result.sections, itemsById, serverId);
        friendFavorites.userId = userId;
        friendFavorites.at = Date.now();
      })
      .catch(function () {
        // Keep what was shown; try again on the next visit to the tab.
        // userId is recorded too, or the next scan sees "another viewer" and asks again at once.
        friendFavorites.userId = userId;
        friendFavorites.at = Date.now() - FRIEND_FAVORITES_TTL_MS + 30 * 1000;
      })
      .then(function () {
        friendFavorites.loading = false;
        paintFriendFavorites();
      });
  }

  function paintFriendFavorites() {
    var tab = document.getElementById('favoritesTab');
    if (!tab) {
      return;
    }

    var host = tab.querySelector(':scope > .federation-friend-favorites');
    if (!host) {
      host = document.createElement('div');
      host.className = 'federation-friend-favorites';
      tab.appendChild(host);
    }

    if (host.getAttribute('data-html-at') !== String(friendFavorites.at)) {
      host.innerHTML = friendFavorites.html;
      host.setAttribute('data-html-at', String(friendFavorites.at));
    }
  }

  function renderFriendFavorites() {
    var tab = document.getElementById('favoritesTab');
    if (!tab) {
      return;
    }

    paintFriendFavorites();
    var userId = currentUserId();
    var visible = tab.classList.contains('is-active') || tab.offsetParent !== null;
    if (!userId || !visible || friendFavorites.loading) {
      return;
    }

    if (friendFavorites.userId !== userId) {
      // Another viewer signed in on this browser: never show the last one's rows.
      friendFavorites.html = '';
      friendFavorites.at = 0;
      paintFriendFavorites();
    }

    if (friendFavorites.userId !== userId || Date.now() - friendFavorites.at > FRIEND_FAVORITES_TTL_MS) {
      loadFriendFavorites(userId);
    }
  }

  // ---------------------------------------------------------------------
  // Popular on friends' servers
  //
  // On the Home tab, one row per friend: "Popular on <friend>", the titles
  // most played there in the last 30 days, most popular first. Same cards and
  // row as the friends' favorites; the server only lists titles this viewer can
  // open here. A friend with nothing to show (or an older one) gets no row.
  // ---------------------------------------------------------------------
  var FRIEND_POPULAR_TTL_MS = 10 * 60 * 1000;
  var friendPopular = { userId: '', at: 0, loading: false, html: '' };

  function friendPopularHtml(sections, itemsById, serverId) {
    return sections.map(function (section) {
      var cards = (section.itemIds || [])
        .map(function (id) { return itemsById[normalizeId(id)]; })
        .filter(Boolean)
        .map(function (item) { return favoriteCardHtml(item, serverId); });
      if (cards.length === 0) {
        return '';
      }

      return '<div class="verticalSection federation-popular-section" data-server-id="' + escapeHtml(section.serverId) + '">'
        + '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left">'
        + '<h2 class="sectionTitle sectionTitle-cards">Popular on ' + escapeHtml(section.name) + '</h2>'
        + '</div>'
        + '<div class="federation-favorites-row padded-left padded-right">' + cards.join('') + '</div>'
        + '</div>';
    }).join('');
  }

  function loadFriendPopular(userId) {
    friendPopular.loading = true;
    federationFetch('/Plugins/Federation/FriendPopular', { credentials: 'same-origin' })
      .then(function (res) {
        if (!res.ok) { throw new Error('Popular titles unavailable'); }
        return res.json();
      })
      .then(function (data) {
        var sections = (data && data.enabled !== false && data.sections) || [];
        var ids = [];
        sections.forEach(function (section) { (section.itemIds || []).forEach(function (id) { ids.push(id); }); });
        if (ids.length === 0 || !window.ApiClient || typeof window.ApiClient.getItems !== 'function') {
          return { sections: sections, items: [] };
        }

        return Promise.resolve(window.ApiClient.getItems(userId, {
          Ids: ids.join(','),
          Fields: 'PrimaryImageAspectRatio',
          EnableImageTypes: 'Primary',
          ImageTypeLimit: 1,
          EnableTotalRecordCount: false
        })).then(function (result) {
          return { sections: sections, items: (result && result.Items) || [] };
        });
      })
      .then(function (result) {
        var itemsById = {};
        result.items.forEach(function (item) { itemsById[normalizeId(item.Id)] = item; });
        var serverId = '';
        try { serverId = window.ApiClient.serverId(); } catch (e) { /* links still open on this server */ }
        friendPopular.html = friendPopularHtml(result.sections, itemsById, serverId);
        friendPopular.userId = userId;
        friendPopular.at = Date.now();
      })
      .catch(function () {
        // Keep what was shown; try again shortly.
        // userId is recorded too, or the next scan sees "another viewer" and asks again at once.
        friendPopular.userId = userId;
        friendPopular.at = Date.now() - FRIEND_POPULAR_TTL_MS + 30 * 1000;
      })
      .then(function () {
        friendPopular.loading = false;
        paintFriendPopular();
      });
  }

  function paintFriendPopular() {
    var tab = document.getElementById('homeTab');
    if (!tab) {
      return;
    }

    var host = tab.querySelector(':scope > .federation-friend-popular');
    if (!host) {
      if (!friendPopular.html) {
        return;
      }

      host = document.createElement('div');
      host.className = 'federation-friend-popular';
      tab.appendChild(host);
    }

    if (host.getAttribute('data-html-at') !== String(friendPopular.at)) {
      host.innerHTML = friendPopular.html;
      host.setAttribute('data-html-at', String(friendPopular.at));
    }
  }

  function renderFriendPopular() {
    var tab = document.getElementById('homeTab');
    if (!tab) {
      return;
    }

    paintFriendPopular();
    var userId = currentUserId();
    var visible = tab.classList.contains('is-active') || tab.offsetParent !== null;
    if (!userId || !visible || friendPopular.loading) {
      return;
    }

    if (friendPopular.userId !== userId) {
      // Another viewer signed in on this browser: never show the last one's rows.
      friendPopular.html = '';
      friendPopular.at = 0;
      var stale = tab.querySelector(':scope > .federation-friend-popular');
      if (stale) { stale.innerHTML = ''; stale.removeAttribute('data-html-at'); }
    }

    if (friendPopular.userId !== userId || Date.now() - friendPopular.at > FRIEND_POPULAR_TTL_MS) {
      loadFriendPopular(userId);
    }
  }

  // ---------------------------------------------------------------------
  // Loading bar
  //
  // A federated title can take a while to start because its first seconds have to
  // cross the friend's connection. A black screen for that long looks broken, so
  // when playback of a federated title has not started 1.5 s after the viewer
  // pressed play, a plain bar appears at the bottom with a projected countdown,
  // the reason for the wait and a fun fact. It never blocks the player controls
  // and disappears the moment video is actually playing.
  // ---------------------------------------------------------------------
  var OVERLAY_ID = 'federation-loading-overlay';
  var OVERLAY_STYLE_ID = 'federation-loading-style';
  var OVERLAY_SHOW_DELAY_MS = 1500;
  var OVERLAY_GIVE_UP_MS = 10 * 60 * 1000;
  var OVERLAY_NO_VIDEO_GIVE_UP_MS = 25 * 1000;
  var FACT_ROTATE_SECONDS = 12;
  var overlayState = null;

  function injectOverlayStyle() {
    if (document.getElementById(OVERLAY_STYLE_ID)) {
      return;
    }

    var style = document.createElement('style');
    style.id = OVERLAY_STYLE_ID;
    style.textContent = [
      '#' + OVERLAY_ID + '{position:fixed;left:0;right:0;bottom:0;z-index:100000;box-sizing:border-box;',
      'padding:.9em 1.4em 1.1em;background:rgba(0,0,0,.82);color:#fff;font-size:clamp(13px,1.1vw,16px);line-height:1.4;',
      'pointer-events:none;}',
      '#' + OVERLAY_ID + ' .fl-bar{height:4px;background:rgba(255,255,255,.22);border-radius:2px;overflow:hidden;margin-bottom:.6em;}',
      '#' + OVERLAY_ID + ' .fl-fill{height:100%;width:0;background:#fff;transition:width .9s linear;}',
      '#' + OVERLAY_ID + ' .fl-fill.fl-indeterminate{width:30%;animation:fl-slide 1.4s ease-in-out infinite;}',
      '#' + OVERLAY_ID + ' .fl-time{font-weight:600;}',
      '#' + OVERLAY_ID + ' .fl-reason,#' + OVERLAY_ID + ' .fl-fact{opacity:.85;}',
      '#' + OVERLAY_ID + ' .fl-fact{margin-top:.35em;font-style:italic;}',
      '@keyframes fl-slide{0%{margin-left:-30%}100%{margin-left:100%}}',
      '@media (prefers-reduced-motion:reduce){#' + OVERLAY_ID + ' .fl-fill.fl-indeterminate{animation:none;width:100%;opacity:.5}}'
    ].join('');
    document.head.appendChild(style);
  }

  function formatWait(seconds) {
    seconds = Math.max(0, Math.round(seconds));
    if (seconds < 60) {
      return seconds + ' s';
    }

    var minutes = Math.floor(seconds / 60);
    var rest = seconds % 60;
    return minutes + ' min' + (rest ? ' ' + rest + ' s' : '');
  }

  function playingVideoElement() {
    var video = document.querySelector('video');
    return !!(video && !video.paused && video.currentTime > 0 && video.readyState >= 3);
  }

  function stopLoadingOverlay() {
    var state = overlayState;
    overlayState = null;
    if (!state) {
      return;
    }

    state.cancelled = true;
    if (state.showTimer) {
      clearTimeout(state.showTimer);
    }

    if (state.tickTimer) {
      clearInterval(state.tickTimer);
    }

    var node = document.getElementById(OVERLAY_ID);
    if (node && node.parentNode) {
      node.parentNode.removeChild(node);
    }
  }

  function renderOverlayFrame(state) {
    var node = document.getElementById(OVERLAY_ID);
    if (!node) {
      return;
    }

    var elapsed = (Date.now() - state.startedAt) / 1000;
    var time = node.querySelector('.fl-time');
    var fill = node.querySelector('.fl-fill');
    if (state.seconds != null) {
      var remaining = state.seconds - elapsed;
      time.textContent = remaining > 0
        ? 'Starting in about ' + formatWait(remaining)
        : 'Almost there - this is taking longer than projected';
      fill.classList.remove('fl-indeterminate');
      fill.style.width = Math.min(95, Math.max(3, (elapsed / state.seconds) * 100)) + '%';
    } else {
      time.textContent = 'Getting things ready... ' + formatWait(elapsed);
      fill.classList.add('fl-indeterminate');
    }

    node.querySelector('.fl-reason').textContent = state.reason || '';
  }

  function loadFunFact(state) {
    federationFetch('/Plugins/Federation/FunFact', { credentials: 'same-origin' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (data) {
        var node = document.getElementById(OVERLAY_ID);
        if (!data || !data.text || !node || overlayState !== state) {
          return;
        }

        node.querySelector('.fl-fact').textContent = 'Fun fact: ' + data.text;
      })
      .catch(function () { /* the bar simply has no fact line */ });
  }

  function showLoadingOverlay(state) {
    if (overlayState !== state || document.getElementById(OVERLAY_ID)) {
      return;
    }

    if (playingVideoElement()) {
      stopLoadingOverlay();
      return;
    }

    injectOverlayStyle();
    var node = document.createElement('div');
    node.id = OVERLAY_ID;
    node.innerHTML = '<div class="fl-bar"><div class="fl-fill"></div></div>'
      + '<div class="fl-time"></div><div class="fl-reason"></div><div class="fl-fact"></div>';
    document.body.appendChild(node);
    renderOverlayFrame(state);
    loadFunFact(state);
  }

  function tickLoadingOverlay(state) {
    if (overlayState !== state) {
      return;
    }

    var elapsedMs = Date.now() - state.startedAt;
    var hasVideo = !!document.querySelector('video');
    if (hasVideo) {
      state.seenVideo = true;
    }

    // Playing, abandoned (the player was closed), never started, or simply too long.
    if (playingVideoElement()
      || (state.seenVideo && !hasVideo)
      || (!state.seenVideo && elapsedMs > OVERLAY_NO_VIDEO_GIVE_UP_MS)
      || elapsedMs > OVERLAY_GIVE_UP_MS) {
      stopLoadingOverlay();
      return;
    }

    renderOverlayFrame(state);
    var elapsedSeconds = Math.floor(elapsedMs / 1000);
    if (document.getElementById(OVERLAY_ID) && elapsedSeconds > 0 && elapsedSeconds % FACT_ROTATE_SECONDS === 0) {
      loadFunFact(state);
    }
  }

  function startLoadingOverlay(rawId) {
    if (!loadingOverlayEnabled) {
      return;
    }

    var id = normalizeId(rawId);
    if (!id || !federatedIds.has(id)) {
      return;
    }

    // jellyfin-web asks for playback info more than once per play; keep the bar
    // (and its countdown) rather than restarting it.
    if (overlayState && overlayState.id === id) {
      return;
    }

    stopLoadingOverlay();
    var state = { id: id, startedAt: Date.now(), seconds: null, reason: '', cancelled: false, seenVideo: false };
    overlayState = state;
    state.showTimer = setTimeout(function () { showLoadingOverlay(state); }, OVERLAY_SHOW_DELAY_MS);
    state.tickTimer = setInterval(function () { tickLoadingOverlay(state); }, 1000);

    federationFetch('/Plugins/Federation/StartEstimate/' + id, { credentials: 'same-origin' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (data) {
        if (!data || data.federated === false || overlayState !== state) {
          return;
        }

        state.seconds = typeof data.seconds === 'number' ? data.seconds : null;
        state.reason = typeof data.reason === 'string' ? data.reason : '';
        renderOverlayFrame(state);
      })
      .catch(function () { /* the bar still shows, just without a projection */ });
  }

  function hookPlaybackOverlay() {
    if (!document.__federationLoadingBound) {
      document.__federationLoadingBound = true;
      // `playing` does not bubble, so listen in the capture phase.
      document.addEventListener('playing', function (event) {
        if (event.target && event.target.tagName === 'VIDEO') {
          stopLoadingOverlay();
        }
      }, true);
      document.addEventListener('error', function (event) {
        if (event.target && event.target.tagName === 'VIDEO') {
          stopLoadingOverlay();
        }
      }, true);
    }

    if (!window.ApiClient || window.ApiClient.__federationLoadingHook) {
      return;
    }

    window.ApiClient.__federationLoadingHook = true;
    if (typeof window.ApiClient.getPlaybackInfo === 'function') {
      var origGetPlaybackInfo = window.ApiClient.getPlaybackInfo.bind(window.ApiClient);
      window.ApiClient.getPlaybackInfo = function (itemId) {
        try { startLoadingOverlay(itemId); } catch (e) { /* never get in the way of playback */ }
        return origGetPlaybackInfo.apply(window.ApiClient, arguments);
      };
    }
  }

  // Before anything loads: a library page opened right away otherwise fetched
  // its first page unfiltered, so the selected tab only sometimes applied.
  hookOriginQueries();

  Promise.all([refreshFederatedIds(), refreshClientSettings(), resolveAdminState()]).then(function () {
    if (currentUserIsAdmin) {
      refreshSharingDisabledIds();
      resumeActiveDownloads();
      refreshDownloadsList();
    }

    scheduleScan();
  });
  setInterval(refreshFederatedIds, 30 * 1000);
  setInterval(refreshClientSettings, 5 * 60 * 1000);
  setInterval(refreshSharingDisabledIds, 5 * 60 * 1000);

  setInterval(refreshDownloadsList, 3000);

  // jellyfin-web is a client-routed SPA (pushState navigation), which does
  // not reliably fire DOM mutations synchronously with a route change - this
  // catches detail-page navigation the MutationObserver below might miss.
  setInterval(scheduleScan, 1500);

  var observer = new MutationObserver(scheduleScan);
  observer.observe(document.body, { childList: true, subtree: true });

})();
