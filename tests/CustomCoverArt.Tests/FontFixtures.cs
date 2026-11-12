using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CustomCoverArt.Tests;

/// <summary>Committed font fixtures (Noto Sans subsets, see Fixtures/fonts/OFL.txt) and a scratch folder per test.</summary>
internal static class FontFixtures
{
    public static string Path(string name)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", name);

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    /// <summary>Offset of the 16-byte directory record of <paramref name="tag"/> in a single (non-collection) sfnt.</summary>
    public static int RecordOffset(byte[] sfnt, string tag)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4, 2));
        for (var i = 0; i < count; i++)
        {
            var at = 12 + (i * 16);
            if (Encoding.Latin1.GetString(sfnt, at, 4) == tag)
            {
                return at;
            }
        }

        throw new InvalidOperationException("No table " + tag);
    }

    public static void SetTag(byte[] sfnt, string tag, string replacement)
        => Encoding.Latin1.GetBytes(replacement, 0, 4, sfnt, RecordOffset(sfnt, tag));

    public static void SetOffsetAndLength(byte[] sfnt, string tag, uint offset, uint length)
    {
        var at = RecordOffset(sfnt, tag);
        BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(at + 8, 4), offset);
        BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(at + 12, 4), length);
    }
}

/// <summary>A temp folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cca_fonts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Copy(string fixture, string asName)
    {
        var target = File(asName);
        System.IO.File.Copy(FontFixtures.Path(fixture), target);
        return target;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}
