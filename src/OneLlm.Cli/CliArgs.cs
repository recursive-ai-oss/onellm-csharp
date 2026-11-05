namespace OneLlm.Cli;

/// <summary>A command line split into positional words and options. Options are <c>--name value</c> or <c>--name=value</c>; flags take no value.</summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    /// <summary>The words that are not options, in order.</summary>
    public List<string> Positional { get; } = [];

    /// <summary>Splits <paramref name="args"/>.</summary>
    /// <param name="args">The arguments after the verb.</param>
    /// <param name="valued">Options that take a value, as <c>--name value</c> or <c>--name=value</c>.</param>
    /// <param name="optional">Options whose value is only given as <c>--name=value</c> (<c>--allow-shell</c>).</param>
    /// <param name="flags">Options with no value.</param>
    /// <returns>The split line.</returns>
    /// <exception cref="UsageException">An option is unknown or lacks its value.</exception>
    public static CliArgs Parse(IReadOnlyList<string> args, string[] valued, string[] optional, string[] flags)
    {
        var result = new CliArgs();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--")
            {
                result.Positional.AddRange(args.Skip(i + 1));
                break;
            }

            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(a);
                continue;
            }

            string name = a[2..];
            string? value = null;
            int eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }

            if (flags.Contains(name, StringComparer.Ordinal))
            {
                if (value is not null)
                {
                    throw new UsageException($"--{name} takes no value.");
                }

                result.Add(name, "");
            }
            else if (optional.Contains(name, StringComparer.Ordinal))
            {
                result.Add(name, value ?? "");
            }
            else if (valued.Contains(name, StringComparer.Ordinal))
            {
                if (value is null)
                {
                    i++;
                    value = i < args.Count ? args[i] : throw new UsageException($"--{name} needs a value.");
                }

                result.Add(name, value);
            }
            else
            {
                throw new UsageException($"Unknown option --{name}.");
            }
        }

        return result;
    }

    /// <summary>Whether the option was given.</summary>
    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>The last value given for an option, or null. A flag or bare optional option has the empty string.</summary>
    public string? Get(string name) => _options.TryGetValue(name, out List<string>? v) ? v[^1] : null;

    /// <summary>Every value given for a repeatable option.</summary>
    public IReadOnlyList<string> GetAll(string name) => _options.TryGetValue(name, out List<string>? v) ? v : [];

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out List<string>? list))
        {
            _options[name] = list = [];
        }

        list.Add(value);
    }
}

/// <summary>The command line is wrong. The message says how; the exit code is 2.</summary>
internal sealed class UsageException(string message) : Exception(message);
