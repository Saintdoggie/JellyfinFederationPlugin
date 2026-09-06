namespace FederationCompanion;

/// <summary>Imported libraries are never eligible for onward sharing.</summary>
public static class CompanionLibraryPolicy
{
    public static bool IsImported(CompanionState state, CompanionLibrary library)
    {
        if (state.ImportPeers.Any(p => p.PlexMovieSectionKey == library.SectionKey || p.PlexShowSectionKey == library.SectionKey)) return true;
        var roots = state.ImportPeers.Select(p => p.ExportPath)
            .Concat(new[] { state.PlexVisibleImportRoot, state.PlexMountRoot, state.MediaMountRoot,
                Path.Combine(AppContext.BaseDirectory, "imported") })
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Replace('\\', '/').TrimEnd('/'));
        return library.Locations.Any(location => roots.Any(root =>
            string.Equals(location.Replace('\\', '/').TrimEnd('/'), root, StringComparison.OrdinalIgnoreCase)
            || location.Replace('\\', '/').StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)));
    }

    public static void PrepareServerChange(CompanionState state, string? machineIdentifier)
    {
        if (!string.IsNullOrEmpty(machineIdentifier) && machineIdentifier == state.ServerMachineIdentifier) return;
        // Section keys are only unique within a Plex server. Never reuse consent
        // or library attachment keys just because the new server also has "1".
        state.Libraries.Clear();
        foreach (var peer in state.ImportPeers)
        {
            peer.PlexMovieSectionKey = null;
            peer.PlexShowSectionKey = null;
            peer.PlexSectionKey = null;
        }
    }

    public static bool IsShared(CompanionState state, CompanionLibrary library)
        => library.Shared && !IsImported(state, library);
}
