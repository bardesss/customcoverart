using CustomCoverArt.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace CustomCoverArt.Services;

public class MediaItemService : IMediaItemService
{
    // Cover-bearing "container" kinds across every library type (movies, TV,
    // music, books, home videos, photos). Leaf items that would flood the picker
    // (individual Episodes and Audio tracks) are intentionally excluded — album
    // and series covers stand in for them.
    private static readonly BaseItemKind[] DefaultKinds =
    {
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season,
        BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist, BaseItemKind.MusicVideo,
        BaseItemKind.Book, BaseItemKind.AudioBook,
        BaseItemKind.Photo, BaseItemKind.PhotoAlbum,
        BaseItemKind.Video, BaseItemKind.BoxSet
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ILoggingService _loggingService;

    public MediaItemService(
        ILibraryManager libraryManager,
        ILoggingService loggingService)
    {
        _libraryManager = libraryManager;
        _loggingService = loggingService;
    }

    public Task<ItemSearchResponse> SearchItemsAsync(ItemSearchRequest request)
    {
        try
        {
            var query = new InternalItemsQuery
            {
                SearchTerm = request.Query,
                IncludeItemTypes = ParseKinds(request.ItemTypes),
                ImageTypes = new[] { ImageType.Primary },
                Recursive = true,
                Limit = request.PageSize,
                StartIndex = (request.Page - 1) * request.PageSize
            };

            if (!string.IsNullOrEmpty(request.LibraryId) && Guid.TryParse(request.LibraryId, out var libId))
            {
                query.ParentId = libId;
            }

            var result = _libraryManager.GetItemsResult(query);
            var totalCount = result.TotalRecordCount;
            var mediaItems = result.Items.Select(MapToMediaItemInfo).ToList();
            var totalPages = request.PageSize > 0
                ? (int)Math.Ceiling((double)totalCount / request.PageSize)
                : 0;

            return Task.FromResult(new ItemSearchResponse
            {
                Items = mediaItems,
                TotalCount = totalCount,
                Page = request.Page,
                PageSize = request.PageSize,
                TotalPages = totalPages
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to search items", ex);
            return Task.FromResult(new ItemSearchResponse
            {
                Items = Enumerable.Empty<MediaItemInfo>(),
                TotalCount = 0,
                Page = request.Page,
                PageSize = request.PageSize,
                TotalPages = 0
            });
        }
    }

    public Task<IReadOnlyList<string>> GetPosterPathsAsync(string parentId, int max)
    {
        try
        {
            if (!Guid.TryParse(parentId, out var id))
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var query = new InternalItemsQuery
            {
                ParentId = id,
                ImageTypes = new[] { ImageType.Primary },
                Recursive = true,
                Limit = max
            };

            var paths = new List<string>();
            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (item.HasImage(ImageType.Primary))
                {
                    var p = item.GetImagePath(ImageType.Primary, 0);
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        paths.Add(p);
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get poster paths for {ParentId}", ex, parentId);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    private static BaseItemKind[] ParseKinds(string[]? itemTypes)
    {
        if (itemTypes is null || itemTypes.Length == 0)
        {
            return DefaultKinds;
        }

        var kinds = new List<BaseItemKind>();
        foreach (var type in itemTypes)
        {
            if (Enum.TryParse<BaseItemKind>(type, ignoreCase: true, out var kind))
            {
                kinds.Add(kind);
            }
        }

        return kinds.Count > 0 ? kinds.ToArray() : DefaultKinds;
    }

    private MediaItemInfo MapToMediaItemInfo(BaseItem item)
    {
        var library = _libraryManager.GetCollectionFolders(item)?.FirstOrDefault();

        var mediaItem = new MediaItemInfo
        {
            Id = item.Id.ToString(),
            Name = item.Name ?? "Unknown",
            Type = GetItemType(item),
            Year = item.ProductionYear?.ToString(),
            Overview = item.Overview,
            LibraryId = library?.Id.ToString() ?? string.Empty,
            LibraryName = library?.Name ?? "Unknown Library"
        };

        if (item is Season season)
        {
            mediaItem.SeriesName = season.SeriesName;
            mediaItem.SeasonNumber = season.IndexNumber;
        }
        else if (item is Episode episode)
        {
            mediaItem.SeriesName = episode.SeriesName;
            mediaItem.SeasonNumber = episode.ParentIndexNumber;
            mediaItem.EpisodeNumber = episode.IndexNumber;
        }

        return mediaItem;
    }

    private static string GetItemType(BaseItem item)
    {
        return item switch
        {
            Movie => "Movie",
            Series => "Series",
            Season => "Season",
            Episode => "Episode",
            _ => item.GetType().Name
        };
    }
}
