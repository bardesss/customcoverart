using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Text checks on the workflows and Dependabot config: the plugin zip carries only the plugin's own
/// DLL, the host-pinned Skia packages are left alone, and the weekly compat run watches the pin.
/// The YAML itself is parsed outside the suite (see the task report); these pin the content.
/// </summary>
public class WorkflowGuardTests
{
    private const string FontconfigInstall = "sudo apt-get update && sudo apt-get install -y libfontconfig1";

    private static string Release => RepoFiles.Read(".github/workflows/release.yml");

    private static string Compat => RepoFiles.Read(".github/workflows/jellyfin-compat.yml");

    /// <summary>The ERE the release job runs with <c>grep -i -E</c> over the stage folder, read from the workflow.</summary>
    private static Regex StageGuard()
    {
        var match = Regex.Match(Release, @"grep -i -E '([^']+)'");
        Assert.True(match.Success, "release.yml has no grep -i -E guard");
        return new Regex(match.Groups[1].Value, RegexOptions.IgnoreCase);
    }

    [Fact]
    public void Release_PackagesOnlyThePluginDll()
    {
        var text = Release;
        Assert.Contains("cp bin/Release/net10.0/CustomCoverArt.dll dist/stage/", text);
        Assert.DoesNotContain("SixLabors", text);
        Assert.Contains("if ls dist/stage | grep -i -E '^(sixlabors|skiasharp|harfbuzzsharp)|^runtimes$'; then", text);
        Assert.Contains("echo \"Unexpected third-party binaries in the plugin package\"; exit 1", text);

        // Exactly one file is copied in; meta.json is written, not copied.
        Assert.Single(Regex.Matches(text, @"^\s*cp .* dist/stage/", RegexOptions.Multiline));
    }

    [Fact]
    public void Release_GuardRunsBeforeTheZipIsMade()
    {
        var text = Release;
        Assert.True(text.IndexOf("grep -i -E", StringComparison.Ordinal) < text.IndexOf("zip -r", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SixLabors.ImageSharp.dll")]
    [InlineData("SixLabors.ImageSharp.Drawing.dll")]
    [InlineData("SkiaSharp.dll")]
    [InlineData("SkiaSharp.HarfBuzz.dll")]
    [InlineData("skiasharp.dll")]
    [InlineData("HarfBuzzSharp.dll")]
    [InlineData("HARFBUZZSHARP.DLL")]
    [InlineData("runtimes")]
    public void Release_Guard_MatchesEveryForbiddenName(string stagedName)
    {
        Assert.Matches(StageGuard(), stagedName);
    }

    [Theory]
    [InlineData("CustomCoverArt.dll")]
    [InlineData("meta.json")]
    [InlineData("runtimes.json")]
    [InlineData("CustomCoverArt.SkiaSharp.notes")]
    public void Release_Guard_LeavesThePluginsOwnFilesAlone(string stagedName)
    {
        Assert.DoesNotMatch(StageGuard(), stagedName);
    }

    [Fact]
    public void Dependabot_IgnoresTheHostPinnedSkiaPackages()
    {
        var text = RepoFiles.Read(".github/dependabot.yml");
        Assert.DoesNotContain("SixLabors", text);
        Assert.DoesNotContain("ImageSharp", text);
        Assert.Contains("dependency-name: \"SkiaSharp*\"", text);
        Assert.Contains("dependency-name: \"HarfBuzzSharp*\"", text);
        Assert.Contains("dependency-name: \"Jellyfin.*\"", text);
    }

    [Fact]
    public void Compat_ChecksTheSkiaPin()
    {
        var text = Compat;
        Assert.Contains("Directory.Packages.props", text);
        Assert.Contains("SkiaSharpVersion", text);
        Assert.Contains("skia-pin:", text);
        Assert.Contains("gh api repos/jellyfin/jellyfin/releases/latest --jq .tag_name", text);
        Assert.Contains("Jellyfin $TAG ships SkiaSharp $HOST (plugin pins $PINNED)", text);
        Assert.Contains("--label jellyfin-compat", text);
    }

    /// <summary>The job's PCRE for the pin, run against the real csproj, finds the version the plugin compiles against.</summary>
    [Fact]
    public void Compat_PinPattern_ReadsTheCsprojVersion()
    {
        var pattern = Regex.Match(Compat, @"PINNED=\$\(grep -oPm1 '([^']*SkiaSharpVersion[^']*)' CustomCoverArt\.csproj");
        Assert.True(pattern.Success, "skia-pin job does not read PINNED from CustomCoverArt.csproj");

        var pinned = Regex.Match(RepoFiles.Read("CustomCoverArt.csproj"), pattern.Groups[1].Value);
        Assert.Equal("3.119.4", pinned.Value);
    }

    /// <summary>The job's PCRE for the host version picks SkiaSharp itself, not SkiaSharp.* siblings.</summary>
    [Fact]
    public void Compat_HostPattern_ReadsOnlyTheSkiaSharpPackage()
    {
        var pattern = Regex.Match(Compat, @"grep -oP '(\(\?<=Include=""SkiaSharp""[^']+)'");
        Assert.True(pattern.Success, "skia-pin job does not read HOST from Directory.Packages.props");

        const string props = """
            <Project>
              <ItemGroup>
                <PackageVersion Include="SkiaSharp.HarfBuzz" Version="9.9.9" />
                <PackageVersion Include="SkiaSharp" Version="3.119.4" />
                <PackageVersion Include="SkiaSharp.NativeAssets.Linux" Version="8.8.8" />
              </ItemGroup>
            </Project>
            """;
        var found = Regex.Matches(props, pattern.Groups[1].Value).Select(m => m.Value).ToList();
        Assert.Equal(new[] { "3.119.4" }, found);
    }

    /// <summary>SkiaSharp's native library needs fontconfig; CI installs it rather than relying on the runner image.</summary>
    [Fact]
    public void Ci_InstallsFontconfigBeforeTesting()
    {
        AssertFontconfigBeforeEveryTestRun(RepoFiles.Read(".github/workflows/ci.yml"), expectedRuns: 1);
    }

    [Fact]
    public void Compat_InstallsFontconfigBeforeEveryTestRun()
    {
        AssertFontconfigBeforeEveryTestRun(Compat, expectedRuns: 2);
    }

    /// <summary>Each job that runs <c>dotnet test</c> installs libfontconfig1 earlier in the same job.</summary>
    private static void AssertFontconfigBeforeEveryTestRun(string workflow, int expectedRuns)
    {
        var jobs = Regex.Split(workflow, @"^  [A-Za-z0-9_-]+:\s*$", RegexOptions.Multiline).Skip(1).ToList();
        var testing = jobs.Where(j => j.Contains("dotnet test", StringComparison.Ordinal)).ToList();
        Assert.Equal(expectedRuns, testing.Count);
        foreach (var job in testing)
        {
            var install = job.IndexOf(FontconfigInstall, StringComparison.Ordinal);
            Assert.True(install >= 0, "a job runs dotnet test without installing libfontconfig1");
            Assert.True(install < job.IndexOf("dotnet test", StringComparison.Ordinal), "libfontconfig1 is installed after dotnet test");
        }
    }
}
