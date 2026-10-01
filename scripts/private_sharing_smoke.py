#!/usr/bin/env python3
"""Real Companion HTTP/process lifecycle against a sandbox network CLI.

This checks integration, not real Tailscale login, TLS, NAT traversal or throughput.
It never changes the host's Tailscale state. Actual media bytes are covered by
plex_smoke.py; a two-account private network check remains a separate validation.
"""
import concurrent.futures
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


FAKE_CLI = r'''#!/usr/bin/env python3
import json, os, sys, time
from pathlib import Path
root = Path(os.environ['FED_NETWORK_SANDBOX'])
args = sys.argv[1:]
with (root / 'calls.jsonl').open('a') as log: log.write(json.dumps(args) + '\n')
mode = (root / 'mode').read_text()
config = json.loads((root / 'network.json').read_text())
dns = 'sandbox.tail123456.ts.net'
if args == ['status', '--json']:
    print(json.dumps({'BackendState': 'NeedsLogin' if mode == 'login' else 'Running', 'Self': {'DNSName': dns + '.'}}))
elif args == ['serve', 'status', '--json']:
    print(json.dumps(config))
elif args[0] == 'serve':
    port = next(a.split('=', 1)[1] for a in args if a.startswith('--https='))
    endpoint = dns + ':' + port
    if mode == 'slow':
        (root / 'started').touch()
        time.sleep(1.5)
    if args[-1] == 'off':
        if mode != 'stop-fail':
            for key, name in [('TCP', port), ('Web', endpoint), ('AllowFunnel', endpoint)]: config.get(key, {}).pop(name, None)
    else:
        config.setdefault('TCP', {})[port] = {'HTTPS': True}
        config.setdefault('Web', {})[endpoint] = {'Handlers': {'/': {'Proxy': args[-1]}}}
        config.setdefault('AllowFunnel', {})[endpoint] = False
    (root / 'network.json').write_text(json.dumps(config))
else:
    sys.exit(9)
'''


def smoke(dotnet, published):
    work = Path(tempfile.mkdtemp(prefix='federation-private-sharing-'))
    os.chmod(work, 0o700)
    app = work / 'app'
    shutil.copytree(published, app)
    # Publishing normally has no state; do not accidentally copy local consent.
    (app / 'companion-state.json').unlink(missing_ok=True)
    binary = work / 'bin'
    binary.mkdir()
    cli = binary / 'tailscale'
    cli.write_text(FAKE_CLI)
    cli.chmod(0o700)
    (work / 'mode').write_text('normal')
    unrelated = {'TCP': {'8443': {'HTTPS': True}}, 'Web': {
        'sandbox.tail123456.ts.net:8443': {'Handlers': {'/': {'Proxy': 'http://127.0.0.1:1234'}}}}}
    (work / 'network.json').write_text(json.dumps(unrelated))
    process = None
    log = None

    def check(condition, message):
        if not condition:
            raise RuntimeError(message)

    def state():
        return json.loads((app / 'companion-state.json').read_text())

    def request(path, method='GET', data=None, authenticated=True, expected=200):
        headers = {'X-Companion-Admin': state()['AdminAccessKey']} if authenticated else {}
        if data is not None:
            headers['Content-Type'] = 'application/json'
        req = urllib.request.Request(base + path, method=method, headers=headers,
            data=None if data is None else json.dumps(data).encode())
        try:
            with urllib.request.urlopen(req, timeout=15) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read()
        check(status == expected, f'{path} HTTP {status}; expected {expected}')
        return json.loads(body) if body else {}

    def wait(action, message):
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            try:
                if action():
                    return
            except (OSError, ValueError, RuntimeError):
                pass
            time.sleep(0.1)
        raise RuntimeError(message)

    def start():
        nonlocal process, log, base
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        log = (work / 'private-runtime.log').open('ab')
        os.chmod(work / 'private-runtime.log', 0o600)
        environment = {**os.environ, 'PATH': str(binary) + os.pathsep + os.environ.get('PATH', ''),
                       'FED_NETWORK_SANDBOX': str(work), 'XDG_DATA_HOME': str(work / 'user-data')}
        process = subprocess.Popen([str(dotnet), str(app / 'FederationCompanion.dll'), '--urls', base],
            cwd=app, env=environment, stdout=log, stderr=subprocess.STDOUT)
        wait(lambda: request('/api/app/info'), 'Sandbox Companion did not start')
        return port

    def stop():
        nonlocal process, log
        if process is not None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            process = None
        if log:
            log.close()
            log = None

    base = ''
    try:
        port = start()
        for method in ['POST', 'DELETE']:
            request('/api/tailscale/private-share', method, authenticated=False, expected=401)
        check(not (work / 'calls.jsonl').exists(), 'Unauthenticated request ran the network helper')
        (work / 'mode').write_text('login')
        request('/api/tailscale/private-share', 'POST', expected=400)
        check(state().get('PublicUrl') is None, 'Failed setup saved a share URL')
        (work / 'mode').write_text('normal')
        result = request('/api/tailscale/private-share', 'POST')
        check(result['sharingUrl'] == 'https://sandbox.tail123456.ts.net:10000', 'Did not discover sharing endpoint')
        check(state()['PrivateSharingLocalPort'] == port, 'Did not discover actual listener')
        request('/api/public-url', 'POST', {'url': 'https://unrelated.example.com'}, expected=409)
        request('/api/tailscale/funnel', 'POST', expected=409)
        (work / 'mode').write_text('slow')
        with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
            pending = executor.submit(request, '/api/tailscale/private-share', 'POST')
            wait(lambda: (work / 'started').exists(), 'Network setup did not enter slow command')
            request('/api/tailscale/private-share', 'DELETE', expected=409)
            pending.result(timeout=15)
        (work / 'mode').write_text('normal')
        stop()
        new_port = start()
        wait(lambda: state()['PrivateSharingLocalPort'] == new_port, 'Private listener did not restore after restart')
        network = json.loads((work / 'network.json').read_text())
        check(network['Web']['sandbox.tail123456.ts.net:10000']['Handlers']['/']['Proxy'] == f'http://127.0.0.1:{new_port}', 'Restored proxy targets old port')
        (work / 'mode').write_text('stop-fail')
        request('/api/tailscale/private-share', 'DELETE', expected=400)
        check(state()['PrivateSharingPort'] == 10000, 'Unconfirmed stop cleared ownership')
        (work / 'mode').write_text('normal')
        request('/api/tailscale/private-share', 'DELETE')
        check(state().get('PublicUrl') is None and state().get('PrivateSharingPort') is None, 'Stop retained managed address')
        remaining = json.loads((work / 'network.json').read_text())
        check(remaining['TCP'] == unrelated['TCP'] and remaining['Web'] == unrelated['Web'], 'Setup or stop changed unrelated services')
        print('PASS: private-sharing sandbox HTTP authorization, setup failure, discovery, concurrency, restart and scoped stop. Network CLI simulated; real tailnet/TLS not tested.', flush=True)
    finally:
        stop()
        shutil.rmtree(work)


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default=os.environ.get('DOTNET', 'dotnet'))
    parser.add_argument('--published', type=Path, default=Path(__file__).resolve().parents[1] / 'artifacts/qa/companion')
    options = parser.parse_args()
    smoke(options.dotnet, options.published)
