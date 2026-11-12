'use strict';

// The injected UI takes its colours from whatever theme jellyfin-web is
// showing. These pin the rules that make that safe on a theme nobody tested:
// text always reads on its surface, an accent stays an accent, an explicit
// --federation-* override wins, and a theme change is picked up.

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');

const root = path.resolve(__dirname, '../..');
const badgeScript = fs.readFileSync(path.join(root, 'Web/client/federation-badge.js'), 'utf8');

function makeWindow(css) {
  const dom = new JSDOM(
    '<!doctype html><html><head><style id="theme">' + (css || '') + '</style></head><body></body></html>',
    { runScripts: 'outside-only', url: 'http://localhost/web/index.html' }
  );
  dom.window.ApiClient = {
    getCurrentUser: () => Promise.resolve({ Policy: { IsAdministrator: false } }),
    accessToken: () => 'test-token'
  };
  dom.window.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
  dom.window.setInterval = () => 0;
  dom.window.eval(badgeScript);
  return dom.window;
}

function theme() {
  return makeWindow().__federationTheme;
}

function ratio(t, a, b) {
  return t.contrastRatio(t.parseColor(a), t.parseColor(b));
}

// What a colour painted with alpha looks like on an opaque backdrop.
function over(t, fg, bg) {
  const f = t.parseColor(fg);
  const b = t.parseColor(bg);
  return 'rgb(' + [0, 1, 2].map((i) => {
    const k = ['r', 'g', 'b'][i];
    return Math.round(f[k] * f.a + b[k] * (1 - f.a));
  }).join(', ') + ')';
}

const themes = {
  'nothing readable (fallback)': {},
  'stock dark': {
    surface: ['rgb(16, 16, 16)'], raised: ['rgb(32, 32, 32)'], text: ['rgb(255, 255, 255)'],
    accent: ['rgb(0, 164, 220)'], accentText: ['rgb(255, 255, 255)']
  },
  'stock light': {
    surface: ['rgb(242, 242, 242)'], raised: ['rgb(255, 255, 255)'], text: ['rgba(0, 0, 0, 0.87)'],
    accent: ['rgb(0, 164, 220)'], accentText: ['rgb(255, 255, 255)']
  },
  'purple glass (translucent panels)': {
    surface: ['rgba(30, 10, 60, 0.6)', 'rgb(12, 4, 28)'], raised: ['rgba(255, 255, 255, 0.08)'],
    text: ['rgb(235, 225, 255)'], accent: ['rgb(170, 92, 255)'], accentText: ['rgb(170, 92, 255)']
  },
  'cream paper with a pale accent': {
    surface: ['rgb(250, 244, 230)'], raised: ['rgb(255, 250, 240)'], text: ['rgb(70, 60, 50)'],
    accent: ['rgb(255, 230, 150)'], accentText: ['rgb(255, 255, 255)']
  },
  'theme text too faint to read': {
    surface: ['rgb(20, 20, 24)'], raised: ['rgb(28, 28, 34)'], text: ['rgb(52, 52, 60)'],
    accent: ['rgb(229, 9, 20)'], accentText: ['rgb(229, 9, 20)']
  },
  'grey buttons, coloured progress': {
    surface: ['rgb(18, 18, 18)'], raised: ['rgb(30, 30, 30)'], text: ['rgb(240, 240, 240)'],
    accent: ['rgb(70, 70, 70)', 'rgb(229, 160, 13)'], accentText: ['rgb(255, 255, 255)']
  }
};

for (const [name, sample] of Object.entries(themes)) {
  test('theme tokens stay readable: ' + name, () => {
    const t = theme();
    const tokens = t.deriveTokens(sample);

    assert.ok(ratio(t, tokens.text, tokens.surface) >= 4.5, 'text on surface');
    assert.ok(ratio(t, tokens.text, tokens['surface-raised']) >= 3, 'text on a raised surface');
    assert.ok(ratio(t, over(t, tokens['text-muted'], tokens.surface), tokens.surface) >= 4.5, 'muted text on surface');
    assert.ok(ratio(t, tokens['accent-text'], tokens.accent) >= 2.5, 'text on the accent');
    assert.ok(ratio(t, tokens['accent-strong'], tokens.surface) >= 3, 'accent used as text or an outline');
    assert.ok(ratio(t, tokens['toast-text'], tokens['toast-surface']) >= 4.5, 'toast text');
    assert.ok(ratio(t, tokens['danger-text'], tokens['surface-raised']) >= 4.5, 'error text');

    // Over a film or a poster the plate must carry its own text whatever is behind it.
    for (const picture of ['rgb(0, 0, 0)', 'rgb(255, 255, 255)']) {
      assert.ok(ratio(t, tokens['osd-text'], over(t, tokens['osd-surface'], picture)) >= 4.5, 'player text over ' + picture);
    }

    for (const [token, value] of Object.entries(tokens)) {
      assert.ok(String(value).trim().length > 0, token + ' has a value');
      assert.doesNotMatch(String(value), /gradient|NaN|undefined/, token + ' is a plain value');
    }
  });
}

test('a neutral button colour does not hide the theme\'s real accent', () => {
  const t = theme();
  const tokens = t.deriveTokens(themes['grey buttons, coloured progress']);

  assert.equal(tokens.accent, 'rgb(229, 160, 13)');
});

test('the theme\'s own button text is kept when legible, replaced when not', () => {
  const t = theme();

  assert.equal(t.deriveTokens(themes['stock dark'])['accent-text'], 'rgb(255, 255, 255)');
  assert.equal(t.deriveTokens(themes['cream paper with a pale accent'])['accent-text'], 'rgb(0, 0, 0)');
});

test('a theme with no colour at all still gets a visible accent', () => {
  const t = theme();
  const tokens = t.deriveTokens({ surface: ['rgb(0, 0, 0)'], text: ['rgb(255, 255, 255)'], accent: ['rgb(90, 90, 90)'] });

  assert.equal(tokens.accent, 'rgb(90, 90, 90)');
  assert.ok(ratio(t, tokens['accent-strong'], tokens.surface) >= 3);
});

test('light and dark themes get their own shadow and soft accent', () => {
  const t = theme();
  const dark = t.deriveTokens(themes['stock dark']);
  const light = t.deriveTokens(themes['stock light']);

  assert.notEqual(dark.shadow, light.shadow);
  assert.notEqual(dark['accent-soft'], light['accent-soft']);
  assert.equal(light.text, 'rgb(0, 0, 0)');
});

test('fully round theme shapes are kept round, square ones square', () => {
  const t = theme();

  assert.equal(t.deriveTokens({ radiusBadge: ['50%'] })['radius-badge'], '50%');
  assert.equal(t.deriveTokens({ radiusBadge: ['100em'] })['radius-badge'], '50%');
  assert.equal(t.deriveTokens({ radiusBadge: ['0px'] })['radius-badge'], '0px');
  assert.equal(t.deriveTokens({ radius: ['12px'] }).radius, '12px');
  assert.equal(t.deriveTokens({ radius: ['calc(1px + 2vw)'] }).radius, '.2em');
});

test('every token can be overridden by a theme or Custom CSS', () => {
  const t = theme();
  const css = t.tokenCss(t.deriveTokens(themes['stock dark']));

  assert.match(css, /^:root\{/);
  assert.match(css, /--fed-accent:var\(--federation-accent,rgb\(0, 164, 220\)\);/);
  assert.match(css, /--fed-surface:var\(--federation-surface,rgb\(16, 16, 16\)\);/);
  for (const declaration of css.slice(6, -1).split(';').filter(Boolean)) {
    assert.match(declaration, /^--fed-([a-z-]+):var\(--federation-\1,.+\)$/);
  }
});

test('the injected styles use only theme tokens, never a fixed colour', () => {
  // Everything outside the token section: the styles and elements the script adds.
  const start = badgeScript.indexOf('var THEME_FALLBACK');
  const end = badgeScript.indexOf('// FED-TOKENS-END');
  assert.ok(start > 0 && end > start, 'token section found');
  const rest = badgeScript.slice(0, start) + badgeScript.slice(end);

  assert.doesNotMatch(rest, /#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b(?=[;'" ])/);
  assert.doesNotMatch(rest, /\brgba?\(|\bhsla?\(/);
  assert.doesNotMatch(rest, /gradient/);
  assert.ok((rest.match(/var\(--fed-/g) || []).length > 20, 'styles are written with tokens');
});

test('the live theme is read from the page and a theme change is picked up', () => {
  const window = makeWindow('html,body{background-color:rgb(250, 250, 250);color:rgb(20, 20, 20);}');
  const tokens = () => window.document.getElementById('federation-theme-tokens').textContent;

  window.__federationTheme.refresh();
  assert.match(tokens(), /--fed-surface:var\(--federation-surface,rgb\(250, 250, 250\)\);/);
  assert.match(tokens(), /--fed-text:var\(--federation-text,rgb\(20, 20, 20\)\);/);

  window.document.getElementById('theme').textContent = 'html,body{background-color:rgb(10, 10, 30);color:rgb(230, 230, 255);}';
  window.__federationTheme.refresh();
  assert.match(tokens(), /--fed-surface:var\(--federation-surface,rgb\(10, 10, 30\)\);/);
  assert.match(tokens(), /--fed-text:var\(--federation-text,rgb\(230, 230, 255\)\);/);
});
