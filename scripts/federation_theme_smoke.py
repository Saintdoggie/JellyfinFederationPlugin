#!/usr/bin/env python3
"""The "themes" stage of federation_ui_smoke.py: everything the web script adds must
stay readable on every theme.

For each of Jellyfin's stock themes, and each community theme stylesheet found in
FEDERATION_QA_THEME_CSS (a folder of .css files, applied as Custom CSS; none are
kept in this repository), it opens the real pages in Chrome and measures every
piece of text the plugin added against what is painted behind it:

  library page   server tabs (plain and chosen), source badges on cards
  title page     the server name tag, the Video dropdown, the plugin's entries in the "..." menu
  dialog         "Download to this server" (its submit button is the theme's own
                 button and is not judged; nor is a gradient the theme itself paints)

It fails on text under 3:1 against its background, on any gradient the plugin
painted, and when the plugin's colour tokens were not derived for the theme.
Text between 3:1 and 4.5:1 is listed as a note.

Run through federation_ui_smoke.py (`... federation_ui_smoke.py themes`).
"""
import os
from pathlib import Path
import federation_metadata_smoke as qa
import federation_browser as fb
import federation_ui_smoke as ui

STOCK = ['dark', 'light', 'appletv', 'blueradiance', 'purplehaze', 'wmc']
FAIL_UNDER = 3.0
NOTE_UNDER = 4.5

MEASURE = r"""() => {
  const parse = text => {
    const m = /rgba?\(([^)]+)\)/.exec(text || '');
    if (!m) return null;
    const p = m[1].split(/[\s,\/]+/).filter(Boolean).map(Number);
    return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
  };
  const over = (f, b, a) => ({ r: f.r * a + b.r * (1 - a), g: f.g * a + b.g * (1 - a), b: f.b * a + b.b * (1 - a), a: 1 });
  const lum = c => { const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }; return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b); };
  const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
  // What is painted behind an element: its ancestors' colours, nearest first, down to the first solid one.
  const behind = el => {
    const layers = []; let picture = false; let solid = false; let ends = null;
    for (let n = el; n && n.nodeType === 1; n = n.parentElement) {
      const cs = getComputedStyle(n);
      const c = parse(cs.backgroundColor);
      if (c && c.a > 0) { layers.push(c); if (c.a >= 0.999) { solid = true; break; } }
      if (cs.backgroundImage && cs.backgroundImage !== 'none') {
        // A theme's gradient is measured at every colour it names; a real picture cannot be.
        const stops = /url\(/.test(cs.backgroundImage) ? [] : (cs.backgroundImage.match(/rgba?\([^)]+\)/g) || []).map(parse).filter(c => c && c.a > 0);
        if (stops.length) { ends = stops; } else { picture = true; }
        break;
      }
    }
    let base = { r: 255, g: 255, b: 255, a: 1 };
    if (!solid && !picture && !ends) { const root = parse(getComputedStyle(document.documentElement).backgroundColor); if (root && root.a > 0) base = over(root, base, root.a); }
    const bases = (ends || [null]).map(end => {
      let b = end ? over(end, base, end.a) : base;
      for (let i = layers.length - 1; i >= 0; i--) b = over(layers[i], b, layers[i].a);
      return b;
    });
    return { colors: bases, picture: picture && !solid };
  };
  const visible = el => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return r.width > 2 && r.height > 2 && cs.visibility !== 'hidden' && cs.display !== 'none' && Number(cs.opacity) > 0.05; };
  const own = el => [...el.childNodes].some(n => n.nodeType === 3 && n.textContent.trim().length > 0);
  const mine = el => el.closest('[class*="federation-"], [data-federation-copy-picker], [data-federation-badge="1"]');
  const out = { texts: [], gradients: [], tokens: 0 };
  const rootStyle = getComputedStyle(document.documentElement);
  out.tokens = ['--fed-accent', '--fed-surface', '--fed-text', '--fed-hover'].filter(n => rootStyle.getPropertyValue(n).trim()).length;
  document.querySelectorAll('[class*="federation-"], [class*="federation-"] *, select[data-federation-copy-picker]').forEach(el => {
    if (!visible(el) || el.closest('.federation-theme-probe')) return;
    const cs = getComputedStyle(el);
    if (/gradient/i.test(cs.backgroundImage) && String(el.className).includes('federation-')) {
      // Only a gradient the plugin's own rules paint: with the plugin's class names
      // taken off, one that stays is the theme's (Apple TV paints every dialog header).
      const kept = el.className; el.className = kept.split(' ').filter(c => !c.includes('federation-')).join(' ');
      const themes = /gradient/i.test(getComputedStyle(el).backgroundImage); el.className = kept;
      if (!themes) out.gradients.push(String(kept).slice(0, 80));
    }
    // The theme's own submit button, drawn by the theme like every other one of its buttons.
    if (el.classList.contains('button-submit')) return;
    const isSelect = el.tagName === 'SELECT';
    if (!own(el) && !isSelect) return;
    const text = parse(cs.color); if (!text) return;
    // An element's own opacity fades its text and its background together into what is under it.
    let opacity = 1; for (let n = el; n && n.nodeType === 1 && mine(n); n = n.parentElement) opacity *= Number(getComputedStyle(n).opacity);
    const under = behind(el.parentElement || el);
    const ownBg = parse(cs.backgroundColor);
    // The lowest figure over every colour that can be behind the text.
    let lowest = Infinity;
    under.colors.forEach(base => {
      let bg = base;
      if (ownBg && ownBg.a > 0) bg = over(ownBg, base, ownBg.a * opacity);
      const shown = over(text, bg, Math.min(1, text.a * opacity));
      lowest = Math.min(lowest, ratio(shown, bg));
    });
    out.texts.push({ what: (isSelect ? 'select ' : '') + String(el.className).split(' ').filter(c => c.includes('federation-'))[0] || el.tagName.toLowerCase(),
      text: (el.textContent || el.value || '').trim().slice(0, 28), ratio: Math.round(lowest * 100) / 100, picture: under.picture && !(ownBg && ownBg.a >= 0.999) });
  });
  return out;
}"""


def set_custom_css(base, admin, css):
    branding = qa.request(base, '/Branding/Configuration', admin)
    qa.request(base, '/System/Configuration/branding', admin,
               {'LoginDisclaimer': branding.get('LoginDisclaimer') or '', 'CustomCss': css, 'SplashscreenEnabled': bool(branding.get('SplashscreenEnabled'))}, expected=204)


def measure(page, where, findings):
    result = page.evaluate(MEASURE)
    for text in result['texts']:
        findings.append({**text, 'where': where})
    return result


def one_theme(browser, lab, label, theme, friend_title, shared_title, library_id):
    base = lab['receiver']['base']
    # jellyfin-web keeps the chosen theme in the browser, not on the server.
    page, _, errors = fb.signed_in_page(browser, base, 'qa-admin', lab['receiver']['password'], local={'appTheme': theme})
    findings, gradients = [], []
    step = ['applying the theme']
    try:
        applied = page.evaluate("() => [...document.querySelectorAll('link[rel=stylesheet], script[src], style[id]')].map(e => (e.href || e.src || e.id || '')).filter(u => /theme/i.test(u)).join(' ')")
        qa.check(('/' + theme + '/') in applied or (theme + '.') in applied, f'the {theme} theme was not applied (found: {applied[-160:] or "nothing"})')
        step[0] = 'library page'
        ui.library_grid(page, base, library_id, 1)
        page.wait_for_function("name => [...document.querySelectorAll('.federation-origin-tab')].some(t => t.textContent === name)", arg=lab['friend_name'], timeout=45000)
        page.locator('.federation-origin-tab', has_text=lab['friend_name']).first.click()
        page.wait_for_function("document.querySelector('.federation-origin-tab[aria-selected=true]').textContent !== 'All'", timeout=30000)
        page.wait_for_timeout(1200)
        result = measure(page, 'library', findings)
        qa.check(result['tokens'] == 4, f'{label}: the plugin colours were not derived from the theme ({result["tokens"]} of 4 tokens set)')
        gradients += result['gradients']
        ui.shot(page, f'theme-{label}-library')
        page.locator('.federation-origin-tab', has_text='All').first.click()

        step[0] = 'page of a title owned here with a copy elsewhere'
        fb.go(page, base, '#/details?id=' + shared_title)
        page.wait_for_selector('.itemDetailPage:not(.hide) .selectVideo[data-federation-copy-picker="1"]', timeout=45000)
        gradients += measure(page, 'title page (own title, copies)', findings)['gradients']

        step[0] = 'page of a friend title'
        fb.go(page, base, '#/details?id=' + friend_title)
        page.wait_for_selector('.itemDetailPage:not(.hide) .btnMoreCommands', timeout=45000)
        page.wait_for_timeout(1500)
        gradients += measure(page, 'title page (friend title)', findings)['gradients']
        step[0] = 'the "..." menu on a friend title'
        page.locator('.itemDetailPage:not(.hide) .btnMoreCommands').first.click()
        page.wait_for_selector('.federation-actionsheet-item', timeout=20000)
        page.wait_for_timeout(600)
        gradients += measure(page, '"..." menu', findings)['gradients']
        ui.shot(page, f'theme-{label}-menu')
        step[0] = 'the download dialog'
        page.locator('.federation-actionsheet-item', has_text='Download').first.click()
        page.wait_for_selector('.federation-dest', timeout=20000)
        page.wait_for_timeout(500)
        gradients += measure(page, 'download dialog', findings)['gradients']
        ui.shot(page, f'theme-{label}-dialog')
    except Exception as error:
        ui.shot(page, f'theme-{label}-failed')
        raise RuntimeError(f'{label}: {step[0]}: {str(error).splitlines()[0][:200]}') from None
    finally:
        page.context.close()
    return findings, sorted(set(gradients)), errors


def stage_themes(browser, lab):
    base = lab['receiver']['base']
    admin, admin_id = ui.token(base, 'qa-admin', lab['receiver']['password'])
    items = qa.request(base, f'/Items?userId={admin_id}&Recursive=true&IncludeItemTypes=Movie', admin)['Items']
    friend_title = next(i['Id'] for i in items if i['Name'].startswith('QA Friend'))
    shared_title = next(i['Id'] for i in items if i['Name'].startswith('QA Shared'))
    library_id = qa.request(base, f'/UserViews?userId={admin_id}', admin)['Items'][0]['Id']
    settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
    badges_before = settings.get('ShowFederatedCloudBadges')
    settings['ShowFederatedCloudBadges'] = True
    qa.request(base, '/Plugins/Federation/Configuration', admin, settings)

    folder = os.environ.get('FEDERATION_QA_THEME_CSS')
    community = sorted(Path(folder).glob('*.css')) if folder else []
    runs = [(theme, theme, '') for theme in STOCK] + [(css.stem, 'dark', css.read_text(errors='replace')) for css in community if css.stat().st_size > 500]
    problems, notes, checked, detail = [], [], 0, []
    try:
        for label, theme, css in runs:
            set_custom_css(base, admin, css)
            findings, gradients, errors = one_theme(browser, lab, label, theme, friend_title, shared_title, library_id)
            measured = [f for f in findings if not f['picture']]
            checked += len(measured)
            qa.check(len(measured) >= 8, f'{label}: only {len(measured)} pieces of the plugin text could be measured; the pages did not draw as expected')
            detail += [f'{label}\t{f["where"]}\t{f["what"]}\t{f["text"]}\t{f["ratio"]}\t{"picture" if f["picture"] else ""}' for f in findings]
            for f in measured:
                line = f'{label}: {f["where"]}: {f["what"]} "{f["text"]}" {f["ratio"]}:1'
                if f['ratio'] < FAIL_UNDER:
                    problems.append(line)
                elif f['ratio'] < NOTE_UNDER:
                    notes.append(line)
            problems += [f'{label}: gradient painted by {g}' for g in gradients]
            problems += [f'{label}: page error: {e}' for e in errors[:2]]
            worst = min((f['ratio'] for f in measured), default=0)
            print(f'UI: theme {label}: {len(measured)} texts measured, lowest {worst}:1' + (f', {len(findings) - len(measured)} over a picture not measured' if len(findings) != len(measured) else ''), flush=True)
    finally:
        set_custom_css(base, admin, '')
        settings = qa.request(base, '/Plugins/Federation/Configuration', admin)
        settings['ShowFederatedCloudBadges'] = bool(badges_before)
        qa.request(base, '/Plugins/Federation/Configuration', admin, settings)
    if os.environ.get('FEDERATION_QA_THEME_DETAIL'):
        Path(os.environ['FEDERATION_QA_THEME_DETAIL']).write_text('\n'.join(detail))
    for note in sorted(set(notes))[:40]:
        print('UI NOTE (readable, under 4.5:1): ' + note, flush=True)
    qa.check(not problems, f'{len(set(problems))} theme problem(s): ' + ' | '.join(sorted(set(problems))[:12]))
    print(f'UI PASS: themes: {len(runs)} themes ({", ".join(r[0] for r in runs)}), {checked} pieces of plugin text measured on the library page, title pages, "..." menu and download dialog; none under {FAIL_UNDER}:1, no gradients', flush=True)


ui.register('themes', stage_themes)
