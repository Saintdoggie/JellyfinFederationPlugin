# Federation Companion

Companion connects a Plex owner with private Jellyfin Federation friends. Each owner chooses what to share. **A friend's federated imports must never be shared onward as if they were local media.**

Plugin 0.0.133 and the matching Companion rolling build include the Plex
mount repair. See [TODO.md](TODO.md) for remaining Windows/Funnel/client checks.

## Plex → Jellyfin

1. Run Companion on the Plex owner's computer and unlock it with the owner key shown at startup.
2. Sign into Plex and select a server you own, or enter its local address and token. Companion uses a tested local upstream connection; a remote friend connects through the public Companion relay.
3. Select your local libraries to share. Imported libraries cannot be selected for onward sharing.
4. Configure a public HTTPS Companion address, usually Tailscale Funnel. Funnel must point to Companion's listening port, not directly to Plex. Your friend does not need to join your tailnet.
5. Send a share request to the friend's Jellyfin address or generate a connect code. A Funnel code contains a short-lived claim token; after claiming, each friend gets a separate revocable relay credential.
6. Use **Test playback connection**. It reads actual video bytes and checks Range seeking, separately from listing the catalog. A successful local check does not prove the friend's network can reach the Funnel.

Switching Plex servers resets library sharing choices because different servers can reuse the same section IDs. Removing a friend revokes its Companion relay credential. Direct Plex connections created by older versions have different credential/consent boundaries; reconnect through Companion to use its current sharing controls.

## Jellyfin → Plex on Windows

Plex may match the title and poster of an imported `.strm` file while showing **Video: None / Audio: None**. That file contains a text URL, not video bytes Plex can analyze. The repair uses a read-only media mount so Plex reads the real media, including video/audio tracks and byte ranges.

1. Update the source Jellyfin Federation plugin to 0.0.133 (or later) and Companion from the rolling `companion-latest` installer.
2. On the Plex computer, install [WinFsp](https://winfsp.dev/rel/) and [rclone](https://rclone.org/downloads/). Put `rclone.exe` beside Companion or on PATH. Use the same Windows account for Plex and Companion; elevated and ordinary user sessions can see different mounts.
3. Paste a code from the source Jellyfin's Companion tab. Click **Choose libraries**, select the libraries to import, and confirm the selection. New source libraries will not automatically be selected for newly connected peers.
4. Open **Set up playable media in Plex** and click **Start media mount on this computer**. Companion creates its own `plex-media` mount and restores it after restarting. Keep Companion running during Plex scans and playback.
5. Click **Add to Plex** for the friend. The new Movies/Shows libraries have `(Streaming)` in their names. Let Plex scan them, then verify video/audio details and playback.
6. Once the new libraries work, remove the old `.strm` library entries in Plex. Companion does not delete those Plex entries automatically.

See [rclone's Windows mount requirements and account-visibility notes](https://rclone.org/commands/rclone_mount/#installing-on-windows). Windows-specific runtime validation remains in TODO; local Linux tests do not replace testing on the actual friend's Windows host.

## Manual mounts, Linux/macOS, and Docker

The advanced setup can download a private `companion-rclone.conf`. Its token permits read access to imported media, so keep it private. Use a Companion address reachable from the mount machine, then download the configuration again after changing that address.

Example for an empty Linux mount directory:

```sh
rclone mount companion: /path/to/empty-folder --config companion-rclone.conf --read-only --vfs-cache-mode full --vfs-cache-max-size 2G --dir-cache-time 30s
```

Example for an unused Windows drive letter:

```powershell
rclone mount companion: X: --config companion-rclone.conf --read-only --vfs-cache-mode full --vfs-cache-max-size 2G --dir-cache-time 30s
```

Use the platform prerequisites in the [rclone mount documentation](https://rclone.org/commands/rclone_mount/). On Linux, Plex running under a different user may need `--allow-other` and the corresponding FUSE configuration. The app's local-mount button does not change system FUSE permissions. Docker needs the mounted filesystem visible inside both the Plex and Companion containers; configure mount propagation or mount before creating the containers. Enter the path each container sees, and verify Plex can read it before relying on a scan.

## Sync, removal, and troubleshooting

- Sync runs at startup and every 30 minutes; **Sync now** runs the same path. Successful syncs add/remove media based on selected, currently shared source libraries. Source failures preserve the last committed catalog.
- **Source titles and import issues** shows exact source season/episode numbers and explains why an item lacks usable media information. Combined episodes preserve their source episode range. The importer does not infer numbering from release dates.
- If a title comes from another federated server, it must not appear in the outgoing catalog. Upgrade the source plugin and sync existing imports to remove previously forwarded entries. Receiving-side filtering also protects against older peers returning a `FederationKey`.
- Removing an import disconnects its mounted media immediately. You can separately remove only Companion-owned legacy links; unrelated files are preserved. Plex's scan/trash settings govern its remaining unavailable database entries.
- A source title can differ from Plex's matched title if their episode-order settings differ. Compare source numbering before changing anything; see [Plex episode ordering](https://support.plex.tv/articles/naming-and-organizing-your-tv-show-files/#toc-1). Do not renumber third-party libraries to work around onward-sharing bugs.
- An offline mount and an empty catalog are different conditions. Configure Plex's automatic trash behavior appropriately for an occasionally unavailable network filesystem.

## Running and state

Published install commands remain:

```sh
curl -fsSL https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.sh | bash
```

```powershell
irm https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.ps1 | iex
```

To run a source build:

```sh
dotnet run --project Companion/FederationCompanion.csproj
```

Open the listener URL with the `#access=...` owner key printed at startup. Browser owner APIs require `X-Companion-Admin`. State is in `companion-state.json` beside the executable; it contains credentials and is restricted to the Unix owner where supported. Keep state/private mount configuration when updating, and never publish them.

The WebDAV mount uses a separate read-only credential. Item streams request fresh authorization from the actual content owner. New Funnel claims do not include the real Plex token as an automatic fallback. The legacy unsigned `/import-stream` endpoint is retired.
