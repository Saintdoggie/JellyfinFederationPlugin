#!/usr/bin/env python3
"""A signed-in jellyfin-web page in headless Chrome, for the browser stages of the
two-server checks.

Signing in through the form is not used: on Jellyfin 12.4's web client a headless
browser that opens the sign-in address directly signs in but is left on the
sign-in page (with or without the plugin). The session is stored the way the web
client stores it, and the page is opened already signed in.
"""
import json
import os
import shutil
import federation_metadata_smoke as qa

DEVICE_ID = 'federation-browser-qa'


def chrome_path():
    executable = os.environ.get('FEDERATION_QA_BROWSER')
    if not executable or executable == '1':
        executable = shutil.which('google-chrome') or shutil.which('chromium') or shutil.which('chromium-browser')
    return executable


def launch(playwright):
    return playwright.chromium.launch(executable_path=chrome_path(), headless=True, args=['--no-sandbox'])


def signed_in_page(browser, base, username, password='', viewport=None, path='#/home'):
    """Returns (page, user id, page errors list). The page is on `path`, settled."""
    header = {'Authorization': f'MediaBrowser Client="Jellyfin Web", Device="Chrome", DeviceId="{DEVICE_ID}", Version="12.4.0"'}
    auth = qa.request(base, '/Users/AuthenticateByName', header, {'Username': username, 'Pw': password})
    info = qa.request(base, '/System/Info/Public')
    credentials = {'Servers': [{'Id': info['Id'], 'Name': info.get('ServerName', 'qa'), 'ManualAddress': base, 'LocalAddress': base,
        'manualAddressOnly': True, 'LastConnectionMode': 2, 'DateLastAccessed': 1893456000000,
        'AccessToken': auth['AccessToken'], 'UserId': auth['User']['Id']}]}
    context = browser.new_context(viewport=viewport or {'width': 1440, 'height': 960})
    context.add_init_script(
        "try{localStorage.setItem('jellyfin_credentials', %s);localStorage.setItem('_deviceId2', %s);}catch(e){}"
        % (json.dumps(json.dumps(credentials)), json.dumps(DEVICE_ID)))
    page = context.new_page()
    errors = []
    page.on('pageerror', lambda error: errors.append(error.name + ': ' + error.message))
    page.goto(base + '/web/index.html' + path, wait_until='domcontentloaded')
    page.wait_for_function("window.ApiClient && ApiClient.getCurrentUserId && ApiClient.getCurrentUserId() && !/login|selectserver/i.test(location.hash)", timeout=60000)
    page.wait_for_load_state('networkidle', timeout=60000)
    return page, auth['User']['Id'], errors


def go(page, base, path, settle_ms=1500):
    """In-app navigation by hash; waits for the network to go quiet."""
    page.evaluate("p => { location.hash = p; }", path.lstrip('#'))
    page.wait_for_load_state('networkidle', timeout=60000)
    page.wait_for_timeout(settle_ms)
