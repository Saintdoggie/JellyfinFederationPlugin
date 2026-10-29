'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { JSDOM } = require('jsdom');
const script = fs.readFileSync(require('node:path').join(__dirname, '../client/federation-adaptive.js'), 'utf8');
function fixture() {
  const dom = new JSDOM('<html><head></head><body></body></html>', { runScripts: 'outside-only', url: 'http://localhost/web/' });
  dom.window.MediaSource = function () {}; dom.window.eval(script); return { dom, core: dom.window.__federationAdaptiveCore };
}
function source(id, score = 80) {
  return { Id: id, Score: score, TimelineName: 'The same movie', RunTimeTicks: 7200e7, Streams: [
    { Type: 'Audio', Index: 2, Language: 'eng', Title: 'Main' },
    { Type: 'Subtitle', Index: 3, Language: 'eng', IsForced: true } ] };
}
function state() { return { currentId: 'a', reference: source('a'), audio: 2, subtitle: -1, lastSwitch: 0, rejected: {} }; }
test('matches equivalent tracks by semantics across differing indexes', () => {
  const { dom, core } = fixture(); const next = source('b'); next.Streams[0].Index = 7; next.Streams[1].Index = 9;
  assert.deepEqual(JSON.parse(JSON.stringify(core.compatible(source('a'), next, 2, 3))), { audio: 7, subtitle: 9 }); dom.window.close();
});
test('rejects alternate cuts, unknown timelines, absent or ambiguous commentary and subtitles', () => {
  const { dom, core } = fixture();
  for (const mutate of [s => s.RunTimeTicks += 30e7, s => s.TimelineName = 'Extended', s => s.TimelineName = '',
    s => s.Streams[0].Title = 'Commentary', s => s.Streams.push({ ...s.Streams[0], Index: 8 }),
    s => s.Streams[1].IsForced = false]) {
    const next = source('b'); mutate(next); assert.equal(core.compatible(source('a'), next, 2, 3), null);
  }
  dom.window.close();
});
test('upgrade waits for dwell and sustained improvement, then cooldown prevents flapping', () => {
  const { dom, core } = fixture(); const policy = state(); const sources = [source('a'), source('b', 95)];
  assert.equal(core.choose(policy, sources, 1000, false), null);
  assert.equal(core.choose(policy, sources, 46000, false), null);
  assert.equal(core.choose(policy, sources, 65000, false), null);
  assert.equal(core.choose(policy, sources, 67000, false).Id, 'b');
  policy.currentId = 'b'; policy.reference = sources[1]; policy.lastSwitch = 67000;
  assert.equal(core.choose(policy, [source('a', 99), source('b', 80)], 68000, false), null); dom.window.close();
});
test('actual source failure bypasses dwell, lower quality remains eligible and failed preparation backs off', () => {
  const { dom, core } = fixture(); const policy = state(); const next = source('b', 60);
  assert.equal(core.choose(policy, [next], 500, true).Id, 'b');
  policy.rejected.b = 60000; assert.equal(core.choose(policy, [next], 500, true), null);
  assert.equal(core.choose(policy, [next], 60001, true).Id, 'b'); dom.window.close();
});
test('brief improvement cannot accumulate across another candidate or decline', () => {
  const { dom, core } = fixture(); const policy = state();
  core.choose(policy, [source('a'), source('b', 95)], 46000, false);
  assert.equal(core.choose(policy, [source('a'), source('b', 81)], 67000, false), null);
  assert.equal(core.choose(policy, [source('a'), source('b', 95)], 68000, false), null); dom.window.close();
});
test('ranks 100 eligible sources without probes and rejects mismatching highest score', () => {
  const { dom, core } = fixture(); const sources = Array.from({length: 100}, (_, i) => source('peer' + i, i));
  sources[99].RunTimeTicks += 100e7;
  assert.equal(core.choose(state(), sources, 500, true).Id, 'peer98'); dom.window.close();
});
test('buffer readiness is measured at the target time rather than at a different buffered range', () => {
  const { dom, core } = fixture(); const video = { buffered: { length: 2, start: i => [0, 30][i], end: i => [5, 40][i] } };
  assert.equal(core.bufferedAhead(video, 20), 0); assert.equal(core.bufferedAhead(video, 32), 8); dom.window.close();
});
test('HLS buffering tolerates slow segments without unbounded memory use', () => {
  const { dom, core } = fixture();
  const options = core.hlsOptions(-4);
  assert.equal(options.startPosition, 0);
  assert.equal(options.maxBufferLength, 45);
  assert.equal(options.maxMaxBufferLength, 60);
  assert.equal(options.backBufferLength, 30);
  assert.equal(options.maxBufferSize, 128 * 1024 * 1024);
  assert.ok(options.maxBufferSize * 8 / 80000000 >= 6 * 2, 'byte cap must hold several maximum-bitrate segments');
  assert.ok(options.fragLoadingTimeOut >= 20000);
  assert.ok(options.fragLoadingMaxRetry >= 3);
  assert.ok(options.manifestLoadingTimeOut >= 20000);
  assert.ok(options.manifestLoadingMaxRetry >= 3);
  assert.equal(core.hlsOptions(12).startPosition, 12);
  dom.window.close();
});
test('quality steps down quickly on failure and up only after sustained health', () => {
  const { dom, core } = fixture();
  assert.equal(core.nextQualityCap(null, 80000000, true, 0), 48000000);
  assert.equal(core.nextQualityCap(48000000, 80000000, false, 59999), null);
  assert.equal(core.nextQualityCap(48000000, 80000000, false, 60000), 60000000);
  assert.equal(core.nextQualityCap(2000000, 80000000, true, 0), null);
  assert.equal(core.nextQualityCap(null, 80000000, false, 120000), null);
  dom.window.close();
});
test('plugin factory is idempotent and restricts automatic selection to federated movies and episodes', async () => {
  const { dom } = fixture(); const original = dom.window.FederationAdaptivePlayer; dom.window.eval(script);
  assert.equal(dom.window.FederationAdaptivePlayer, original);
  const Player = await original(); const player = new Player({});
  assert.equal(Boolean(player.canPlayItem({ Type: 'Movie', MediaSources: [{ Path: 'http://localhost/Plugins/Federation/Stream?itemId=x' }] })), true);
  assert.equal(Boolean(player.canPlayItem({ Type: 'Episode', ProviderIds: { FederationKey: 'x' } })), true);
  assert.equal(Boolean(player.canPlayItem({ Type: 'Movie', MediaSources: [{ Path: '/owned/file.mkv' }] })), false);
  dom.window.close();
});
test('startup recovery opens a fresh authorized source before stopping the failed original', async () => {
  const { dom } = fixture();
  const Player = await dom.window.FederationAdaptivePlayer(); const player = new Player({});
  player.api = { getUrl: path => 'http://localhost' + path, serverAddress: () => 'http://localhost' };
  player.policy = state();
  player.playbackInfo = async id => ({ PlaySessionId: 'new-' + id, MediaSources: [{ Id: id, TranscodingUrl: '/videos/' + id + '/main.m3u8' }] });
  player.openSource = async (source, url, session, offset, at) => ({ source, url, session, offset, at, video: {} });
  const stopped = []; player.stopEncoding = id => stopped.push(id);
  const entry = await player.recoverStartup([source('a'), source('b')], { playerStartPositionTicks: 120000000, playSessionId: 'old-session' });
  assert.equal(entry.source.Id, 'b');
  assert.equal(entry.session, 'new-b');
  assert.equal(entry.at, 12);
  assert.equal(player.policy.currentId, 'b');
  assert.deepEqual(stopped, ['old-session']);
  dom.window.close();
});
test('startup recovery rejects cleanly when no compatible source remains', async () => {
  const { dom } = fixture();
  const Player = await dom.window.FederationAdaptivePlayer(); const player = new Player({});
  player.api = { getUrl: path => 'http://localhost' + path, serverAddress: () => 'http://localhost' };
  player.policy = state();
  player.playbackInfo = async () => { throw new Error('capacity refused'); };
  player.stopEncoding = () => {};
  await assert.rejects(player.recoverStartup([source('a'), source('b')], { playerStartPositionTicks: 0, playSessionId: 'old' }), /No replacement source/);
  assert.ok(player.policy.rejected.b > 0);
  dom.window.close();
});
