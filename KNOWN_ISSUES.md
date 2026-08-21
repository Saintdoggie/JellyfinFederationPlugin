# Known minor issues (deliberately not fixed this release)

Bigger items were fixed in 0.0.78; these are smaller and set aside:

1. **Version skew with old peers** — a friend running a pre-0.0.70 plugin can't use scoped tokens; handshake is rejected with an upgrade message rather than mixing protocols. Fix = both sides upgrade.
2. **Item `Path` static source not per-user gated** — the primary source stamped on `item.Path` at sync time can't be re-gated per user at browse time (alternate sources are). Playback-time checks still apply. Known, documented in `FederationMediaSourceProvider`.
3. **Disabled servers' deletions don't propagate while disabled** — sync skips disabled servers entirely; remote-side deletions appear only after re-enable + next sync (offline servers propagate on next successful sync).
4. **`LeavePool` is reversible by the next pool notice** — leaving a pool doesn't notify members, and a subsequent roster fan-out re-adopts the membership.
5. **`_remoteIndex` in `FederationItemCache` grows monotonically** — never swept on entry removal; in-process only, bounded by usage volume.
6. **Vestigial config fields** — `RemoteServer.UserId` (unused under token model) and `RemoteServer.RequireApiKeyForImages` (superseded by token-gated `Peer/Images`).
7. **Stale security comment** in `FederationLibraryManager.BuildPlaybackUrl` still says it "carries the remote's real api_key"; post-rewrite it carries a scoped federation token. Doc drift only.
8. **Resume points lost on item delete/recreate** — reconciliation dedup and migrations delete/recreate virtual items, wiping all users' watch progress on those items.
9. **Federation tokens stored plaintext in config XML** — scoped and non-admin, but unencrypted at rest.
