namespace OneLlm.Tests;

public class SmokeTests
{
    [Fact]
    public void Assembly_marker_has_a_version() => Assert.False(string.IsNullOrEmpty(AssemblyMarker.Version));
}
