# Jellyfin plugin for federation.

Disclaimer: this fork has been primarily written by AI, and has not had a human-review: use at your own risk.

## Jellyfin compatibility

- **Jellyfin 12.x:** Federation 0.0.160, built for .NET 10. Legacy authorization can stay disabled.
- **Jellyfin 10.11.x:** keep Federation 0.0.135. The 12.x DLL cannot load on 10.11.
- Existing friend connections and plugin configuration are retained. Jellyfin 12 with 0.0.160 can connect to Jellyfin 10.11 with 0.0.135; sync and Direct/Proxy playback are verified in both directions. Friends on 10.11 do not need to upgrade together.

## Installation
1. Add https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/manifest.json to your Jellyfin plugin repositories
2. Install the Jellyfin Federation plugin
3. Restart Jellyfin, then open Dashboard → Plugins → Federation.

## Aims
The goal of this plugin is to sync Jellyfin servers together, merging connected servers' libraries together as seemlessly as possible. This is NOT a true federation plugin. It does not let you federate with unknown public jellyfin servers.

## Playing a friend's title

When one of your viewers plays a friend's title that has to be converted for their device,
the friend's server converts its own copy and streams it straight to the player. The viewer
stays signed in to your server, which keeps their progress and history; the video never passes
through your server. If the friend's server can't (older plugin, switched off, busy, not
reachable from the player), your server converts it as before. Settings: Advanced tab →
"Who converts friends' video", and per friend on the Friends tab.

Rules a friend sets for one of your users (block, only some libraries or titles, rating limit)
hide those titles from that user everywhere, not only at playback. Unsharing a library removes
its titles from the friend right away.

## Downloading a friend's title to your device

Any viewer allowed to download (Jellyfin's own "Allow media downloading" user setting) gets the
normal Download button on a friend's movies, episodes and music, in the web client and in apps
such as Jellyfin for Android, Findroid and Swiftfin. The file comes straight from the friend's
server with its real file name; if that isn't possible (an older friend, or an https/http
mismatch) your server passes it through instead, and an interrupted download can resume. The
friend stays in control: "Allow this friend and their viewers to download my titles" on their
Friends tab, and per-viewer rules, turn it off. Without bulk permission each viewer can save
30 titles an hour from that friend.

## Friends' favorites

Your Favorites tab gets a "Favorites on (friend)" row per friend with the titles that friend's
users favorited: titles only, never who, and only titles you can already open. Your server does
the same for your friends. Both directions can be turned off under Advanced → "Friends' favorites".

## Plex
Plex owners use the separate [Federation Companion](Companion/README.md). Its setup guide covers library consent, friend connections, playback diagnostics, and the media mount required for Jellyfin imports.

### Companion app permissions and unsigned Windows downloads

Before installing the Plex Companion, read its [permissions, Windows security notices, Linux setup and removal guide](Companion/README.md#windows-app-tray-and-permissions). It runs a local background dashboard and saves private access tokens. The read-only media helper, sign-in startup and remote sharing are optional. Windows builds are unsigned: investigate Defender detections; do not disable protection or add blanket antivirus exclusions. Release installers verify published SHA-256 checksums.
