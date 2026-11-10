# Federation Plex add-in

This optional add-in adds Plex catalogs, Companion connections, Plex metadata and guided imports, and `.strm` export to Jellyfin Federation. Ordinary Jellyfin friends do not need it.

The add-in and core plugin must have the same version. This package is for Federation **0.0.208** on Jellyfin 12.

## Install or upgrade

1. Install or upgrade the main **Jellyfin Federation** plugin first.
2. Stop Jellyfin. This is required on Windows, which can lock loaded DLLs.
3. Open the existing **Jellyfin Federation** plugin folder and copy `Jellyfin.Plugin.Federation.Plex.dll` directly into that folder, beside `Jellyfin.Plugin.Federation.dll`. Replace an older add-in DLL with the same name.
4. Do not create a separate Plex plugin folder and do not copy a second core DLL from another package.
5. Start Jellyfin. In Dashboard, open **Federation**, then **Friends → Open Plex add-in**. A **Federation Plex** dashboard page is also registered.

Keep the normal Federation configuration and cache files in place. The add-in does not need its own configuration file.

## Remove

1. Stop Jellyfin.
2. Remove only `Jellyfin.Plugin.Federation.Plex.dll` from the Jellyfin Federation plugin folder. Leave the core DLL, configuration, and cache untouched.
3. Start Jellyfin.

Existing Plex and Companion settings, mappings, and cached titles are retained. They cannot refresh or play while the add-in is absent; Jellyfin friends continue working. Reinstalling the matching add-in enables those sources again.
