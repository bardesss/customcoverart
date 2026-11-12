using System;
using System.IO;

namespace CustomCoverArt.Tests;

/// <summary>Reads files from the repository checkout, found by walking up to CustomCoverArt.csproj.</summary>
internal static class RepoFiles
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Root => RootLazy.Value;

    public static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CustomCoverArt.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("CustomCoverArt.csproj not found above " + AppContext.BaseDirectory);
    }
}
