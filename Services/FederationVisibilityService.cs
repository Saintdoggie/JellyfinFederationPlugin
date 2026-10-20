using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Makes a friend's per-user rules hide titles, not just refuse to play them.
    /// </summary>
    /// <remarks>
    /// Jellyfin stores one copy of each federated item for every local user, so a
    /// rule that applies to one user can't change the item itself. Instead every
    /// local user a friend restricts gets one private tag
    /// (<see cref="HiddenTagFor"/>), the tag is stamped on each federated item that
    /// user may not see, and the tag is added to that user's own
    /// <c>BlockedTags</c> policy. Jellyfin filters blocked tags in the database
    /// query against the item, its series, every parent and its library, so the
    /// title disappears from grids, search, Latest, Next Up, resume and direct
    /// links alike - for that user only.
    /// <para>
    /// The decision per item is <see cref="RemoteAccessControlService.Evaluate"/>,
    /// the same function playback uses, so seeing and playing never disagree. A
    /// title several friends share stays visible while any enabled source still
    /// allows the user.
    /// </para>
    /// </remarks>
    public sealed class FederationVisibilityService
    {
        /// <summary>Prefix of every tag this service owns.</summary>
        public const string HiddenTagPrefix = "federation-hidden-";

        private readonly ILogger<FederationVisibilityService> _logger;
        private readonly FederationItemCache _cache;
        private readonly IUserManager? _users;
        private readonly SemaphoreSlim _policyGate = new(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationVisibilityService"/> class.
        /// </summary>
        public FederationVisibilityService(
            ILogger<FederationVisibilityService> logger,
            FederationItemCache cache,
            IUserManager users)
        {
            _logger = logger;
            _cache = cache;
            _users = users;
        }

        /// <summary>Test constructor: no user policy access.</summary>
        internal FederationVisibilityService(ILogger<FederationVisibilityService> logger, FederationItemCache cache)
        {
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// The hide tag for one local user. A hash rather than the id itself: the
        /// tag is visible on items to other users, and it should say nothing about
        /// who it hides them from.
        /// </summary>
        public static string HiddenTagFor(Guid localUserId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("federation-hidden:" + localUserId.ToString("N")));
            return HiddenTagPrefix + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        /// <summary>True for a tag this service owns.</summary>
        public static bool IsHiddenTag(string? tag)
            => tag != null && tag.StartsWith(HiddenTagPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Captures the rules in force right now, for one reconcile pass. Cheap to
        /// take; evaluate many entries against one snapshot.
        /// </summary>
        public Snapshot TakeSnapshot(PluginConfiguration? config)
            => new(config?.RemoteServers ?? new List<RemoteServer>(), _cache, LocalUserIds());

        /// <summary>
        /// This server's user ids, or null when unknown (tests). A friend's rule for
        /// an id that isn't a user here hides nothing and must not tag every item.
        /// </summary>
        private HashSet<Guid>? LocalUserIds()
        {
            try
            {
                return _users?.GetUsers().Select(u => u.Id).ToHashSet();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not list local users for friend-rule hiding");
                return null;
            }
        }

        /// <summary>
        /// Local users at least one enabled friend restricts in a way that can
        /// hide something (see <see cref="RemoteAccessControlService.RestrictsVisibility"/>).
        /// </summary>
        public static HashSet<Guid> RestrictedUsers(IEnumerable<RemoteServer> servers)
        {
            var users = new HashSet<Guid>();
            foreach (var server in servers.Where(s => s.Enabled))
            {
                foreach (var rule in server.FriendUserAccessRules ?? new List<RemoteUserAccessRule>())
                {
                    if (Guid.TryParse(rule.RemoteUserId, out var id)
                        && id != Guid.Empty
                        && RemoteAccessControlService.RestrictsVisibility(rule))
                    {
                        users.Add(id);
                    }
                }
            }

            return users;
        }

        /// <summary>
        /// Adds this service's tag to the <c>BlockedTags</c> policy of every
        /// restricted user and removes it from everyone else. Leaves every other
        /// tag, and every other policy field, exactly as the admin set it. Returns
        /// how many users changed. Never throws.
        /// </summary>
        public async Task<int> ApplyUserPoliciesAsync(PluginConfiguration? config, CancellationToken cancellationToken = default)
        {
            if (_users == null || config == null)
            {
                return 0;
            }

            await _policyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var restricted = RestrictedUsers(config.RemoteServers ?? new List<RemoteServer>());
                var changed = 0;
                foreach (var user in _users.GetUsers().ToList())
                {
                    try
                    {
                        var policy = _users.GetUserDto(user).Policy;
                        if (policy == null)
                        {
                            continue;
                        }

                        var current = policy.BlockedTags ?? Array.Empty<string>();
                        var desired = DesiredBlockedTags(current, user.Id, restricted.Contains(user.Id));
                        if (desired.SequenceEqual(current, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        policy.BlockedTags = desired;
                        await _users.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                        changed++;
                        _logger.LogInformation(
                            "[Federation] {Action} friend-rule hiding for local user {User}",
                            restricted.Contains(user.Id) ? "Turned on" : "Turned off",
                            user.Username);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Could not update the hidden-title policy of local user {User}", user.Username);
                    }
                }

                return changed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not apply friend-rule hiding to user policies");
                return 0;
            }
            finally
            {
                _policyGate.Release();
            }
        }

        /// <summary>
        /// The user's blocked tags with exactly this service's tag for them when
        /// <paramref name="restricted"/>, and none of this service's tags otherwise.
        /// A stale tag of ours (another user's, or one from a renamed scheme) is
        /// dropped; the admin's own tags keep their order.
        /// </summary>
        internal static string[] DesiredBlockedTags(IReadOnlyCollection<string> current, Guid userId, bool restricted)
        {
            var kept = current.Where(t => !IsHiddenTag(t)).ToList();
            if (restricted)
            {
                kept.Add(HiddenTagFor(userId));
            }

            return kept.ToArray();
        }

        /// <summary>
        /// Rules in force for one pass. <see cref="HiddenTags"/> is the only thing
        /// reconciliation needs per item.
        /// </summary>
        public sealed class Snapshot
        {
            private readonly Dictionary<string, RemoteServer> _servers;
            private readonly List<Guid> _restricted;
            private readonly FederationItemCache _cache;

            internal Snapshot(IEnumerable<RemoteServer> servers, FederationItemCache cache, IReadOnlySet<Guid>? localUsers = null)
            {
                var list = servers.ToList();
                _servers = list
                    .Where(s => !string.IsNullOrEmpty(s.Id))
                    .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                _restricted = RestrictedUsers(list)
                    .Where(id => localUsers == null || localUsers.Contains(id))
                    .OrderBy(id => id)
                    .ToList();
                _cache = cache;
            }

            /// <summary>True when no user is restricted at all (the common case).</summary>
            public bool IsEmpty => _restricted.Count == 0;

            /// <summary>
            /// The hide tags <paramref name="entry"/> must carry, sorted: one per
            /// restricted user that no enabled source lets see it. An entry with no
            /// enabled source is left alone; disabling/removing a server already
            /// takes its titles away.
            /// </summary>
            public IReadOnlyList<string> HiddenTags(FederatedCacheEntry? entry)
            {
                if (entry == null || _restricted.Count == 0)
                {
                    return Array.Empty<string>();
                }

                var sources = entry.GetSourcesSnapshot()
                    .Select(s => (Source: s, Server: _servers.GetValueOrDefault(s.ServerId)))
                    .Where(x => x.Server is { Enabled: true })
                    .ToList();
                if (sources.Count == 0)
                {
                    return Array.Empty<string>();
                }

                List<string>? tags = null;
                foreach (var user in _restricted)
                {
                    var visible = sources.Any(x => RemoteAccessControlService.Evaluate(
                        x.Server!, user, entry.MappingName, x.Source.RemoteItemId, _cache, out _));
                    if (!visible)
                    {
                        (tags ??= new List<string>()).Add(HiddenTagFor(user));
                    }
                }

                if (tags == null)
                {
                    return Array.Empty<string>();
                }

                tags.Sort(StringComparer.Ordinal);
                return tags;
            }
        }
    }
}
