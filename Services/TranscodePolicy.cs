namespace Jellyfin.Plugin.Federation.Services
{
    using Jellyfin.Plugin.Federation.Configuration;

    /// <summary>
    /// Single place that answers "may this server convert video?", so the owner
    /// side (friends converting here), the receiver side (this server converting
    /// a friend's title for its own viewers) and smart placement cannot drift
    /// apart. <see cref="LocalTranscodeRestriction.BlockFriends"/> and
    /// <see cref="LocalTranscodeRestriction.BlockAll"/> both refuse friends'
    /// conversions; only <see cref="LocalTranscodeRestriction.BlockAll"/> stops
    /// this server converting for its own viewers. Whether a *friend* converts
    /// for this server's viewers is a separate decision and is never limited
    /// here - that is the point of the switch on a weak server.
    /// </summary>
    internal static class TranscodePolicy
    {
        /// <summary>
        /// True when a friend may convert this server's own titles here. Also
        /// honours the older <see cref="PluginConfiguration.EnableRemoteTranscodeForFriends"/>
        /// switch, so either control can refuse and existing installs keep their
        /// behaviour.
        /// </summary>
        public static bool FriendsMayConvertHere(PluginConfiguration? config)
            => config?.EnableRemoteTranscodeForFriends == true
                && config.LocalTranscodeRestriction == LocalTranscodeRestriction.None;

        /// <summary>
        /// True when this server may convert a federated title for its own
        /// viewers. False only under <see cref="LocalTranscodeRestriction.BlockAll"/>;
        /// an unknown/absent configuration means no restriction, matching how the
        /// rest of the plugin treats a missing config.
        /// </summary>
        public static bool MayConvertForOwnViewers(PluginConfiguration? config)
            => config?.LocalTranscodeRestriction != LocalTranscodeRestriction.BlockAll;
    }
}
