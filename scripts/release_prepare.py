#!/usr/bin/env python3
"""Bump version files, add the manifest entry. Usage: release.py prepare OLD NEW 'changelog' ; release.py checksum NEW zip"""
import sys, re, json, datetime, hashlib
cmd = sys.argv[1]
if cmd == 'prepare':
    old, new, ch = sys.argv[2:5]
    for p in ['JellyfinFederationPlugin.csproj','Plex/Jellyfin.Plugin.Federation.Plex.csproj','PlexPlugin/Jellyfin.Plugin.FederationPlex.csproj','Plex/INSTALL.md','integrations/catalog.json','PlexPlugin/meta.json']:
        s = open(p).read(); assert old in s, p; open(p,'w').write(s.replace(old,new))
    ts = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:00Z')
    s = open('meta.json').read()
    assert '"version": "%s"' % old in s
    s = s.replace('"version": "%s"' % old, '"version": "%s"' % new)
    s = re.sub(r'"changelog": "%s:.*?",\n' % re.escape(old), lambda m: '"changelog": %s,\n' % json.dumps(ch, ensure_ascii=False), s, flags=re.S)
    s = re.sub(r'"timestamp": "[^"]+"', '"timestamp": "%s"' % ts, s); json.loads(s); open('meta.json','w').write(s)
    m = open('manifest.json').read()
    i = m.index('      {\n        "version": "%s"' % old)
    entry = '      {\n        "version": "%s",\n        "changelog": %s,\n        "targetAbi": "12.0.0.0",\n        "timestamp": "%s",\n        "sourceUrl": "https://github.com/Saintdoggie/JellyfinFederationPlugin/releases/download/%s/Jellyfin.Federation.zip",\n        "checksum": "CHECKSUM_PENDING"\n      },\n' % (new, json.dumps(ch, ensure_ascii=False), ts, new)
    m = m[:i] + entry + m[i:]; json.loads(m); open('manifest.json','w').write(m); print('prepared', new, ts)
elif cmd == 'checksum':
    md5 = hashlib.md5(open(sys.argv[3],'rb').read()).hexdigest()
    m = open('manifest.json').read(); assert m.count('CHECKSUM_PENDING') == 1
    m = m.replace('CHECKSUM_PENDING', md5); d = json.loads(m); assert d[0]['versions'][0]['version'] == sys.argv[2]
    open('manifest.json','w').write(m); print(md5)
