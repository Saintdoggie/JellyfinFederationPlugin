# Federation plugin — trust model & security review

Written 2026-08-15, following an ultra-review pass on the whole repo. This
covers every finding from that review, independently re-verified against the
actual code (not just the review's one-line descriptions — several turned out
worse, or different, than the summary suggested). Intended as a briefing doc
for the next session to act on, not a finished spec — the Tier 2 items below
are architecture decisions, not bug fixes, and need a human call before
anyone (Claude included) starts changing code.

Already fixed and shipped in 0.0.50 (not covered further here): proxy stream
connection resets on cancellation, retry-splice corruption when a remote
ignores a resumed Range request, auto-resolved UserId persisting to disk,
orphaned episodes at library root when their series wasn't synced yet.

---

## Tier 1 — mechanical bugs, safe to fix without a design discussion

These don't change what any admin can assume about trust between servers.
Each entry has been read in full, not just grepped.

### 1. `ApplySharePolicyAsync` wipes the enforcement account's entire policy, can silently demote an admin

**File:** `Services/FederationFriendService.cs:952-969`

```csharp
private async Task<Guid> ApplySharePolicyAsync(RemoteServer server, CancellationToken cancellationToken)
{
    var userId = Guid.Parse(server.LocalShareUserId);
    var policy = new UserPolicy
    {
        IsAdministrator = false,
        EnableAllFolders = false,
        EnabledFolders = server.SharedLibraryFolderIds...,
        EnableMediaPlayback = true
    };
    await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
    return userId;
}
```

Two distinct problems:

- `UpdatePolicyAsync` **replaces** the user's whole `UserPolicy` object. Every
  field not set above (parental rating limits, blocked tags, device/channel
  restrictions, sync-play access, remote-access flags, …) silently resets to
  the `UserPolicy` class's C# defaults on *every* sharing-settings save for
  that friend. Any customization the admin had on that account is gone,
  repeatedly, with no warning.
- There is no check that `server.LocalShareUserId` isn't an administrator
  account. The admin picks *any* existing local user from a dropdown (see
  `Services/LibraryProvisioningService`/`GetLocalUsers` and the config page's
  "restrict to this account" picker) — nothing stops them from picking their
  own real admin account, or another admin's, by mistake. If they do, this
  call forcibly sets `IsAdministrator = false` on it. Permanent, silent,
  first-save.

**Fix shape:**
1. In `UpdateFriendSharingAsync` (line 825), before calling
   `ApplySharePolicyAsync`, fetch the chosen user via `_userManager` and
   reject if `user.HasPermission(PermissionKind.IsAdministrator)` (or
   equivalent) — return an error message telling the admin to pick a
   non-admin account.
2. In `ApplySharePolicyAsync`, fetch the user's *existing* `UserPolicy` first
   (`_userManager.GetUserById(userId).Policy`, or however this Jellyfin
   version exposes it — check what `GetLocalUsers` already does, it likely
   already has the shape), mutate only `EnableAllFolders`, `EnabledFolders`,
   and `EnableMediaPlayback` on that existing object, and save the patched
   object back — never construct a fresh `UserPolicy()`.
3. Consider also surfacing in the UI, next to the account picker, an explicit
   warning: "this account's other settings (parental controls, device
   limits) will be preserved, but its library visibility will be fully
   controlled by this friend's sharing settings from now on" — so the admin
   understands the account becomes federation-managed.

### 2. `UpdateFriendSharingAsync`: switching a restricted friend back to "share everything" doesn't undo the restriction

**File:** `Services/FederationFriendService.cs:825-871`

When `shareAll` is `true`, the method sets `server.ShareAllLibraries = true`
and *skips* calling `ApplySharePolicyAsync` entirely (line 858:
`if (!shareAll) { ... }`). But the local enforcement account
(`server.LocalShareUserId`) that was restricted the last time sharing was
narrowed still has `EnableAllFolders = false` / a specific `EnabledFolders`
list applied to its Jellyfin policy from the *previous* save. Nothing
resets it. The friend's remote-side behavior does correctly revert (the
comment at line 878 explains they just go back to querying with whatever
`UserId` they had before — that part's fine), but if the admin *re-narrows*
sharing later, or if that local account is reused for something else, its
policy is still the old restricted one from before the "share everything"
toggle, not full-access, not default — stale.

**Fix shape:** when `shareAll` flips true and `server.LocalShareUserId` is
non-empty, either explicitly reset that account's policy (`EnableAllFolders =
true`, matching what "no restriction" should mean), or clear
`server.LocalShareUserId` on the `RemoteServer` record so a future re-narrow
starts clean rather than assuming stale state. Decide which — resetting the
account is probably the least surprising (an admin who flips back to
"restricted" a week later shouldn't inherit whatever was set two toggles
ago).

### 3. `UpdateConfiguration` wipes `RemoteServer.FederationId` on every save from the main config page

**File:** `Configuration/FederationPluginController.cs:108-183`,
cross-checked against `Configuration/configPage.html`

The handler already has an established, well-documented pattern for exactly
this class of bug — server-internal fields the config page's JS never
sends get explicitly restored from the existing config before saving
(`ApiKey`, all the `Migrated*` flags, `LocalFederationId`,
`IncomingFriendRequests`, `OutgoingFriendRequests`, `Pools` — see the
comments at lines 118, 137, 149, 158). `RemoteServer.FederationId` is **not**
in that restore list, and:

```
$ grep -n "federationId\|FederationId" Configuration/configPage.html
(no output)
```

confirms the UI's `saveConfiguration()` never includes it when building the
`servers` array it POSTs. Every save from the main config page (not the
Pools/Friends sub-endpoints — the main "Save" button) silently resets every
friend's `FederationId` to `""`.

This matters more than the earlier fields the comments already call out,
because `FederationId` is the *sole* matching key for
`ReceiveSharedUserUpdate` (line 929) and `ReceivePoolNotice` (line 680) — both
do `config.RemoteServers.FirstOrDefault(s => s.FederationId ==
payload.FromFederationId)`. Once wiped to `""`, every inbound sharing update
or pool notice from every friend silently no-ops (logged as "unrecognized
federation id", but the log line 683/932 easily goes unnoticed) until the
friendship is somehow re-established. Per-friend sharing (the whole feature
0.0.49 added) breaks silently the moment an admin saves an unrelated setting
on the main page.

**Fix shape:** add `RemoteServer.FederationId` to the same
per-server-preserve loop already handling `ApiKey` at lines 118-126 — for
each incoming server in the POST body, if `FederationId` is empty, look up
the existing record by `Id` and copy its `FederationId` across, exactly
mirroring the `ApiKey` logic already there. Add a regression test that saves
config via `UpdateConfiguration` without `FederationId` in the payload and
asserts it survives — the existing `ApiKey`-preservation behavior almost
certainly already has one to copy the shape from.

### 4. `SafeFolderName` doesn't handle Windows-reserved names

**File:** `Services/LibraryProvisioningService.cs:405-427`

Strips path separators, `:`, and control characters, but not the
Windows-reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`,
`LPT1`-`LPT9`, case-insensitive, with or without an extension) or the other
Windows-invalid characters (`< > " | ? *`). A library mapping literally named
"Con" or containing `?` would fail `Directory.CreateDirectory` on Windows.
Low severity (Windows-hosted Jellyfin is uncommon, and it only breaks a
folder name an admin explicitly typed), but a one-paragraph fix:

```csharp
private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
{
    "CON", "PRN", "AUX", "NUL",
    "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
    "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
};
```
strip `< > " | ? *` alongside the existing separator/`:`/control-char pass,
and if the whole trimmed name (ignoring any extension-like suffix) matches
`WindowsReservedNames`, append a suffix (e.g. `"_"`) rather than leaving it
as-is.

### 5. Static `HttpClient`s with no DNS-refresh policy

**Files:** `Services/FederationFriendService.cs:30`,
`Services/FederationStreamHandler.cs:19`

Both are long-lived `static readonly HttpClient` instances (deliberately, per
their own comments, to reuse sockets across the app's lifetime — that part's
correct). But neither sets
`SocketsHttpHandler.PooledConnectionLifetime`, so a `HttpClient` will keep
reusing a pooled connection to a friend's old IP indefinitely if that
friend's DNS record changes (dynamic DNS, a re-hosted server, a changed
reverse-proxy target) — connections just start failing until the process
restarts. Given several federation setups in the wild are explicitly dynamic
DNS / home-hosted-behind-a-relay (this session's own debugging saw exactly
that setup), this is worth fixing:

```csharp
private static readonly HttpClient DefaultHttpClient = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
})
{ Timeout = TimeSpan.FromSeconds(20) };
```

(5 minutes is a reasonable default — long enough to still get real connection
reuse, short enough that a DNS change recovers quickly. Apply the same
pattern to `FederationStreamHandler`'s `DefaultProxyHttpClient` and
`RemoteServerClient.CreateDefaultHttpClient`.)

### 6. `WebClientInjector` never cleans up its injected `<script>` tag

**File:** `Services/WebClientInjector.cs`

Injects `<script defer src="/Plugins/Federation/ClientScript"></script>` plus
a marker comment into jellyfin-web's `index.html` on every startup
(idempotent, guarded by the marker — that part's fine). There's no
corresponding removal on plugin uninstall. After uninstalling, `index.html`
keeps requesting a now-404 route on every page load — cosmetic (one extra
failed request, visible in the browser console/network tab), not a security
issue, but sloppy. Two options:
- Implement `IHasWebPages`... actually check what interface Jellyfin plugins
  use for an uninstall hook (`Plugin.OnUninstalling` if this Jellyfin version
  exposes it) and strip the marker + script tag there.
- If no clean uninstall hook exists in this Jellyfin/plugin-abstraction
  version, at minimum document it in the plugin's README/description so an
  admin uninstalling manually knows to check `index.html`.

### 7. Quality/perf nits (lower priority, listed for completeness)

- `FederationItemPersistenceService.CollectServerWideLocalProviderIds` scans
  the *entire* local library (restricted to `DedupCandidateKinds`, see line
  28-40) once per library **mapping** (Movies, Shows, …), not once per sync
  cycle. On a large local library with several mappings this repeats an
  expensive full scan needlessly. Fix: hoist the collection up to once per
  `SyncAllAsync` call and pass the result into each mapping's reconciliation,
  rather than recomputing inside `ReconcileMappingAsync` per mapping.
- `Services/FederationItemCache.cs` around line 395 (`TryGetLocalKeyForRemoteItem`
  and the raw-key parsing helpers nearby) — the review flagged something here
  but the description was truncated in what got pasted back and I couldn't
  reconstruct which specific concern it meant from the code alone. Needs the
  full review text, or a fresh look with fresh eyes, before acting on it.
- `Configuration/FederationPluginController.cs`, the `Friends/Send` /
  `SendFriendRequest` action — uses `body?.Url ?? string.Empty` and the
  review flagged its HTTP status handling as questionable. Worth a quick read
  before the next session to see if a malformed/missing URL returns a
  clear 400 vs. silently attempting an empty-string request.

---

## Tier 2 — trust-model questions that need a decision before any code changes

These four are related: all four exist because the plugin's friend/pool
system was designed around "trust is established once, by a human clicking
Accept, and everything after that between two already-trusted servers is
low-stakes." That assumption breaks down in a few specific spots. Fixing them
piecemeal (just patching the symptom) risks leaving the actual gap open, so
this section tries to name the *shape* of the real problem before proposing
fixes.

### The core issue: pool membership has no admin/permission concept at all

**File:** `Configuration/PluginConfiguration.cs:498-536` (`FederationPool`,
`PoolMember`)

```csharp
/// <summary>
/// Gets or sets a value indicating whether this server created the pool.
/// Informational only (e.g. for the UI to show "you own this pool") - every
/// member can invite new servers in, ownership does not gate that.
/// </summary>
public bool IsOwner { get; set; }
```

That comment is completely accurate and completely deliberate — this isn't
an oversight, it's the documented design. **Every** pool member, not just the
owner, can trigger an invite to a new server
(`SendPoolInviteAsync`/`AddExistingFriendToPoolAsync`), and — this is the
part that turns "informal trust" into a real vulnerability — **the roster a
member reports in a pool notice is taken entirely at face value and acted on
automatically**, with no human involved on the receiving end:

**File:** `Services/FederationFriendService.cs:587-664`
(`AdoptPoolRosterAndFanOutAsync`), fed by `ReceivePoolNotice` (line 672) and
the controller endpoint `Pools/Notice` (`FederationPluginController.cs:921`,
gated only by `[Authorize(Policy = "RequiresElevation")]` — any valid
federation-minted key, not specifically the pool owner's).

Walk the actual flow:
1. Any current friend (their own genuinely-issued, elevated federation API
   key — no impersonation needed) POSTs to `/Plugins/Federation/Pools/Notice`
   with a `Roster` list. That list is fully attacker-controlled — it's just
   JSON in the request body, nothing validates its entries against what this
   server actually knows about the pool.
2. `AdoptPoolRosterAndFanOutAsync` adds every roster entry as a
   `PoolMember` (line 635-638, `AddMember`) — no validation beyond "is the
   URL non-empty."
3. For every member not already known, it calls `SendPoolInviteAsync` (line
   649-663) with **no human approval step**.
4. `SendPoolInviteAsync` → `SendFriendRequestAsync`
   (`FederationFriendService.cs:111-`) which — before any confirmation from
   the target, before any admin sees or approves this — **mints a brand-new
   elevated Jellyfin API key** (`CreateApiKeyAsync`, line 135) and POSTs it
   directly to the attacker-chosen URL as `ApiKeyForYou`.

Net effect: being accepted as a friend *once*, ever, by *any* admin, gives
that friend's server (or whoever compromises it) a standing ability to make
this server mint and hand out fresh admin-level API keys to arbitrary URLs of
their choosing, with zero human involvement on this server's side. That's
both an SSRF primitive (this server will make an authenticated-shaped POST to
any URL the attacker names) and a credential-exfiltration primitive (a live
admin-equivalent key gets sent to that URL).

This is worse than what the review's one-line summary ("sender identity
taken from payload") suggested — the sender-identity spoofing problem (item
B below) is real too, but *even a completely honest, correctly-identified*
existing friend can trigger this today, by design, because the roster
content itself is never validated or gated by any notion of "who's allowed
to grow this pool."

### What "pool admin" should probably mean

Proposing a concrete shape, not a final answer — this is the part worth
Opus's attention, since it's a product/UX decision as much as a security one:

**Roles, not just an owner flag.** Give `PoolMember` a `Role` (`Owner`,
`Admin`, `Member`). The pool creator is `Owner` (as `IsOwner` already
captures — this can literally become `Role == Owner` on their own
`PoolMember` entry rather than a separate bool). The owner can promote other
members to `Admin`. Only `Owner`/`Admin` members can:
- Invite a new server into the pool (i.e., call `SendPoolInviteAsync`).
- Have their pool-notice roster updates *actually trusted* to trigger
  auto-invites on the receiving end.
- Remove a member from the pool (there's currently no way to remove anyone
  except each member individually leaving — worth adding regardless).

Plain `Member`s can still see the roster, still leave, but a `PoolNotice`
*they* send should be treated as informational only (updates this server's
local view of "who's in the pool," for display) and should **never**
autonomously trigger `SendFriendRequestAsync`/key-minting. If a plain member
learns about a new server through the mesh, the right behavior is: surface
it to the local admin as a suggestion ("Member X reports Y wants to join
this pool — invite them?") and let a human click a button, restoring the
"a human decided this" trust boundary that pool auto-fan-out currently skips
entirely.

**Verifying "who is Owner/Admin" server-to-server is itself the crux
problem** — this is where item B (sender identity from payload) and this
issue meet. A role field is worthless if any member can just claim
`FromFederationId = <the owner's id>` in their notice payload and have it
believed. Which leads to:

### Item B: sender identity should come from the authenticated caller, not a self-reported payload field

**Files:** `Services/FederationFriendService.cs:921-939`
(`ReceiveSharedUserUpdate`), `:672-697` (`ReceivePoolNotice`)

Both resolve "who sent this" via:
```csharp
var server = config.RemoteServers.FirstOrDefault(s => s.FederationId == payload.FromFederationId);
```
— a field the caller supplies in the request body. The endpoint's
`[Authorize(Policy = "RequiresElevation")]` only proves *some* validly-minted
elevated API key was presented — not *whose* key. Any friend can claim to be
any other friend in the payload.

**The real fix needs a way to map "which API key authenticated this request"
back to "which `RemoteServer` we minted it for."** Currently
`RemoteServer.ApiKey` only tracks the key *we* use calling *them* — there's
no field tracking the key *we* minted *for them* to call *us* with (it's
created in `CreateApiKeyAsync`/`AcceptFriendRequestAsync` and handed over,
then never referenced again on our side).

Proposed shape:
1. When minting a key for a friend (`AcceptFriendRequestAsync` line 372,
   `SendFriendRequestAsync` line 135), record which Jellyfin API-key id (or
   the key value itself, if that's simplest given `IAuthenticationManager`'s
   API — check what `_authManager.GetApiKeys()` returns) was issued, on a new
   `RemoteServer.IssuedApiKeyId` field (this is *our* record of *their*
   inbound credential, separate from `RemoteServer.ApiKey` which is *their*
   outbound-to-us... sorry, *our*-outbound-to-*them* credential — name it
   carefully, this direction confusion is exactly how the current bug
   happened).
2. In `ReceiveSharedUserUpdate`/`ReceivePoolNotice`, instead of trusting
   `payload.FromFederationId`, resolve the actual presented token from
   `IHttpContextAccessor`/the request's auth header, and look up which
   `RemoteServer.IssuedApiKeyId` matches it. That's the real sender.
3. Treat `payload.FromFederationId` as, at most, a consistency check (log a
   warning, or reject outright, if it doesn't match the resolved sender) —
   never as the primary identity source.

This is a real, non-trivial change (touches key minting, storage, and every
inbound federation callback), which is exactly why it's Tier 2 rather than
something to just patch inline.

### Item C: the anonymous `Friends/Accept` callback trusts the caller's self-reported identity, gated only by guessing a request GUID

**File:** `Services/FederationFriendService.cs:753-790`
(`HandleAcceptCallback`), controller at
`Configuration/FederationPluginController.cs:703-704` (`[AllowAnonymous]`,
by necessity — the sender has no key for us yet at this point in the
handshake, that part is a real constraint, not an oversight).

```csharp
var entry = config.OutgoingFriendRequests.FirstOrDefault(r => r.Id == payload.RequestId);
...
var memberUrl = string.IsNullOrEmpty(payload.FromServerUrl) ? entry.RemoteServerUrl : payload.FromServerUrl.TrimEnd('/');
config.RemoteServers.Add(new RemoteServer { ..., Url = memberUrl, ApiKey = payload.ApiKeyForYou, FederationId = payload.FromServerId ?? string.Empty, ... });
```

Anyone who learns a pending `entry.Id` (a `Guid.NewGuid()` — not brute-
forceable, but interceptable if the original request traveled over plain
HTTP, or leaked via a log line, or a compromised intermediary) can send a
forged Accept that gets unconditionally believed: whatever URL and
whatever API key value they include becomes a new trusted `RemoteServer`
record on this side, no further check. Impact: content-source spoofing (this
server starts merging a federated library from an attacker's server) and
this server making authenticated-shaped calls to that URL believing it's the
intended friend.

The file already has the right pattern to fix this **symmetrically** —
`ReceiveFriendRequestAsync` (line 283-332) does exactly this kind of
check via `VerifyOutgoingRequestExistsAsync` (line 327), which calls back to
the claimed sender's own server to confirm they really do have a matching
*outgoing* request under this id before trusting it. `HandleAcceptCallback`
has no equivalent verification in the accept direction.

**Fix shape:** before adding the `RemoteServer` in `HandleAcceptCallback`,
call back to `payload.FromServerUrl` and confirm — via some new small
endpoint on their side, or by reusing/adapting `Friends/Outgoing/{id}` if its
semantics still make sense for a completed acceptance — that they genuinely
consider this request accepted, the same "don't just trust what's in this
one payload, cross-check with the other side" pattern already proven out
elsewhere in this file. Also worth revisiting whether `ConfigValidator`
should require/strongly prefer `https://` for federation URLs generally,
given how much of this trust model rests on "nobody intercepted this one
plaintext HTTP round-trip."

### Item D: the anonymous stream proxy doesn't check the item was ever federated

**File:** `Configuration/FederationPluginController.cs:982-1009`
(`Stream`), backed by `Services/FederationStreamHandler.cs`

```csharp
[HttpGet("Stream")]
[AllowAnonymous]
public async Task<IActionResult> Stream([FromQuery] string serverId, [FromQuery] string itemId, ...)
{
    var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
    if (server == null) return NotFound(...);
    if (!Guid.TryParse(itemId, out _)) return BadRequest("Invalid item id");
    await _streamHandler.HandleProxyAsync(serverId, itemId, Request, Response, cancellationToken, audio).ConfigureAwait(false);
    ...
}
```

`[AllowAnonymous]` is a real constraint here, not an oversight — media
players fetch stream URLs without Jellyfin's normal auth headers, and that's
how Jellyfin's own native `/Videos/{id}/stream` endpoint works too, for the
same reason. But Jellyfin's own version compensates by requiring an
`api_key`/session token as a *query parameter on the URL itself* — a bearer
credential scoped to the request, not just "any network caller." This
endpoint requires nothing beyond a valid `serverId` (itself a locally-
generated GUID, but discoverable — `/Plugins/Federation/FederatedIds` is also
`[AllowAnonymous]`, line 239-240, and likely exists specifically to let
clients resolve these) and a syntactically-valid `itemId` GUID — **with no
check that `itemId` corresponds to anything this server has actually
decided to federate.**

Practical effect: anyone who can reach this server on the network (not
necessarily a Jellyfin user at all) can request this server proxy-stream
*any* item id from the configured remote server that the stored `ApiKey`
happens to have access to — not limited to what's actually in this server's
federated library. That's a real access-control bypass: Jellyfin's own
per-user library restrictions on *this* server become irrelevant, since the
attacker never has to touch Jellyfin's own auth at all.

**Minimal fix:** before calling `HandleProxyAsync`, check the requested
`(serverId, itemId)` pair actually exists in `_federationManager.Cache` (i.e.
corresponds to some `FederatedCacheEntry` this server has synced and is
currently offering) — reject with 404 otherwise. This closes "arbitrary item
on the remote server" down to "only items this server chose to federate,"
which at least matches what a legitimate, authenticated PlaybackInfo request
would have been allowed to discover in the first place.

**Worth also considering** (bigger change, optional): embed a short-lived,
per-request signed token in the stream URL itself (mirroring how Jellyfin's
own `api_key` query param works), generated when `FederationMediaSourceProvider`
builds the URL for an *authenticated* PlaybackInfo request, and required by
`Stream` to match. That closes the residual gap that even a legitimately-
issued stream URL, once obtained, currently has no expiry and no binding to
the user/session that requested it — anyone who captures that URL (a proxy
log, a browser history sync, a shared link) can replay it indefinitely. This
is a bigger change than the minimal fix and probably not needed for the
immediate threat model (a friend's/attacker's network access), but the
review's "any network caller" framing is accurate today and worth deciding
on deliberately rather than by omission.

---

## Suggested order of work for next session

1. Tier 1, items 1-6: mechanical, no design discussion needed, should take
   one session including tests. Item 1 (admin demotion) is the most
   important of these to land quickly even in isolation.
2. Decide the Tier 2 shape (pool roles, sender-identity resolution) — this
   doc's "What 'pool admin' should probably mean" section is a starting
   proposal, not a spec. Once the shape is agreed, items B and the pool-role
   gating share a lot of the same underlying change (resolving sender
   identity from the authenticated caller instead of the payload), so they
   should land together.
3. Item C (Accept callback verification) is architecturally independent of
   B/pool-roles and could land any time — it's a self-contained "add the
   missing symmetric verification call" fix.
4. Item D (stream endpoint cache check) is also independent and cheap — the
   minimal fix (cache-membership check) is low-risk and worth doing even
   before the bigger Tier 2 items are decided.
