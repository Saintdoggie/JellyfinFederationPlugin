'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { JSDOM } = require('jsdom');
const script = fs.readFileSync(require('node:path').join(__dirname, '../Web/federation-adaptive.js'), 'utf8');
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
test('plugin factory is idempotent and restricts automatic selection to federated movies', async () => {
  const { dom } = fixture(); const original = dom.window.FederationAdaptivePlayer; dom.window.eval(script);
  assert.equal(dom.window.FederationAdaptivePlayer, original);
  const Player = await original(); const player = new Player({});
  assert.equal(Boolean(player.canPlayItem({ Type: 'Movie', MediaSources: [{ Path: 'http://localhost/Plugins/Federation/Stream?itemId=x' }] })), true);
  assert.equal(Boolean(player.canPlayItem({ Type: 'Episode', ProviderIds: { FederationKey: 'x' } })), false);
  assert.equal(Boolean(player.canPlayItem({ Type: 'Movie', MediaSources: [{ Path: '/owned/file.mkv' }] })), false);
  dom.window.close();
});
