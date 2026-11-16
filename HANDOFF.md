# Handoff: where the work stands

For any agent or person taking over. Read `AGENTS.md` first (what the code is and
the rules), then this file (what is going on now), then `TODO.md` (every open item,
newest release first). Keep this file current: update "State right now" whenever a
release goes out or a piece of work stops half way.

## State right now (2026-11-16)

- `master` = **0.0.228** (released 2026-11-16). 0.0.225 to 0.0.227 went out on
  2026-11-15/16.
- No work is half done. Nothing is installed on any live server by these releases:
  installing is the owner's call (see "Rules that are easy to break").
- 0.0.228 closed queue item 1 (themes) and three sign-in bugs the theme check
  exposed (`loadSession`/`watchSession`, `getToken`, `resolveAdminState` in
  `Web/client/federation-badge.js`). What the theme check does not open yet is
  listed in `TODO.md` under 0.0.228.
- Next up: the queue below, top first.

## What the owner asked for (2026-11-15) and where each item is

| Asked | Status | Where |
|---|---|---|
| Release the Video dropdown and Windows link work | Done: 0.0.225, 0.0.226 | `TODO.md` |
| Downloads from federated servers must not bring up a local IP | Done in 0.0.227 | `Services/PlayerReach.cs` |
| Server view "condensed": no empty space where a title was | Done in 0.0.227 (offline servers' titles left out by the server) | `FederationLocalOnlyItemsFilter.UnavailableItemIds` |
| "A plugin page for the plugin page" | Done in 0.0.227, read as the plugin's page under Dashboard → Plugins | `Services/FederationPluginPageFilter.cs` |
| Bug fixes | 0.0.227 has three (below); keep looking | "How bugs were found" |
| Injected web additions work across all themes | Done in 0.0.228 (9 themes measured in Chrome) | `scripts/federation_theme_smoke.py` |
| Major: better experience | Queue item 3 | `TODO.md` "Queue" |
| Major: better federation, platform and app compatibility | Queue item 2 | `TODO.md` "Queue" |

Two of the owner's sentences were read, not confirmed. If the owner says the
reading is wrong, the work is small to redo:

- "donwlaod stuff without it binking up a local ip": read as "a device that
  downloads a friend's title is sent to an address like 192.168.x or 100.x". The
  owner's home server knows its friends by https names, so the case probably comes
  from another server of theirs or from a friend's side. Not reproduced on their
  servers; the rule is proven on test servers.
- "a plugin padge for the plugin padge": read as Jellyfin's own page for the plugin
  (Dashboard → Plugins → Jellyfin Federation), which had no logo, said
  "Repository: Unknown" and showed an error banner on hand-copied installs.

## How to build, test and release

Tools on the owner's machine: `~/.dotnet/dotnet` (SDK 10), Node 22, Podman, Python
Playwright with `/usr/bin/google-chrome`, Jellyfin test image
`docker.io/jellyfin/jellyfin:12.4`.

```sh
~/.dotnet/dotnet build JellyfinFederationPlugin.csproj -c Release -v q --no-incremental
~/.dotnet/dotnet test Tests/Jellyfin.Plugin.Federation.Tests.csproj -c Release -v q   # twice
npm test                                                                             # twice
export JELLYFIN_TEST_IMAGE=docker.io/jellyfin/jellyfin:12.4
python3 scripts/federation_ui_smoke.py           # real Chrome: tabs, download, plugin page, offline friend
python3 scripts/federation_peer_smoke.py         # the other two-server checks are listed in scripts/README.md
FEDERATION_QA_BROWSER=1 python3 scripts/federation_owned_title_smoke.py
```

Release (one version everywhere, see `AGENTS.md` "Validation and release policy"):

```sh
python3 scripts/release_prepare.py prepare OLD NEW "NEW: one-paragraph changelog"   # then rebuild
scripts/release_publish.sh NEW "NEW: title" notes.md commit-message.txt
```

`release_publish.sh` needs `~/.config/federation-release-denylist` (one regex of
names, domains and addresses that must never be committed) and stops if the diff
matches it. Release notes end with `---` and an "Authored by Claude <model> (Claude
Code), working with the repository owner." line. Write notes for a reader who has
not followed the work: what changed for them, what was checked, what was not.

Things that will waste your time if you do not know them:

- **Browser stages sign in through `scripts/federation_browser.py`.** On Jellyfin
  12.4's web client a headless browser that opens the sign-in address directly
  signs in and then stays on the sign-in page, plugin or not. Do not use the form.
- **Library scans in fixtures sometimes miss the files.** `federation_ui_lab.py`
  asks for the scan again; `federation_owned_title_smoke.py` can still fail in
  "setup" with "did not scan its title". Run it again before suspecting the code,
  and do not run two fixtures at once.
- **The checks set `FEDERATION_PLAYERS_REACH_PRIVATE=1`** on their containers,
  because the servers know each other by container name (a private name). The UI
  lab does not, so it shows the real default.
- **The script starts before jellyfin-web has a session.** On a fresh page load
  `window.ApiClient` does not exist yet when `federation-badge.js` runs. Anything
  that needs the signed-in person belongs in `loadSession()` (run again by
  `watchSession()` when the token appears or changes), not at the top level.
- **jellyfin-web keeps the theme in the browser** (`localStorage["<user id>-appTheme"]`),
  not in the server's display preferences. `fb.signed_in_page(..., local={'appTheme': x})`
  sets it; the theme check fails if the theme's stylesheet is not on the page.
- The theme check wants community stylesheets in a folder named by
  `FEDERATION_QA_THEME_CSS` (ElegantFin, Scyfin, Zesty were used; fetch their
  released `.css` yourself, they are not in the repository). Without it only the six
  stock themes run.
- `federation_ui_lab.py --hold` keeps two servers up for looking at things by
  hand; `FEDERATION_QA_KEEP=1` keeps them after a failure. Remove what you keep.
- Old stopped `fdl-*` containers and a `fed-ui-preview` container on the owner's
  machine belong to earlier sessions. Leave them.
- Since 0.0.227 the manifest writes versions with four parts (`0.0.227.0`); tags,
  `meta.json` and the csproj keep three. `release_prepare.py` does this.

## How bugs were found (do this again)

The three fixes in 0.0.227 came from two cheap sources, not from reading code:

1. The owner's home server log, grouped by message with addresses masked (the
   commands are in `TODO.md` under 0.0.227). 364 failed `Peer/Items` requests in
   three days pointed at the catalogue listing that never finished.
2. Looking at the real pages in Chrome on the lab (`federation_ui_lab.py --hold`).

Seen in that log and **not** fixed, because they are Jellyfin's or the machine's:
trickplay errors for friends' titles (Jellyfin's task tries every video; friends'
titles have no local file), `database is locked` during library scans, and
"No space left on device" on 2026-11-14.

## Rules that are easy to break

- Never install into or restart the owner's live servers without their go-ahead in
  the conversation, and never while someone is watching (check `/Sessions`).
- Never commit real addresses, domains, names or passwords. Examples use
  `203.0.113.x`, `example.com`, `friend.example`.
- Never delete files or folders the session did not create.
- No CSS gradients; every colour the web script adds comes from `--fed-*` tokens.
- No new setting where the plugin can decide by itself.
- Release after each finished phase; say plainly what was not checked on real
  devices.
