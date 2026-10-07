/* Jellyfin Federation adaptive web player preview. No upstream credentials or byte splicing. */
(function () {
  'use strict';
  if (window.FederationAdaptivePlayer) return;
  var TICKS = 10000000;
  function normalize(value) { return String(value || '').trim().toLowerCase(); }
  function field(object, name) { return object && (object[name] !== undefined ? object[name] : object[name[0].toLowerCase() + name.slice(1)]); }
  function matchTrack(current, alternatives, type, index) {
    if (type === 'Subtitle' && (index == null || index < 0)) return -1;
    var streams = field(current, 'Streams') || current.MediaStreams || [];
    var old = streams.find(function (s) { return field(s, 'Type') === type && field(s, 'Index') === index; });
    if (!old) return null;
    var language = normalize(field(old, 'Language'));
    var title = normalize(field(old, 'Title'));
    // Unknown tracks are not enough evidence that two copies contain the same language/commentary.
    if (!language && !title) return null;
    var matching = alternatives.filter(function (s) {
      return field(s, 'Type') === type && normalize(field(s, 'Language')) === language
        && normalize(field(s, 'Title')) === title && !!field(s, 'IsForced') === !!field(old, 'IsForced');
    });
    return matching.length === 1 ? field(matching[0], 'Index') : null;
  }
  function compatible(current, next, audio, subtitle) {
    var duration = field(current, 'RunTimeTicks');
    var otherDuration = field(next, 'RunTimeTicks');
    var title = normalize(field(current, 'TimelineName'));
    if (!title || title !== normalize(field(next, 'TimelineName')) || !(duration > 0) || !(otherDuration > 0)
      || Math.abs(duration - otherDuration) > TICKS / 4) return null;
    var streams = field(next, 'Streams') || [];
    var a = matchTrack(current, streams, 'Audio', audio);
    var s = matchTrack(current, streams, 'Subtitle', subtitle);
    return a == null || s == null ? null : { audio: a, subtitle: s };
  }
  function choose(state, sources, now, failed) {
    var current = sources.find(function (s) { return field(s, 'Id') === state.currentId; });
    var eligible = sources.filter(function (s) {
      return field(s, 'Id') !== state.currentId && (!state.rejected[field(s, 'Id')] || now >= state.rejected[field(s, 'Id')])
        && compatible(state.reference, s, state.audio, state.subtitle);
    }).sort(function (a, b) { return field(b, 'Score') - field(a, 'Score'); });
    var best = eligible[0];
    if (!best) { state.pendingId = null; return null; }
    if (failed) return best;
    if (!current || now - state.lastSwitch < 45000 || field(best, 'Score') < field(current, 'Score') + 6) {
      state.pendingId = null; return null;
    }
    if (state.pendingId !== field(best, 'Id')) { state.pendingId = field(best, 'Id'); state.pendingSince = now; }
    return now - state.pendingSince >= 20000 ? best : null;
  }
  function bufferedAhead(video, at) {
    for (var i = 0; i < video.buffered.length; i++) {
      if (video.buffered.start(i) <= at + 0.05 && video.buffered.end(i) > at) return video.buffered.end(i) - at;
    }
    return 0;
  }
  var core = { compatible: compatible, choose: choose, bufferedAhead: bufferedAhead };
  window.__federationAdaptiveCore = core;
  var hlsLoading;
  function loadHls(api) {
    if (window.FederationHls) return Promise.resolve(window.FederationHls);
    if (!hlsLoading) hlsLoading = new Promise(function (resolve, reject) {
      var script = document.createElement('script');
      var previousHls = window.Hls;
      var deadline = setTimeout(function () { hlsLoading = null; script.remove(); reject(new Error('Player library timed out')); }, 10000);
      script.src = api.getUrl('Plugins/Federation/Adaptive/Hls');
      script.onload = function () { clearTimeout(deadline); window.FederationHls = window.Hls; window.Hls = previousHls; resolve(window.FederationHls); };
      script.onerror = function () { clearTimeout(deadline); hlsLoading = null; script.remove(); reject(new Error('Player library unavailable')); };
      document.head.appendChild(script);
    });
    return hlsLoading;
  }
  function delay(ms, signal) {
    return new Promise(function (resolve, reject) {
      var timer = setTimeout(done, ms);
      function done() { if (signal) signal.removeEventListener('abort', cancel); resolve(); }
      function cancel() { clearTimeout(timer); signal.removeEventListener('abort', cancel); reject(new Error('Cancelled')); }
      if (signal) { if (signal.aborted) cancel(); else signal.addEventListener('abort', cancel, { once: true }); }
    });
  }
  var profile = {
    Name: 'Federation prepared web playback', MaxStreamingBitrate: 80000000,
    DirectPlayProfiles: [],
    TranscodingProfiles: [{ Container: 'ts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac', Protocol: 'hls',
      Context: 'Streaming', MaxAudioChannels: '2', MinSegments: 2, SegmentLength: 2, BreakOnNonKeyFrames: false }],
    CodecProfiles: [], ContainerProfiles: [], SubtitleProfiles: [
      { Format: 'srt', Method: 'Encode' }, { Format: 'ass', Method: 'Encode' },
      { Format: 'ssa', Method: 'Encode' }, { Format: 'pgssub', Method: 'Encode' },
      { Format: 'dvdsub', Method: 'Encode' }, { Format: 'vtt', Method: 'Encode' }]
  };
  window.FederationAdaptivePlayer = async function () {
    return class FederationPreparedPlayer {
      constructor(dependencies) {
        this.name = 'Federation adaptive web preview'; this.id = 'federation-adaptive'; this.type = 'mediaplayer'; this.priority = -1;
        this.isLocalPlayer = true; this.deps = dependencies; this.revision = 0; this.active = null;
        this.preparing = null; this.timer = null; this.enabled = true; this.lastTime = 0;
      }
      canPlayMediaType(type) { return type === 'Video'; }
      canPlayItem(item, options) {
        var federated = !window.NativeShell && !!window.MediaSource && item.Type === 'Movie' && !(item.PartCount > 1)
          && ((item.ProviderIds && item.ProviderIds.FederationKey)
          || (item.MediaSources || []).some(function (s) { return /\/Plugins\/Federation\/Stream\?/.test(s.Path || ''); }));
        // jellyfin-web treats an alternate MediaSourceId as a separate local
        // item during startup. Federation copies are sources of this same item.
        // Resolve an explicit copy through PlaybackInfo inside this player instead.
        if (federated && options && options.mediaSourceId && options.mediaSourceId !== item.Id) {
          this.initialSourceId = options.mediaSourceId;
          options.mediaSourceId = null;
        }
        return federated;
      }
      supportsPlayMethod(method) { return method === 'Transcode'; }
      getDeviceProfile() { return Promise.resolve(JSON.parse(JSON.stringify(profile))); }
      supports(feature) { return feature === 'PlaybackRate'; }
      canSetAudioStreamIndex() { return false; }
      canSetSubtitleStreamIndex() { return false; }
      currentMediaSource() { return this.active && this.active.source; }
      playSessionId() { return this.active && this.active.session; }
      playMethod() { return 'Transcode'; }
      currentSrc() { return this.active && this.active.url; }
      currentTime(value) {
        if (!this.active) return this.lastTime;
        var initialOffset = this.streamInfo && this.streamInfo.transcodingOffsetTicks || 0;
        if (value != null) { this.cancelPreparation(); this.active.video.currentTime = value / 1000 + initialOffset / TICKS - this.active.offset; return; }
        return (this.active.video.currentTime + this.active.offset - initialOffset / TICKS) * 1000;
      }
      duration() { return this.active ? this.active.source.RunTimeTicks / 10000 : 0; }
      seekable() { return true; }
      paused() { return !this.active || this.active.video.paused; }
      pause() { this.cancelPreparation(); if (this.active) this.active.video.pause(); }
      unpause() { return this.active ? this.active.video.play() : Promise.resolve(); }
      resume() { return this.unpause(); }
      getVolume() { return this.active ? this.active.video.volume * 100 : 100; }
      setVolume(value) { if (this.active) this.active.video.volume = Math.max(0, Math.min(1, value / 100)); }
      isMuted() { return this.active ? this.active.video.muted : false; }
      setMute(value) { if (this.active) this.active.video.muted = value; }
      getPlaybackRate() { return this.active ? this.active.video.playbackRate : 1; }
      setPlaybackRate(value) { this.cancelPreparation(); if (this.active) this.active.video.playbackRate = value; }
      getSupportedPlaybackRates() { return [0.5, 0.75, 1, 1.25, 1.5, 2]; }
      getBufferedRanges() {
        if (!this.active) return [];
        var ranges = [], video = this.active.video;
        for (var i = 0; i < video.buffered.length; i++) ranges.push({ start: (video.buffered.start(i) + this.active.offset) * TICKS,
          end: (video.buffered.end(i) + this.active.offset) * TICKS });
        return ranges;
      }
      getStats() { return []; }
      emit(name, args) { this.deps.events.trigger(this, name, args || []); }
      authorization(api) {
        return 'MediaBrowser Client="Federation adaptive web", Device="Browser", DeviceId="' + String(api.deviceId()).replace(/["\r\n]/g, '') + '", Version="1", Token="' + api.accessToken() + '"';
      }
      async request(path, method, signal) {
        var response = await fetch(this.api.getUrl('Plugins/Federation/Adaptive/' + path), {
          method: method || 'GET', headers: { Authorization: this.authorization(this.api) }, signal: signal, cache: 'no-store'
        });
        if (!response.ok) throw new Error('Adaptive request refused (' + response.status + ')');
        return response.status === 204 ? null : response.json();
      }
      async sources(signal) {
        var result = await this.request(this.item.Id + '/Sources', 'GET', signal);
        return field(result, 'Sources') || [];
      }
      async playbackInfo(id, position, audio, subtitle, signal) {
        var response = await fetch(this.api.getUrl('Items/' + this.item.Id + '/PlaybackInfo'), {
          method: 'POST', signal: signal, headers: { Authorization: this.authorization(this.api), 'Content-Type': 'application/json' },
          body: JSON.stringify({ UserId: this.api.getCurrentUserId(), MediaSourceId: id, StartTimeTicks: Math.round(position * TICKS),
            AudioStreamIndex: audio, SubtitleStreamIndex: subtitle == null ? -1 : subtitle, DeviceProfile: await this.getDeviceProfile(),
            IsPlayback: true, AutoOpenLiveStream: false, EnableDirectPlay: false, EnableDirectStream: false,
            MaxStreamingBitrate: this.deps.playbackManager.getMaxStreamingBitrate(this), AllowVideoStreamCopy: false, AllowAudioStreamCopy: false })
        });
        if (!response.ok) throw new Error('Replacement refused');
        var result = await response.json();
        if (!result.MediaSources || !result.MediaSources[0] || result.MediaSources[0].Id !== id || !result.MediaSources[0].TranscodingUrl) throw new Error('Replacement unavailable');
        return result;
      }
      async play(options) {
        this.cleanup();
        var revision = this.revision;
        this.item = options.item;
        this.api = this.deps.ServerConnections.getApiClient(this.item.ServerId);
        this.disabledKey = 'federationAdaptiveDisabled:' + this.api.getCurrentUserId();
        this.enabled = localStorage.getItem(this.disabledKey) !== 'true';
        this.createSurface();
        var sources = await this.sources();
        var preferred = this.initialSourceId; this.initialSourceId = null;
        if (preferred && preferred !== options.mediaSource.Id) {
          var replacement = await this.playbackInfo(preferred, options.playerStartPositionTicks / TICKS || 0,
            options.mediaSource.DefaultAudioStreamIndex, options.mediaSource.DefaultSubtitleStreamIndex, null);
          this.stopEncoding(options.playSessionId);
          Object.assign(options.mediaSource, replacement.MediaSources[0]);
          options.url = this.api.getUrl(options.mediaSource.TranscodingUrl);
          options.playSessionId = replacement.PlaySessionId;
          options.transcodingOffsetTicks = this.offsetFor(options.url, options.playerStartPositionTicks / TICKS || 0) * TICKS;
        }
        if (revision !== this.revision) throw new Error('Playback cancelled');
        var current = sources.find(function (s) { return field(s, 'Id') === options.mediaSource.Id; });
        // Jellyfin's static source id can differ from the provider's deterministic id.
        // Resolve it from the exact source-bound Path; never guess a different copy.
        if (!current) {
          var original = new URL(options.mediaSource.Path, this.api.serverAddress());
          var server = original.searchParams.get('serverId'), remote = original.searchParams.get('itemId');
          current = sources.find(function (s) { return field(s, 'StaticIdentity') === server + ':' + normalize(remote).replace(/-/g, ''); });
        }
        if (!current) throw new Error('Source is no longer available');
        this.policy = { currentId: field(current, 'Id'), reference: current, audio: options.mediaSource.DefaultAudioStreamIndex,
          subtitle: options.mediaSource.DefaultSubtitleStreamIndex == null ? -1 : options.mediaSource.DefaultSubtitleStreamIndex,
          lastSwitch: Date.now(), rejected: {}, pendingId: null };
        this.initialOffset = options.transcodingOffsetTicks || 0;
        var initial = await this.openSource(options.mediaSource, options.url, options.playSessionId,
          this.offsetFor(options.url, options.transcodingOffsetTicks / TICKS || 0), options.playerStartPositionTicks / TICKS || 0);
        if (revision !== this.revision) { this.disposeSource(initial); throw new Error('Playback cancelled'); }
        this.active = initial;
        initial.video.controls = true; initial.video.style.visibility = 'visible'; initial.video.muted = false;
        this.bind(initial);
        await initial.video.play();
        this.refreshTrackControls(); this.schedule();
      }
      offsetFor(url, fallback) {
        return /copytimestamps=true/i.test(url) || /\.m3u8(?:\?|$)/i.test(url) ? 0 : fallback;
      }
      createSurface() {
        var surface = document.createElement('div');
        surface.className = 'federation-adaptive-surface';
        surface.style.cssText = 'position:fixed;inset:0;background:#000;z-index:10000;display:flex;align-items:center;justify-content:center;';
        var bar = document.createElement('div');
        bar.style.cssText = 'position:absolute;top:1rem;left:1rem;display:flex;gap:.7rem;align-items:center;z-index:2;color:#fff;background:#111c;padding:.6rem;border-radius:.4rem;';
        var close = document.createElement('button'); close.textContent = 'Close player';
        close.onclick = () => this.deps.playbackManager.stop(this);
        var toggle = document.createElement('input'); toggle.type = 'checkbox'; toggle.checked = this.enabled;
        var label = document.createElement('label'); label.append(toggle, document.createTextNode(' Automatic server switching'));
        toggle.onchange = () => { this.enabled = toggle.checked; localStorage.setItem(this.disabledKey, String(!this.enabled)); if (!this.enabled) this.cancelPreparation(); };
        var fullscreen = document.createElement('button'); fullscreen.textContent = 'Fullscreen';
        fullscreen.onclick = () => { if (document.fullscreenElement) document.exitFullscreen(); else if (surface.requestFullscreen) surface.requestFullscreen().catch(function () {}); };
        bar.append(close, label, fullscreen);
        this.audioSelect = document.createElement('select'); this.audioSelect.setAttribute('aria-label', 'Audio track');
        this.audioSelect.onchange = () => this.deps.playbackManager.setAudioStreamIndex(Number(this.audioSelect.value), this);
        this.subtitleSelect = document.createElement('select'); this.subtitleSelect.setAttribute('aria-label', 'Subtitle track');
        this.subtitleSelect.onchange = () => this.deps.playbackManager.setSubtitleStreamIndex(Number(this.subtitleSelect.value), this);
        bar.append(this.audioSelect, this.subtitleSelect);
        bar.style.flexWrap = 'wrap';
        this.notice = document.createElement('div'); this.notice.setAttribute('role', 'status'); this.notice.setAttribute('aria-live', 'polite');
        this.notice.style.cssText = 'position:absolute;top:1rem;right:1rem;max-width:40vw;color:white;background:#111d;padding:.6rem;border-radius:.4rem;pointer-events:none;display:none;z-index:3;';
        surface.append(bar, this.notice); document.body.appendChild(surface); this.surface = surface;
      }
      refreshTrackControls() {
        var source = this.active.source;
        [[this.audioSelect, 'Audio', source.DefaultAudioStreamIndex], [this.subtitleSelect, 'Subtitle', source.DefaultSubtitleStreamIndex]].forEach(function (group) {
          var select = group[0]; select.replaceChildren();
          if (group[1] === 'Subtitle') { var off = document.createElement('option'); off.value = '-1'; off.textContent = 'Subtitles off'; select.appendChild(off); }
          (source.MediaStreams || []).filter(function (s) { return s.Type === group[1]; }).forEach(function (s) {
            var option = document.createElement('option'); option.value = String(s.Index); option.textContent = s.DisplayTitle || s.Title || s.Language || group[1]; select.appendChild(option);
          });
          select.value = String(group[2] == null ? -1 : group[2]);
        });
      }
      async openSource(source, url, session, offset, at, signal) {
        var parsed = new URL(url, this.api.serverAddress());
        if (parsed.origin !== new URL(this.api.serverAddress()).origin) throw new Error('Replacement must use the receiving server');
        var video = document.createElement('video'); video.preload = 'auto'; video.playsInline = true; video.muted = true;
        video.style.cssText = 'position:absolute;width:100%;height:100%;object-fit:contain;visibility:hidden;';
        this.surface.appendChild(video);
        var entry = { video: video, source: source, url: parsed.href, session: session, offset: offset, hls: null, disposed: false, api: this.api };
        try {
          if (/\.m3u8(?:\?|$)/i.test(parsed.pathname + parsed.search)) {
            {
              var Hls = await loadHls(this.api);
              if (!Hls || !Hls.isSupported()) throw new Error('HLS is not supported');
              entry.hls = new Hls({ maxBufferLength: 12, maxMaxBufferLength: 20, maxBufferSize: 16 * 1024 * 1024,
                backBufferLength: 2, startPosition: Math.max(0, at - offset), enableWorker: false,
                manifestLoadingTimeOut: 10000, fragLoadingTimeOut: 10000, fragLoadingMaxRetry: 1 });
              entry.hls.attachMedia(video); entry.hls.loadSource(parsed.href);
              entry.hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) entry.error = true; });
            }
          } else video.src = parsed.href;
          var until = Date.now() + 18000;
          while (video.readyState < 1) {
            if (Date.now() >= until || entry.error || video.error || (signal && signal.aborted)) throw new Error('Replacement did not load');
            await delay(50, signal);
          }
          if (at - offset > 0.1) video.currentTime = Math.max(0, at - offset);
          return entry;
        } catch (error) { this.disposeSource(entry); throw error; }
      }
      bind(entry) {
        var video = entry.video;
        ['timeupdate', 'pause', 'volumechange', 'waiting'].forEach((name) => video.addEventListener(name, () => {
          if (this.active !== entry) return;
          if (name === 'timeupdate') { this.waitingAt = 0; this.lastTime = this.currentTime(); }
          if (name === 'waiting' && !this.waitingAt) this.waitingAt = Date.now();
          this.emit(name);
        }));
        video.addEventListener('playing', () => { if (this.active === entry) { this.waitingAt = 0; this.emit('unpause'); } });
        video.addEventListener('seeking', () => { if (this.active === entry) this.cancelPreparation(); });
        video.addEventListener('error', () => { if (this.active === entry) { entry.error = true; this.schedule(true); } });
        video.addEventListener('ended', () => { if (this.active === entry) { this.lastTime = this.currentTime(); this.emit('stopped'); this.cleanup(); } });
      }
      schedule(immediate) {
        clearTimeout(this.timer);
        if (!this.active) return;
        this.timer = setTimeout(() => this.evaluate().finally(() => this.schedule()), immediate ? 0 : 9000 + Math.random() * 2000);
      }
      async evaluate() {
        if (!this.enabled || !this.active || this.preparing || document.fullscreenElement === this.active.video
          || document.pictureInPictureElement === this.active.video || (this.paused() && !this.active.error)) return;
        var revision = this.revision;
        try {
          var sources = await this.sources();
          if (revision !== this.revision || !this.active) return;
          var failed = this.active.error || !sources.some((s) => field(s, 'Id') === this.policy.currentId)
            || (this.waitingAt && Date.now() - this.waitingAt > 3000 && bufferedAhead(this.active.video, this.active.video.currentTime) < 1);
          var next = choose(this.policy, sources, Date.now(), failed);
          if (next) await this.prepare(next, failed);
        } catch (_) { /* A ranking service outage must not stop a playable stream. */ }
      }
      async prepare(next, failed) {
        if (this.preparing || !this.active) return;
        var preparation = { abort: new AbortController(), candidate: null, lease: null, revision: this.revision };
        this.preparing = preparation;
        var api = this.api;
        var signal = preparation.abort.signal;
        var timeout = setTimeout(() => preparation.abort.abort(), 20000);
        try {
          var grant = await this.request('Preparation', 'POST', signal); preparation.lease = field(grant, 'Lease');
          var tracks = compatible(this.policy.reference, next, this.policy.audio, this.policy.subtitle);
          if (!tracks) throw new Error('Tracks no longer match');
          var current = this.active;
          var position = current.video.currentTime + current.offset + (failed ? 0 : 2 * current.video.playbackRate);
          var result = await this.playbackInfo(field(next, 'Id'), position, tracks.audio, tracks.subtitle, signal);
          var source = result.MediaSources && result.MediaSources[0];
          if (!source || source.Id !== field(next, 'Id') || !source.TranscodingUrl) throw new Error('Replacement unavailable');
          var actualTracks = compatible(this.policy.reference, Object.assign({}, next, { RunTimeTicks: source.RunTimeTicks, Streams: source.MediaStreams }), this.policy.audio, this.policy.subtitle);
          if (!actualTracks || actualTracks.audio !== tracks.audio || actualTracks.subtitle !== tracks.subtitle) throw new Error('Replacement metadata changed');
          var url = this.api.getUrl(source.TranscodingUrl);
          preparation.session = result.PlaySessionId || new URL(url, this.api.serverAddress()).searchParams.get('PlaySessionId');
          if (signal.aborted || this.active !== current) throw new Error('Preparation cancelled');
          var entry = await this.openSource(source, url, preparation.session, this.offsetFor(url, position), position, signal);
          preparation.candidate = entry;
          entry.video.playbackRate = current.video.playbackRate;
          // Buffer the projected position while the old source remains audible.
          while (true) {
            if (entry.error || entry.video.error || signal.aborted || this.active !== current || current.video.paused && !failed) throw new Error('Replacement failed');
            var at = current.video.currentTime + current.offset - entry.offset;
            if (at >= 0 && bufferedAhead(entry.video, at) >= 2 && entry.video.readyState >= 3) {
              entry.video.currentTime = at;
              await entry.video.play();
              break;
            }
            await delay(100, signal);
          }
          // Validate permission/source membership again immediately before handoff.
          var fresh = await this.sources(signal);
          if (!fresh.some((s) => field(s, 'Id') === field(next, 'Id') && compatible(this.policy.reference, s, this.policy.audio, this.policy.subtitle))) throw new Error('Replacement permission changed');
          while (entry.video.seeking || entry.video.readyState < 3 || Math.abs(entry.video.currentTime + entry.offset - current.video.currentTime - current.offset) > 0.15) {
            if (entry.error || entry.video.error || this.active !== current) throw new Error('Replacement failed');
            if (!entry.video.seeking) entry.video.currentTime = current.video.currentTime + current.offset - entry.offset;
            await delay(30, signal);
          }
          if (signal.aborted || preparation.revision !== this.revision || this.active !== current || !this.enabled) throw new Error('Preparation cancelled');
          entry.video.volume = current.video.volume;
          var muted = current.video.muted;
          entry.video.controls = true; this.bind(entry);
          this.active = entry; current.video.muted = true; entry.video.muted = muted;
          entry.video.style.visibility = 'visible'; current.video.style.visibility = 'hidden'; current.video.pause();
          this.policy.currentId = field(next, 'Id'); this.policy.reference = next;
          this.policy.audio = tracks.audio; this.policy.subtitle = tracks.subtitle; this.policy.lastSwitch = Date.now(); this.policy.pendingId = null;
          this.audioStreamIndex = tracks.audio; this.subtitleStreamIndex = tracks.subtitle;
          this.streamInfo.mediaSource = source; this.streamInfo.playSessionId = entry.session; this.streamInfo.url = entry.url;
          preparation.candidate = null; preparation.session = null;
          this.disposeSource(current); this.refreshTrackControls(); this.emit('timeupdate');
          this.notice.textContent = 'Switched to ' + field(next, 'Name'); this.notice.style.display = 'block';
          clearTimeout(this.noticeTimer); this.noticeTimer = setTimeout(() => { if (this.notice) this.notice.style.display = 'none'; }, 4000);
        } catch (_) { if (preparation.revision === this.revision && this.policy) this.policy.rejected[field(next, 'Id')] = Date.now() + 60000; }
        finally {
          clearTimeout(timeout);
          if (preparation.candidate) this.disposeSource(preparation.candidate);
          else if (preparation.session) this.stopEncoding(preparation.session, api);
          if (preparation.lease) fetch(api.getUrl('Plugins/Federation/Adaptive/Preparation/' + preparation.lease), { method: 'DELETE', headers: { Authorization: this.authorization(api) } }).catch(function () {});
          if (this.preparing === preparation) this.preparing = null;
        }
      }
      stopEncoding(session, api) {
        api = api || this.api;
        if (session && api) fetch(api.getUrl('Videos/ActiveEncodings', { deviceId: api.deviceId(), playSessionId: session }),
          { method: 'DELETE', headers: { Authorization: this.authorization(api) } }).catch(function () {});
      }
      disposeSource(entry) {
        if (!entry || entry.disposed) return; entry.disposed = true;
        if (entry.hls) entry.hls.destroy();
        entry.video.pause(); entry.video.removeAttribute('src'); entry.video.load(); entry.video.remove(); this.stopEncoding(entry.session, entry.api);
      }
      cancelPreparation() { if (this.preparing) this.preparing.abort.abort(); }
      cleanup() {
        this.revision++; clearTimeout(this.timer); clearTimeout(this.noticeTimer); this.cancelPreparation();
        var old = this.active; this.active = null; this.disposeSource(old);
        if (this.surface) this.surface.remove(); this.surface = null;
      }
      stop() { this.lastTime = this.currentTime(); this.emit('stopped'); this.cleanup(); return Promise.resolve(); }
      destroy() { this.cleanup(); }
    };
  };
}());
