using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CustomCoverArt.Controllers;
using CustomCoverArt.Services;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// DELETE targets/{id}/backup: the editor's "Reset backup". Success means "there is no
/// backup now", so a reset with nothing to delete also succeeds.
/// </summary>
public class ResetBackupEndpointTests
{
    private const string Id = "44444444444444444444444444444444"; // N-format, as libraries send it

    [Theory]
    [InlineData(BackupResetOutcome.Deleted)]
    [InlineData(BackupResetOutcome.NoBackup)]
    public async Task Reset_Succeeds_WhenNoBackupIsLeft(BackupResetOutcome outcome)
    {
        var library = Substitute.For<ILibraryDetectionService>();
        library.ResetBackup(Id).Returns(outcome);

        var result = await ControllerHost.New(library: library).ResetBackup(Id);

        Assert.True(result.Success);
        library.Received(1).ResetBackup(Id);
    }

    [Fact]
    public async Task Reset_InUse_ReturnsTheLocalisedReason()
    {
        var library = Substitute.For<ILibraryDetectionService>();
        library.ResetBackup(Id).Returns(BackupResetOutcome.InUse);

        var result = await ControllerHost.New(library: library).ResetBackup(Id);

        Assert.False(result.Success);
        Assert.Equal("errors.backup_in_use", result.ErrorMessage);
    }

    [Fact]
    public async Task Reset_MalformedId_NeverReachesTheService()
    {
        var library = Substitute.For<ILibraryDetectionService>();

        var result = await ControllerHost.New(library: library).ResetBackup("../../etc");

        Assert.False(result.Success);
        library.DidNotReceive().ResetBackup(Arg.Any<string>());
    }

    [Fact]
    public async Task Reset_ServiceThrows_FailsWithoutLeakingDetails()
    {
        var library = Substitute.For<ILibraryDetectionService>();
        library.ResetBackup(Id).Throws(new IOException("C:\\secret\\path is locked"));

        var result = await ControllerHost.New(library: library).ResetBackup(Id);

        Assert.False(result.Success);
        Assert.Equal("Failed to reset backup.", result.ErrorMessage);
    }

    /// <summary>The config page calls exactly this route; pin it.</summary>
    [Fact]
    public void Reset_IsADeleteOnTheBackupRoute()
    {
        var attr = typeof(CustomCoverArtController)
            .GetMethod(nameof(CustomCoverArtController.ResetBackup))!
            .GetCustomAttribute<HttpDeleteAttribute>();

        Assert.NotNull(attr);
        Assert.Equal("targets/{id}/backup", attr!.Template);
    }
}
