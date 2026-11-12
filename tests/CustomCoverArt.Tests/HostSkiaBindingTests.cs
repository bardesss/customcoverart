using System;
using System.Linq;
using Xunit;

namespace CustomCoverArt.Tests;

public class HostSkiaBindingTests
{
    [Fact]
    public void PluginReferencesTheHostSkiaSharpAssemblyVersion()
    {
        var skia = typeof(Plugin).Assembly.GetReferencedAssemblies().Single(a => a.Name == "SkiaSharp");
        Assert.Equal(new Version(3, 119, 0, 0), skia.Version);
    }

    [Fact]
    public void CsprojPins_MatchWhatJellyfin12Ships()
    {
        foreach (var proj in new[] { "CustomCoverArt.csproj", "tests/CustomCoverArt.Tests/CustomCoverArt.Tests.csproj" })
        {
            var text = RepoFiles.Read(proj);
            Assert.Contains("<SkiaSharpVersion Condition=\"'$(SkiaSharpVersion)' == ''\">3.119.4</SkiaSharpVersion>", text);
            Assert.Contains("<HarfBuzzSharpVersion Condition=\"'$(HarfBuzzSharpVersion)' == ''\">8.3.1.5</HarfBuzzSharpVersion>", text);
        }
    }

    [Fact]
    public void PluginProject_ReferencesSkiaCompileOnly()
    {
        var text = RepoFiles.Read("CustomCoverArt.csproj");
        Assert.Contains("<PackageReference Include=\"SkiaSharp\" Version=\"$(SkiaSharpVersion)\" ExcludeAssets=\"runtime;native\" />", text);
        Assert.Contains("<PackageReference Include=\"SkiaSharp.HarfBuzz\" Version=\"$(SkiaSharpVersion)\" ExcludeAssets=\"runtime;native\" />", text);
    }
}
