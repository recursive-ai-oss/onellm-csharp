using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using OneLlm;
using OneLlm.Definitions;
using OneLlm.Instances;
using OneYaml;

// Usage: onellm-corpus <list-file> <report-file>
// The list file has one entry per line: stratum <TAB> kind <TAB> path, with kind one of agent, flow, flowmeta, decider, instance.
// For each file the tool loads it and writes it back, then compares the YAML trees of the original and the written text.
// The report holds counts and field paths only. File contents never leave this process.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: onellm-corpus <list-file> <report-file>");
    return 2;
}

var strata = new SortedDictionary<string, Stratum>(StringComparer.Ordinal);
foreach (string line in File.ReadLines(args[0]))
{
    string[] parts = line.Split('\t');
    if (parts.Length < 3)
    {
        continue;
    }

    (string stratumName, string kind, string path) = (parts[0], parts[1], parts[2]);
    if (!strata.TryGetValue(stratumName, out Stratum? st))
    {
        strata[stratumName] = st = new Stratum();
    }

    st.Files++;
    Check(kind, path, st);
}

var report = new StringBuilder();
report.AppendLine($"onellm-corpus report, {DateTime.UtcNow:yyyy-MM-dd}");
long files = 0, lossless = 0, bytesEqual = 0, errors = 0;
foreach ((string name, Stratum s) in strata)
{
    files += s.Files;
    lossless += s.Lossless;
    bytesEqual += s.ByteEqual;
    errors += s.ParseErrors;
    report.AppendLine();
    report.AppendLine($"[{name}] files={s.Files} lossless={s.Lossless} lossy={s.Lossy} parseErrors={s.ParseErrors} unchangedByteEqual={s.ByteEqual} orderIndexAdded={s.OrderIndex}");
    foreach ((string key, int count) in s.ErrorKinds.OrderByDescending(k => k.Value).Take(10))
    {
        report.AppendLine($"  parse error: {key} x{count}");
    }

    foreach ((string key, int count) in s.Diffs.OrderByDescending(k => k.Value).Take(25))
    {
        report.AppendLine($"  lossy: {key} x{count}");
    }
}

report.AppendLine();
report.AppendLine($"TOTAL files={files} lossless={lossless} lossy={files - lossless - errors} parseErrors={errors} unchangedByteEqual={bytesEqual}");
File.WriteAllText(args[1], report.ToString());
WriteExamples(args[1]);
Console.WriteLine($"files={files} lossless={lossless} lossy={files - lossless - errors} parseErrors={errors} unchangedByteEqual={bytesEqual}");
return 0;

static void WriteExamples(string reportPath)
{
    // File paths of one example per difference, for the developer to open; kept apart from the report.
    File.WriteAllLines(Path.ChangeExtension(reportPath, ".examples.txt"), Examples.Lines());
}

static void Check(string kind, string path, Stratum st)
{
    string original;
    string typed;
    string unchanged;
    try
    {
        original = File.ReadAllText(path);
        switch (kind)
        {
            case "agent":
            case "flow":
                AgentDefinition d = AgentDefinition.Parse(original, path);
                typed = DefinitionFiles.WriteAgent(d);
                unchanged = d.ToYaml();
                break;
            case "instance":
                Instance i = Instance.Parse(original, path);
                typed = InstanceWriter.Write(i);
                unchanged = i.ToYaml();
                break;
            case "decider":
                DeciderDefinition dd = DeciderDefinition.Parse(original, path);
                typed = dd.WriteTyped();
                unchanged = dd.ToYaml();
                break;
            case "flowmeta":
                FlowMetadata fm = FlowMetadata.Parse(original, path);
                typed = fm.WriteTyped();
                unchanged = fm.ToYaml();
                break;
            default:
                return;
        }
    }
    catch (Exception e)
    {
        st.ParseErrors++;
        string key = e.GetType().Name + (e is BadDefinitionException b ? $" ({FirstWords(StripLocation(b))})" : e is YamlException y ? $" ({y.Category})" : "");
        st.ErrorKinds[key] = st.ErrorKinds.GetValueOrDefault(key) + 1;
        return;
    }

    if (unchanged == original)
    {
        st.ByteEqual++;
    }

    var diffs = new Dictionary<string, int>();
    bool orderIndex = false;
    try
    {
        object? a = Normalize(YamlSerializer.Deserialize<object>(original));
        object? b = Normalize(YamlSerializer.Deserialize<object>(typed));
        Compare(a, b, "", diffs, ref orderIndex);
    }
    catch (Exception e)
    {
        diffs["<written text does not parse: " + e.GetType().Name + ">"] = 1;
    }

    if (orderIndex)
    {
        st.OrderIndex++;
    }

    if (diffs.Count == 0)
    {
        st.Lossless++;
        return;
    }

    st.Lossy++;
    foreach ((string k, int c) in diffs)
    {
        st.Diffs[k] = st.Diffs.GetValueOrDefault(k) + c;
        Examples.TryAdd(k, path + "\t" + Examples.LastConcrete.GetValueOrDefault(k, ""));
    }
}

static string StripLocation(BadDefinitionException b)
{
    // The message starts with "file:line:col: "; the file name can carry an instance id, so drop it.
    string marker = $"{b.Line}:{b.Column}: ";
    int at = b.Message.IndexOf(marker, StringComparison.Ordinal);
    return at < 0 ? b.Message : b.Message[(at + marker.Length)..];
}

static string FirstWords(string message)
{
    // Messages from the parser may quote file content; keep only a few leading words that name the problem.
    string[] w = message.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
    return string.Join(' ', w.Take(5));
}

// Canonical form: dictionaries keyed by string, lists, strings, decimals (or doubles when out of range), bools, null.
static object? Normalize(object? v)
{
    switch (v)
    {
        case null:
            return null;
        case string or bool:
            return v;
        case sbyte or byte or short or ushort or int or uint or long or ulong or BigInteger:
            return (decimal?)ToDecimal(v) ?? (object)Convert.ToDouble(v, CultureInfo.InvariantCulture);
        case float or double:
            double dv = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            return double.IsFinite(dv) && Math.Abs(dv) < 7.9e27 ? (object)(decimal)dv : dv;
        case decimal:
            return v;
        case IDictionary dict:
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry e in dict)
            {
                d[Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? ""] = Normalize(e.Value);
            }

            return d;
        case IEnumerable seq:
            var l = new List<object?>();
            foreach (object? item in seq)
            {
                l.Add(Normalize(item));
            }

            return l;
        case DateTime or DateTimeOffset:
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        default:
            return v.ToString();
    }
}

static decimal? ToDecimal(object v)
{
    try
    {
        return v is BigInteger bi ? (decimal)bi : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
    }
    catch (OverflowException)
    {
        return null;
    }
}

static void Compare(object? a, object? b, string path, Dictionary<string, int> diffs, ref bool orderIndex, string concrete = "")
{
    void Add(string kind) => diffs[$"{kind} at {(path.Length == 0 ? "<root>" : path)}"] = diffs.GetValueOrDefault($"{kind} at {(path.Length == 0 ? "<root>" : path)}") + 1;

    switch (a, b)
    {
        case (Dictionary<string, object?> da, Dictionary<string, object?> db):
            foreach ((string k, object? va) in da)
            {
                string child = path.Length == 0 ? k : path + "." + k;
                if (!db.TryGetValue(k, out object? vb))
                {
                    // A key with a null value and an absent key mean the same (spec/instances.md), so a dropped null is not a loss.
                    if (va is null || (k == "text" && va is ""))
                    {
                        continue;
                    }

                    diffs[$"key dropped at {child}"] = diffs.GetValueOrDefault($"key dropped at {child}") + 1;
                    Examples.LastConcrete[$"key dropped at {child}"] = (concrete.Length == 0 ? k : concrete + "." + k);
                    continue;
                }

                Compare(va, vb, child, diffs, ref orderIndex, concrete.Length == 0 ? k : concrete + "." + k);
            }

            foreach (string k in db.Keys)
            {
                if (!da.ContainsKey(k))
                {
                    if (db[k] is null)
                    {
                        continue;
                    }

                    if (k is "partOrder" or "extraParts")
                    {
                        orderIndex = true;
                        continue;
                    }

                    string child = path.Length == 0 ? k : path + "." + k;
                    diffs[$"key added at {child}"] = diffs.GetValueOrDefault($"key added at {child}") + 1;
                }
            }

            break;
        case (List<object?> la, List<object?> lb):
            if (la.Count != lb.Count)
            {
                Add($"list length {la.Count.ToString(CultureInfo.InvariantCulture).Length}-digit→{lb.Count.ToString(CultureInfo.InvariantCulture).Length}-digit");
                break;
            }

            for (int i = 0; i < la.Count; i++)
            {
                Compare(la[i], lb[i], path + "[]", diffs, ref orderIndex, concrete + "[" + i + "]");
            }

            break;
        case (null, null):
            break;
        default:
            if (!Equals(a, b) && !(a is decimal x && b is decimal y && x == y))
            {
                Add($"value differs ({TypeName(a)}→{TypeName(b)})");
            }

            break;
    }
}

static string TypeName(object? v) => v switch
{
    null => "null",
    string => "string",
    bool => "bool",
    decimal or double => "number",
    Dictionary<string, object?> => "map",
    List<object?> => "list",
    _ => v.GetType().Name,
};

internal static class Examples
{
    public static readonly Dictionary<string, string> LastConcrete = new();

    private static readonly Dictionary<string, string> Map = new();

    public static void TryAdd(string key, string path) => Map.TryAdd(key, path);

    public static IEnumerable<string> Lines() => Map.Select(kv => kv.Key + "\t" + kv.Value);
}

internal sealed class Stratum
{
    public int Files, Lossless, Lossy, ParseErrors, ByteEqual, OrderIndex;
    public readonly Dictionary<string, int> Diffs = new();
    public readonly Dictionary<string, int> ErrorKinds = new();
}
