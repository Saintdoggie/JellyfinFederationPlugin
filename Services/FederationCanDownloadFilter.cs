using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Shows the Download button on friends' titles in every app.
    /// </summary>
    /// <remarks>
    /// Jellyfin reports <c>CanDownload = false</c> for anything that isn't a local
    /// file, and every app (web, Android, Findroid, Swiftfin) hides Download on
    /// that. For a friend's video or audio title that the viewer may download (see
    /// <see cref="FederationDeviceDownloadService.CanOffer"/>), this turns it back
    /// on in item responses; <see cref="FederationDeviceDownloadFilter"/> then
    /// serves the download. Only items whose response asked for the field and came
    /// back false are looked at, and a failure leaves the response as it was.
    /// </remarks>
    public sealed class FederationCanDownloadFilter : IAsyncResultFilter
    {
        private readonly IAuthorizationContext _authorization;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly FederationDeviceDownloadService _downloads;
        private readonly ILogger<FederationCanDownloadFilter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationCanDownloadFilter"/> class.
        /// </summary>
        public FederationCanDownloadFilter(
            IAuthorizationContext authorization,
            IUserManager users,
            ILibraryManager library,
            FederationDeviceDownloadService downloads,
            ILogger<FederationCanDownloadFilter> logger)
        {
            _authorization = authorization;
            _users = users;
            _library = library;
            _downloads = downloads;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            try
            {
                if (context.Result is ObjectResult { Value: { } value })
                {
                    var candidates = Candidates(value).ToList();
                    if (candidates.Count > 0)
                    {
                        await ApplyAsync(context, candidates).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not mark friends' titles downloadable");
            }

            await next().ConfigureAwait(false);
        }

        /// <summary>
        /// Items in a response that may be a friend's file Jellyfin refused to
        /// offer: the field was asked for and is false, the item isn't stored on
        /// this server, and it's a video or audio file rather than a folder. A
        /// friend's title that only some viewers may see has no shared path, so
        /// Jellyfin calls it Virtual rather than Remote; both are looked at.
        /// </summary>
        internal static IEnumerable<BaseItemDto> Candidates(object value)
        {
            IEnumerable<BaseItemDto> items = value switch
            {
                BaseItemDto dto => new[] { dto },
                QueryResult<BaseItemDto> page => page.Items ?? Array.Empty<BaseItemDto>(),
                IEnumerable<BaseItemDto> list => list,
                _ => Array.Empty<BaseItemDto>()
            };

            return items.Where(dto => dto != null
                && dto.CanDownload == false
                && dto.LocationType is LocationType.Remote or LocationType.Virtual
                && dto.IsFolder != true
                && dto.MediaType is Jellyfin.Data.Enums.MediaType.Video or Jellyfin.Data.Enums.MediaType.Audio);
        }

        private async Task ApplyAsync(ResultExecutingContext context, List<BaseItemDto> candidates)
        {
            var info = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            var user = info?.User ?? (info != null && info.UserId != Guid.Empty ? _users.GetUserById(info.UserId) : null);
            if (user == null)
            {
                return;
            }

            foreach (var dto in candidates)
            {
                var item = _library.GetItemById(dto.Id);
                if (item != null
                    && FederationLibraryManager.GetFederationKey(item) != null
                    && _downloads.CanOffer(user, item))
                {
                    dto.CanDownload = true;
                }
            }
        }
    }
}
