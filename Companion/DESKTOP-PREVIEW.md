# Desktop and two-way sharing preview

Native desktop and Plex-to-Plex preview updated on 2026-09-13. Published separately from the stable
catalog, with existing assembly versions retained.

Home guides the next setup step. Friends shows what leaves your Plex and what
comes back from Jellyfin; Incoming manages library choices, sync, and the media
folder. Settings includes Plex sign-in, Tailscale setup and background startup.
Windows setup uses buttons and links, with manual options tucked away. Status
refreshes preserve unsaved library choices. Once the media folder is ready,
saving an import selection also attaches eligible libraries to Plex.

The Windows build now uses native WinForms controls for navigation, friend requests,
library consent, imports, Plex sign-in, Tailscale, startup and app settings. It does
not load HTML or use WebView2, Electron, or a browser renderer. The optional browser
admin page remains available separately. The self-contained executable includes
the .NET runtime; it no longer needs the WebView2 Runtime.

The native palette is black and charcoal with white text. Service labels and the
active navigation marker use small Plex yellow or Jellyfin purple accents. No
gradients. View changes settle over 150 ms, advanced sections expand over 180 ms,
and service accents transition briefly. Animations stop when complete and honor
Windows' [client-area animation setting](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow).
Per-monitor DPI scaling is enabled; native rendering still needs Windows validation.

Closing releases the native UI while keeping the tray, listener and media folder
available. Plex account approval opens the owner's normal browser. Desktop API
commands only target this process's loopback port, authenticate as the owner and
never follow redirects. Failed actions remain retryable, background status does
not redraw forms, and unsaved choices on other sections survive a save.

## One friend connection, two directions

1. In Companion, connect the owner's Plex server and switch on the locally owned
   libraries to share. Private and imported libraries are labelled separately.
2. Enter the Jellyfin friend's server address and send a request. In Jellyfin,
   accept it under Federation → Companion.
3. Jellyfin offers a return connection through the already accepted Companion
   relay. New Plex friends start with no outgoing Jellyfin libraries shared.
   Under Friends → Manage → Sharing, the Jellyfin owner selects their own libraries.
4. In Companion, follow the return-import link on that same friend card. Refresh
   available libraries, select the ones to import, and save/sync. Set up the media
   folder and choose Add to Plex. No second code is required.

Both sides need this preview for automatic return setup. Existing connections
can use **Set up / repair two-way sharing** on the Jellyfin friend card. Retrying
preserves library choices and Plex IDs; a matching previous code-based import
is adopted. It does not select new/future libraries. Direct Plex Remote Access
connections without a Companion relay keep the separate import-code flow.

The return endpoint authenticates the existing peer token; credentials stay in
server-side state. Delivery derives its destination from the accepted relay URL,
uses public-address connection validation, and does not follow redirects. A return
offer itself makes no outbound request and cannot start an import. Source identity
or address changes require reconnecting. Disconnecting the Companion friend also
removes its linked import. Existing ownership and stream-time revocation checks
remain authoritative; third-party imported media cannot be shared onward.

## Plex Companion ↔ Plex Companion

Both owners install this preview, connect their own Plex, save public HTTPS
Companion addresses and select locally owned libraries to share. In Friends,
choose **Plex Companion**, enter the friend's Companion address and send a request.
The receiving owner accepts in Friends. The sender checks requests (also checked
in the background). Both incoming connections initially have no libraries selected.
Each owner opens Incoming, refreshes available libraries, chooses what to import,
and saves/syncs. Start the media folder and attach the imports to Plex.

Requests are idempotent, expire after a day, and can be declined/cancelled. Incoming
requests never fetch an offered URL until the owner accepts and chooses imports.
Pending peers cannot read media. Companion-to-Companion connections require public
DNS addresses and reject redirects. Stream grants expire after ten minutes and
recheck current ownership/sharing on every byte request. Revoking a friend or
unsharing a library also blocks previously minted grants.

Plex imports use the existing read-only media mount. They include owned movies and
episodes with complete single-part files; multi-part video is skipped rather than
presenting one part as a complete movie. Imported Plex sections cannot be shared
onward. Plex analyzes the mounted bytes for video/audio quality. Custom poster
repair described below applies to receiving Jellyfin; Plex still controls its own
metadata matching for mounted imports.

## Source posters and media details

Jellyfin primary/backdrop artwork now uses the source's scoped image endpoint too.
Existing missing files and posters overwritten by another image provider are
repaired during Federation refresh. If multiple Plex servers offer the same movie,
its native item ID and artwork revision remain attached to the exact source, so
one server's poster ID cannot select a different movie on another server.

Plex artwork paths contribute credential-free revision hashes to the catalog.
Reconciliation fetches the source's current primary/backdrop images, including
custom uploads, and saves them through Jellyfin's image storage. It backfills
existing items and retries failures; unchanged successful artwork is skipped.
No item deletion/recreation is needed. Real source container/stream changes now
refresh existing media information, and title/plot/year/rating/genre/studio
corrections also reach existing series and movies.

Plex detail fetches overlap up to four requests within each page. Output order
and series-before-episode processing are preserved. Catalog fetch failure still
preserves the last successful catalog. This is not a measured speedup on the
friend's actual server or a fix for the separately reported stuttering route.

## Validation and limits

- Focused regressions: return consent, repair/adoption, changed identity,
  credential destination, disabled peers, navigation, source poster revisions,
  image-download failure, metadata idempotence and bounded detail requests.
- Full automated gate: plugin, Companion and UI suites twice; Windows x64 GUI
  cross-publish; disposable Jellyfin → Companion/rclone → Plex playback gate.
- Real Chromium fixture at 390, 1180 and 1920 pixels: no page errors or horizontal
  overflow; return libraries start unchecked; source text renders safely.
- Native Windows/WinFsp, the friend's actual Funnel and Plex clients,
  Plex sign-in popup behavior and the complete two-server ordinary-user/admin
  release matrix remain open. Cross-compilation is not Windows runtime testing.

Local preview archives are intentionally separate from the stable release assets
and keep the existing assembly version. Do not replace companion-latest or the
plugin manifest with these until the remaining platform checks are complete.
The QA publisher sets `CompanionReleaseChannel=preview`; these binaries refuse
rolling in-app updates to prevent downgrading to the older stable experience.
