#!/usr/bin/env python3
"""The web additions in real Chrome against two disposable servers.

    JELLYFIN_TEST_IMAGE=<image> python3 scripts/federation_ui_smoke.py            # own lab, removed afterwards
    python3 scripts/federation_ui_smoke.py --lab /path/to/lab.json [stage ...]   # a lab held by federation_ui_lab.py

Stages: tabs, download, plugin, themes, offline (last: it stops the friend and
starts it again). Needs Python Playwright and Chrome or Chromium.
Screenshots go to FEDERATION_QA_SHOTS when set (a private folder: they show the
fixture only, but keep them out of the repository).
"""
import json
import os
import sys
import urllib.parse
import federation_metadata_smoke as qa
import federation_browser as fb
from federation_peer_smoke import no_redirect

PLUGIN_ID = '495feadbd27f46c3bb9b0732ae8926fa'
SHOTS = os.environ.get('FEDERATION_QA_SHOTS')

GRID = """() => {
  const page = document.querySelector('.page:not(.hide)');
  const cards = page ? [...page.querySelectorAll('.itemsContainer .card[data-id]')] : [];
  return {
    names: cards.filter(c => getComputedStyle(c).display !== 'none').map(c => (c.querySelector('.cardText') || {}).textContent || ''),
    hidden: cards.filter(c => getComputedStyle(c).display === 'none').length,
    tabs: [...document.querySelectorAll('.federation-origin-tab')].map(t => t.textContent),
    selected: ([...document.querySelectorAll('.federation-origin-tab[aria-selected=true]')][0] || {}).textContent || ''
  };
}"""


def shot(page, name):
    if SHOTS:
        os.makedirs(SHOTS, exist_ok=True)
        page.screenshot(path=os.path.join(SHOTS, name + '.png'), full_page=True)


def token(base, user, password=''):
    header = {'Authorization': 'MediaBrowser Client="Federation UI QA", Device="Sandbox", DeviceId="ui-qa-api", Version="1"'}
    auth = qa.request(base, '/Users/AuthenticateByName', header, {'Username': user, 'Pw': password})
    return {'Authorization': 'MediaBrowser Token="' + auth['AccessToken'] + '"'}, auth['User']['Id']


def library_grid(page, base, library_id, expected_count, tabs=True):
    fb.go(page, base, '#/movies?topParentId=' + library_id)
    page.wait_for_function("n => document.querySelectorAll('.page:not(.hide) .itemsContainer .card[data-id]').length >= n", arg=expected_count, timeout=45000)
    if tabs:
        page.wait_for_selector('.federation-origin-tab', timeout=45000)


def pick_tab(page, label, expected_count):
    page.locator('.federation-origin-tab', has_text=label).first.click()
    page.wait_for_function("""([label, n]) => {
        const on = document.querySelector('.federation-origin-tab[aria-selected=true]');
        const cards = [...document.querySelectorAll('.page:not(.hide) .itemsContainer .card[data-id]')];
        return on && on.textContent === label && cards.length === n;
    }""", arg=[label, expected_count], timeout=45000)
    page.wait_for_timeout(800)
    return page.evaluate(GRID)


def stage_tabs(browser, lab):
    base, counts = lab['receiver']['base'], lab['counts']
    viewer, viewer_id = token(base, 'qa-viewer')
    library_id = qa.request(base, f'/UserViews?userId={viewer_id}', viewer)['Items'][0]['Id']
    page, _, errors = fb.signed_in_page(browser, base, 'qa-viewer')
    everything = counts['friend_only'] + counts['here_only'] + counts['shared']
    library_grid(page, base, library_id, everything)
    page.wait_for_function("name => [...document.querySelectorAll('.federation-origin-tab')].some(t => t.textContent === name)", arg=lab['friend_name'], timeout=45000)
    grid = page.evaluate(GRID)
    qa.check(len(grid['names']) == everything and grid['hidden'] == 0, f'All: {len(grid["names"])} cards shown, {grid["hidden"]} hidden, expected {everything} and 0')
    shot(page, 'tabs-all')
    for label, expected in (('This server', counts['here_only'] + counts['shared']), (lab['friend_name'], counts['friend_only'] + counts['shared'])):
        grid = pick_tab(page, label, expected)
        qa.check(len(grid['names']) == expected and grid['hidden'] == 0,
                 f'{label}: {len(grid["names"])} cards shown, {grid["hidden"]} hidden in the page, expected {expected} and 0')
        shot(page, 'tabs-' + ('here' if label == 'This server' else 'friend'))
    pick_tab(page, 'All', everything)
    qa.check(not errors, 'Page errors on the library page: ' + '; '.join(errors[:3]))
    page.context.close()
    print(f'UI PASS: server tabs in Chrome: All {everything}, This server {counts["here_only"] + counts["shared"]}, friend {counts["friend_only"] + counts["shared"]}; no card hidden inside a page', flush=True)


def stage_download(browser, lab):
    base = lab['receiver']['base']
    viewer, viewer_id = token(base, 'qa-viewer')
    items = qa.request(base, f'/Items?userId={viewer_id}&Recursive=true&IncludeItemTypes=Movie', viewer)['Items']
    friend_title = next(i for i in items if i['Name'].startswith('QA Friend'))
    qa.wait_for(lambda: qa.request(base, f'/Items/{friend_title["Id"]}?userId={viewer_id}', viewer).get('CanDownload') or None,
                'Download is not offered on a friend title', 60)
    status, headers, body = no_redirect(base, f'/Items/{friend_title["Id"]}/Download', {**viewer, 'Range': 'bytes=0-65535'})
    location = headers.get('Location') or headers.get('location') or ''
    qa.check(status in (200, 206), f'Download answered {status} {urllib.parse.urlsplit(location).netloc}: the friend is only known by a container name here, so the file must come through this server')
    qa.check(len(body) > 1000 and 'attachment' in (headers.get('Content-Disposition') or '').lower(), 'The relayed download is not a named file')
    print('UI PASS: download of a title from a friend known by a private name comes through this server as a named file; the device is never sent to the private address', flush=True)


def stage_plugin(browser, lab):
    base = lab['receiver']['base']
    admin, _ = token(base, 'qa-admin', lab['receiver']['password'])
    listed = next(p for p in qa.request(base, '/Plugins', admin) if p['Id'] == PLUGIN_ID)
    qa.check(listed.get('HasImage'), 'The plugin list says the plugin has no picture')
    headers, picture = qa.request(base, f'/Plugins/{PLUGIN_ID}/{listed["Version"]}/Image', admin, raw=True)
    qa.check(picture[:8] == b'\x89PNG\r\n\x1a\n', 'The plugin picture is not a PNG')
    page, _, errors = fb.signed_in_page(browser, base, 'qa-admin', lab['receiver']['password'])
    fb.go(page, base, f'#/dashboard/plugins/{PLUGIN_ID}?name=' + urllib.parse.quote('Jellyfin Federation'), 2500)
    page.wait_for_function("document.body.innerText.includes('Jellyfin Federation') && document.body.innerText.includes('Version')", timeout=45000)
    state = page.evaluate("""() => {
        const text = document.body.innerText;
        const rows = {};
        document.querySelectorAll('table tr').forEach(r => { const c = r.querySelectorAll('th,td'); if (c.length === 2) rows[c[0].innerText.trim()] = c[1].innerText.trim(); });
        const img = [...document.querySelectorAll('img')].find(i => /\\/Plugins\\/.*\\/Image/.test(i.src));
        return { error: /error occurred while getting the plugin details/i.test(text), rows, picture: !!img && img.naturalWidth > 0,
                 history: /Revision History/i.test(text) };
    }""")
    shot(page, 'plugin-page')
    qa.check(not state['error'], 'The plugin page shows "An error occurred while getting the plugin details from the repository"')
    qa.check(state['picture'], 'The plugin page shows no picture')
    qa.check(state['rows'].get('Developer') not in (None, '', 'Unknown'), f'Developer reads {state["rows"].get("Developer")!r}')
    qa.check(state['rows'].get('Repository') not in (None, '', 'Unknown'), f'Repository reads {state["rows"].get("Repository")!r}')
    qa.check(state['history'], 'The plugin page has no version notes')
    settings = page.evaluate("[...document.querySelectorAll('a[href*=configurationpage]')].filter(a => /Settings/.test(a.textContent)).map(a => a.getAttribute('href'))")
    qa.check(any('Federation' in href for href in settings), 'The Settings button does not lead to the settings page')
    page.context.close()
    print(f'UI PASS: plugin page in Chrome: picture, no repository error, developer "{state["rows"]["Developer"]}", repository "{state["rows"]["Repository"]}", version notes, Settings button', flush=True)


def stage_offline(browser, lab):
    base, counts = lab['receiver']['base'], lab['counts']
    admin, _ = token(base, 'qa-admin', lab['receiver']['password'])
    viewer, viewer_id = token(base, 'qa-viewer')
    library_id = qa.request(base, f'/UserViews?userId={viewer_id}', viewer)['Items'][0]['Id']
    own = counts['here_only'] + counts['shared']
    everything = own + counts['friend_only']
    listing = f'/Items?userId={viewer_id}&Recursive=true&IncludeItemTypes=Movie&parentId={library_id}&Limit=5'
    def complete():
        qa.request(base, '/Plugins/Federation/Availability/Rescan', admin, {})
        return qa.request(base, listing, viewer)['TotalRecordCount'] == everything or None
    qa.wait_for(complete, 'The library is not complete before the outage', 120)
    qa.run(['podman', 'stop', '-t', '2', lab['friend']['name']])
    try:
        def left_out():
            qa.request(base, '/Plugins/Federation/Availability/Rescan', admin, {})
            return qa.request(base, listing, viewer)['TotalRecordCount'] == own or None
        qa.wait_for(left_out, 'Titles of the offline server are still counted in the library list', 90)
        page, _, errors = fb.signed_in_page(browser, base, 'qa-viewer')
        # No server tabs are drawn while no other server is reachable.
        library_grid(page, base, library_id, own, tabs=False)
        page.wait_for_timeout(2500)
        grid = page.evaluate(GRID)
        shot(page, 'offline-all')
        qa.check(len(grid['names']) == own and grid['hidden'] == 0,
                 f'Friend offline: {len(grid["names"])} cards shown, {grid["hidden"]} hidden in the page, expected {own} and 0')
        page.context.close()
    finally:
        qa.run(['podman', 'start', lab['friend']['name']])
    qa.wait_for(lambda: qa.request(lab['friend']['base'], '/System/Info/Public'), 'The friend did not start again')
    qa.wait_for(complete, 'The friend titles did not return to the library list', 120)
    print(f'UI PASS: friend offline: the server lists {own} titles (count and page), no card hidden in the browser; all {everything} are back when it returns', flush=True)


STAGES = {'tabs': stage_tabs, 'download': stage_download, 'plugin': stage_plugin, 'offline': stage_offline}


def register(name, fn, before='offline'):
    """Lets another module (the theme check) add a stage; offline stays last."""
    items = [(k, v) for k, v in STAGES.items() if k != name]
    at = next((i for i, (k, _) in enumerate(items) if k == before), len(items))
    items.insert(at, (name, fn))
    STAGES.clear(); STAGES.update(items)


def main(argv):
    from playwright.sync_api import sync_playwright
    # Stages live on the importable module, so a stage module that imports this one
    # registers into the same table as the one this run reads.
    import federation_ui_smoke as shared
    import federation_theme_smoke  # noqa: F401  (registers the "themes" stage)
    STAGES = shared.STAGES
    args = list(argv)
    lab_file = args.pop(args.index('--lab') + 1) if '--lab' in args else None
    wanted = [a for a in args if a in STAGES] or list(STAGES)
    lab_object = None
    stage = 'lab'
    try:
        if lab_file:
            lab = json.load(open(lab_file))
        else:
            import federation_ui_lab
            lab_object = federation_ui_lab.Lab()
            lab = lab_object.up().describe()
        with sync_playwright() as playwright:
            browser = fb.launch(playwright)
            try:
                for stage in [s for s in STAGES if s in wanted]:
                    STAGES[stage](browser, lab)
            finally:
                browser.close()
    except Exception as error:
        detail = str(error).splitlines()[0][:400] if str(error) else type(error).__name__
        where = f' Private fixture kept at {lab_object.work}; do not publish its logs.' if lab_object else ''
        raise SystemExit(f'UI FAIL: {stage}: {detail}.{where}') from None
    finally:
        if lab_object:
            lab_object.down()
    print('UI: done' + ('' if lab_file else '; isolated containers removed, production untouched'), flush=True)


if __name__ == '__main__':
    main(sys.argv[1:])
