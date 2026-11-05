using System.Globalization;
using System.Net.Http;
using System.Text;

namespace OneLlm.Bench;

/// <summary>One turn of a synthetic conversation, shared by every client under test so all of them send the same content.</summary>
public readonly record struct Turn(bool User, string Text);

/// <summary>
/// Where the mock server listens (<c>MOCK_URL</c>, default <c>http://127.0.0.1:5311</c>) and how to configure it, plus the synthetic
/// conversation histories every benchmark sends.
/// </summary>
public static class Mock
{
    /// <summary>The server's base address without a trailing slash.</summary>
    public static string Url { get; } = (Environment.GetEnvironmentVariable("MOCK_URL") ?? "http://127.0.0.1:5311").TrimEnd('/');

    /// <summary>Sets the size of the answers the server sends: <paramref name="events"/> text deltas of <paramref name="bytes"/> characters.</summary>
    public static void Configure(int events, int bytes, int flushEvery = 8)
    {
        using var http = new HttpClient();
        using var response = http.PostAsync($"{Url}/_mock/config?events={events.ToString(CultureInfo.InvariantCulture)}&bytes={bytes.ToString(CultureInfo.InvariantCulture)}&flushEvery={flushEvery.ToString(CultureInfo.InvariantCulture)}", null).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A history of about <paramref name="tokens"/> tokens, at roughly four characters per token: alternating user and assistant turns of
    /// about 400 characters, ending on a user turn.
    /// </summary>
    public static List<Turn> History(int tokens)
    {
        int count = Math.Max(1, tokens / 100);
        var list = new List<Turn>(count + 1);
        for (int i = 0; i < count; i++)
        {
            list.Add(new Turn(i % 2 == 0, Paragraph(i)));
        }

        if (!list[^1].User)
        {
            list.Add(new Turn(true, Paragraph(count)));
        }

        return list;
    }

    private static string Paragraph(int i)
    {
        var sb = new StringBuilder(420);
        sb.Append("Turn ").Append(i.ToString(CultureInfo.InvariantCulture)).Append(": ");
        while (sb.Length < 400)
        {
            sb.Append("The committee reviewed the quarterly figures and agreed to revisit the schedule next week. ");
        }

        return sb.ToString();
    }
}
