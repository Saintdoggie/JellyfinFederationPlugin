# Known issues

Bigger items were fixed in 0.0.78/0.0.79; these are smaller and set aside:

1. **Version skew with old peers** — a friend running a pre-0.0.70 plugin can't use scoped tokens; handshake is rejected with an upgrade message rather than mixing protocols. Fix = both sides upgrade.
3. **WAN bitrate caps are inert** — the capped transcode URL is internal-only (never served to a client since 0.0.70); measurement was fixed in 0.0.78 but no client-facing URL applies a cap. Clients on slow links direct-play the raw bitrate and may buffer.
4. **Disabled servers' deletions don't propagate while disabled** — sync skips disabled servers entirely; remote-side deletions appear only after re-enable + next sync (offline servers propagate on next successful sync).
5. **`LeavePool` is reversible by the next pool notice** — leaving a pool doesn't notify members, and a subsequent roster fan-out re-adopts the membership.
6. **`_remoteIndex` in `FederationItemCache` grows monotonically** — never swept on entry removal; in-process only, bounded by usage volume.
7. **Vestigial config fields** — `RemoteServer.UserId` (unused under token model) and `RemoteServer.RequireApiKeyForImages` (superseded by token-gated `Peer/Images`).
8. **Resume points lost on item delete/recreate** — reconciliation dedup and migrations delete/recreate virtual items, wiping all users' watch progress on those items.
9. **Federation tokens stored plaintext in config XML** — scoped and non-admin, but unencrypted at rest.
10. **Deleting a local Jellyfin user leaves stale federation state** — per-user access rules pushed by friends (`FriendUserAccessRules`) and cached session tokens for that user are never swept (no user-deletion hook exists). Inert after deletion, but accumulates.
11. **Direct-mode static source relays through this server** — the Play-button fix routes the stamped static Path through the local proxy gateway (a relay hop). Direct client→remote fetching is still available for the provider-emitted sources where applicable, but the default source relays.

## Resolved in the current quality pass

- The directional missing-Play-button report was traced to a coarse guard that
  blanked every federated item path as soon as any incoming per-user rule existed.
  Paths are now evaluated per item across all configured rules, so universally
  allowed items remain playable without weakening restrictive items.
- Local stream URLs are no longer enumerable server/item pairs. They carry a
  scoped HMAC capability and are revalidated against current server, cache, source,
  and access-rule state at stream time.
