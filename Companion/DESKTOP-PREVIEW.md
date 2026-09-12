# Desktop and two-way sharing preview

Desktop preview developed on 2026-09-12. Published separately from the stable
catalog, with existing assembly versions retained.

Home guides the next setup step. Friends shows what leaves your Plex and what
comes back from Jellyfin; Incoming manages library choices, sync, and the media
folder. Settings includes Plex sign-in, Tailscale setup and background startup.
Windows setup uses buttons and links, with manual options tucked away. Status
refreshes preserve unsaved library choices. Once the media folder is ready,
saving an import selection also attaches eligible libraries to Plex.

The Windows build opens a real taskbar window using Microsoft WebView2. The
HTML/CSS/JavaScript dashboard is embedded in the executable; a separate wwwroot
folder is not required on Windows. Closing the window releases its renderer and
keeps the tray, listener and media folder available. Normal launch, tray Open
and a second launch reopen the same window. The existing installer creates the
shortcuts; the .NET runtime is included in the Windows publish. Microsoft's
WebView2 Runtime is also required. Missing-runtime recovery offers the official
installer page and a browser fallback. See Microsoft's
[WebView2 distribution documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

The window accepts only its own loopback dashboard. Plex sign-in opens in the
default browser. External protocols, frames and unsolicited remote downloads
are blocked; an explicitly downloaded local mount configuration uses a Save
File dialog. Native Windows runtime behavior remains unverified here.

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

## Source posters and media details

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
- Native Windows/WebView2/WinFsp, the friend's actual Funnel and Plex clients,
  Plex sign-in popup behavior and the complete two-server ordinary-user/admin
  release matrix remain open. Cross-compilation is not Windows runtime testing.

Local preview archives are intentionally separate from the stable release assets
and keep the existing assembly version. Do not replace companion-latest or the
plugin manifest with these until the remaining platform checks are complete.
The QA publisher sets `CompanionReleaseChannel=preview`; these binaries refuse
rolling in-app updates to prevent downgrading to the older stable experience.
