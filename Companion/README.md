# Federation Companion

A standalone app a Plex-owning friend runs on their own machine to control what they share with federated Jellyfin servers - no Jellyfin required on their end.

Unlike the original setup (a Jellyfin admin manually enters the friend's raw Plex token into the Federation plugin), this app lets the Plex owner sign in themselves, pick which libraries to share, and generate a one-time connect code that links a Jellyfin friend's Federation plugin automatically. The Jellyfin friend does **not** need to be on the same Tailscale tailnet: Companion prefers Plex Remote Access / Plex Relay for the actual media path, and a Tailscale Funnel URL is only needed if you want the one-time claim to go through this app.

## Install

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.ps1 | iex
```

Either command downloads a self-contained build (no separate .NET install needed), unpacks it to `~/FederationCompanion` (or `%USERPROFILE%\FederationCompanion` on Windows), and starts it. It prints a local URL - open that in a browser to continue.

To run from source instead:

```bash
cd Companion
dotnet run
```

Then open the printed local URL (defaults to an ASP.NET Core-assigned port; set `ASPNETCORE_URLS` to pin one, e.g. `ASPNETCORE_URLS=http://127.0.0.1:7890 dotnet run`).

State (Plex token, server address, public URL, library sharing choices, connected peers) is stored in `companion-state.json` next to the executable - delete it to fully reset/sign out.

## Walkthrough

The app is a single page, worked top to bottom:

**1. Tailscale.** The app checks whether Tailscale is installed and signed in on this machine, and shows the exact command to run if not (`winget`/`brew`/`curl` depending on OS). It never runs anything on your behalf here - Tailscale changes network configuration, so you review and run the command yourself.

![Tailscale, public address, and Plex connection steps](docs/screenshots/companion-setup-steps.jpg)

**2. Public address.** Optional if Plex Remote Access or Plex Relay is already enabled. Funnel is the public Tailscale hostname (`https://...ts.net`) a Jellyfin friend can call *without joining your tailnet*. Do not paste a `100.x` tailnet address here — that only works for people already on your Tailscale.

**3. Plex connection.** Sign in with your Plex account (opens Plex's own sign-in page - your password never touches this app), or paste a local Plex address + token if you do not want to use plex.tv.

**4. Libraries to share.** Toggle which of your Plex libraries are visible to federated friends. Off by default; re-scanning never resets a choice you've already made.

**5. Connect a Jellyfin friend.** Generate a one-time connect code and send it to your friend. They paste it into Jellyfin Federation and connect over the internet — they do not need to join your Tailscale. Codes expire after 15 minutes; claim codes can only be used once.

![Connect code and connected friends list](docs/screenshots/companion-connect-friend.jpg)

**6. Import from a Jellyfin friend.** Paste a connect code from their Federation plugin Companion tab. Companion pulls what they share and **adds Movies/Shows libraries to your Plex automatically** — manage friends from this page (Import into Plex / Sync / Remove) instead of creating libraries by hand in Plex Settings. A background sync keeps the export current every 30 minutes. Removing a friend here only stops syncing; it never deletes files already written.

## Status

- [x] Standalone Kestrel web app, runs on a local port
- [x] Plex OAuth sign-in (PIN flow - no password ever touches this app)
- [x] Library picker with persisted sharing choices
- [x] Tailscale detection and setup guidance
- [x] Public URL configuration
- [x] Connect-code exchange - linking to a Jellyfin server is approved from this side, not just the admin's
- [x] Peer list with revoke
- [x] Import from a Jellyfin friend - connect-code exchange in the other direction, with an automatic `.strm` export and Plex section refresh
- [ ] Phase 3: pool invites (send/receive/accept) and richer peer management
- [ ] Bandwidth limit control (raised as a real requirement by a prospective federation friend)

## Building a release yourself

`.github/workflows/companion-release.yml` builds all four platforms and publishes them to the repo's `companion-latest` release automatically on every push that touches `Companion/**`. To do it locally instead:

```bash
dotnet publish Companion/FederationCompanion.csproj -c Release -r <rid> --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/<rid>
```

where `<rid>` is one of `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.
