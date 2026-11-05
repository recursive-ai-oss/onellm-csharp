namespace OneLlm.Tests.Gemini;

/// <summary>Replays every conformance case against its recorded fixture: request bytes must match, and the case's assertions check the parsed result.</summary>
public class GeminiConformanceTests
{
    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (Case c in GeminiCases.All())
        {
            data.Add(c.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Fixture(string id)
    {
        Case c = GeminiCases.All().Single(x => x.Id == id);
        await GeminiHarness.ReplayAsync(c);
    }
}
