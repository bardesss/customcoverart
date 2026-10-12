using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class ValidationMessageTests
{
    [Fact]
    public async Task OversizeCanvas_ThrowsTheTypedException_Unwrapped()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 5000, Height = 5000, Format = "png" } };

        var ex = await Assert.ThrowsAsync<CoverValidationException>(
            () => AnimationTestHost.NewCoverArtService().GenerateFromDocumentAsync(doc));

        Assert.Contains("2048", ex.Message);
    }

    [Fact]
    public async Task Preview_ReturnsTheValidationMessage()
    {
        var coverArt = Substitute.For<ICoverArtService>();
        coverArt.GenerateFromDocumentAsync(Arg.Any<CoverDocument>())
            .Returns(Task.FromException<string>(new CoverValidationException("Dimensions too large. Maximum: 2048x2048 pixels")));

        var result = await ControllerHost.New(coverArt).GeneratePreviewDocument(new CoverDocument());

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value);
        Assert.Equal("Dimensions too large. Maximum: 2048x2048 pixels", error);
    }

    [Fact]
    public async Task Apply_ReturnsTheValidationMessage()
    {
        var coverArt = Substitute.For<ICoverArtService>();
        coverArt.GenerateFromDocumentAsync(Arg.Any<CoverDocument>())
            .Returns(Task.FromException<string>(new CoverValidationException("Dimensions too small. Minimum: 100x100 pixels")));

        var result = await ControllerHost.New(coverArt).ApplyDocument(new ApplyDocumentRequest
        {
            LibraryId = "11111111-1111-1111-1111-111111111111",
            Document = new CoverDocument(),
        });

        Assert.False(result.Success);
        Assert.Equal("Dimensions too small. Minimum: 100x100 pixels", result.ErrorMessage);
    }

    [Fact]
    public async Task UnexpectedErrors_StayGeneric()
    {
        var coverArt = Substitute.For<ICoverArtService>();
        coverArt.GenerateFromDocumentAsync(Arg.Any<CoverDocument>())
            .Returns(Task.FromException<string>(new System.InvalidOperationException("/srv/secret/path exploded")));

        var result = await ControllerHost.New(coverArt).GeneratePreviewDocument(new CoverDocument());

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Failed to generate preview.", bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value));
    }
}
