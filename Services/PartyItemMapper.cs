using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Translates watch-party titles between servers. Each server plays its own copy:
    /// a title this server owns, a friend's title it has as a federated copy, or (by
    /// provider ids) its own file of the same movie or episode.
    /// </summary>
    public sealed class PartyItemMapper
    {
        private static readonly string[] ProviderKeys = { "Imdb", "Tmdb", "Tvdb" };

        private readonly ILibraryManager _library;
        private readonly FederationLibraryManager _federation;
        private readonly FederationPeerAccessService _peerAccess;

        /// <summary>
        /// Initializes a new instance of the <see cref="PartyItemMapper"/> class.
        /// </summary>
        public PartyItemMapper(ILibraryManager library, FederationLibraryManager federation, FederationPeerAccessService peerAccess)
        {
            _library = library;
            _federation = federation;
            _peerAccess = peerAccess;
        }

        /// <summary>
        /// Describes a local title for <paramref name="peer"/>. A title this server
        /// owns but doesn't share with that friend is described by kind only, so the
        /// friend learns nothing about it and simply can't play it.
        /// </summary>
        public PartyItemRef Describe(Guid itemId, RemoteServer peer)
        {
            var item = _library.GetItemById(itemId);
            if (item == null)
            {
                return new PartyItemRef();
            }

            var config = Plugin.Instance?.Configuration;
            var reference = new PartyItemRef { Type = item.GetBaseItemKind().ToString() };
            var key = FederationLibraryManager.GetFederationKey(item);
            if (key != null)
            {
                var entry = _federation.Cache.GetEntryByKey(key);
                foreach (var source in entry?.GetSourcesSnapshot() ?? Array.Empty<FederatedSource>())
                {
                    var owner = _federation.GetServer(source.ServerId);
                    if (!string.IsNullOrEmpty(owner?.FederationId))
                    {
                        reference.Owners.Add(new PartyOwnerRef { Fid = owner.FederationId, Id = source.RemoteItemId.ToString("N") });
                    }
                }
            }
            else
            {
                if (FederationLibraryManager.IsIneligibleForOutgoingShare(item)
                    || !_peerAccess.IsItemVisible(peer, null, item.Id))
                {
                    return reference;
                }

                if (!string.IsNullOrEmpty(config?.LocalFederationId))
                {
                    reference.Owners.Add(new PartyOwnerRef { Fid = config.LocalFederationId, Id = item.Id.ToString("N") });
                }
            }

            reference.Name = item.Name;
            reference.ProviderIds = Providers(item.ProviderIds);
            if (item is Episode episode)
            {
                reference.Season = episode.ParentIndexNumber;
                reference.Episode = episode.IndexNumber;
                reference.SeriesProviderIds = Providers(episode.Series?.ProviderIds);
            }

            return reference;
        }

        /// <summary>
        /// This server's copy of a party title, or null when it has none: the owner's
        /// own id when this server is the owner, its federated copy of a friend's
        /// title, or its own file with the same provider ids.
        /// </summary>
        public Guid? Resolve(PartyItemRef reference)
        {
            var config = Plugin.Instance?.Configuration;
            foreach (var owner in reference.Owners ?? new List<PartyOwnerRef>())
            {
                if (!Guid.TryParse(owner.Id, out var id) || string.IsNullOrEmpty(owner.Fid))
                {
                    continue;
                }

                if (string.Equals(owner.Fid, config?.LocalFederationId, StringComparison.Ordinal))
                {
                    var own = _library.GetItemById(id);
                    if (own != null && FederationLibraryManager.GetFederationKey(own) == null)
                    {
                        return own.Id;
                    }

                    continue;
                }

                var friend = config?.RemoteServers?.FirstOrDefault(s => s.Enabled && string.Equals(s.FederationId, owner.Fid, StringComparison.Ordinal));
                var key = friend == null ? null : _federation.Cache.TryGetLocalKeyForRemoteItem(friend.Id, id);
                var entry = key == null ? null : _federation.Cache.GetEntryByKey(key);
                if (entry != null)
                {
                    var localId = _federation.ComputeItemId(entry);
                    if (_library.GetItemById(localId) != null)
                    {
                        return localId;
                    }
                }
            }

            return ResolveByProviders(reference);
        }

        /// <summary>The provider ids worth matching on (imdb, tmdb, tvdb), or null.</summary>
        internal static Dictionary<string, string>? Providers(IReadOnlyDictionary<string, string>? ids)
        {
            if (ids == null)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ProviderKeys)
            {
                var match = ids.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Value))
                {
                    result[name] = match.Value.Trim();
                }
            }

            return result.Count == 0 ? null : result;
        }

        private Guid? ResolveByProviders(PartyItemRef reference)
        {
            if (string.Equals(reference.Type, nameof(BaseItemKind.Episode), StringComparison.Ordinal))
            {
                if (reference.SeriesProviderIds is not { Count: > 0 } || reference.Season == null || reference.Episode == null)
                {
                    return null;
                }

                var shows = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    HasAnyProviderId = reference.SeriesProviderIds,
                    Recursive = true
                });
                foreach (var show in shows)
                {
                    var episode = _library.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { BaseItemKind.Episode },
                        AncestorIds = new[] { show.Id },
                        ParentIndexNumber = reference.Season,
                        IndexNumber = reference.Episode,
                        Recursive = true,
                        Limit = 1
                    }).FirstOrDefault();
                    if (episode != null)
                    {
                        return episode.Id;
                    }
                }

                return null;
            }

            if (reference.ProviderIds is not { Count: > 0 }
                || !Enum.TryParse<BaseItemKind>(reference.Type, out var kind)
                || kind is not (BaseItemKind.Movie or BaseItemKind.MusicVideo or BaseItemKind.Video))
            {
                return null;
            }

            return _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                HasAnyProviderId = reference.ProviderIds,
                Recursive = true,
                Limit = 1
            }).FirstOrDefault()?.Id;
        }
    }
}
