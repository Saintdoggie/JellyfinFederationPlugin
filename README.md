# Jellyfin Federation



Disclaimer: this fork has been primarily written by AI, and has not had a human review: use at your own risk.

Jellyfin Federation connects your Jellyfin server with your friends' servers. Their libraries
show up in yours and play from their server, with no copying or syncing of files.

This is NOT true federation: it does not connect you to unknown public Jellyfin servers. You only
connect with servers whose admins you send a friend request to (or accept one from).

Current release: **0.0.193** for Jellyfin 12.

## Features

**Browsing and playing**
- Friends' movies, shows and music appear in your libraries, kept up to date automatically.
- You choose what each friend can see of your library, and can set rules per viewer (block,
  only some libraries or titles, rating limit). Hidden titles are hidden everywhere, not just at playback.
- When a friend's title needs converting for a device, the friend's server converts its own copy
  and streams it straight to the player. Your viewer stays signed in to your server, which keeps
  their progress and history.
- A friend's title plays only from that friend while it converts for you; if the friend is busy
  or can't play it, the player says so. Your server converts instead only when the friend has
  converting switched off or runs an older version.
- Seeking and starting are quick when a friend converts: your server starts the friend's conversion early,
  and a friend's conversion log for each play shows in your Dashboard → Logs.

**Downloads**
- Download to your device: viewers allowed to download get the normal Download button on
  friends' titles (web, Jellyfin for Android, Findroid, Swiftfin). The friend can turn this off,
  and without bulk permission each viewer can save 30 titles an hour from a friend.
- Download to your server (admins): copy a friend's title into your Movies or Shows library
  (the default), another library, a new folder, or a separate "Federation Downloads" library.
  Files are named so Jellyfin identifies them. Downloaded copies are never shared on to other friends.

**Watching together**
- Watch parties across servers using Jellyfin's group watching (SyncPlay): choose "New group with
  friends" or "Invite friends". Everyone plays their own server's copy; play, pause and seeking are shared.
- Friends' favorites: a "Favorites on (friend)" row per friend (titles only, never who favorited them).
- Friends' viewers watching your titles show on your dashboard under Active devices.
- Playback Info in the player has a "Federation" section showing which friend, how the video
  travels and who converts it.

**Optional**
- Hardware sharing: servers can share their conversion hardware and let it decide who converts
  a friend's title. Off by default (Advanced tab).
- Pools: groups of servers that all federate with each other.
- Plex: bring Plex libraries into Jellyfin from the Plex tab, or use the separate Companion app (see below).

Most features can be switched off under the plugin's Advanced tab.

## Requirements

- Jellyfin 12.x for the current release (0.0.193, built for .NET 10).
- Jellyfin 10.11.x: use 0.0.135 (the last 10.11 build). The 12.x plugin cannot load on 10.11.
- Your server must be reachable by your friends (port forwarding to your own domain, or Tailscale
  with Funnel; the Friends tab helps set this up).

## Installation

1. In Jellyfin, go to Dashboard → Plugins → Repositories and add:
   `https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/manifest.json`
2. Install the **Jellyfin Federation** plugin from the catalog.
3. Restart Jellyfin, then open Dashboard → Plugins → Federation.

Existing friend connections and settings are kept when you update.

## Adding a friend

1. Open the **Friends** tab and choose **Create new friend request**.
2. Enter your friend's server address and pick what they can see of your library.
3. Send the request. Once your friend accepts it on their server, both servers add each other
   automatically and their titles appear after the first sync.

Manage a friend from the Friends tab to change what they can see, set per-viewer rules, or
adjust connection settings.

## Compatibility between friends

Friends don't have to upgrade at the same time; basic sync and playback keep working with older
friends. Newer features need **both** servers on at least:

| Feature | Both servers need |
| --- | --- |
| Friend's server converts and streams directly to the player | 0.0.187 |
| Direct device downloads and friends' favorites | 0.0.188 (older friends: downloads go through your server) |
| Watch parties across servers | 0.0.189 |
| Friends' viewers on your dashboard, hardware sharing | 0.0.190 |
| Smooth seeking and faster start when the friend converts, the friend's conversion logs in your Dashboard → Logs | 0.0.193 |

## Troubleshooting

- **A friend's titles disappeared:** the friend is probably unreachable. Their titles are hidden
  (not deleted) until they come back. Use **Check now** on the Friends tab.
- **A friend's title won't convert or play:** check the player's Playback Info → Federation section
  for which server converts and why. The friend may be at its limit or unable to
  play that title on its side.
- **A new feature doesn't work with one friend:** check both servers' plugin versions against the
  table above.
- More detail is in the plugin's **Log** tab and in Jellyfin's server logs.

## Plex Companion

Plex owners use the separate [Federation Companion](Companion/README.md). Its setup guide covers
library consent, friend connections, playback diagnostics, and the media mount required for Jellyfin imports.

Before installing it, read its [permissions, Windows security notices, Linux setup and removal guide](Companion/README.md#windows-app-tray-and-permissions).
It runs a local background dashboard and saves private access tokens. The read-only media helper,
sign-in startup and remote sharing are optional. Windows builds are unsigned: investigate Defender
detections; do not disable protection or add blanket antivirus exclusions. Release installers
verify published SHA-256 checksums.

## Credits and license

Thanks to Stephan Voeth (@svoeth) for the faster, more reliable sync in 0.0.186.

MIT License. See [LICENSE](LICENSE).

## Disclaimer

This fork has been primarily written by AI and has not had a human review. Use it at your own risk.
