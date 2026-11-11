namespace Jellyfin.Plugin.Federation.Services
{
    using System;
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
                && Effective(config) == LocalTranscodeRestriction.None;

        /// <summary>
        /// True when this server may convert a federated title for its own
        /// viewers. False under <see cref="LocalTranscodeRestriction.BlockAll"/>,
        /// chosen by the admin or by the automatic hardware check; an
        /// unknown/absent configuration means no restriction, matching how the
        /// rest of the plugin treats a missing config.
        /// </summary>
        public static bool MayConvertForOwnViewers(PluginConfiguration? config)
            => Effective(config) != LocalTranscodeRestriction.BlockAll;

        /// <summary>
        /// Answers "is this server's hardware too weak to convert video?". Set once
        /// at startup; null (tests, early startup) means "not known to be weak".
        /// </summary>
        internal static Func<bool>? WeakHardwareProbe { get; set; }

        /// <summary>
        /// Test seam: a probe that applies only to the calling test's flow (and is
        /// never cached), so a test can make the hardware "weak" without other tests
        /// running in parallel seeing it.
        /// </summary>
        internal static System.Threading.AsyncLocal<Func<bool>?> TestProbe { get; } = new();

        /// <summary>
        /// The restriction in force: the admin's choice, except that the automatic
        /// setting becomes "never convert" on hardware that cannot do it.
        /// </summary>
        public static LocalTranscodeRestriction Effective(PluginConfiguration? config)
        {
            var chosen = config?.LocalTranscodeRestriction ?? LocalTranscodeRestriction.None;
            if (chosen == LocalTranscodeRestriction.AlwaysAllow)
            {
                return LocalTranscodeRestriction.None;
            }

            return chosen == LocalTranscodeRestriction.None && config != null && IsWeak()
                ? LocalTranscodeRestriction.BlockAll
                : chosen;
        }

        /// <summary>True when the automatic check is what stops this server converting.</summary>
        public static bool BlockedByHardware(PluginConfiguration? config)
            => config?.LocalTranscodeRestriction == LocalTranscodeRestriction.None && IsWeak();

        private static long _checkedTicks;
        private static bool _weak;

        // The probe reads Jellyfin's settings; hot paths ask many times a second.
        private static bool IsWeak()
        {
            var local = TestProbe.Value;
            if (local != null)
            {
                try
                {
                    return local();
                }
                catch (Exception)
                {
                    return false;
                }
            }

            var probe = WeakHardwareProbe;
            if (probe == null)
            {
                return false;
            }

            var now = Environment.TickCount64;
            var last = System.Threading.Interlocked.Read(ref _checkedTicks);
            if (last != 0 && now - last < 60_000)
            {
                return System.Threading.Volatile.Read(ref _weak);
            }

            bool weak;
            try
            {
                weak = probe();
            }
            catch (Exception)
            {
                weak = false;
            }

            System.Threading.Volatile.Write(ref _weak, weak);
            System.Threading.Interlocked.Exchange(ref _checkedTicks, now == 0 ? 1 : now);
            return weak;
        }

        /// <summary>Test seam: forgets the cached answer.</summary>
        internal static void ResetProbeCache() => System.Threading.Interlocked.Exchange(ref _checkedTicks, 0);
    }
}
