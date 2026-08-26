# Federation Companion

A standalone app a Plex-owning friend runs on their own machine to control what they share with federated Jellyfin servers - no Jellyfin required on their end.

Unlike today's setup (a Jellyfin admin manually enters the friend's raw Plex token into the Federation plugin), this app lets the Plex owner sign in themselves, pick which libraries to share, and eventually approve/revoke Jellyfin peers and manage bandwidth limits and pool invites from their own local UI.

## Status: Phase 1 (core)

- [x] Standalone Kestrel web app, runs on a local port
- [x] Plex OAuth sign-in (PIN flow - no password ever touches this app)
- [x] Library picker with persisted sharing choices
- [ ] Phase 2: connect-code exchange so linking to a Jellyfin server is approved from this side, not just the admin's
- [ ] Phase 3: pool invites (send/receive/accept) and a peer management dashboard (revoke access per peer)
- [ ] Bandwidth limit control (raised as a real requirement by a prospective federation friend)
- [ ] HTTPS/Tailscale-only reachability options (same friend's requirement - see project memory)

## Running it

```
cd Companion
dotnet run
```

Then open the printed local URL (defaults to an ASP.NET Core-assigned port; set `ASPNETCORE_URLS` to pin one, e.g. `ASPNETCORE_URLS=http://127.0.0.1:7890 dotnet run`).

State (Plex token, server address, library sharing choices) is stored in `companion-state.json` next to the built executable - delete it to fully reset/sign out.
