# Player fixes: direct play of federated titles — plan and working notes

**Taking over?** Read "State right now" first, then "Steps". Everything an agent
needs to continue is in this file, `AGENTS.md` (the code) and `TODO.md` (release
checklist). Nothing lives only in a chat.

Owner's rule (2026-11-12): a federated title plays **from the server that has the
file straight to the viewer's app or browser**. The viewer's own server takes part
only when nothing else works. Reasons: the viewer's server may have little upload,
every extra hop adds delay, and a small server must not do work for a file it does
not hold. This holds with the direct link (UDP/QUIC) and without it.

This file is the plan of record for that work. Update the step list when a step is
finished, and add findings where the next agent will look for them. `TODO.md` holds
the release checklist; `AGENTS.md` describes the code. No personal data here: use
`example.com` and `203.0.113.x`.

## State right now (update this block every time you stop)

- **Released in 0.0.223:** steps 1–3 below (copy play from the owner server, both
  sides, proven on two test servers) and the linked-server rule (a linked server's
  title is never converted on the other server; the viewer is told what the device
  could not take).
- **Not yet proven on a real device.** All proof so far is the test servers in
  `scripts/`. Step 8 lists the devices.
- **After 0.0.223, test-only commits:** phone-app stage and seek stage in
  `scripts/federation_handoff_smoke.py` (steps 3 and 4).
- **Next:** real-internet timing with a disposable test server on the owner's VPS
  (see "Real-world test bench"), then steps 5, 6, 7, 7b.
- **0.0.223 is not installed on the owner's live servers** as of 2026-11-12
  evening (someone was watching; ask before any restart).
- **Known and unfixed:** see "Open problems".

## The rules, in the owner's words (2026-11-12)

1. Direct from the server that has the file to the player is always better: less
   delay, and no second server's upload in the way. "Has to work flawlessly",
   with the UDP direct link and without it, in apps and in browsers.
2. Linked servers: "I don't want it to transcode through the other server …
   it should try to direct play, if not able to then fail and explain the codec
   needed."
3. Write steps, ideas and plans into documents so any agent can take over.

## Words used here

- **Owner server**: the friend or linked server that holds the file.
- **Receiver**: the server the viewer is signed in to.
- **As stored**: the file's own bytes, unchanged (Jellyfin: direct play).
- **Copy play**: the owner server repackages the file into a streaming playlist
  without re-encoding picture or sound (ffmpeg `-c copy`). Nearly free on CPU. Not a
  conversion.
- **Conversion**: picture or sound is re-encoded.

## Where each player gets a federated title from (Direct mode friend)

| Player | Device plays the file as stored | Device needs another wrapper only | Needs picture or sound re-encoded |
| --- | --- | --- | --- |
| Android TV app, Findroid, Swiftfin, Roku, Kodi, desktop app | owner server, as stored (0.0.218, 0.0.221) | **step 1–2** | owner converts if it may, else receiver |
| Android phone app | **step 1–2** (its player reads an Http source as a playlist, so it never takes the plain file) | **step 1–2** | same |
| Browser | owner server when both are https or both http (302 from the receiver's static route); otherwise relayed by the receiver | **step 1–2** | same |

Before step 1 the two "step 1–2" cells are served by the receiver's own ffmpeg (it
reads the owner's file over http and repackages it), so every byte crosses the
receiver. That is the gap this plan closes.

Proxy mode friends and non-Jellyfin sources always go through the receiver by
design; this plan is about Direct mode Jellyfin friends.

## Facts established (do not re-derive)

- A source offered with `SupportsDirectPlay=false` makes Jellyfin hand a
  server-driven app a `TranscodingUrl` (HLS) even when the device plays the file.
  jellyfin-web still fetches the original then, through
  `Videos/{id}/stream?static=true`.
- The per-app `SupportsDirectPlay` flag only applies to sources the plugin's
  provider emits (Direct mode). In Proxy mode Jellyfin builds the source from the
  item's own address.
- Jellyfin's `MediaInfoHelper` turns direct stream off (`EnableDirectStream=false`
  unless forced), so "wrapper only" plays arrive as HLS transcode requests whose
  `TranscodeReasons` say why: `ContainerNotSupported`, or `DirectPlayError` when
  nothing but our flag stood in the way.
- A Jellyfin streaming request with `VideoCodec=copy` and `AudioCodec=copy` is taken
  as stream copy (`EncodingHelper.IsCopyCodec`); `TryStreamCopy` never turns a
  requested copy back into an encode. The variant and segment links Jellyfin writes
  repeat the request's query, so the choice holds for the whole play.
- The receiver's `Peer/Play` handoff already fetches the master playlist from the
  owner and points the player at the owner (or at the direct link relay for a home
  viewer). Copy play reuses all of it.
- The Android phone app (jellyfin-android) hands a direct-play Http source to an
  HLS-only reader. It cannot take the plain file; it can take a playlist.
- Android TV 0.19: direct play uses `source.path` when `isRemote`; direct stream
  and transcode both use `transcodingUrl`.

## Design: copy play from the owner server

Owner side (`Peer/Play/{token}/…`, `FederationRemoteTranscodeGateway`):

- A playlist or segment request for a title the caller may play is served even
  when this server converts nothing for friends (`LocalTranscodeRestriction`
  `BlockAll`, weak hardware, or converting switched off for that friend). In that
  case the request is rewritten so it **cannot** encode: `VideoCodec=copy`,
  `AudioCodec=copy`, size and bitrate limits removed, burned-in subtitles removed.
- The owner says it does this with `copyPlay` in the playback-token answer.
- A copy play counts as one stream against the friend's stream limit and data cap.
  It does not count against the conversion limit.

Receiver side (`FederationRemoteTranscodeFilter`):

- For a Direct mode friend that does not convert but offers `copyPlay`, a playlist
  request is sent to the owner when it needs no re-encoding:
  every entry of `TranscodeReasons` is `ContainerNotSupported` or
  `DirectPlayError`, no subtitle is to be burned in, stream copy is not refused by
  the player, and the source fits under the bitrate cap for that friend.
- Anything else stays as it is today (owner converts if it may; else the receiver).
- The owner's token for this is kept apart from conversion grants
  (`PeerPlaybackGrants`, the same store subtitles use), so it can never route a
  real conversion to an owner that refuses to convert.
- If the owner fails to answer, the receiver does what it did before.

Policy line: copy play is allowed wherever plain direct play is allowed. It must
never start an encoder on the owner. The rewrite on the owner is the guarantee;
the receiver's check only avoids sending requests that would then not play.

## Steps

- [x] **1. Owner serves copy plays.** (0.0.223) Token answer `copyPlay`; the
  gateway (`RemoteTranscodeGateway` action → `FederationRemoteTranscodeGateway`)
  accepts playlist/segment kinds without the conversion permission and rewrites
  them with `CopyPlay.ForceCopy`. `CopyPlay.OwnerAccepts` refuses a request that
  would need an encoder (403, so the receiver falls back as before).
- [x] **2. Receiver sends copy-eligible plays to the owner.** (0.0.223)
  `PeerTranscodeCapability.ServesCopyPlay`; `CopyPlay.Fits` in
  `FederationRemoteTranscodeFilter.ResolveAsync`; grant from
  `PeerPlaybackGrants.FindDirect` (the no-conversion store); `Handoff.CopyPlay`.
- [x] **3. Two-server proof.** (0.0.223) Stage "copy play" in
  `scripts/federation_handoff_smoke.py`: owner converts nothing; the player gets a
  playlist whose segments come from the owner; the owner's log is an
  `FFmpeg.Remux` with `-codec:v:0 copy -codec:a:0 copy`; the receiver runs no
  ffmpeg; a must-convert request is not sent as a copy play.
  The Android phone identity (`Client="Jellyfin Android"`) has its own stage: it
  is offered no plain address, Jellyfin's reasons are
  `AudioCodecNotSupported,DirectPlayError` (the first one is the false reason
  described below), and the play comes from the owner as an `FFmpeg.Remux`.
- [x] **3b. Linked servers never convert each other's titles.** (0.0.223)
  `LinkedPlayRule` + `FederationRemoteTranscodeFilter.LocalRefusal`: HTTP 409
  `linked-server-no-conversion` with a message built from `TranscodeReasons` and
  the file's tracks; the same text goes to the player's screen through Jellyfin's
  session message (`ShowRefusalAsync`). The owner converting its own file and
  sending it straight to the player is still allowed: that is not "through the
  other server". Stage in `scripts/federation_linked_smoke.py`.
- [x] **4. Seeking.** (test added after 0.0.223) Stage "seeking forward and back"
  in the handoff smoke: segments 8, 1, 4, 0 of a copy play each start (first
  picture timestamp) within 0.25 s of where the playlist says. The test file has
  keyframes about every 10.4 s; Jellyfin cuts copy-play segments on the file's
  keyframes (6.7–10.4 s here), so sparse keyframes give long segments, not wrong
  ones. **Not covered:** resume from a saved position in a real app (apps send
  `StartTimeTicks` or just ask for a later segment; both reach the owner
  unchanged), and files with keyframes more than ~20 s apart.
- [ ] **5. Sound.** When the usual track cannot be copied for the device and
  another in the viewer's language can (0.0.218 picks it), the copy play uses that
  track. When none can, say so plainly instead of failing.
- [ ] **6. Browsers.** Mixed content (https receiver, http owner) and a
  cross-origin owner: use the direct link relay or the owner's https address;
  confirm in Chromium with hls.js and with native HLS.
- [ ] **7. Receiver that converts nothing.** Offer a `TranscodingUrl` when the
  owner can copy-play, so a phone on a no-convert receiver can play at all.
- [ ] **7b. Labels.** A copy play is a handed-off HLS play, so Playback Info and
  the dashboard still call it "friend converts". Give it its own route label
  (`FederatedRoute` in `Services/FederationViewerActivity.cs`) and wording
  ("repackaged by <server>, not converted").
- [ ] **7c. Friends reached through a link** (`RemoteServer.ViaLinkedServerId`)
  are not covered by the linked rule; only `Linked` servers are. Ask the owner
  whether they should be.
- [ ] **8. Real devices.** Android TV, Android phone, desktop app, Chrome, an iOS
  app. Record what each one requested and where the bytes came from.

## Real-world test bench (owner's idea, 2026-11-12)

The smokes run every server on one machine, so they prove routing, not speed.
The owner allows disposable test servers on their VPS to measure delay and
throughput over the real internet: a test owner server there, a test receiver at
home, and a script playing the part of the player, comparing a copy play straight
from the far server with the old route through the near one. Keep test servers
apart from the live ones (own container names, own folders, own ports, generated
test clips only), never restart or reconfigure the live servers for it, and do
not delete anything afterwards without asking. Record results here.

## Jellyfin behaviour found while building this (saves you a day)

- `TranscodeReasons` can contain `AudioCodecNotSupported` although the player
  takes the sound as it is: when the user's track preference leaves Jellyfin no
  candidate track (for instance a file whose only sound track is not flagged
  default), `StreamBuilder` reports it anyway. `CopyPlay.Fits` therefore accepts
  that one reason, but only when every sound track of the file is in the
  request's `AudioCodec` list, can ride in the segment kind, and has no more
  channels than the player takes. No track number is compared, because the two
  servers number tracks differently (a separate subtitle file shifts them).
- A transcoding profile that lists the file's own picture and sound formats makes
  the play copyable even when the direct-play profile names another picture
  format: the player said it takes them in a playlist.
- The owner's variant and segment links repeat the rewritten query
  (`VideoCodec=copy&AudioCodec=copy`), without the original `AudioCodec` list.
  `OwnerAccepts` lets those through as they are.
- ffmpeg logs always contain the word `libx264` (build banner, source metadata).
  Check the command line (`-codec:v:0 …`) and the `FFmpeg.Remux` file name.
- The global `EnableRemoteTranscodeForFriends` posted through
  `Plugins/Federation/Configuration` did not stop a linked test server from
  converting; `Friends/{id}/RemoteTranscode {"Allow": false}` did. Not looked into.
- With `UseFriendsTranscoders` off the filter used to step aside completely, which
  also skipped the "this server converts nothing" refusal. It now still refuses.

## Open problems

- **A storage mount can hand every player wrong bytes.** Seen 2026-11-12 on a
  server whose library is an rclone SFTP mount with `--sftp-chunk-size 512k`:
  every read after the first 255 KiB was wrong (same length, different bytes), so
  every title from that server showed a broken picture in every player, and
  conversions logged thousands of decode errors. rclone's documentation says to
  stay at 255k or below. This is outside the plugin (its direct path copies the
  bytes Jellyfin reads), but it looks exactly like a playback bug. Check it first
  when a direct play shows blocks: compare `md5sum` of a range read through the
  mount with `rclone cat --offset N --count M` of the same range.
  *Idea:* a plugin health check that reads a small range twice by two routes, or
  spots ffmpeg logs full of "Invalid NAL unit size", and warns the admin.
- The Android phone app still plays through the viewer's server until step 3's
  phone stage and step 8 confirm copy play for it.
- Burning in a friend's separate subtitle file fails (see `TODO.md`).

## How to tell where the bytes came from

- Receiver log: `handed to <friend>: it repackages its file without converting
  (copy play)` is a copy play; `handed to <friend>: it converts` is a conversion on
  the owner; `Not converting <item> here: …` is the linked-server refusal;
  `is not a copy play from <friend> (… reasons: …)` says why a play stayed here.
- Receiver log: `handed to <friend>` lines mean the owner serves the play;
  `converting … here` or an ffmpeg start on the receiver means it does not.
- Owner ffmpeg log (`log/FFmpeg.*`): `-codec:v:0 copy` and `-codec:a:0 copy` for a
  copy play; `Remux` in the file name.
- Player's Playback Info → Federation block.

## Ideas not yet planned

- The owner could offer a plain progressive remux (`stream.mp4`, copy) for players
  that take a file address but not MKV; cheaper than a playlist for those.
- Burning a friend's separate subtitle file into the picture needs the conversion
  to run on the owner, or the file fetched to the receiver first.
- When the owner's upload is the narrow side, a lower-bitrate version on another
  server should win (the scorer already ranks by link speed; check it with copy
  plays).
- A browser could be given the owner's file directly when the owner's address is
  https and sends the right cross-origin headers; today that happens by redirect
  only when the schemes match.
