using System;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The flat CoverArtSettings model predates the layered CoverDocument. Client and server
/// ship in the same DLL, so the client never talks to an older server: every request now
/// carries only the document. Stored templates saved before the canvas editor (Settings
/// only) are still honoured, by migrating them to a document.
/// </summary>
public class LegacySettingsRemovalTests
{
    [Fact]
    public void Client_SendsOnlyTheLayeredDocument()
    {
        var js = ConfigPageSource.Js();
        Assert.DoesNotContain("collectSettings", js, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[{,]\s*Settings\s*:", js);
        Assert.DoesNotContain("backgroundImagePath", js, StringComparison.Ordinal);
        Assert.DoesNotContain("customFontPath", js, StringComparison.Ordinal);
        Assert.DoesNotContain("function collectGradientStops(", js, StringComparison.Ordinal);
        Assert.Contains("{ Document: batchDoc, Targets: targets }", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_StillMigratesSettingsOnlyTemplates()
    {
        Assert.Contains("function migrateTemplateToDocument(", ConfigPageSource.Js(), StringComparison.Ordinal);
    }
}
