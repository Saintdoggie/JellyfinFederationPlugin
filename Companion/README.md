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

1. On the Plex computer, install Companion with `install.ps1` (or the in-app Update). Open the dashboard and review **Set up / start media folder**. Accepting enables rclone and, if needed, requests the Windows media driver installation. Use the same Windows account for Plex and Companion.
2. Sign in, then paste a code from the friend's Jellyfin Federation Companion tab. Click **Choose libraries**, select the libraries to import, and confirm. Keep Companion running while Plex scans or plays.
3. Click **Add to Plex** for the friend. The new Movies/Shows libraries have `(Streaming)` in their names. Let Plex scan them, then verify video/audio details and playback.
4. Once the new libraries work, remove the old `.strm` library entries in Plex. Companion does not delete those Plex entries automatically.

See [rclone's Windows mount requirements and account-visibility notes](https://rclone.org/commands/rclone_mount/#installing-on-windows). Windows-specific runtime validation remains in TODO; local tests do not replace testing on the actual friend's Windows host.

## Windows app, tray, and permissions

The Windows download is a desktop app (`WinExe`), not a console program. It starts in the
notification area (tray) and keeps running in the background so Plex and friends can reach
it at any time. Double-click the tray icon (or the Start Menu/Desktop shortcut) to open the
dashboard in your browser.

**Windows builds are currently unsigned.** SmartScreen can show an unrecognized-app warning because a new or unsigned build lacks reputation. This is different from Defender reporting a specific malware detection. We cannot promise that a detection is a false positive. Do not disable Defender or add an exclusion for the whole install folder.

Download only from this repository's [Companion release](https://github.com/Saintdoggie/JellyfinFederationPlugin/releases/tag/companion-latest). New releases include `SHA256SUMS`; the installers check it before extraction. You can compare a manual download with `Get-FileHash .\FederationCompanion-win-x64.zip -Algorithm SHA256`. A checksum checks consistency with that release, not publisher identity or absence of malware. If a file is flagged, stop and report the release revision, hash and detection name privately to the maintainer; submit suspected false detections to [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission). Never attach your state file or tokens.

[Microsoft's SmartScreen developer guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation) explains reputation and signing. Publisher signing is still a release task; this project does not claim signing eliminates every warning.

| Capability | When used | What changes |
| --- | --- | --- |
| Local dashboard | While Companion runs | Listens on IPv4 loopback, normally port 5000. Explicit `--urls` or `ASPNETCORE_URLS` can change the address. |
| Plex account and friends | When you connect them | Contacts plex.tv, the selected Plex server and the friends you connect. Saves access tokens and selected libraries. |
| Read-only media folder | After **Set up / start media folder** | Runs rclone; downloads the pinned, checksummed helper from downloads.rclone.org if missing. Windows may ask for UAC to install the pinned WinFsp driver from GitHub. Linux needs FUSE. |
| Playback cache | While scanning or playing imported media | Writes `media-cache/`. Cleanup targets 2 GB and removes old closed files after one hour; open files can exceed the target. Memory buffering is 4 MB per open file, in addition to process/catalog overhead. |
| Start at sign-in | Only after you enable the toggle | Windows: current user's HKCU Run entry. Linux desktop: XDG autostart entry. Disable using the same toggle. |
| Remote access | Only after you configure it | Tailscale Funnel exposes the Companion relay. Owner APIs still require the owner key; friends use their own revocable credentials. |
| Updates | Dashboard checks; owner requests installation | Contacts GitHub; replaces app binaries and restarts. Installers create shortcuts. No antivirus exclusions or firewall exceptions are added. |

`companion-state.json` and `media-mount.conf` contain credentials **in plaintext**. Companion restricts them to the current Windows user with file ACLs or the Unix owner with mode 0600, before writing secret content. Administrators and software running as your account remain able to read them. Install in a private user folder on a filesystem that supports permissions. Never upload these files, `imported/`, or the media cache to GitHub.

`companion.log` rotates at 2 MB with one previous file. Startup and lifecycle entries omit keys; exception entries record the exception type instead of upstream URLs. Interactive Linux/macOS launches show the owner key in the terminal; background/redirected launches omit it. Copy owner key in the Windows tray is an explicit clipboard action.

Companion runs as your normal user. The optional Windows WinFsp driver needs administrator approval; denying it leaves the dashboard available. **Stop media folder** persists until you start it again, including across Companion restarts. Exiting Companion stops its owned helper. Closing the browser leaves it running.

### Linux desktop and Plex server

The Linux x64 build already runs the same Plex bridge. The installer now adds **Federation Companion** to your application menu and opens its browser dashboard without keeping a terminal open. Enable **Start Companion when I sign in** for XDG autostart. There is currently no Linux tray icon; use the application launcher to reopen the running dashboard. GNOME/KDE login and desktop integration still need native validation.

Linux ARM builds are not published yet; the installer rejects unsupported architectures rather than downloading x64. A future ARM release also needs an ARM rclone bundle and playback validation.

For a headless server, run `./FederationCompanion --no-browser` interactively for setup. Keep loopback listening and use an SSH tunnel (`ssh -L 5000:127.0.0.1:5000 your-server`) to reach the dashboard at `http://127.0.0.1:5000`; substitute the actual listening port. Copy the owner key from your private terminal. Do not expose the owner dashboard on a public HTTP listener.

For persistent server operation, create `~/.config/systemd/user/federation-companion.service` (adjust the executable path if you chose another install directory):

```ini
[Unit]
Description=Federation Companion for Plex and Jellyfin

[Service]
Type=simple
ExecStart=%h/FederationCompanion/FederationCompanion --background
Restart=on-failure
RestartSec=10
TimeoutStopSec=30
UMask=0077

[Install]
WantedBy=default.target
```

Then run `systemctl --user daemon-reload` and `systemctl --user enable --now federation-companion`. Choose the systemd service or desktop autostart, not both. For an in-app binary update, stop the user service, launch Companion interactively and update, then exit that copy and start the service again. Running across logout requires your administrator's user-lingering policy. A user service does not grant Plex access to the mount.

Plex commonly runs as a separate `plex` service account on Linux. Follow [rclone's FUSE mount requirements](https://rclone.org/commands/rclone_mount/#mounting-on-linux) and the manual-mount instructions below. `--allow-other` exposes the mount to other local users and must be an explicit administrator decision; Companion does not edit `/etc/fuse.conf`, change Plex's account or weaken your home-directory permissions. Verify actual playback using Plex's account before relying on a scan.

### Removing Companion

Disable **Start Companion when I sign in**, then choose **Exit Companion**. If you configured systemd, disable and stop that user service first and remove its unit. Remove the install folder and Start Menu/Desktop shortcut (Windows) or `~/.local/share/applications/federation-companion.desktop` (Linux; use your XDG data directory if customized). Removing the install folder also removes saved credentials and cache. Keep a private backup if you intend to reconnect later. WinFsp and Tailscale are shared system components: remove them with the OS's normal uninstall tools only if other applications do not need them. Remove imported library entries separately in Plex.

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
dotnet run --project Companion/FederationCompanion.csproj -f net9.0
```

Open the listener URL with the `#access=...` owner key printed at startup. Browser owner APIs require `X-Companion-Admin`. State is in `companion-state.json` beside the executable; it contains credentials and is restricted to the current Windows user or Unix owner. Keep state/private mount configuration when updating, and never publish them.

The WebDAV mount uses a separate read-only credential. Item streams request fresh authorization from the actual content owner. New Funnel claims do not include the real Plex token as an automatic fallback. The legacy unsigned `/import-stream` endpoint is retired.
