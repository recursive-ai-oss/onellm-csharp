namespace OneLlm.Tests.Media;

/// <summary>Replays every media conformance case against its fixture: request bytes must match, and the case's assertions check the parsed result.</summary>
public class MediaConformanceTests
{
    internal static IEnumerable<MCase> Cases() => OpenAIMediaCases.All().Concat(GeminiMediaCases.All());

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (MCase c in Cases())
        {
            data.Add(c.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Fixture(string id)
    {
        MCase c = Cases().Single(x => x.Id == id);
        await MediaHarness.ReplayAsync(c);
    }
}
