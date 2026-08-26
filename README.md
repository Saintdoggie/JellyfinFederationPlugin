# Jellyfin plugin for federation.

Disclaimer: this fork has been primarily written by AI, and has not had a human-review: use at your own risk.

## Installation
1. Add https://raw.github.com/Saintdoggie/JellyfinFederationPluginManifest/main/manifest.json to your Jellyfin plugin repositories
2. Install the Jellyfin Federation plugin
3. TODO

## Aims
The goal of this plugin is to sync Jellyfin servers together, merging connected servers' libraries together as seemlessly as possible. This is NOT a true federation plugin. It does not let you federate with unknown public jellyfin servers.

Federation is not limited to Jellyfin-to-Jellyfin: a pluggable catalog provider architecture also lets you federate with a friend's **Plex** server, syncing their library into your Jellyfin instance (including HDR/Dolby Vision metadata, full audio tracks, and cover art) without them needing to run Jellyfin at all.

## Federation Companion

For a Plex-owning friend who doesn't want to hand over a raw API token, [Federation Companion](Companion/README.md) is a standalone app they run on their own machine: it walks them through Tailscale setup, signs into Plex themselves, lets them pick which libraries to share, and generates a one-time connect code that links your Jellyfin plugin automatically.

```bash
curl -fsSL https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.sh | bash
```

```powershell
irm https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.ps1 | iex
```

See [Companion/README.md](Companion/README.md) for the full walkthrough and screenshots.

