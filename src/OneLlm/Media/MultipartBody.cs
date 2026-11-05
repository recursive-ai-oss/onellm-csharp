using System.Security.Cryptography;
using System.Text;

namespace OneLlm.Media;

/// <summary>
/// Builds a <c>multipart/form-data</c> body (RFC 7578) in memory. The boundary is derived from the content with SHA-256, so the same request
/// always has the same bytes (recorded requests can be matched exactly) and the boundary cannot occur inside a part.
/// </summary>
internal sealed class MultipartBody
{
    private readonly List<(string Name, string? FileName, string? ContentType, ReadOnlyMemory<byte> Data)> _parts = [];

    public void Add(string name, string value) => _parts.Add((name, null, null, Encoding.UTF8.GetBytes(value)));

    public void AddFile(string name, string fileName, string contentType, ReadOnlyMemory<byte> data) => _parts.Add((name, fileName, contentType, data));

    /// <summary>Writes the body.</summary>
    /// <returns>The bytes and the <c>Content-Type</c> header value that names the boundary.</returns>
    public (byte[] Body, string ContentType) Build()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string name, string? fileName, string? contentType, ReadOnlyMemory<byte> data) in _parts)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\0" + fileName + "\0" + contentType + "\0"));
            hash.AppendData(data.Span);
        }

        // A hash can be found inside content only by chance 2^-128 per position; the check below makes even that impossible.
        string boundary = "onellm-" + Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
        byte[] marker = Encoding.ASCII.GetBytes("--" + boundary);
        foreach (var part in _parts)
        {
            if (part.Data.Span.IndexOf(marker) >= 0)
            {
                boundary += "-x";
                marker = Encoding.ASCII.GetBytes("--" + boundary);
            }
        }

        using var ms = new MemoryStream();
        foreach ((string name, string? fileName, string? contentType, ReadOnlyMemory<byte> data) in _parts)
        {
            WriteAscii(ms, "--" + boundary + "\r\n");
            string disposition = "Content-Disposition: form-data; name=\"" + Quote(name) + "\"";
            if (fileName is not null)
            {
                disposition += "; filename=\"" + Quote(fileName) + "\"";
            }

            WriteAscii(ms, disposition + "\r\n");
            if (contentType is not null)
            {
                WriteAscii(ms, "Content-Type: " + contentType + "\r\n");
            }

            WriteAscii(ms, "\r\n");
            ms.Write(data.Span);
            WriteAscii(ms, "\r\n");
        }

        WriteAscii(ms, "--" + boundary + "--\r\n");
        return (ms.ToArray(), "multipart/form-data; boundary=" + boundary);
    }

    // Quotes and line breaks in a name would end the header value early; they are percent-encoded as browsers do (HTML Living Standard, form submission).
    private static string Quote(string s) => s.Replace("\"", "%22", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    private static void WriteAscii(MemoryStream ms, string text) => ms.Write(Encoding.UTF8.GetBytes(text));
}
