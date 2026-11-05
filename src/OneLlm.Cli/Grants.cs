namespace OneLlm.Cli;

/// <summary>Turns the grant flags into the runtime's capabilities. Nothing is granted unless a flag says so.</summary>
internal static class Grants
{
    /// <summary>
    /// Files under <paramref name="root"/> always; shell only with <c>--allow-shell</c> (any command), <c>--allow-shell=git,dotnet</c> (those
    /// programs, no shell) or <c>--allow-shell=ask</c> (any command, each one confirmed at the terminal); network only with
    /// <c>--allow-net</c> (public hosts) or <c>--allow-net=host,*.example.com</c>.
    /// </summary>
    public static Capabilities FromFlags(string root, CliArgs args, ShellApprover terminalApprover)
    {
        Capabilities caps = Capabilities.Files(root);
        if (args.Has("allow-shell"))
        {
            string v = args.Get("allow-shell") ?? "";
            caps += v switch
            {
                "" => Capabilities.Shell,
                "ask" => Capabilities.ShellWithApproval(terminalApprover),
                _ => Capabilities.ShellCommands(Split(v)),
            };
        }

        if (args.Has("allow-net"))
        {
            string v = args.Get("allow-net") ?? "";
            caps += v.Length == 0 ? Capabilities.AnyPublicHosts : Capabilities.Network(Split(v));
        }

        return caps;
    }

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
