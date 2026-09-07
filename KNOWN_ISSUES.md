# Known issues

Bigger items were fixed in 0.0.78/0.0.79; these are smaller and set aside:

1. **Version skew with old peers** — a friend running a pre-0.0.70 plugin can't use scoped tokens; handshake is rejected with an upgrade message rather than mixing protocols. Fix = both sides upgrade.
2. **Disabled servers' deletions don't propagate while disabled** — sync skips disabled servers entirely; remote-side deletions appear only after re-enable + next sync (offline servers propagate on next successful sync).
3. **`LeavePool` is reversible by the next pool notice** — leaving a pool doesn't notify members, and a subsequent roster fan-out re-adopts the membership.
4. **Vestigial config fields** — `RemoteServer.UserId` (unused under token model) and `RemoteServer.RequireApiKeyForImages` (superseded by token-gated `Peer/Images`).
5. **Resume points lost on item delete/recreate** — reconciliation dedup and migrations delete/recreate virtual items, wiping all users' watch progress on those items.
6. **Deleting a local Jellyfin user leaves stale federation state** — per-user access rules pushed by friends (`FriendUserAccessRules`) and cached session tokens for that user are never swept (no user-deletion hook exists). Inert after deletion, but accumulates.
7. **Direct-mode static source relays through this server** — the Play-button fix routes the stamped static Path through the local proxy gateway (a relay hop). Direct client→remote fetching is still available for the provider-emitted sources where applicable, but the default source relays.

## Resolved in the current quality pass

- The persisted remote-item index is rebuilt on startup and stale index entries
  are swept when cache entries are removed.
- Federation, relay, and Plex credentials are encrypted before configuration is
  written to disk and decrypted only into the plugin's in-memory configuration.
- WAN bitrate limits now request lower-bitrate remote transcodes in Direct mode
  and pace bytes in the local relay for Proxy and Plex sources.

- The directional missing-Play-button report was traced to a coarse guard that
  blanked every federated item path as soon as any incoming per-user rule existed.
  Paths are now evaluated per item across all configured rules, so universally
  allowed items remain playable without weakening restrictive items.
- Local stream URLs are no longer enumerable server/item pairs. They carry a
  scoped HMAC capability and are revalidated against current server, cache, source,
  and access-rule state at stream time.

## Plex repair remaining after 0.0.133

- Legacy Companion `.strm` exports do not give native Plex playable video/audio.
  0.0.133 exposes real media through a read-only rclone mount; see
  `Companion/README.md`. Existing legacy Plex libraries are not automatically
  deleted during migration.
- Windows still needs WinFsp, but Companion/install.ps1 installs it during setup
  (one UAC prompt). Companion also starts the media folder by itself and ships or
  downloads rclone. Linux managed mounts and Plex media decode have passed
  disposable live tests; Windows host permissions, real Plex clients, and the
  friend's actual Funnel still need validation.
- Source files without size/container or episode numbering are reported in the
  import catalog and omitted from the mount. Multi-part videos need additional
  design; combined episode ranges are preserved from source metadata.
- Revocation blocks new upstream requests. Bytes already downloaded or held in
  Plex/rclone caches cannot be remotely erased. Failed source reads preserve the
  last successful catalog; successful revocation/deletion removes its entries.
