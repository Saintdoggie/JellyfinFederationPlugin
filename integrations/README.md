# Federation integration catalog

This directory is the reviewed catalog consumed by the Federation settings page's **Integrations** tab. The plugin filters entries by Federation version, Jellyfin ABI, and platform. If GitHub is unavailable it uses the copy bundled into the plugin.

## Current security boundary

The catalog is **discovery-only**. The plugin does not download packages, execute catalog scripts, render catalog HTML, or install third-party DLLs. An `addin` entry can link to reviewed installation instructions and report whether its already-installed built-in provider is present.

Do not submit executable package URLs, post-install scripts, remote HTML/JavaScript, credentials, or arbitrary provider URLs. A future executable installer needs signed packages, signed catalog metadata, rollback/quarantine behavior, and an explicit trust review. In-process .NET add-ins are fully trusted; `AssemblyLoadContext` is not a sandbox.

## Entry rules

- `schemaVersion` must be `1` and the catalog is limited to 64 entries.
- IDs are immutable and unique. Use reverse-DNS naming.
- `type` is `declarative` or `addin`.
- Declarative entries may only name a `builtinProviderId` already compiled and reviewed in core. They cannot point to code.
- Executable add-ins are currently limited to the first-party `Plex` provider. Third-party executable submissions are not accepted yet.
- Set inclusive `minHostVersion` / `maxHostVersion`, `jellyfinAbi`, supported `platforms`, and a plain-text capability list.
- Text is plain text only. Installation guides must be HTTPS GitHub URLs.
- Update the bundled catalog and its compatibility range in the same review as the relevant core/add-in release. Core and Plex add-in versions must match exactly.

Run `python3 scripts/test_integrations.py` after editing the catalog. The normal .NET suite also tests compatibility filtering and malformed metadata.
