using CustomCoverArt.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for browsing and managing media items from Jellyfin libraries.
/// </summary>
public class MediaItemService : IMediaItemService
{
    private static readonly BaseItemKind[] DefaultKinds =
    {
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season
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

    public Task<IEnumerable<MediaItemInfo>> GetLibraryItemsAsync(string libraryId)
    {
        try
        {
            if (!Guid.TryParse(libraryId, out var id))
            {
                _loggingService.LogWarning("Invalid library id: {LibraryId}", libraryId);
                return Task.FromResult(Enumerable.Empty<MediaItemInfo>());
            }

            var query = new InternalItemsQuery
            {
                ParentId = id,
                IncludeItemTypes = DefaultKinds,
                ImageTypes = new[] { ImageType.Primary },
                Recursive = true,
                Limit = 100
            };

            var items = _libraryManager.GetItemList(query);
            return Task.FromResult(items.Select(MapToMediaItemInfo));
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get library items: {Error}", ex.Message);
            return Task.FromResult(Enumerable.Empty<MediaItemInfo>());
        }
    }

    public Task<MediaItemInfo?> GetItemByIdAsync(string itemId)
    {
        try
        {
            if (!Guid.TryParse(itemId, out var id))
            {
                return Task.FromResult<MediaItemInfo?>(null);
            }

            var item = _libraryManager.GetItemById<BaseItem>(id);
            return Task.FromResult(item is null ? null : MapToMediaItemInfo(item));
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get item by ID: {Error}", ex.Message);
            return Task.FromResult<MediaItemInfo?>(null);
        }
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
            _loggingService.LogError("Failed to search items: {Error}", ex.Message);
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

    public async Task<byte[]?> GetItemCoverArtAsync(string itemId)
    {
        try
        {
            var imagePath = GetPrimaryImagePath(itemId);
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                _loggingService.LogWarning("No primary image found for item: {ItemId}", itemId);
                return null;
            }

            return await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get item cover art: {Error}", ex.Message);
            return null;
        }
    }

    public Task<string?> GetItemCoverArtUrlAsync(string itemId)
    {
        var imagePath = GetPrimaryImagePath(itemId);
        var url = string.IsNullOrEmpty(imagePath) ? null : $"/CustomCoverArt/items/{itemId}/cover";
        return Task.FromResult(url);
    }

    public Task<string?> GetItemImageSourcePathAsync(string itemId)
    {
        return Task.FromResult(GetPrimaryImagePath(itemId));
    }

    public Task<IEnumerable<MediaItemInfo>> GetRecentItemsAsync(int count = 20)
    {
        try
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = DefaultKinds,
                ImageTypes = new[] { ImageType.Primary },
                Recursive = true,
                Limit = count,
                OrderBy = new[] { (ItemSortBy.DateCreated, Jellyfin.Database.Implementations.Enums.SortOrder.Descending) }
            };

            var items = _libraryManager.GetItemList(query);
            return Task.FromResult(items.Select(MapToMediaItemInfo));
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get recent items: {Error}", ex.Message);
            return Task.FromResult(Enumerable.Empty<MediaItemInfo>());
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

    private string? GetPrimaryImagePath(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id))
        {
            return null;
        }

        var item = _libraryManager.GetItemById<BaseItem>(id);
        if (item is null || !item.HasImage(ImageType.Primary))
        {
            return null;
        }

        return item.GetImagePath(ImageType.Primary, 0);
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
        var itemId = item.Id.ToString();
        var hasPrimaryImage = item.HasImage(ImageType.Primary);
        var coverArtUrl = hasPrimaryImage ? $"/CustomCoverArt/items/{itemId}/cover" : null;

        var mediaItem = new MediaItemInfo
        {
            Id = itemId,
            Name = item.Name ?? "Unknown",
            Type = GetItemType(item),
            Year = item.ProductionYear?.ToString(),
            Overview = item.Overview,
            LibraryId = library?.Id.ToString() ?? string.Empty,
            LibraryName = library?.Name ?? "Unknown Library",
            CoverArtUrl = coverArtUrl,
            ThumbnailUrl = coverArtUrl
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
