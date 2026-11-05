using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneLlm.Conformance;

/// <summary>What a request looked like when it arrived, for matching against recorded exchanges.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path and query.</param>
/// <param name="Headers">All request headers, names compared ignoring case.</param>
/// <param name="Body">The body bytes (empty when there is none).</param>
public sealed record IncomingRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>Decides whether a request matches a recorded one, and says why not.</summary>
public static class FixtureMatcher
{
    /// <summary>Compares a request with a recorded request.</summary>
    /// <param name="expected">The recorded request.</param>
    /// <param name="actual">The request that arrived.</param>
    /// <returns>Null when they match; otherwise the first difference. The text names header names and JSON paths, never an actual header value.</returns>
    public static string? Diff(RecordedRequest expected, IncomingRequest actual)
    {
        if (!string.Equals(expected.Method, actual.Method, StringComparison.OrdinalIgnoreCase))
        {
            return $"method: expected {expected.Method}, got {actual.Method}";
        }

        if (!string.Equals(expected.Path, actual.Path, StringComparison.Ordinal))
        {
            return $"path: expected {expected.Path}, got {actual.Path}";
        }

        foreach ((string name, string value) in expected.Headers)
        {
            if (!actual.Headers.TryGetValue(name, out string? got))
            {
                return $"header {name}: expected {value}, but the request has none";
            }

            // A recorded content type without parameters matches any charset the client added.
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase) && !value.Contains(';', StringComparison.Ordinal))
            {
                got = got.Split(';')[0].Trim();
            }

            if (!string.Equals(value, got, StringComparison.Ordinal))
            {
                return FixtureScrubber.IsSecretHeaderName(name) ? $"header {name} differs" : $"header {name}: expected {value}, got {got}";
            }
        }

        return DiffBody(expected.Body, actual.Body);
    }

    private static string? DiffBody(RecordedBody expected, byte[] actual)
    {
        if (expected.IsEmpty)
        {
            return actual.Length == 0 ? null : "body: expected none";
        }

        if (expected.Json is not null)
        {
            JsonNode? got;
            try
            {
                got = JsonNode.Parse(actual, nodeOptions: null, new JsonDocumentOptions { MaxDepth = 256 });
            }
            catch (JsonException)
            {
                return "body: expected JSON, got something that does not parse";
            }

            return DiffJson(expected.Json, got, "$");
        }

        if (expected.Text is not null)
        {
            return string.Equals(expected.Text, Encoding.UTF8.GetString(actual), StringComparison.Ordinal) ? null : "body text differs";
        }

        return expected.Bytes.AsSpan().SequenceEqual(actual) ? null : "body bytes differ";
    }

    /// <summary>Compares two JSON values exactly: object keys in order, numbers by their text, strings by ordinal comparison.</summary>
    /// <param name="expected">The expected value.</param>
    /// <param name="actual">The actual value.</param>
    /// <param name="path">The path to report, starting at <c>$</c>.</param>
    /// <returns>Null when equal; otherwise the path of the first difference and the two short values.</returns>
    public static string? DiffJson(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"body {path}: expected {Short(expected)}, got {Short(actual)}";
        }

        switch (expected)
        {
            case JsonObject eo:
                if (actual is not JsonObject ao)
                {
                    return $"body {path}: expected an object, got {Short(actual)}";
                }

                string[] ek = [.. eo.Select(p => p.Key)];
                string[] ak = [.. ao.Select(p => p.Key)];
                for (int i = 0; i < Math.Max(ek.Length, ak.Length); i++)
                {
                    if (i >= ek.Length)
                    {
                        return $"body {path}: unexpected key '{ak[i]}'";
                    }

                    if (i >= ak.Length || ek[i] != ak[i])
                    {
                        return i < ak.Length && ak.Contains(ek[i])
                            ? $"body {path}: key order differs at '{ek[i]}'"
                            : $"body {path}: missing key '{ek[i]}'";
                    }
                }

                foreach (string key in ek)
                {
                    if (DiffJson(eo[key], ao[key], path + "." + key) is { } d)
                    {
                        return d;
                    }
                }

                return null;
            case JsonArray ea:
                if (actual is not JsonArray aa)
                {
                    return $"body {path}: expected an array, got {Short(actual)}";
                }

                if (ea.Count != aa.Count)
                {
                    return $"body {path}: expected {ea.Count} items, got {aa.Count}";
                }

                for (int i = 0; i < ea.Count; i++)
                {
                    if (DiffJson(ea[i], aa[i], $"{path}[{i}]") is { } d)
                    {
                        return d;
                    }
                }

                return null;
            default:
                return expected.ToJsonString() == actual.ToJsonString() ? null : $"body {path}: expected {Short(expected)}, got {Short(actual)}";
        }
    }

    private static string Short(JsonNode? node)
    {
        string s = node?.ToJsonString() ?? "null";
        return s.Length <= 80 ? s : s[..80] + "...";
    }
}
