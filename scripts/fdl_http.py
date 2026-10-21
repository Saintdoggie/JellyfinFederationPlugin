#!/usr/bin/env python3
"""In-container HTTP helper for the direct-link harness (stdin JSON -> stdout JSON).

Request: {url, method, headers, body_b64, timeout, max_bytes}. Response:
{status, headers, body_b64, error}. No redirects are followed.
"""
import base64
import json
import sys
import urllib.error
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def main():
    req = json.load(sys.stdin)
    body = base64.b64decode(req['body_b64']) if req.get('body_b64') else None
    r = urllib.request.Request(req['url'], data=body, headers=req.get('headers') or {}, method=req.get('method'))
    opener = urllib.request.build_opener(NoRedirect)
    out = {'status': 0, 'headers': {}, 'body_b64': '', 'error': ''}
    try:
        try:
            resp = opener.open(r, timeout=req.get('timeout', 45))
            status, headers = resp.status, dict(resp.headers)
        except urllib.error.HTTPError as e:
            resp, status, headers = e, e.code, dict(e.headers)
        data = resp.read(req.get('max_bytes', 64 * 1024 * 1024))
        out.update(status=status, headers=headers, body_b64=base64.b64encode(data).decode())
    except Exception as e:  # noqa: BLE001 - report to the harness
        out['error'] = f'{type(e).__name__}: {e}'
    json.dump(out, sys.stdout)


main()
