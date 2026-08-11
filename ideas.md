# Roadmap / TODO

Future feature ideas for the federation plugin, captured so they don't get lost
between sessions. This is a planning list, not a changelog - items marked
**Done** note where the work landed.

## Known follow-ups on streaming (0.0.26)

- `item.Path` is stamped once at sync time with the source server's address and
  api_key. Reconciliation only creates and deletes items, never updates them,
  so if a server's address or key changes the stored path goes stale until the
  next forced rebuild. The media source provider papers over this at playback
  time (it compares the stored path against a freshly built one and serves the
  fresh source when they differ), but the stored path itself stays wrong. A
  proper fix would fingerprint each server's url+key and force a rebuild of
  just that server's items when it changes.
- First playback of a federated item runs an ffprobe against the remote URL to
  discover codecs, so it is slower than subsequent plays. Could be avoided by
  pulling MediaStreams from the remote at sync time and persisting them via
  `IItemRepository.SaveMediaStreams`.
- The "which server is this from" indicator is a Jellyfin tag chip on the item
  detail page. A real badge overlaid on the poster in grid views needs a
  client-side jellyfin-web plugin, which is out of scope for a server plugin.

## Dedup management

- A UI to explicitly deselect specific items (or a whole remote source) from
  being shared/synced, on top of the existing automatic
  `EnableDedup`/`DedupProviderIds` matching. Right now dedup is all-or-nothing
  and automatic; admins have no way to say "don't federate this one out."

## Access control

- Per-friend rate limiting (bandwidth and/or concurrent-stream caps).
- Time-of-day restrictions - allow/deny federated access during configured
  hours.

## Friend system (replaces manual API key exchange)

- **Done (0.0.24):** connect-by-URL friend requests. Enter a friend's server
  address in the new "Friends" section, send a request; their admin sees it
  pending on their own config page and can accept or reject. On accept, each
  side mints a fresh Jellyfin API key for the other automatically and adds
  them as a connected server - no manual key copy-paste. See
  `FederationFriendService` and the `Friends/*` endpoints on
  `FederationController`.
- **Not done:** a directory/lookup service so an admin can find a friend's
  server *by name* instead of needing their address up front. Would need
  somewhere to host that directory (opt-in registry of "servers running
  Federation") - out of scope for this repo alone until that's decided.
- **Done (0.0.25):** friends-of-friends federation (second degree), gated
  behind a new opt-in `AllowFriendsOfFriends` setting (off by default - both
  revealing your friend list and reaching out to strangers on your friends'
  behalf are bigger trust decisions than one direct friendship). When on,
  each sync cycle asks your friends who their other friends are and
  auto-sends requests to anyone new - still needs that server's own admin to
  accept, same as a manual request. The "never relay content federated into
  you from somewhere else" constraint turned out to already be handled: 
  `FederationSyncService` already skips any remote item carrying a
  `FederationKey` provider id (content that server only has because *it*
  federated it in), regardless of degree - see `FetchAndUpsertPagesAsync`.

## Send-only / receive-only per friend

- Per-friend toggle: "send only" (share my library with them, don't pull
  theirs) and "receive only" (pull their library, don't share mine).
- Toggling either one should post a visible notice on the *other* admin's
  federation dashboard, so it's a transparent change, not a silent one.

## Federation score / gamification

- A points system rewarding uptime and hours streamed to friends.
- Collectible badges for milestones (e.g. "100 hours federated").
- Room for lighthearted/themed badges (e.g. "Take a Penny, Leave a Penny" for
  a healthy give/take ratio) - artwork for these to be supplied separately
  (AI-generated) rather than designed here.
