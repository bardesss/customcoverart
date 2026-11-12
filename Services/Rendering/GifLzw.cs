namespace CustomCoverArt.Services.Rendering;

/// <summary>GIF variable-width LZW (GIF89a appendix F), with a clear code whenever the 4096-entry table fills.</summary>
internal static class GifLzw
{
    private const int MaxCodes = 4096;
    private const int HashSize = 8192; // power of two, at most half full

    /// <summary>
    /// Writes one table-based image data block: the LZW minimum code size byte, the codes packed
    /// LSB first into data sub-blocks of at most 255 bytes, and the zero-length block terminator.
    /// </summary>
    public static void Encode(ReadOnlySpan<byte> indices, int minCodeSize, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(minCodeSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCodeSize, 8);
        var alphabet = 1 << minCodeSize;
        foreach (var index in indices)
        {
            if (index >= alphabet)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), $"Index {index} does not fit a {minCodeSize}-bit code.");
            }
        }

        output.WriteByte((byte)minCodeSize);
        var packer = new BitPacker(output);
        var clear = alphabet;
        var endOfInformation = clear + 1;
        var keys = new int[HashSize];
        var codes = new short[HashSize];
        Array.Fill(keys, -1);
        var width = minCodeSize + 1;
        var next = clear + 2;

        packer.Write(clear, width);
        if (!indices.IsEmpty)
        {
            int prefix = indices[0];
            for (var i = 1; i < indices.Length; i++)
            {
                int k = indices[i];
                var key = (k << 12) | prefix;
                var slot = (int)(((uint)key * 0x9E3779B1u) >> 19); // top 13 bits: 0..8191
                while (keys[slot] >= 0 && keys[slot] != key)
                {
                    slot = (slot + 1) & (HashSize - 1);
                }

                if (keys[slot] == key)
                {
                    prefix = codes[slot];
                    continue;
                }

                Emit(ref packer, prefix, ref width, next);
                if (next < MaxCodes)
                {
                    keys[slot] = key;
                    codes[slot] = (short)next++;
                }
                else
                {
                    packer.Write(clear, width);
                    Array.Fill(keys, -1);
                    width = minCodeSize + 1;
                    next = clear + 2;
                }

                prefix = k;
            }

            Emit(ref packer, prefix, ref width, next);
        }

        packer.Write(endOfInformation, width);
        packer.Flush();
        output.WriteByte(0);
    }

    /// <summary>Writes a code, then widens once the next code to assign no longer fits (the decoder's rule, one step ahead).</summary>
    private static void Emit(ref BitPacker packer, int code, ref int width, int next)
    {
        packer.Write(code, width);
        if (next >= 1 << width && width < 12)
        {
            width++;
        }
    }

    private struct BitPacker
    {
        private readonly Stream _output;
        private readonly byte[] _block;
        private int _count;
        private int _bits;
        private int _pending;

        public BitPacker(Stream output)
        {
            _output = output;
            _block = new byte[256];
            _count = 0;
            _bits = 0;
            _pending = 0;
        }

        public void Write(int code, int width)
        {
            _bits |= code << _pending;
            _pending += width;
            while (_pending >= 8)
            {
                Put((byte)_bits);
                _bits >>= 8;
                _pending -= 8;
            }
        }

        public void Flush()
        {
            if (_pending > 0)
            {
                Put((byte)_bits);
                _bits = 0;
                _pending = 0;
            }

            if (_count > 0)
            {
                WriteBlock();
            }
        }

        private void Put(byte value)
        {
            _block[++_count] = value;
            if (_count == 255)
            {
                WriteBlock();
            }
        }

        private void WriteBlock()
        {
            _block[0] = (byte)_count;
            _output.Write(_block, 0, _count + 1);
            _count = 0;
        }
    }
}
