# Bundled native libraries

`libmsquic.so.2` (Microsoft MsQuic 2.6.3, MIT licence, see `LICENSE-msquic.txt`) for
linux-x64 and linux-arm64, taken unmodified from Microsoft's Debian 12 packages
(`packages.microsoft.com/debian/12/prod`, `libmsquic_2.6.3_{amd64,arm64}.deb`).
Checksums in `SHA256SUMS`. Needs `libcrypto.so.3` and `libnuma.so.1`, which the
official Jellyfin images (12.0, 12.2) already ship.

.NET's `System.Net.Quic` needs this library on Linux, and Jellyfin's images don't
include it. The plugin loads it from its own folder (`DirectLink/MsQuicLoader.cs`)
before anything touches `System.Net.Quic`, which powers the friend-to-friend direct link.
Windows ships MsQuic with .NET itself, so nothing is bundled for it.
