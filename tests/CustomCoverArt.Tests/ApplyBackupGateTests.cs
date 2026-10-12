using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Applying hands the cover to Jellyfin's image saver, which deletes the target's previous
/// local image. So an apply may only go ahead once the current image is safely backed up
/// (or needs no backup).
/// </summary>
public class ApplyBackupGateTests
{
    private const string LibraryId = "33333333-3333-3333-3333-333333333333";

    private static (ICoverArtService CoverArt, ILibraryDetectionService Library) Host(BackupOutcome backup)
    {
        var coverArt = Substitute.For<ICoverArtService>();
        coverArt.GenerateFromDocumentAsync(Arg.Any<CoverDocument>()).Returns("gen.png");
        coverArt.SaveCoverArtAsync(LibraryId, "gen.png").Returns("saved.png");
        var library = Substitute.For<ILibraryDetectionService>();
        library.BackupCurrentCoverArtAsync(LibraryId).Returns(backup);
        library.UpdateLibraryCoverArtAsync(LibraryId, "saved.png").Returns(true);
        return (coverArt, library);
    }

    [Fact]
    public async Task Apply_BackupFailed_RefusesAndLeavesTheCoverAlone()
    {
        var (coverArt, library) = Host(BackupOutcome.Failed);

        var result = await ControllerHost.New(coverArt, library)
            .ApplyDocument(new ApplyDocumentRequest { LibraryId = LibraryId, Document = new CoverDocument() });

        Assert.False(result.Success);
        Assert.Equal("errors.backup_failed", result.ErrorMessage);
        await library.DidNotReceive().UpdateLibraryCoverArtAsync(Arg.Any<string>(), Arg.Any<string>());
        coverArt.DidNotReceive().PruneLibraryOutputs(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Apply_NothingToBackUp_StillApplies()
    {
        var (coverArt, library) = Host(BackupOutcome.NotNeeded);

        var result = await ControllerHost.New(coverArt, library)
            .ApplyDocument(new ApplyDocumentRequest { LibraryId = LibraryId, Document = new CoverDocument() });

        Assert.True(result.Success);
        await library.Received(1).UpdateLibraryCoverArtAsync(LibraryId, "saved.png");
        coverArt.Received(1).PruneLibraryOutputs(LibraryId, "saved.png");
    }
}
