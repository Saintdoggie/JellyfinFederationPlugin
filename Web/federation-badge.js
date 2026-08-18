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
      // Injected item in jellyfin-web's own "more actions" (⋮) menu, rather
      // than a standalone pill fighting the title for space. Cloned from a
      // real menu item so its base look already matches; this just adds a
      // state color once busy/done/error, same states as the old pill.
      '.federation-menu-download[data-state="busy"]{opacity:.7;pointer-events:none;}',
      '.federation-menu-download[data-state="done"]{color:#7fd996 !important;}',
      '.federation-menu-download[data-state="error"]{color:#f08a8a !important;}',
      // Theme-adaptive surface tokens, used only by the elements below that
      // float over ordinary page chrome (the toast and the origin filter).
      // The corner badge and title/hide pills above are deliberately NOT
      // themed - they overlay posters/backdrop images, not page background,
      // so there is no "page theme" to match in the first place; a solid
      // dark chip is the same reasoning Jellyfin's own played-checkmark and
      // unwatched-count badges use, and stays legible over any poster
      // regardless of which theme is active. These two, by contrast, sit
      // over the theme's own surface, so they pull the theme's own color
      // variables (falling back to the same dark palette if a theme
      // doesn't define them, so a bare/incomplete theme never breaks this).
      ':root{--fed-surface:var(--theme-body-background-color, #161a22);',
      '--fed-text:var(--theme-body-text-color, var(--primary-text-color, #e4ebf5));',
      '--fed-accent:var(--theme-primary-color, var(--primary-accent-color, #3c6bb3));}',
      // Small toast for download progress once the menu (which triggered it)
      // has already closed - same fixed-position-overlay approach as the
      // origin filter control below, for the same reason: no jellyfin-web
      // markup to reliably dock into.
      '.federation-toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);z-index:9999;',
      'padding:.6em 1.1em;border-radius:.5em;background:var(--fed-surface);border:1px solid var(--fed-accent);color:var(--fed-text);',
      'font-size:13px;font-weight:600;box-shadow:0 4px 18px rgba(0,0,0,.5);display:flex;align-items:center;gap:.5em;}',
      '.federation-toast[data-state="error"]{border-color:#a34a4a;color:#f5cccc;}',
      '.federation-toast[data-state="done"]{border-color:#3c8a55;color:#c9f0d3;}',
      // Origin filter: a small floating control (see "Origin filter" section
      // below for why this is standalone rather than docked into jellyfin-web's
      // own filter dialog). Fixed bottom-right so it never depends on knowing
      // any of jellyfin-web's internal toolbar markup, which differs across
      // pages (grid, search results) and versions.
      '.federation-filter-fab{position:fixed;right:20px;bottom:20px;z-index:9999;',
      'display:flex;align-items:center;gap:.4em;padding:.55em .9em;border-radius:2em;cursor:pointer;',
      'background:var(--fed-surface);color:var(--fed-accent);border:1px solid var(--fed-accent);box-shadow:0 2px 10px rgba(0,0,0,.4);',
      'font-size:13px;font-weight:600;user-select:none;}',
      '.federation-filter-fab:hover{filter:brightness(1.25);}',
      '.federation-filter-fab svg{width:16px;height:16px;flex-shrink:0;}',
      '.federation-filter-fab[data-active="1"]{background:#2a5a3a;border-color:#4a9a63;color:#d3f5dc;}',
      '.federation-filter-fab.federation-filter-hidden-fab{display:none;}',
      '.federation-filter-panel{position:fixed;right:20px;bottom:70px;z-index:9999;min-width:220px;',
      'max-width:280px;max-height:60vh;overflow-y:auto;padding:.6em;border-radius:.6em;',
      'background:var(--fed-surface);border:1px solid var(--fed-accent);box-shadow:0 4px 18px rgba(0,0,0,.5);',
      'color:var(--fed-text);font-size:13px;}',
      '.federation-filter-panel.federation-filter-hidden-panel{display:none;}',
      '.federation-filter-panel-title{font-weight:700;text-transform:uppercase;font-size:11px;',
      'letter-spacing:.04em;color:var(--fed-accent);margin:.1em .3em .5em;}',
      '.federation-filter-row{display:flex;align-items:center;gap:.5em;padding:.3em .3em;border-radius:.35em;}',
      '.federation-filter-row:hover{background:rgba(255,255,255,.06);}',
      '.federation-filter-row input{margin:0;flex-shrink:0;}',
      '.federation-filter-row label{flex:1;cursor:pointer;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}',
      '.federation-filter-empty{padding:.4em .3em;opacity:.7;font-style:italic;}',
      '.federation-filter-hidden{display:none !important;}'
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
        applyFilters();
        if (filterPanelOpen) {
          renderFilterPanel();
        }
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

  // Single toast element reused across a download's lifetime - the menu item
  // that triggered it closes almost immediately (see closeActionMenu), so
  // this is the only surface left to show progress on.
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

  function pollDownloadProgress(operationId) {
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
          showToast('Downloading ' + (data.itemName ? data.itemName + ' - ' : '') + Math.round(data.percentComplete || 0) + '%', 'busy');
          setTimeout(function () { pollDownloadProgress(operationId); }, 1500);
          return;
        }

        if (data.success) {
          showToast('Downloaded ' + (data.itemName || 'item') + ' to this server', 'done');
        } else {
          showToast(data.status || 'Download failed', 'error');
        }

        hideToastAfter(6000);
      })
      .catch(function () {
        setTimeout(function () { pollDownloadProgress(operationId); }, 3000);
      });
  }

  function startDownload(itemId) {
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
          showToast((result.data && result.data.message) || 'Could not start download', 'error');
          hideToastAfter(6000);
          return;
        }

        pollDownloadProgress(result.data.operationId);
      })
      .catch(function () {
        showToast('Could not start download', 'error');
        hideToastAfter(6000);
      });
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
    // Only the "streamed from" fact and the (destructive, local-only) Hide
    // action live next to the title - Download moved into the native "more
    // actions" (⋮) menu (see injectDownloadMenuItem below) since a big pill
    // fighting a stylized show-logo title for space read as broken, not as
    // an action bar.
    var pill = '<span class="federation-badge-pill" title="' + label.replace(/"/g, '&quot;') + '">'
      + INLINE_ICON_HTML + '<span>' + (srv || 'Another server') + '</span></span>'
      + '<span class="federation-badge-hide" data-state="idle" title="Hide this item from your local library (does not affect the friend sharing it)">'
      + '<span class="federation-badge-icon">' + HIDE_ICON_SVG + '</span><span class="federation-badge-label">Hide</span></span>';

    var selectors = ['.nameContainer bdi', '.itemName-primary bdi', '.detailPagePrimaryContainer h1 bdi', 'h1 bdi'];
    for (var i = 0; i < selectors.length; i++) {
      var title = document.querySelector(selectors[i]);
      if (title && !title.querySelector('.federation-badge-pill')) {
        title.insertAdjacentHTML('afterbegin', pill);
        var hideBtn = title.querySelector('.federation-badge-hide');
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
  }

  // -------------------------------------------------------------------------
  // Download-to-server menu item
  //
  // Injected into jellyfin-web's own "more actions" (⋮) menu on the detail
  // page instead of a standalone pill, so it reads as one more action among
  // Edit metadata/Refresh metadata/etc. rather than fighting the title for
  // space. There is no jellyfin-web checkout to verify exact markup against
  // (see the same caveat on the origin filter below), so rather than
  // guessing class names, this clones a real menu item already in the open
  // menu - its look (icon layout, hover/focus states, spacing) is copied
  // exactly because it IS the real thing, just with the icon and label
  // swapped, so it inherits whatever theme is active for free.
  function findActionMenu() {
    var candidates = document.querySelectorAll('[role="menu"], .actionsheet-not-fullscreen, .actionSheetScroller, .actionsheetScroller');
    for (var i = 0; i < candidates.length; i++) {
      // "Edit metadata"/"Refresh metadata" only appear in the detail page's
      // own item context menu - this is what tells an ordinary "⋮" popup on
      // some other page apart from the one we want to inject into.
      if (/edit metadata|refresh metadata/i.test(candidates[i].textContent || '')) {
        return candidates[i];
      }
    }

    return null;
  }

  // Finds the element inside a cloned menu item that actually carries the
  // visible label text, whatever tag jellyfin-web/MUI happens to use for it
  // (span, p, div...) - the element with the most direct text of any
  // descendant, found structurally rather than by a guessed class name.
  function findDeepestTextElement(root) {
    var best = null;
    var bestLen = 0;
    var all = root.querySelectorAll('*');
    for (var i = 0; i < all.length; i++) {
      var node = all[i];
      var text = '';
      for (var j = 0; j < node.childNodes.length; j++) {
        if (node.childNodes[j].nodeType === 3) {
          text += node.childNodes[j].textContent;
        }
      }

      text = text.trim();
      if (text && text.length > bestLen) {
        best = node;
        bestLen = text.length;
      }
    }

    return best;
  }

  function closeActionMenu() {
    // Escape closes both jellyfin-web's legacy actionsheet and a MUI Menu -
    // simpler and more robust than hunting for a specific close button/
    // backdrop element to click.
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }));
  }

  function injectDownloadMenuItem() {
    var menu = findActionMenu();
    if (!menu || menu.querySelector('.federation-menu-download')) {
      return;
    }

    var match = location.href.match(/[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}/i);
    if (!match || !federatedIds.has(normalizeId(match[0]))) {
      return;
    }

    var rawId = match[0];
    var items = menu.querySelectorAll('[role="menuitem"], li, button');
    var template = null;
    for (var i = 0; i < items.length; i++) {
      if (items[i].textContent && items[i].textContent.trim()) {
        template = items[i];
        break;
      }
    }

    if (!template) {
      return;
    }

    try {
      var clone = template.cloneNode(true);
      clone.classList.add('federation-menu-download');
      clone.removeAttribute('id');
      clone.setAttribute('data-state', 'idle');

      var iconEl = clone.querySelector('svg');
      if (iconEl) {
        iconEl.outerHTML = DOWNLOAD_ICON_SVG;
      }

      var textEl = findDeepestTextElement(clone);
      if (textEl) {
        textEl.textContent = 'Download to server';
        textEl.classList.add('federation-badge-label');
      }

      clone.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        if (clone.getAttribute('data-state') !== 'idle') {
          return;
        }

        clone.setAttribute('data-state', 'busy');
        closeActionMenu();
        startDownload(rawId);
      }, true);

      menu.appendChild(clone);
    } catch (e) {
      // Best-effort: a jellyfin-web markup change that breaks this should
      // never take the rest of the badge script down with it.
    }
  }

  // -------------------------------------------------------------------------
  // Origin filter
  //
  // Reuses the same federatedIds map the badge already fetches - no new
  // endpoint is needed, since every server name a user could filter on is
  // already present as a value in that map. An item id absent from the map is
  // local content, represented below by LOCAL_ORIGIN_KEY.
  //
  // jellyfin-web ships its own filter dialog/sort menu for library views, but
  // this plugin has no jellyfin-web checkout to verify current markup
  // against, and that markup differs across pages (grid vs. search results)
  // and has changed across jellyfin-web releases. Guessing at internal class
  // names here risks silently failing (control never appears) or, worse,
  // inserting into the wrong place on some version. So this is a standalone
  // control instead: a small fixed-position toggle, always in the same spot,
  // that works regardless of which page or theme is active - the same
  // reasoning that makes the corner/pill badges DOM overlays rather than a
  // jellyfin "tag".
  var FILTER_STORAGE_KEY = 'federationOriginFilter.hidden';
  var LOCAL_ORIGIN_KEY = '\u0000federation-local';
  var UNKNOWN_ORIGIN_KEY = '\u0000federation-unknown';
  var LOCAL_ORIGIN_LABEL = 'This server (local)';
  var UNKNOWN_ORIGIN_LABEL = 'Unknown server';

  var FILTER_ICON_SVG =
    '<svg viewBox="0 0 24 24" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">' +
    '<path d="M4 5h16"></path><path d="M7 12h10"></path><path d="M10.5 19h3"></path>' +
    '</svg>';

  function loadHiddenOrigins() {
    try {
      var raw = JSON.parse(localStorage.getItem(FILTER_STORAGE_KEY) || '[]');
      if (Array.isArray(raw)) {
        return new Set(raw);
      }
    } catch (e) { /* corrupt or missing - start fresh */ }

    return new Set();
  }

  // Persisted the same way the rest of this file keeps client state - plain
  // localStorage, no server round-trip, so the choice survives navigation and
  // page reloads without needing any backend support.
  var hiddenOrigins = loadHiddenOrigins();

  function saveHiddenOrigins() {
    try {
      localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(Array.from(hiddenOrigins)));
    } catch (e) { /* localStorage unavailable (private mode, quota) - filter still works this session */ }
  }

  function originKeyForId(id) {
    if (!federatedIds.has(id)) {
      return LOCAL_ORIGIN_KEY;
    }

    var srv = federatedIds.get(id);
    return srv ? srv : UNKNOWN_ORIGIN_KEY;
  }

  function originLabel(key) {
    if (key === LOCAL_ORIGIN_KEY) {
      return LOCAL_ORIGIN_LABEL;
    }

    if (key === UNKNOWN_ORIGIN_KEY) {
      return UNKNOWN_ORIGIN_LABEL;
    }

    return key;
  }

  // Every origin currently known: local plus every distinct server name seen
  // in federatedIds. Recomputed each time the panel is (re)opened/redrawn so
  // a friend added after the page loaded still shows up.
  function knownOrigins() {
    var seen = new Set();
    federatedIds.forEach(function (srv) {
      seen.add(srv ? srv : UNKNOWN_ORIGIN_KEY);
    });

    var origins = [LOCAL_ORIGIN_KEY].concat(Array.from(seen).sort(function (a, b) {
      return originLabel(a).localeCompare(originLabel(b));
    }));

    return origins;
  }

  var FILTER_CARD_SELECTOR = '.card[data-id],.listItem[data-id]';

  function applyFilterToCard(el) {
    var id = normalizeId(el.getAttribute('data-id'));
    if (!id) {
      return;
    }

    var hide = hiddenOrigins.size > 0 && hiddenOrigins.has(originKeyForId(id));
    el.classList.toggle('federation-filter-hidden', hide);
  }

  function applyFilters() {
    document.querySelectorAll(FILTER_CARD_SELECTOR).forEach(applyFilterToCard);
  }

  var filterFab = null;
  var filterPanel = null;
  var filterPanelOpen = false;

  function updateFabState() {
    if (!filterFab) {
      return;
    }

    filterFab.setAttribute('data-active', hiddenOrigins.size > 0 ? '1' : '0');
  }

  function renderFilterPanel() {
    if (!filterPanel) {
      return;
    }

    var origins = knownOrigins();
    if (origins.length <= 1) {
      // Only "local" known - nothing federated has loaded yet (or this
      // server has no friends at all). Say so rather than showing a
      // one-item list that can't actually filter anything.
      filterPanel.innerHTML = '<div class="federation-filter-panel-title">Filter by origin</div>'
        + '<div class="federation-filter-empty">No federated content found yet.</div>';
      return;
    }

    var html = '<div class="federation-filter-panel-title">Filter by origin</div>';
    origins.forEach(function (key, i) {
      var checked = hiddenOrigins.has(key) ? '' : ' checked';
      var inputId = 'federation-filter-opt-' + i;
      html += '<div class="federation-filter-row">'
        + '<input type="checkbox" id="' + inputId + '" data-origin-key="' + i + '"' + checked + '>'
        + '<label for="' + inputId + '">' + originLabel(key).replace(/</g, '&lt;') + '</label>'
        + '</div>';
    });

    filterPanel.innerHTML = html;
    filterPanel.querySelectorAll('input[type="checkbox"]').forEach(function (input, i) {
      input.addEventListener('change', function () {
        var key = origins[i];
        if (input.checked) {
          hiddenOrigins.delete(key);
        } else {
          hiddenOrigins.add(key);
        }

        saveHiddenOrigins();
        updateFabState();
        applyFilters();
      });
    });
  }

  function toggleFilterPanel() {
    filterPanelOpen = !filterPanelOpen;
    if (filterPanelOpen) {
      renderFilterPanel();
    }

    filterPanel.classList.toggle('federation-filter-hidden-panel', !filterPanelOpen);
  }

  function ensureFilterControl() {
    if (filterFab) {
      return;
    }

    filterFab = document.createElement('div');
    filterFab.className = 'federation-filter-fab federation-filter-hidden-fab';
    filterFab.setAttribute('data-active', '0');
    filterFab.title = 'Filter library by origin server';
    filterFab.innerHTML = FILTER_ICON_SVG + '<span>Origin filter</span>';
    filterFab.addEventListener('click', toggleFilterPanel);

    filterPanel = document.createElement('div');
    filterPanel.className = 'federation-filter-panel federation-filter-hidden-panel';

    document.body.appendChild(filterFab);
    document.body.appendChild(filterPanel);
    updateFabState();
  }

  // The control is only useful, and only shown, on a page currently
  // displaying a card grid (library view, search results, home screen rows) -
  // no point floating a filter button over the playback or settings pages.
  function updateFilterControlVisibility() {
    if (!filterFab) {
      return;
    }

    var hasCards = !!document.querySelector(FILTER_CARD_SELECTOR);
    filterFab.classList.toggle('federation-filter-hidden-fab', !hasCards);
    if (!hasCards && filterPanelOpen) {
      filterPanelOpen = false;
      filterPanel.classList.add('federation-filter-hidden-panel');
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
    injectDownloadMenuItem();
    ensureFilterControl();
    applyFilters();
    updateFilterControlVisibility();
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
