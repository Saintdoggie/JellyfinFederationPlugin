# Jellyfin plugin for federation.

Disclaimer: this fork has been primarily written by AI, and has not had a human-review: use at your own risk.

## Jellyfin compatibility

- **Jellyfin 12.x:** Federation 0.0.136, built for .NET 10. Legacy authorization can stay disabled.
- **Jellyfin 10.11.x:** keep Federation 0.0.135. The 12.x DLL cannot load on 10.11.
- Existing friend connections and plugin configuration are retained. Jellyfin 12 with 0.0.136 can connect to Jellyfin 10.11 with 0.0.135; sync and Direct/Proxy playback are verified in both directions. Friends on 10.11 do not need to upgrade together.

## Installation
1. Add https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/manifest.json to your Jellyfin plugin repositories
2. Install the Jellyfin Federation plugin
3. Restart Jellyfin, then open Dashboard → Plugins → Federation.

## Aims
The goal of this plugin is to sync Jellyfin servers together, merging connected servers' libraries together as seemlessly as possible. This is NOT a true federation plugin. It does not let you federate with unknown public jellyfin servers.

## Plex
Plex owners use the separate [Federation Companion](Companion/README.md). Its setup guide covers library consent, friend connections, playback diagnostics, and the media mount required for Jellyfin imports.
