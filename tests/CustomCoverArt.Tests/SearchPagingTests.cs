using System;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class SearchPagingTests
{
    private static (MediaItemService Svc, Func<InternalItemsQuery?> LastQuery) Host()
    {
        var lib = Substitute.For<ILibraryManager>();
        InternalItemsQuery? last = null;
        lib.GetItemsResult(Arg.Do<InternalItemsQuery>(q => last = q)).Returns(new QueryResult<BaseItem>());
        return (new MediaItemService(lib, NullLogger<MediaItemService>.Instance), () => last);
    }

    [Theory]
    [InlineData(10000, 1, 100, 0, 1)]
    [InlineData(0, 1, 1, 0, 1)]
    [InlineData(-5, -3, 1, 0, 1)]
    [InlineData(20, 3, 20, 40, 3)]
    public async Task ClampsPageAndPageSize(int pageSize, int page, int expectedLimit, int expectedStart, int expectedPage)
    {
        var (svc, last) = Host();

        var response = await svc.SearchItemsAsync(new ItemSearchRequest { PageSize = pageSize, Page = page });

        Assert.Equal((int?)expectedLimit, last()!.Limit);
        Assert.Equal((int?)expectedStart, last()!.StartIndex);
        Assert.Equal(expectedLimit, response.PageSize);
        Assert.Equal(expectedPage, response.Page);
    }

    [Fact]
    public async Task HugePage_DoesNotOverflow()
    {
        var (svc, last) = Host();

        await svc.SearchItemsAsync(new ItemSearchRequest { PageSize = 100, Page = int.MaxValue });

        Assert.Equal((int?)((MediaItemService.MaxPage - 1) * MediaItemService.MaxPageSize), last()!.StartIndex);
    }

    [Fact]
    public async Task NullRequest_ReturnsEmptyFirstPage()
    {
        var (svc, _) = Host();

        var response = await svc.SearchItemsAsync(null!);

        Assert.Empty(response.Items);
        Assert.Equal(1, response.Page);
    }
}
