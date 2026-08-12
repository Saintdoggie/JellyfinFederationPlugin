# Roadmap / TODO

Future feature ideas for the federation plugin, captured so they don't get lost
between sessions. This is a planning list, not a changelog - items marked
**Done** note where the work landed.

## Withdrawn, preserved on `archive/friend-system`

The friend-request system and friends-of-friends discovery (0.0.24 / 0.0.25) were
pulled out in 0.0.27 to get the core back to a working state. Nothing is lost -
the full implementation, its tests, and its config-page UI are on the
`archive/friend-system` branch and can be brought back once streaming and
sharing are solid. What is still open from that work: a directory/lookup service
so friends can be found by name instead of by address.

## Never subclass Jellyfin's entity types

Learned the hard way in 0.0.22 -> 0.0.27. `BaseItem.GetBaseItemKind()` resolves
an item's kind with `Enum.Parse<BaseItemKind>(GetType().Name)` - it parses the
CLR *class name*. Any subclass whose name is not already a value of that enum
throws `ArgumentException`, and the call sits underneath both
`DtoService.AttachBasicFields` and `Folder.GetCachedChildren`, so it breaks every
API response and every folder enumeration that touches the item. Federated items
must always be instantiated as Jellyfin's own types.

Removing the subclasses in 0.0.27 wasn't the end of it: any server that had
already run 0.0.22-0.0.24 still had rows in its database stored under those
now-deleted type names. `BaseItemRepository.DeserializeBaseItem` throws
`InvalidOperationException("Cannot deserialize unknown type.")` on those, and
same as above, that aborts the whole folder listing rather than just skipping
the bad row - so reconciliation kept failing even on a clean 0.0.27 install.
0.0.28 self-heals this: `FederationItemPersistenceService` catches that
specific failure, uses `IItemRepository.GetItemIdsList` (id-only, no
deserialization) to enumerate everything under the library's physical
folders, probes each id individually with `RetrieveItem` to isolate which
ones are unrecoverable, and deletes those directly by id via
`IItemRepository.DeleteItem` before retrying. General lesson: a plugin that
ever changes an item's CLR type needs a cleanup path for the *old* rows, not
just code that stops writing new ones that way.

0.0.28's purge was still reactive - it only ran once this plugin's own
reconciliation happened to hit the crash, several seconds into server
startup (the background sync's own startup delay). Anything else that
enumerated the same folder first - a native library scan, the web UI,
another plugin - could still hit the same crash outside this plugin's
control in that window. 0.0.29 runs the same purge eagerly and
synchronously from `FederationEntryPoint.StartAsync`
(`PurgeUndeserializableItemsAtStartup`), before that delay, so it has
already happened by the time anything else gets a chance to run.

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
