using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class LibraryOutputPruneTests
{
    private const string LibraryId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Each apply copied a new cover_&lt;guid&gt; into the library folder and nothing ever removed one.</summary>
    [Fact]
    public async Task Prune_KeepsOnlyTheLiveCover()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var generated = Path.Combine(dataDir, "customcoverart", "generated");
            var older = Path.Combine(generated, "cover_old.png");
            var newer = Path.Combine(generated, "cover_new.png");
            File.WriteAllText(older, "a");
            File.WriteAllText(newer, "b");

            var savedOld = await svc.SaveCoverArtAsync(LibraryId, older);
            var savedNew = await svc.SaveCoverArtAsync(LibraryId, newer);

            svc.PruneLibraryOutputs(LibraryId, savedNew!);

            Assert.False(File.Exists(savedOld));
            Assert.True(File.Exists(savedNew));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(savedNew)!));
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    [Fact]
    public void Prune_InvalidLibraryId_IsANoOp()
    {
        var svc = AnimationTestHost.NewCoverArtService();
        Assert.Null(Record.Exception(() => svc.PruneLibraryOutputs("../../etc", "/tmp/x.png")));
    }

    /// <summary>document/apply now runs through the shared apply path, and that path prunes after a successful update.</summary>
    [Fact]
    public async Task ApplyDocument_PrunesAfterASuccessfulUpdate()
    {
        var coverArt = Substitute.For<ICoverArtService>();
        coverArt.GenerateFromDocumentAsync(Arg.Any<CoverDocument>()).Returns("gen.png");
        coverArt.SaveCoverArtAsync(LibraryId, "gen.png").Returns("saved.png");
        var library = Substitute.For<ILibraryDetectionService>();
        library.UpdateLibraryCoverArtAsync(LibraryId, "saved.png").Returns(true);

        var result = await ControllerHost.New(coverArt, library)
            .ApplyDocument(new ApplyDocumentRequest { LibraryId = LibraryId, Document = new CoverDocument() });

        Assert.True(result.Success);
        await library.Received(1).BackupCurrentCoverArtAsync(LibraryId);
        coverArt.Received(1).PruneLibraryOutputs(LibraryId, "saved.png");
    }

    [Fact]
    public async Task ApplyDocument_FailedUpdate_PrunesNothing()
    {
        var coverArt = Substitute.For<ICoverArtService>();
        coverArt.GenerateFromDocumentAsync(Arg.Any<CoverDocument>()).Returns("gen.png");
        coverArt.SaveCoverArtAsync(LibraryId, "gen.png").Returns("saved.png");
        var library = Substitute.For<ILibraryDetectionService>();
        library.UpdateLibraryCoverArtAsync(LibraryId, "saved.png").Returns(false);

        var result = await ControllerHost.New(coverArt, library)
            .ApplyDocument(new ApplyDocumentRequest { LibraryId = LibraryId, Document = new CoverDocument() });

        Assert.False(result.Success);
        coverArt.DidNotReceive().PruneLibraryOutputs(Arg.Any<string>(), Arg.Any<string>());
    }
}
