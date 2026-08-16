(function () {
  'use strict';

  function q(sel) { return document.querySelector(sel); }
  function qa(sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); }
  function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  // Picks up the browser's existing Jellyfin login (this page is served from
  // the same origin as jellyfin-web itself, see FederationAppController) -
  // same pattern the old in-Jellyfin config page used, so there's nothing
  // separate to log into here.
  function getToken() {
    try {
      if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
        var t = window.ApiClient.accessToken();
        if (t) { return t; }
      }
    } catch (e) { /* fall through to credentials store */ }

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

  function api(path, options) {
    options = options || {};
    var opts = { method: options.method || 'GET', headers: {} };
    if (options.body !== undefined) {
      opts.headers['Content-Type'] = 'application/json';
      opts.body = JSON.stringify(options.body);
    }

    var token = getToken();
    if (token) { opts.headers['X-Emby-Token'] = token; }

    return fetch('/Plugins/Federation/App/api' + path, opts).then(function (res) {
      if (res.status === 401 || res.status === 403) {
        throw new Error('Not authorized. Open this page while logged into Jellyfin as an administrator.');
      }

      if (!res.ok) {
        return res.json().catch(function () { return {}; }).then(function (data) {
          throw new Error(data.error || ('HTTP ' + res.status));
        });
      }

      return res.json();
    });
  }

  function setStatus(el, msg, kind) {
    el.textContent = msg;
    el.className = 'fed-status ' + (kind || 'info');
    el.style.display = msg ? 'block' : 'none';
  }

  // ---------------- Navigation ----------------

  function showPanel(name) {
    qa('.fed-panel').forEach(function (p) { p.classList.toggle('active', p.id === 'panel-' + name); });
    qa('.fed-sidenav button').forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-panel') === name); });
    loaders[name] && loaders[name]();
  }

  qa('.fed-sidenav button').forEach(function (btn) {
    btn.addEventListener('click', function () { showPanel(btn.getAttribute('data-panel')); });
  });

  // ---------------- Home ----------------

  function loadHome() {
    api('/dashboard').then(function (d) {
      q('#statServers').textContent = d.serverCount;
      q('#statDisabled').textContent = d.disabledServerCount;
      q('#statMappings').textContent = d.mappingCount;
      q('#statSessions').textContent = d.activeSessions;

      if (d.lastSync) {
        q('#homeSyncSummary').textContent = (d.lastSync.success ? 'Last sync succeeded' : 'Last sync had issues') +
          ' - ' + new Date(d.lastSync.finishedUtc).toLocaleString();
        q('#homeSyncDetail').textContent = d.lastSync.message || (d.lastSync.itemCount + ' item(s) refreshed');
      }

      var budgetCard = q('#uploadBudgetCard');
      if (d.uploadBudget && d.uploadBudget.autoManageUploadBudget) {
        budgetCard.style.display = '';
        q('#homeUploadBudgetDetail').textContent =
          d.uploadBudget.projectedPerStreamMbps + ' Mbps per stream, ' + d.activeSessions +
          ' active - capacity ' + d.uploadBudget.localUploadCapacityMbps + ' Mbps.';
      } else {
        budgetCard.style.display = 'none';
      }
    }).catch(function () { /* leave dashes on failure */ });
  }

  // ---------------- Library ----------------

  function renderLibraryPosters(container, mappingId) {
    container.innerHTML = '<p class="fed-muted">Loading…</p>';
    api('/library/' + encodeURIComponent(mappingId) + '/items?take=30').then(function (r) {
      var items = r.items || [];
      if (!items.length) { container.innerHTML = '<p class="fed-muted">No items yet - run a sync.</p>'; return; }
      container.innerHTML = '<div class="fed-poster-grid">' + items.map(function (i) {
        var img = i.hasImage
          ? '<img src="/Items/' + encodeURIComponent(i.id) + '/Images/Primary?maxWidth=200" alt="" />'
          : '<div class="fed-poster-fallback">' + escapeHtml(i.name) + '</div>';
        return '<a class="fed-poster" href="/web/index.html#!/details?id=' + encodeURIComponent(i.id) + '" title="' + escapeHtml(i.name) + '">' + img + '</a>';
      }).join('') + '</div>';
    }).catch(function () { container.innerHTML = '<p class="fed-status err">Could not load items.</p>'; });
  }

  function renderLibrary(libraries) {
    var list = q('#libraryList');
    if (!libraries.length) { list.innerHTML = '<p class="fed-muted">No libraries yet - add one under "Mappings".</p>'; return; }

    list.innerHTML = libraries.map(function (lib) {
      var sources = (lib.sources || []).map(function (s) { return escapeHtml(s.serverName); }).join(', ');
      var openLink = lib.folderId
        ? '<a class="fed-link" href="/web/index.html#!/videos?topParentId=' + encodeURIComponent(lib.folderId) + '" target="_blank" rel="noopener">Open in Jellyfin</a>'
        : '';
      return '<div class="fed-card" style="margin-bottom:10px;">' +
        '<div class="fed-row"><div class="fed-row-main">' +
          '<div class="fed-row-title">' + escapeHtml(lib.localLibraryName) + ' <span class="fed-badge ' + (lib.enabled ? 'on' : 'off') + '">' + (lib.enabled ? 'enabled' : 'disabled') + '</span></div>' +
          '<div class="fed-row-sub">' + lib.itemCount + ' item(s) &middot; ' + escapeHtml(lib.mediaType) + (sources ? ' &middot; from ' + sources : '') + '</div>' +
        '</div>' +
        '<div class="fed-inline" style="flex:0 0 auto;">' + openLink + ' <button class="fed-btn fed-flat" data-library-expand="' + escapeHtml(lib.id) + '" style="margin-left:8px;">Preview</button></div>' +
        '</div>' +
        '<div id="library-posters-' + escapeHtml(lib.id) + '"></div>' +
      '</div>';
    }).join('');

    qa('[data-library-expand]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var id = btn.getAttribute('data-library-expand');
        renderLibraryPosters(q('#library-posters-' + id), id);
      });
    });
  }

  function loadLibrary() {
    return api('/library').then(renderLibrary);
  }

  // ---------------- Profile & settings ----------------

  function loadProfile() {
    api('/status').then(function (status) {
      q('#profileUsername').value = status.username || '';
      q('#profileAvatarPreview').src = status.hasAvatar ? ('/Plugins/Federation/Avatar?_=' + Date.now()) : '';
    });
  }

  function loadSettings() {
    loadProfile();
    api('/settings').then(function (s) {
      q('#settingsServerUrl').value = s.serverUrl || '';
      q('#settingsRefreshHours').value = s.refreshIntervalHours;
      q('#settingsAutoProvision').checked = !!s.autoProvisionLibraries;
      q('#settingsEnableDedup').checked = !!s.enableDedup;
      q('#settingsFriendsOfFriends').checked = !!s.allowFriendsOfFriends;
      q('#settingsHostDirectory').checked = !!s.hostDirectory;
      q('#settingsUploadCapacity').value = s.localUploadCapacityMbps || 0;
      q('#settingsAutoManageBudget').checked = !!s.autoManageUploadBudget;
      updateBudgetPreview();
    });
  }

  function updateBudgetPreview() {
    var capacity = parseInt(q('#settingsUploadCapacity').value, 10) || 0;
    var preview = q('#settingsBudgetPreview');
    if (!capacity) { preview.textContent = ''; return; }
    api('/dashboard').then(function (d) {
      preview.textContent = 'With ' + d.activeSessions + ' active stream(s) right now, each would get roughly ' +
        Math.round(capacity * 0.85 / Math.max(1, d.activeSessions)) + ' Mbps.';
    }).catch(function () { preview.textContent = ''; });
  }

  q('#settingsUploadCapacity').addEventListener('input', updateBudgetPreview);

  q('[data-fed-action="save-settings"]').addEventListener('click', function () {
    var status = q('#settingsStatus');
    api('/settings', {
      method: 'POST',
      body: {
        serverUrl: q('#settingsServerUrl').value.trim(),
        refreshIntervalHours: parseInt(q('#settingsRefreshHours').value, 10) || 1,
        autoProvisionLibraries: q('#settingsAutoProvision').checked,
        enableDedup: q('#settingsEnableDedup').checked,
        allowFriendsOfFriends: q('#settingsFriendsOfFriends').checked,
        hostDirectory: q('#settingsHostDirectory').checked,
        localUploadCapacityMbps: parseInt(q('#settingsUploadCapacity').value, 10) || 0,
        autoManageUploadBudget: q('#settingsAutoManageBudget').checked
      }
    }).then(function (r) {
      setStatus(status, r.success ? 'Saved.' : (r.error || 'Failed.'), r.success ? 'ok' : 'err');
    }).catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  q('[data-fed-action="save-profile"]').addEventListener('click', function () {
    var status = q('#profileStatus');
    api('/profile', { method: 'POST', body: { username: q('#profileUsername').value.trim() } })
      .then(function (r) { setStatus(status, r.success ? 'Saved.' : (r.message || 'Failed.'), r.success ? 'ok' : 'err'); })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  q('#profileAvatarFile').addEventListener('change', function () {
    var file = this.files && this.files[0];
    if (!file) { return; }

    var status = q('#profileStatus');
    var headers = { 'Content-Type': file.type };
    var token = getToken();
    if (token) { headers['X-Emby-Token'] = token; }
    fetch('/Plugins/Federation/App/api/profile/avatar', { method: 'POST', headers: headers, body: file })
      .then(function (res) { return res.json(); })
      .then(function (r) {
        setStatus(status, r.success ? 'Avatar saved.' : (r.error || 'Failed.'), r.success ? 'ok' : 'err');
        if (r.success) { q('#profileAvatarPreview').src = '/Plugins/Federation/Avatar?_=' + Date.now(); }
      })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  // ---------------- Servers ----------------

  function serverEditFormHtml(s, localUsers) {
    var id = escapeHtml(s.id);
    var userOptions = '<option value="">Choose an account…</option>' + (localUsers || []).map(function (u) {
      return '<option value="' + escapeHtml(u.id) + '"' + (u.id === s.localShareUserId ? ' selected' : '') + '>' + escapeHtml(u.name) + '</option>';
    }).join('');
    return '<div class="fed-field"><label>Priority (lower = preferred when deduped)</label>' +
        '<input type="number" class="fed-input" data-edit-priority="' + id + '" value="' + s.priority + '" /></div>' +
      '<div class="fed-field"><label>Streaming mode</label>' +
        '<select class="fed-select" data-edit-mode="' + id + '">' +
          '<option value="Direct"' + (s.streamingMode === 'Direct' ? ' selected' : '') + '>Direct (client fetches from them)</option>' +
          '<option value="Proxy"' + (s.streamingMode === 'Proxy' ? ' selected' : '') + '>Proxy (routed through this server)</option>' +
        '</select></div>' +
      '<div class="fed-field"><label>WAN bitrate cap</label>' +
        '<select class="fed-select" data-edit-capmode="' + id + '">' +
          '<option value="Auto"' + (s.wanCapMode === 'Auto' ? ' selected' : '') + '>Auto (measure and cap only when needed)</option>' +
          '<option value="Manual"' + (s.wanCapMode === 'Manual' ? ' selected' : '') + '>Manual (fixed value)</option>' +
          '<option value="Off"' + (s.wanCapMode === 'Off' ? ' selected' : '') + '>Off (never cap)</option>' +
        '</select></div>' +
      '<div class="fed-field"><label>Manual bitrate cap (Mbps)</label>' +
        '<input type="number" class="fed-input" data-edit-bitrate="' + id + '" value="' + s.wanMaxBitrateMbps + '" /></div>' +
      '<div class="fed-field"><label>Max height when capped</label>' +
        '<input type="number" class="fed-input" data-edit-height="' + id + '" value="' + s.wanMaxHeight + '" /></div>' +
      '<label class="fed-toggle"><input type="checkbox" data-edit-enabled="' + id + '"' + (s.enabled ? ' checked' : '') + ' /> Enabled</label>' +
      '<label class="fed-toggle"><input type="checkbox" data-edit-shareall="' + id + '"' + (s.shareAllLibraries ? ' checked' : '') + ' /> Share all my libraries with this friend</label>' +
      '<div class="fed-field"><label>Restricted-view account (only used when "share all" is off - create a hidden account under Dashboard &rarr; Users first)</label>' +
        '<select class="fed-select" data-edit-shareuser="' + id + '">' + userOptions + '</select></div>' +
      '<button class="fed-btn" data-save-server="' + id + '">Save</button>' +
      '<div class="fed-status" data-edit-status="' + id + '" style="display:none;"></div>';
  }

  function renderServers(servers, localUsers) {
    var list = q('#serverList');
    if (!servers.length) { list.innerHTML = '<p class="fed-muted">No servers yet.</p>'; return; }

    list.innerHTML = servers.map(function (s) {
      var id = escapeHtml(s.id);
      return '<div class="fed-card" style="margin-bottom:10px;">' +
        '<div class="fed-row"><div class="fed-row-main">' +
          '<div class="fed-row-title">' + escapeHtml(s.name) + '</div>' +
          '<div class="fed-row-sub">' + escapeHtml(s.url) + ' &middot; <span class="fed-badge ' + (s.enabled ? 'on' : 'off') + '">' + (s.enabled ? 'enabled' : 'disabled') + '</span> &middot; ' + escapeHtml(s.streamingMode) + '</div>' +
        '</div>' +
        '<div class="fed-inline" style="flex:0 0 auto;">' +
          '<button class="fed-btn fed-flat" data-edit-server="' + id + '">Edit</button>' +
          '<button class="fed-btn fed-danger" data-remove-server="' + id + '">Remove</button>' +
        '</div></div>' +
        '<div class="fed-edit-form" data-edit-form="' + id + '">' + serverEditFormHtml(s, localUsers) + '</div>' +
      '</div>';
    }).join('');

    qa('[data-remove-server]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        if (!confirm('Remove this server? Its federated content will be removed too.')) { return; }
        api('/servers/' + encodeURIComponent(btn.getAttribute('data-remove-server')), { method: 'DELETE' })
          .then(loadServers);
      });
    });

    qa('[data-edit-server]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var id = btn.getAttribute('data-edit-server');
        q('[data-edit-form="' + id + '"]').classList.toggle('open');
      });
    });

    qa('[data-save-server]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var id = btn.getAttribute('data-save-server');
        var status = q('[data-edit-status="' + id + '"]');
        api('/servers/' + encodeURIComponent(id), {
          method: 'PUT',
          body: {
            enabled: q('[data-edit-enabled="' + id + '"]').checked,
            streamingMode: q('[data-edit-mode="' + id + '"]').value,
            priority: parseInt(q('[data-edit-priority="' + id + '"]').value, 10) || 0,
            wanCapMode: q('[data-edit-capmode="' + id + '"]').value,
            wanMaxBitrateMbps: parseInt(q('[data-edit-bitrate="' + id + '"]').value, 10) || 0,
            wanMaxHeight: parseInt(q('[data-edit-height="' + id + '"]').value, 10) || 0,
            shareAllLibraries: q('[data-edit-shareall="' + id + '"]').checked,
            localShareUserId: q('[data-edit-shareuser="' + id + '"]').value.trim()
          }
        }).then(function (r) {
          setStatus(status, r.message || (r.success ? 'Saved.' : 'Failed.'), r.success ? 'ok' : 'err');
          if (r.success) { loadServers(); }
        }).catch(function (e) { setStatus(status, e.message, 'err'); });
      });
    });
  }

  var localUsersCache = null;

  function loadServers() {
    return Promise.all([
      api('/servers'),
      localUsersCache ? Promise.resolve(localUsersCache) : api('/local-users').then(function (u) { localUsersCache = u; return u; }).catch(function () { return []; })
    ]).then(function (results) { renderServers(results[0], results[1]); });
  }

  q('[data-fed-action="add-server"]').addEventListener('click', function () {
    var status = q('#serverAddStatus');
    api('/servers', {
      method: 'POST',
      body: {
        name: q('#serverName').value.trim() || q('#serverUrl').value.trim(),
        url: q('#serverUrl').value.trim(),
        apiKey: q('#serverApiKey').value.trim()
      }
    }).then(function (r) {
      setStatus(status, r.success ? 'Server added.' : (r.error || 'Failed.'), r.success ? 'ok' : 'err');
      if (r.success) {
        q('#serverName').value = ''; q('#serverUrl').value = ''; q('#serverApiKey').value = '';
        loadServers();
      }
    }).catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  // ---------------- Friends ----------------

  function renderFriendList(el, items, isIncoming) {
    if (!items.length) { el.innerHTML = '<p class="fed-muted">None.</p>'; return; }

    el.innerHTML = items.map(function (r) {
      var actions = isIncoming
        ? '<button class="fed-btn" data-accept="' + escapeHtml(r.id) + '">Accept</button> ' +
          '<button class="fed-btn fed-danger" data-reject="' + escapeHtml(r.id) + '">Reject</button>'
        : '<button class="fed-btn fed-danger" data-cancel="' + escapeHtml(r.id) + '">Cancel</button>';
      return '<div class="fed-row">' +
        '<div class="fed-row-main">' +
          '<div class="fed-row-title">' + escapeHtml(r.remoteServerName || r.remoteServerUrl) + '</div>' +
          '<div class="fed-row-sub">' + escapeHtml(r.remoteServerUrl) + '</div>' +
        '</div>' +
        actions +
      '</div>';
    }).join('');

    qa('[data-accept]').forEach(function (b) { b.addEventListener('click', function () { api('/friends/' + encodeURIComponent(b.getAttribute('data-accept')) + '/accept', { method: 'POST', body: {} }).then(loadFriends); }); });
    qa('[data-reject]').forEach(function (b) { b.addEventListener('click', function () { api('/friends/' + encodeURIComponent(b.getAttribute('data-reject')) + '/reject', { method: 'POST', body: {} }).then(loadFriends); }); });
    qa('[data-cancel]').forEach(function (b) { b.addEventListener('click', function () { api('/friends/outgoing/' + encodeURIComponent(b.getAttribute('data-cancel')), { method: 'DELETE' }).then(loadFriends); }); });
  }

  function loadFriends() {
    return api('/friends').then(function (data) {
      renderFriendList(q('#friendIncoming'), data.incoming || [], true);
      renderFriendList(q('#friendOutgoing'), data.outgoing || [], false);
    });
  }

  q('[data-fed-action="send-friend-request"]').addEventListener('click', function () {
    var status = q('#friendSendStatus');
    api('/friends/send', { method: 'POST', body: { url: q('#friendUrl').value.trim() } })
      .then(function (r) {
        setStatus(status, r.message, r.success ? 'ok' : 'err');
        if (r.success) { q('#friendUrl').value = ''; loadFriends(); }
      })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  // ---------------- Pools ----------------

  function renderPools(pools) {
    var list = q('#poolList');
    if (!pools.length) { list.innerHTML = '<p class="fed-muted">No pools yet.</p>'; return; }

    list.innerHTML = pools.map(function (p) {
      var members = (p.members || []).map(function (m) { return escapeHtml(m.name || m.url); }).join(', ');
      return '<div class="fed-card" style="margin-bottom:10px;">' +
        '<div class="fed-row-title">' + escapeHtml(p.name) + ' <span class="fed-badge">' + (p.isOwner ? 'you own this' : 'owned by ' + escapeHtml(p.ownerName)) + '</span></div>' +
        '<div class="fed-row-sub" style="margin:6px 0 10px;">' + (members || 'No other members yet') + '</div>' +
        '<div class="fed-inline">' +
          '<input type="text" class="fed-input" placeholder="Invite a server address" data-pool-invite-input="' + escapeHtml(p.id) + '" />' +
          '<button class="fed-btn fed-flat" data-pool-invite="' + escapeHtml(p.id) + '" style="flex:0 0 auto;">Invite</button>' +
          '<button class="fed-btn fed-danger" data-pool-leave="' + escapeHtml(p.id) + '" style="flex:0 0 auto;">Leave</button>' +
        '</div>' +
      '</div>';
    }).join('');

    qa('[data-pool-invite]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var id = btn.getAttribute('data-pool-invite');
        var input = q('[data-pool-invite-input="' + id + '"]');
        api('/pools/' + encodeURIComponent(id) + '/invite', { method: 'POST', body: { url: input.value.trim() } })
          .then(function () { input.value = ''; loadPools(); });
      });
    });
    qa('[data-pool-leave]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        if (!confirm('Leave this pool?')) { return; }
        api('/pools/' + encodeURIComponent(btn.getAttribute('data-pool-leave')), { method: 'DELETE' }).then(loadPools);
      });
    });
  }

  function loadPools() {
    return api('/pools').then(renderPools);
  }

  q('[data-fed-action="create-pool"]').addEventListener('click', function () {
    var status = q('#poolCreateStatus');
    var name = q('#poolName').value.trim();
    if (!name) { setStatus(status, 'Enter a pool name.', 'err'); return; }
    api('/pools', { method: 'POST', body: { name: name } })
      .then(function (r) {
        setStatus(status, r.success ? 'Pool created.' : 'Failed.', r.success ? 'ok' : 'err');
        if (r.success) { q('#poolName').value = ''; loadPools(); }
      })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  // ---------------- Library mappings ----------------

  function loadMappingServers() {
    return api('/servers').then(function (servers) {
      var sel = q('#mappingServer');
      sel.innerHTML = servers.map(function (s) { return '<option value="' + escapeHtml(s.id) + '">' + escapeHtml(s.name) + '</option>'; }).join('');
      if (servers.length) { loadRemoteLibraries(servers[0].id); }
    });
  }

  function loadRemoteLibraries(serverId) {
    var sel = q('#mappingRemoteLibrary');
    sel.innerHTML = '<option>Loading…</option>';
    api('/remote-libraries/' + encodeURIComponent(serverId)).then(function (libs) {
      sel.innerHTML = libs.map(function (l) { return '<option value="' + escapeHtml(l.id) + '" data-name="' + escapeHtml(l.name) + '">' + escapeHtml(l.name) + '</option>'; }).join('') || '<option value="">No libraries found</option>';
    }).catch(function () { sel.innerHTML = '<option value="">Could not load libraries</option>'; });
  }

  q('#mappingServer').addEventListener('change', function () { loadRemoteLibraries(this.value); });

  function renderMappings(mappings) {
    var list = q('#mappingList');
    if (!mappings.length) { list.innerHTML = '<p class="fed-muted">No libraries yet.</p>'; return; }
    list.innerHTML = mappings.map(function (m) {
      var id = escapeHtml(m.id);
      return '<div class="fed-row"><div class="fed-row-main">' +
        '<div class="fed-row-title">' + escapeHtml(m.localLibraryName) + '</div>' +
        '<div class="fed-row-sub">' + escapeHtml(m.mediaType) + ' &middot; <span class="fed-badge ' + (m.enabled ? 'on' : 'off') + '">' + (m.enabled ? 'enabled' : 'disabled') + '</span></div>' +
      '</div>' +
      '<div class="fed-inline" style="flex:0 0 auto;">' +
        '<button class="fed-btn fed-flat" data-toggle-mapping="' + id + '" data-enabled="' + m.enabled + '">' + (m.enabled ? 'Disable' : 'Enable') + '</button>' +
        '<button class="fed-btn fed-danger" data-delete-mapping="' + id + '">Delete</button>' +
      '</div></div>';
    }).join('');

    qa('[data-toggle-mapping]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var id = btn.getAttribute('data-toggle-mapping');
        var nowEnabled = btn.getAttribute('data-enabled') !== 'true';
        api('/mappings/' + encodeURIComponent(id), { method: 'PUT', body: { enabled: nowEnabled, autoProvision: true } })
          .then(loadMappings);
      });
    });

    qa('[data-delete-mapping]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        if (!confirm('Delete this library mapping? Its virtual library will be removed.')) { return; }
        api('/mappings/' + encodeURIComponent(btn.getAttribute('data-delete-mapping')), { method: 'DELETE' })
          .then(loadMappings);
      });
    });
  }

  function loadMappings() {
    return api('/mappings').then(renderMappings);
  }

  q('[data-fed-action="add-mapping"]').addEventListener('click', function () {
    var status = q('#mappingAddStatus');
    var remoteSel = q('#mappingRemoteLibrary');
    var remoteOpt = remoteSel.options[remoteSel.selectedIndex];
    api('/mappings', {
      method: 'POST',
      body: {
        serverId: q('#mappingServer').value,
        localLibraryName: q('#mappingLocalName').value.trim(),
        mediaType: q('#mappingMediaType').value,
        remoteLibraryId: remoteSel.value,
        remoteLibraryName: remoteOpt ? remoteOpt.getAttribute('data-name') : ''
      }
    }).then(function (r) {
      setStatus(status, r.success ? 'Library added - it will populate on the next sync.' : (r.error || 'Failed.'), r.success ? 'ok' : 'err');
      if (r.success) { q('#mappingLocalName').value = ''; loadMappings(); }
    }).catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  // ---------------- Directory ----------------

  function loadDirectorySettings() {
    return api('/status').then(function (status) {
      q('#directoryUrl').value = status.directoryServerUrl || '';
    });
  }

  q('[data-fed-action="save-directory-url"]').addEventListener('click', function () {
    var status = q('#directorySettingsStatus');
    api('/directory/url', { method: 'POST', body: { url: q('#directoryUrl').value.trim() } })
      .then(function (r) { setStatus(status, r.success ? 'Saved.' : (r.error || 'Failed.'), r.success ? 'ok' : 'err'); })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  q('[data-fed-action="register-directory"]').addEventListener('click', function () {
    var status = q('#directorySettingsStatus');
    api('/directory/register', { method: 'POST', body: {} })
      .then(function (r) { setStatus(status, r.message, r.success ? 'ok' : 'err'); })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  q('[data-fed-action="search-directory"]').addEventListener('click', function () {
    var results = q('#directorySearchResults');
    var query = q('#directorySearch').value.trim();
    if (!query) { return; }
    api('/directory/search?username=' + encodeURIComponent(query)).then(function (r) {
      if (!r.success) { results.innerHTML = '<p class="fed-status err">' + escapeHtml(r.message) + '</p>'; return; }
      if (!r.results.length) { results.innerHTML = '<p class="fed-muted">No matches.</p>'; return; }
      results.innerHTML = r.results.map(function (res) {
        return '<div class="fed-row"><div class="fed-row-main">' +
          '<div class="fed-row-title">' + escapeHtml(res.username) + '</div>' +
          '<div class="fed-row-sub">' + escapeHtml(res.serverUrl) + '</div>' +
        '</div><button class="fed-btn" data-friend-request-url="' + escapeHtml(res.serverUrl) + '">Add friend</button></div>';
      }).join('');
      qa('[data-friend-request-url]').forEach(function (btn) {
        btn.addEventListener('click', function () {
          api('/friends/send', { method: 'POST', body: { url: btn.getAttribute('data-friend-request-url') } })
            .then(function (r) { alert(r.message); });
        });
      });
    });
  });

  q('[data-fed-action="create-invite"]').addEventListener('click', function () {
    var display = q('#inviteCodeDisplay');
    var status = q('#inviteStatus');
    api('/directory/invite/create', { method: 'POST', body: {} }).then(function (r) {
      if (r.success) { display.textContent = 'Code: ' + r.code + ' (valid 24 hours)'; }
      else { setStatus(status, r.message, 'err'); }
    }).catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  q('[data-fed-action="redeem-invite"]').addEventListener('click', function () {
    var status = q('#inviteStatus');
    var code = q('#redeemCode').value.trim();
    if (!code) { setStatus(status, 'Enter a code first.', 'err'); return; }
    api('/directory/invite/redeem', { method: 'POST', body: { code: code } })
      .then(function (r) { setStatus(status, r.message, r.success ? 'ok' : 'err'); if (r.success) { q('#redeemCode').value = ''; } })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  // ---------------- Sync ----------------

  q('[data-fed-action="sync-now"]').addEventListener('click', function () {
    var status = q('#homeSyncStatus');
    setStatus(status, 'Syncing…', 'info');
    api('/sync', { method: 'POST', body: {} })
      .then(function (r) { setStatus(status, r.message || (r.success ? 'Done.' : 'Failed.'), r.success ? 'ok' : 'err'); })
      .catch(function (e) { setStatus(status, e.message, 'err'); });
  });

  // ---------------- Boot ----------------

  var loaders = {
    home: loadHome,
    library: loadLibrary,
    servers: loadServers,
    friends: loadFriends,
    pools: loadPools,
    mappings: function () { loadMappingServers(); loadMappings(); },
    directory: loadDirectorySettings,
    settings: loadSettings
  };

  loadHome();
})();
