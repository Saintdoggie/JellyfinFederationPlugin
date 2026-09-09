# Jellyfin Federation — bug tracker

Source: `/var/home/cranky/Documents/JellyfinFederationPlugin-master` at **0.0.136**.
Reviewed 2026-09-08. Status is updated as fixes land. Do not treat this file as a release note.

Status: `open` | `in_progress` | `fixed` | `wontfix`

---

## A. Security / access

### A1 — Unauthenticated friend-request SSRF
- **Status:** fixed (code; pre-release policy — no version bump)
- **Severity:** high
- **Files:** `Services/FederationFriendService.cs` (~474–534, 2106–2118), `Configuration/ConfigValidator.cs`
- **What:** `POST /Friends/Request` is anonymous. `ReceiveFriendRequestAsync` accepts any `http(s)` `FromServerUrl` (loopback, RFC1918, link-local, metadata IPs) and immediately GETs `{url}/Plugins/Federation/Friends/Outgoing/{id}`. Default `HttpClient` follows redirects.
- **Fix:** Reject private/loopback/link-local/CGNAT/metadata hosts for untrusted callback URLs; do not follow redirects on the verify GET; keep LAN URLs valid for admin-initiated `SendFriendRequest`. Add tests.

### A2 — Peer POSTs trust `FromFederationId` instead of the token
- **Status:** fixed (0.0.137 catalog)
- **Severity:** high
- **Files:** `Configuration/FederationPluginController.cs` (`Friends/RemoteUserRules`, `Pools/Notice`, `Pools/InviteNotice`), `Services/FederationFriendService.cs`
- **What:** Endpoints only check that *some* federation token is valid, then apply the body to whichever friend matches `payload.FromFederationId`. `Friends/Unfriend` already binds identity to `ResolveCaller()`.
- **Fix:** Always apply the authenticated caller’s federation id. Ignore or reject a mismatched body id. Tests for confused-deputy.

### A3 — Proxy download flag is unsigned; no `EnableContentDownloading` check
- **Status:** fixed (0.0.138 catalog)
- **Severity:** high
- **Files:** `Services/FederationLibraryManager.cs` (HMAC), `Configuration/FederationPluginController.cs` (`Stream`, `GetDownloadUrl`)
- **What:** HMAC payload is server/item/audio/user only. Anyone with a play URL can add `download=true`. `GetDownloadUrl` is `[Authorize]` only and does not check Jellyfin’s per-user download policy.
- **Fix:** Bind `download` into the HMAC; reject unsigned `download=true`. Honor `EnableContentDownloading` (and incoming download rules) on `GetDownloadUrl`. Tests.

### A4 — Session/image tokens are library-wide bearers on client URLs
- **Status:** fixed (0.0.139 catalog)
- **Severity:** high
- **Files:** `Services/FederationMediaSourceProvider.cs` (~500–519), `Providers/FederationImageProvider.cs` (~148–163), `Configuration/FederationPluginController.cs` (`DirectStream`, `IsStreamTokenAuthorized`)
- **What:** Direct-mode `Path` prefers a 6-hour session token that `DirectStream` accepts for any currently visible item. Image provider mints a playback token with no acting user and puts it on `<img>` URLs; that token can fetch the full file.
- **Fix:** Direct-mode client Path must use an item-scoped playback token, not a session token. Image URLs must use a purpose-scoped (non-stream) capability. Re-check visibility at stream/image time. Tests.

### A5 — Downloaded federated files become re-shareable as local media
- **Status:** fixed (0.0.140 catalog)
- **Severity:** high
- **Files:** `Services/FederationDownloadService.cs` (~456–482, 787–803), `Services/FederationPeerAccessService.cs`
- **What:** After download-to-server, the virtual item is deleted and `FederationKey` is gone. Outgoing sharing then treats the file as this server’s own media.
- **Fix:** Stamp downloaded copies so they are never eligible for outgoing peer catalog/playback. Tests that a downloaded friend title is not visible to another friend.

### A6 — Anonymous inventory endpoints leak library membership
- **Status:** fixed (code; pre-release policy — no version bump)
- **Severity:** medium
- **Files:** `Configuration/FederationPluginController.cs` (`FederatedIds`, `Sharing/DisabledIds`), `Web/federation-badge.js`
- **What:** Unauthenticated callers get every federated item GUID plus friend display names, and globally-hidden item ids.
- **Fix:** Require a logged-in Jellyfin session (same auth the badge script already sends). Keep responses ids-only. Tests.

### A7 — HMAC stream capabilities never expire
- **Status:** fixed (code; pre-release policy — no version bump)
- **Severity:** medium
- **Files:** `Services/FederationLibraryManager.cs` (`CreateProxySignature` / `ValidateProxySignature`)
- **What:** Payload is `v1` + server + item + audio + user. No `exp`. A leaked static Path works until the friend’s `ApiKey` is rotated.
- **Fix:** Include an expiry in the HMAC (e.g. 24h). Accept current signatures during a short grace window if needed so in-flight ffmpeg does not die. Tests for expiry and still-valid signatures.

### A8 — Unbounded inbound friend requests; peer pool icons uncapped
- **Status:** fixed (code; pre-release policy — no version bump)
- **Severity:** medium
- **Files:** `Services/FederationFriendService.cs` (`ReceiveFriendRequestAsync`, `ReceivePoolNotice`, `ReceivePoolInviteNotice`, `SetPoolIconAsync`)
- **What:** Plex offers cap at 25/24h; friend requests do not. Local pool icon upload rejects `>150_000` chars; peer notices copy `IconBase64` with no limit.
- **Fix:** Cap pending incoming friend requests (same order of magnitude as Plex offers). Enforce the icon size cap on inbound notices. Tests.

---

## B. Correctness

### B1 — Save drops auto-managed mappings for unreachable friends
- **Status:** fixed (code; pre-release policy — no version bump)
- **Severity:** high
- **Files:** `Configuration/configPage.html` (`loadLibraryPicker`, `buildAutoMappings`, `saveConfiguration`)
- **What:** `GetRemoteLibraries` returns `success: true` with `error` + empty `libraries` per failed peer. Picker still sets `pickerLoaded = true`. Save rebuilds AutoManaged mappings from `pickerLibs` only.
- **Fix:** Keep existing AutoManaged sources for servers that failed to load. Do not auto-save a partial picker after accepting a friend. Tests in `Tests/federation-ui.test.js`.

### B2 — “Refresh now” always looks successful
- **Status:** fixed (code; pre-release policy — no version bump)
- **Severity:** high
- **Files:** `Configuration/configPage.html` (~5786–5793)
- **What:** Handler reads `res.Success` / `res.Message`. API returns camelCase `success` / `message`.
- **Fix:** Use `res.success` / `res.message` (and treat `success === false` as an error). Test.

### B3 — Config save wipes `CompanionUrl` and `FederationPluginVersion`
- **Status:** fixed (0.0.141 on fix/b3-preserve-companion-url; not on master yet)
- **Severity:** high
- **Files:** `Configuration/FederationPluginController.cs` (`UpdateConfiguration` preservation block, `SanitizeServer`)
- **What:** Main Save posts a subset of per-server fields. Preservation copies Kind/IssuedApiKey/sharing but not `CompanionUrl` or `FederationPluginVersion`. GET sanitizer also omits them.
- **Fix:** Preserve both on POST. Include non-secret versions (not tokens) on GET if the UI needs them. Tests.

### B4 — Catalog “hide from user” wipes `BlockedItemIds`
- **Status:** fixed (0.0.142)
- **Severity:** high
- **Files:** `Configuration/FederationPluginController.cs` (`SanitizeServer`), `Configuration/configPage.html` (catalog hide-from-user)
- **What:** GET omits `BlockedItemIds`. UI defaults to `[]` and POSTs a full replacement rule.
- **Fix:** Include `BlockedItemIds` in sanitized rules. Merge rather than replace if the UI still sends a partial list. Tests.

### B5 — Device download uses the conservative Path builder
- **Status:** fixed (0.0.143)
- **Severity:** high
- **Files:** `Services/FederationDownloadService.cs` (`GetDownloadUrl` ~336)
- **What:** Calls `BuildStaticPath(entry.ItemType, source)`, which returns null if any `FriendUserAccessRules` exist. Persistence already uses the entry-aware overload.
- **Fix:** Use `BuildStaticPath(entry, source)`. Keep tests that still block genuinely user-dependent items. Tests.

### B6 — Mapping collapse does not remap cache keys
- **Status:** fixed (0.0.144)
- **Severity:** high
- **Files:** `Services/FederationLibraryTargets.cs` (`Collapse`), `Services/FederationItemCache.cs`, `Services/FederationLibraryManager.cs`
- **What:** `Federated Movies` → `Movies`/`Shows` changes `MappingName` but cache keys stay `"{oldName}/..."`. Item ids hash the path, so IDs churn and old keys leak.
- **Fix:** Remap cache keys / `MappingName` when collapsing. Preserve `ComputeItemId` stability. Tests.

### B7 — Short catalog pages prune the rest of the library
- **Status:** fixed (0.0.145)
- **Severity:** high
- **Files:** `Services/FederationSyncService.cs` (~521–525, 745–748, 834–851)
- **What:** `FetchAndUpsertPagesAsync` treats `page.Count < pageSize` and the 1000-page cap as success, then prunes unseen ids. Per-item upsert exceptions are omitted from `seen`.
- **Fix:** Incomplete page / page-cap / cancelled upsert = source failure (do not prune). Tests.

### B8 — Companion “Connect local Plex” / visible-root 401
- **Status:** fixed (0.0.146)
- **Severity:** high
- **Files:** `Companion/wwwroot/index.html` (~749–775)
- **What:** Those buttons use bare `fetch()` without `X-Companion-Admin`. Routes are not public.
- **Fix:** Use `apiFetch()` (or send the owner header). Test in `Tests/companion-ui.test.js`.

### B9 — Funnel miss mints a raw Plex-token connect code
- **Status:** fixed (0.0.155; Funnel miss no longer embeds a Plex token)
- **Severity:** high
- **Files:** `Companion/Program.cs` (~487–541), `Companion/ConnectCodeFactory.cs`
- **What:** If Funnel is not this Companion, generate falls through to `direct` mode with `serverAccessToken`.
- **Fix:** Do not embed the standing PMS token when Funnel was expected and failed. Surface an error telling the owner to fix Funnel or explicitly choose Plex Remote Access. Tests.

### B10 — Plugin `.strm` export deletes every `.strm` under the root
- **Status:** fixed (0.0.148)
- **Severity:** high
- **Files:** `Services/PlexStrmExportService.cs` (~204–238)
- **What:** `RemoveStale` enumerates all `*.strm` recursively and deletes anything not rewritten this run. Companion exporter has an ownership manifest; this does not.
- **Fix:** Only delete files this plugin wrote (manifest or URL adopt). Never delete foreign `.strm`. Tests.

### B11 — Direct-mode WAN transcode never runs at stream time
- **Status:** open
- **Severity:** high
- **Files:** `Configuration/FederationPluginController.cs` (`DirectStream` ~2620), `Services/FederationStreamHandler.cs`, `Services/WanBandwidthMonitor.cs`
- **What:** Live DirectStream always uses `/Videos|Audio/{id}/stream?Static=true`. `BuildPlaybackUrl` (bitrate cap) is unused on that path. Gateway relay does not apply WAN cap.
- **Fix:** Apply the effective WAN cap to Direct-mode upstream (request a lower-bitrate transcode when classified WAN). Do not change LAN Direct. Tests.

### B12 — Stamped Path port can disagree with live Kestrel port
- **Status:** open
- **Severity:** medium
- **Files:** `Services/FederationLibraryManager.cs` (`GetInternalPlaybackBaseUrl`), `Services/FederationMediaSourceProvider.cs` (~456–558)
- **What:** Sync-time Path uses `127.0.0.1:8096`. Per-request provider URLs use the real `LocalPort`.
- **Fix:** Prefer `InternalServerUrl` or the live port; avoid advertising a dead 8096 static source when the provider already built a live URL. Tests.

### B13 — GET `/Pools` omits `IconBase64`
- **Status:** fixed (0.0.151)
- **Severity:** medium
- **Files:** `Configuration/FederationPluginController.cs` (`GetPools` ~1597–1609)
- **What:** Icons can be set but never returned, so the Pools tab is blank after reload.
- **Fix:** Return `IconBase64` on GET (size already capped locally). Tests.

### B14 — Windows update kills every `rclone.exe`
- **Status:** fixed (0.0.152)
- **Severity:** medium
- **Files:** `Companion/CompanionUpdater.cs` (~124–135), `Companion/install.ps1` (~31–36)
- **What:** `taskkill /F /IM rclone.exe` is any rclone on the machine. Tests currently assert this string.
- **Fix:** Stop only Companion-owned rclone (PID from `LocalMediaMountService` / mount marker). Update tests.

### B15 — Receiving-side per-user blocks do not walk ancestors
- **Status:** open
- **Severity:** medium
- **Files:** `Services/RemoteAccessControlService.cs` vs `Services/FederationPeerAccessService.cs` (`IsItemOrAncestorListed`)
- **What:** Owner-side hiding a series hides episodes. Receiver compares only the exact remote item id.
- **Fix:** Walk parent keys in the federation cache (series/season/episode) on the receiving side. Tests.

### B16 — “Federation Downloads” folder exists but path is not attached
- **Status:** fixed (0.0.154)
- **Severity:** medium
- **Files:** `Services/FederationDownloadService.cs` (`EnsureDownloadsLibraryAsync` ~787–794)
- **What:** Returns immediately if any virtual folder has that name, without checking locations. Files write under the plugin data dir and never appear.
- **Fix:** Attach the downloads path like `LibraryProvisioningService` does for shadow folders. Tests.

---

## C. Settings / jellyfin-web QoL

### C1 — Settings persist is inconsistent; page is one 6111-line file
- **Status:** open
- **Severity:** medium
- **Files:** `Configuration/configPage.html`
- **What:** Cloud badge / quality checkbox / several Advanced fields only persist on Save. Friend connection fields autosave.
- **Fix (this pass):** Persist cloud-badge and prefer-quality immediately (or show “unsaved”). Do not split the file in this bug unless asked.

### C2 — Prefer-quality checkbox UI lies until Save
- **Status:** open
- **Files:** `Configuration/configPage.html` (~786–796, 3687–3928, 5869–5874)
- **Fix:** Drive the panel from the checkbox *or* disable the panel until saved. Don’t show stale candidates.

### C3 — Catalog “Hide from everyone” ignores HTTP failures
- **Status:** open
- **Files:** `Configuration/configPage.html` (~4709–4727)
- **Fix:** Use `readJson` / fail the success path on 4xx/5xx, same as hide-from-friend.

### C4 — Pool membership UI does not refresh after add/invite
- **Status:** open
- **Files:** `Configuration/configPage.html` (~1732–1744, 3045–3065)
- **Fix:** Call `loadPools()` / `loadPoolInvites()` on success.

### C5 — Background reload clobbers in-progress Advanced edits
- **Status:** open
- **Files:** `Configuration/configPage.html` (`loadConfiguration(true)` ~5259–5314)
- **Fix:** Don’t overwrite dirty URL/checkbox fields on silent reloads.

### C6 — Copy still says “People tab”
- **Status:** open
- **Files:** `Configuration/configPage.html` (~468–469, 668–669, 4329)
- **Fix:** Say Friends.

### C7 — A11y / light theme / token field / clipboard
- **Status:** open
- **Files:** `Configuration/configPage.html`
- **Fix:** Mask Plex token (`type="password"`); sticky bars must work on light theme; focus-trap the item dialog; clipboard fallback when `navigator.clipboard` is missing; Enter submits search fields.

### C8 — Catalog/Downloads load-more races; blob URLs leak
- **Status:** open
- **Files:** `Configuration/configPage.html`
- **Fix:** Guard catalog load-more like browse. `URL.revokeObjectURL` on re-render.

### C9 — Badge cards stamped once; leftover source tag; stop-sharing badge missing
- **Status:** open
- **Files:** `Web/federation-badge.js`
- **Fix:** Re-evaluate badges on FederatedIds/DisabledIds refresh. Remove source tag on local items. Show eye-off after stop-sharing. Retry action-sheet inject after the sheet exists.

### C10 — Download progress poll can hang forever
- **Status:** open
- **Files:** `Web/federation-badge.js` (~523–585)
- **Fix:** On non-OK progress, retry with backoff and clear the operation lock after a bounded number of failures.

### C11 — No guided repair for offline friends
- **Status:** open
- **Files:** `Configuration/configPage.html`
- **Fix:** For unreachable friends, offer Retry / Edit address / which-side copy. Consistent empty/error/loading on that row.

### C12 — Manual paging in Browse / Catalog / Downloads
- **Status:** open
- **Files:** `Configuration/configPage.html`
- **Fix:** Lazy-load next page on scroll; off-screen image loading.

### C13 — Origin filter advertised but not implemented
- **Status:** open
- **Files:** `Web/federation-badge.js`, `AGENTS.md`
- **Fix:** Optional jellyfin-web filter: this server vs from friends. Default off.

---

## D. Companion / Plex

### D1 — Multi-part Plex videos drop extra parts
- **Status:** open
- **Files:** `Companion/MediaMount.cs`, `Services/PlexApiClient.cs`
- **Fix:** Surface missing multi-part as a catalog reason (do not silently play part 1 as the whole title) until multi-part is designed.

### D2 — Plex subtitles never copied into the federated DTO
- **Status:** open
- **Files:** `Services/PlexApiClient.cs` (~596–608)
- **Fix:** Include `streamType` 3 subtitle streams in MediaStreams.

### D3 — Windows state files have no ACL
- **Status:** open
- **Files:** `Companion/CompanionState.cs` (~207–222), `Companion/LocalMediaMountService.cs`
- **Fix:** Restrict `companion-state.json` and `media-mount.conf` to the current user on Windows.

### D4 — Companion zip install/update is not hash-pinned
- **Status:** open
- **Files:** `Companion/install.sh`, `Companion/CompanionUpdater.cs`
- **Fix:** Pin `companion-latest` the same way rclone/WinFsp are, or verify a published checksum. `install.sh` should not assume linux-x64 only if other artifacts exist.

### D5 — Friend-facing Plex URL matched by server name
- **Status:** open
- **Files:** `Companion/Program.cs` (~1059–1094)
- **Fix:** Prefer `ServerMachineIdentifier`.

### D6 — Legacy `SelectedLibraryIds == null` means all libraries forever
- **Status:** open
- **Files:** `Companion/ImportSyncBackgroundService.cs`, `Companion/CompanionState.cs`
- **Fix:** Treat null as “prompt / import none new”; only explicit lists import.

### D7 — Mount sync still writes `.strm`; remove keeps them
- **Status:** open
- **Files:** `Companion/ImportSyncBackgroundService.cs`
- **Fix:** Stop writing new `.strm` next to the rclone mount. Removal should offer to delete legacy links.

### D8 — `companion-latest` publishes with no tests; CI skips master
- **Status:** open
- **Files:** `.github/workflows/companion-release.yml`, `.github/workflows/validate.yml`
- **Fix:** Run Companion tests before publishing. Run validate on master (or at least on Companion release).

---

## E. Known issues still true

### E1 — Leave pool is local-only
- **Status:** open
- **Files:** `Services/FederationFriendService.cs` (`LeavePool`)
- **Fix:** Notify members; refuse silent re-adopt; require a fresh accept to rejoin.

### E2 — Disabled friend’s remote deletions wait for re-enable
- **Status:** open
- **Files:** `Services/FederationSyncService.cs`
- **Note:** Documented. Optional: prune on disable if the admin confirms.

### E3 — Resume points lost on delete/recreate
- **Status:** open
- **Files:** `Services/FederationItemPersistenceService.cs`
- **Fix:** Copy `UserData` onto the new item id when identity is unchanged in spirit.

### E4 — Deleting a local Jellyfin user leaves federation state
- **Status:** open
- **Fix:** Hook user deletion; sweep `FriendUserAccessRules` and session tokens.

### E5 — Vestigial `RemoteServer.UserId` / `RequireApiKeyForImages`
- **Status:** open
- **Files:** `Configuration/PluginConfiguration.cs`
- **Fix:** Stop serializing/suggesting them in API/UI. Keep unused fields for XML compat if needed.

### E6 — Pre-0.0.70 peers cannot handshake
- **Status:** open
- **Note:** By design. Keep the upgrade message. No protocol mix.

### E7 — Windows + WinFsp + real Plex client + friend’s Funnel unvalidated
- **Status:** open
- **Note:** Live check, not a code defect by itself.

### E8 — Xbox / subtitle-audio matrix / mixed plugin versions unfinished
- **Status:** open
- **Note:** Test matrix, not a single code path.

---

## F. Enhancements (not bugs — do not implement unless asked)

- F1 Durable leave-pool (overlaps E1)
- F2 Preserve watch progress (overlaps E3)
- F3 User-deletion sweep (overlaps E4)
- F4 Playback preflight diagnostic
- F5 Split settings page
- F6 Reduce Direct-mode extra hop
- F7 HMAC download+expiry (overlaps A3/A7)
- F8 Send-only / receive-only per friend
- F9 Federated watch parties
- F10 WAN startup redesign (`work/jellyfin-streaming-redesign`)
- F11 Origin filter (overlaps C13)
- F12 Radarr/Sonarr requests
- F13 Friend ratings/comments (deferred)
- F14 Incoming rating filter scales / fail-closed
- F15 Preserve unknown RemoteServer fields on config POST (overlaps B3)

---

## Agent batches

Max 10 concurrent implementers. Orchestrator updates Status here after each merge.

**Release policy:** A1–A8, B1, B2 landed as code-only commits (no version bump). Every remaining bug (A2–A5, B3+) is a versioned plugin release: bump csproj/meta/manifest, commit `0.0.N: …`. Do not `gh release create` from the agent.

Assigned versions for the current remaining wave:

| Bug | Version |
|-----|---------|
| A2 | 0.0.137 |
| A3 | 0.0.138 |
| A4 | 0.0.139 |
| A5 | 0.0.140 |
| B3 | 0.0.141 |
| B4 | 0.0.142 |
| B5 | 0.0.143 |

Later bugs continue from 0.0.144.

- **Batch 1 code landed:** A1 A6 A7 A8 B1 B2
- **Batch 1 remaining + release:** A2 A3 A4 A5
- **Batch 2 (in_progress):** B3 B4 B5 B6 B7 B8 B9 B10 B11 B13
- **Batch 3:** B12 B14 B15 B16 C1 C2 C3 C4 C5 C6
- **Batch 4:** C7 C8 C9 C10 C11 C12 C13 D1 D2 D3
- **Batch 5:** D4 D5 D6 D7 D8 E1 E3 E4 E5
