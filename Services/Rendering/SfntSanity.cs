using System.Buffers.Binary;
using System.Text;

namespace CustomCoverArt.Services.Rendering;

/// <summary>
/// Managed check of an sfnt font's table directory, run on every upload and before the bytes
/// reach FreeType, DirectWrite or CoreText. It is not a font parser: it only proves the
/// directory is self-consistent (offsets and lengths inside the file, the tables a renderer
/// cannot work without present), which rejects truncated and hand-damaged files cheaply.
/// </summary>
internal static class SfntSanity
{
    /// <summary>Most tables a directory may declare; real fonts stay far below this.</summary>
    public const int MaxTables = 64;

    private const int HeaderLength = 12;
    private const int RecordLength = 16;

    private static readonly string[] RequiredTables = { "cmap", "head", "hhea", "hmtx", "maxp" };

    /// <summary>
    /// True when <paramref name="sfnt"/> is a single font or a collection whose first face has a
    /// sane table directory. Collections are checked for face 0 only, the face the plugin loads.
    /// </summary>
    public static bool LooksValid(ReadOnlySpan<byte> sfnt)
    {
        if (sfnt.Length < HeaderLength)
        {
            return false;
        }

        long faceOffset = 0;
        if (sfnt.StartsWith("ttcf"u8))
        {
            // ttcf header: tag, version (4), numFonts (4), then numFonts uint32 offsets.
            var numFonts = BinaryPrimitives.ReadUInt32BigEndian(sfnt.Slice(8, 4));
            if (numFonts == 0 || numFonts > MaxTables || HeaderLength + (4L * numFonts) > sfnt.Length)
            {
                return false;
            }

            faceOffset = BinaryPrimitives.ReadUInt32BigEndian(sfnt.Slice(HeaderLength, 4));
            if (faceOffset + HeaderLength > sfnt.Length)
            {
                return false;
            }
        }

        return DirectoryLooksValid(sfnt, (int)faceOffset);
    }

    private static bool DirectoryLooksValid(ReadOnlySpan<byte> data, int start)
    {
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(start + 4, 2));
        if (tableCount == 0 || tableCount > MaxTables)
        {
            return false;
        }

        var directoryEnd = start + HeaderLength + ((long)tableCount * RecordLength);
        if (directoryEnd > data.Length)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tableCount; i++)
        {
            var record = data.Slice(start + HeaderLength + (i * RecordLength), RecordLength);
            var tag = Encoding.Latin1.GetString(record.Slice(0, 4));
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(12, 4));

            // ulong arithmetic: offset + length cannot wrap around near uint.MaxValue.
            if ((ulong)offset + length > (ulong)data.Length)
            {
                return false;
            }

            seen.Add(tag);
        }

        foreach (var required in RequiredTables)
        {
            if (!seen.Contains(required))
            {
                return false;
            }
        }

        return (seen.Contains("glyf") && seen.Contains("loca")) || seen.Contains("CFF ") || seen.Contains("CFF2");
    }
}
