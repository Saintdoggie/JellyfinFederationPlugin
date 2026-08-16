using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Backing store for a server acting as a friend directory (see
    /// <see cref="Configuration.PluginConfiguration.HostDirectory"/>): who has
    /// registered a username here, and which invite codes are currently live.
    /// Deliberately kept out of the main XML config - this can grow with every
    /// registration, which the plugin's main config file isn't meant for (same
    /// reasoning as <see cref="FederationItemCache"/> living in its own JSON file).
    /// Holds only what a directory needs to answer "who is this username, and
    /// where do I find them" - never profile images, which stay peer-to-peer
    /// (see <c>FederationController.GetAvatar</c>).
    /// </summary>
    public class FederationDirectoryStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(1);

        private readonly ILogger<FederationDirectoryStore> _logger;
        private readonly object _lock = new();
        private readonly Dictionary<string, DirectoryEntry> _entriesByFederationId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DirectoryInvite> _invitesByCode = new(StringComparer.OrdinalIgnoreCase);
        private string? _storePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDirectoryStore"/> class.
        /// </summary>
        public FederationDirectoryStore(ILogger<FederationDirectoryStore> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Loads any previously-persisted entries/invites from disk. Safe to call
        /// more than once; missing/corrupt files are treated as "start empty"
        /// rather than a fatal error, same as <see cref="FederationItemCache"/>.
        /// </summary>
        public void Initialize(string storePath)
        {
            _storePath = storePath;

            try
            {
                if (!File.Exists(storePath))
                {
                    return;
                }

                var json = File.ReadAllText(storePath);
                var payload = JsonSerializer.Deserialize<DirectoryPayload>(json, JsonOpts);
                if (payload == null)
                {
                    return;
                }

                lock (_lock)
                {
                    _entriesByFederationId.Clear();
                    foreach (var entry in payload.Entries ?? new List<DirectoryEntry>())
                    {
                        _entriesByFederationId[entry.FederationId] = entry;
                    }

                    _invitesByCode.Clear();
                    var now = DateTime.UtcNow;
                    foreach (var invite in payload.Invites ?? new List<DirectoryInvite>())
                    {
                        if (invite.ExpiresUtc > now)
                        {
                            _invitesByCode[invite.Code] = invite;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to load directory store from {Path}; starting empty", storePath);
            }
        }

        /// <summary>
        /// Registers or updates a server's directory entry, keyed by federation id
        /// (so a server changing its address or username updates the same entry
        /// rather than creating a duplicate).
        /// </summary>
        public void Register(string username, string federationId, string serverUrl)
        {
            lock (_lock)
            {
                _entriesByFederationId[federationId] = new DirectoryEntry
                {
                    Username = username,
                    FederationId = federationId,
                    ServerUrl = serverUrl,
                    RegisteredUtc = DateTime.UtcNow
                };
            }

            Save();
        }

        /// <summary>
        /// Case-insensitive substring search by username. Capped so a broad query
        /// against a large directory can't return an unbounded payload.
        /// </summary>
        public IReadOnlyList<DirectoryEntry> Search(string usernameQuery, int limit = 25)
        {
            lock (_lock)
            {
                return _entriesByFederationId.Values
                    .Where(e => e.Username.Contains(usernameQuery, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Username, StringComparer.OrdinalIgnoreCase)
                    .Take(limit)
                    .ToList();
            }
        }

        /// <summary>
        /// Mints a short invite code pointing back at the given server, valid for
        /// <see cref="InviteLifetime"/>. Meant to be pasted into another server's
        /// "join with a code" field as a friendlier alternative to typing a full
        /// URL - redeeming one is still just an ordinary friend request under the
        /// hood (see <see cref="FederationDirectoryService.RedeemInviteAsync"/>),
        /// this only makes finding the address easier.
        /// </summary>
        public string CreateInvite(string serverUrl, string federationId)
        {
            var code = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            lock (_lock)
            {
                _invitesByCode[code] = new DirectoryInvite
                {
                    Code = code,
                    ServerUrl = serverUrl,
                    FederationId = federationId,
                    ExpiresUtc = DateTime.UtcNow.Add(InviteLifetime)
                };
            }

            Save();
            return code;
        }

        /// <summary>
        /// Resolves an invite code to the server it points at, or null if the code
        /// is unknown or has expired. Codes are single-purpose lookups, not
        /// single-use - redeeming one more than once (e.g. a retry after a network
        /// error) is harmless, since the actual friend request it triggers is
        /// itself idempotent-safe (<see cref="FederationFriendService.SendFriendRequestAsync"/>
        /// already refuses to re-request an existing friend/pending request).
        /// </summary>
        public DirectoryInvite? ResolveInvite(string code)
        {
            lock (_lock)
            {
                if (_invitesByCode.TryGetValue(code, out var invite) && invite.ExpiresUtc > DateTime.UtcNow)
                {
                    return invite;
                }

                return null;
            }
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_storePath))
            {
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                DirectoryPayload payload;
                lock (_lock)
                {
                    payload = new DirectoryPayload
                    {
                        Entries = _entriesByFederationId.Values.ToList(),
                        Invites = _invitesByCode.Values.Where(i => i.ExpiresUtc > DateTime.UtcNow).ToList()
                    };
                }

                var json = JsonSerializer.Serialize(payload, JsonOpts);
                var tempPath = _storePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _storePath, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to save directory store to {Path}", _storePath);
            }
        }

        private class DirectoryPayload
        {
            public List<DirectoryEntry>? Entries { get; set; }

            public List<DirectoryInvite>? Invites { get; set; }
        }
    }

    /// <summary>
    /// One server's registration in a friend directory.
    /// </summary>
    public class DirectoryEntry
    {
        /// <summary>Gets or sets the registered username.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Gets or sets the registering server's persistent federation id.</summary>
        public string FederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets the registering server's address.</summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets when this entry was last registered/updated.</summary>
        public DateTime RegisteredUtc { get; set; }
    }

    /// <summary>
    /// A short-lived invite code minted by <see cref="FederationDirectoryStore.CreateInvite"/>.
    /// </summary>
    public class DirectoryInvite
    {
        /// <summary>Gets or sets the code itself.</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Gets or sets the inviting server's address.</summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>Gets or sets the inviting server's persistent federation id.</summary>
        public string FederationId { get; set; } = string.Empty;

        /// <summary>Gets or sets when this code stops being redeemable.</summary>
        public DateTime ExpiresUtc { get; set; }
    }
}
