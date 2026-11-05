using System.Globalization;
using System.Runtime.InteropServices;
using OneLlm.Definitions;
using OneLlm.Messages;

namespace OneLlm.Runners;

/// <summary>Builds the data a definition's templates read: <c>.inputs</c>, <c>.system</c>, <c>.time</c>, <c>.git</c>, <c>.env</c>, <c>.swarm</c>, <c>.invocationMode</c>, <c>.caller</c> and <c>.turns</c>.</summary>
internal static class TemplateScope
{
    public static Dictionary<string, object?> Build(Runtime runtime, AgentDefinition definition, Dictionary<string, object?> inputs, string? callerInstanceId, List<object?> turns)
    {
        DateTimeOffset now = runtime.TimeProvider.GetLocalNow();
        var env = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string name in runtime.AllowedEnvironment)
        {
            env[name] = runtime.GetEnvironmentVariable(name) ?? "";
        }

        var swarm = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (definition.ExtensionData is { } extra && extra.TryGetValue("swarmUri", out object? uri) && uri is string text)
        {
            swarm["uri"] = text;
        }

        var scope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["inputs"] = inputs,
            ["system"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux",
                ["arch"] = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64",
                ["hostname"] = Environment.MachineName,
                ["username"] = Environment.UserName,
                ["homeDir"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["tempDir"] = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                ["workingDir"] = definition.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            },
            ["time"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["now"] = now.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
                ["today"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["timezone"] = ZoneName(runtime.TimeProvider, now),
            },
            // Present but empty until the host supplies it through Runtime.ScopeValues: a template must not run git by itself.
            ["git"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["isRepo"] = false, ["branch"] = "", ["root"] = "", ["remote"] = "" },
            ["env"] = env,
            ["swarm"] = swarm,
            ["invocationMode"] = definition.InvocationMode ?? "autonomous",
            ["caller"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["instanceId"] = callerInstanceId ?? "" },
            ["turns"] = turns,
        };

        foreach ((string key, object? value) in runtime.ScopeValues)
        {
            scope[key] = value;
        }

        return scope;
    }

    // The zone abbreviation Go prints for the local zone: "UTC" at offset zero, otherwise the platform's name for the zone.
    private static string ZoneName(TimeProvider clock, DateTimeOffset local)
    {
        if (local.Offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        TimeZoneInfo zone = clock.LocalTimeZone;
        return zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
    }
}
