# Roadmap / TODO

Future feature ideas for the federation plugin, captured so they don't get lost
between sessions. Nothing here is implemented yet - this is a planning list,
not a changelog.

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
- **Not done:** friends-of-friends federation (second degree). Important
  constraint when this is built: a server must only offer its **own** local
  library to those second-degree friends, never content that was itself
  federated *into* it from somewhere else - no relaying/re-sharing
  third-party content without that party's consent.

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
