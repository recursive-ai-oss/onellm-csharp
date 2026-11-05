using System.Buffers.Binary;

namespace OneLlm.Media;

/// <summary>Moves 16-bit PCM audio in and out of the WAV container (RIFF/WAVE, format 1), for providers that return raw samples.</summary>
internal static class WavAudio
{
    private const int HeaderBytes = 44;

    /// <summary>Wraps little-endian 16-bit PCM samples in a WAV header.</summary>
    /// <param name="pcm">The samples.</param>
    /// <param name="sampleRate">Samples per second per channel.</param>
    /// <param name="channels">The number of interleaved channels.</param>
    /// <returns>The WAV file.</returns>
    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm, int sampleRate, int channels = 1)
    {
        byte[] wav = new byte[HeaderBytes + pcm.Length];
        Span<byte> h = wav;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], pcm.Length);
        pcm.CopyTo(h[HeaderBytes..]);
        return wav;
    }

    /// <summary>Finds the sample data in a WAV file by walking its chunks.</summary>
    /// <param name="wav">The file.</param>
    /// <param name="data">The bytes of the <c>data</c> chunk.</param>
    /// <returns>False when the bytes are not a WAV file with a data chunk.</returns>
    public static bool TryGetData(ReadOnlyMemory<byte> wav, out ReadOnlyMemory<byte> data)
    {
        data = default;
        ReadOnlySpan<byte> s = wav.Span;
        if (s.Length < 12 || !s[..4].SequenceEqual("RIFF"u8) || !s.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        int pos = 12;
        while (pos + 8 <= s.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(s[(pos + 4)..]);
            if (s.Slice(pos, 4).SequenceEqual("data"u8))
            {
                // A streamed file can declare a size of zero or more than it holds; the rest of the file is the data.
                int available = s.Length - (pos + 8);
                int length = size <= 0 || size > available ? available : size;
                data = wav.Slice(pos + 8, length);
                return true;
            }

            if (size < 0 || pos + 8L + size > s.Length)
            {
                return false;
            }

            // Chunks are padded to an even size.
            pos += 8 + size + (size & 1);
        }

        return false;
    }

    /// <summary>Reads the <c>rate=</c> parameter of an <c>audio/L16</c> media type, for example <c>audio/L16;codec=pcm;rate=24000</c>.</summary>
    /// <param name="mimeType">The media type with its parameters.</param>
    /// <param name="fallback">The rate to use when the type states none.</param>
    /// <returns>Samples per second.</returns>
    public static int RateFrom(string mimeType, int fallback = 24000)
    {
        foreach (string part in mimeType.Split(';'))
        {
            string p = part.Trim();
            if (p.StartsWith("rate=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(p.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int rate)
                && rate > 0)
            {
                return rate;
            }
        }

        return fallback;
    }
}
