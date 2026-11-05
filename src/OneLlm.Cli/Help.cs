namespace OneLlm.Cli;

/// <summary>The text of <c>onellm --help</c> and of each verb's help.</summary>
internal static class Help
{
    public const string Text = """
        onellm: run OneLLM agents, scripts and deciders from the command line.

        Usage: onellm <command> [options]

        Commands:
          run <definition>      Run an agent (.yaml), a scripted flow (.flow), a decider (directory with
                                decider.yaml) or an embedded example (examples/<name>).
          chat <model>          Talk to a model, one line at a time.
          instances             list | show <id> | resume <id> | fork <id>
          cost [id]             What an instance cost, or what everything in the state directory cost (--since 7d).
          catalog [model]       List models, or show one model's values and the layer each came from.
          examples [show <n>]   List the embedded example agents, or print one.

        Common options:
          --root <dir>          The directory tools may read and write (default: the current directory).
          --state <dir>         Where instances (instances/) and records (records/) are kept (default: <root>/.onellm).
          --max-cost <usd>      Stop a run that would spend more than this.
          --tools <file|dir>    Load JavaScript tool files (repeatable) so definitions can list them.
          -h, --help            Help for the command.

        Grants (run, instances resume): files under --root are always granted; nothing else is unless you say so.
          --allow-shell             let tools run any shell command
          --allow-shell=git,dotnet  let tools run only these programs (no shell is involved)
          --allow-shell=ask         any command, each one confirmed at the terminal
          --allow-net               let tools reach public hosts
          --allow-net=a.com,*.b.org let tools reach only these hosts

        Keys come from the environment (ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, BRAVE_API_KEY, ...).

        Exit codes: 0 done (or the agent is waiting for your next message), 1 the run failed, 2 bad command line,
        3 a file, definition, grant, model or key problem, 4 the agent used all its iterations, 130 cancelled.

        """;

    public static string For(string verb) => verb switch
    {
        "run" => """
            Usage: onellm run <definition> [--input k=v]... [--input-json '{...}'] [--prompt text] [--model name]
                              [--allow-shell[=cmds]] [--allow-net[=hosts]] [--root dir] [--state dir]
                              [--max-cost usd] [--tools file|dir]... [--yes] [--quiet]

            The file decides the mode: examples/<name> and agent YAML run the tool loop; a .flow file runs its prompts in
            order, one model call each; a decider directory (or its decider.yaml) asks its questions and prints its outputs.
            --input k=v sets an input (k=@file reads the value from a file); --input-json sets inputs from a JSON object
            (or @file). --prompt is the first user message of an agent. --model replaces the definition's model.
            When a tool call needs approval the terminal asks; --yes approves every one. --quiet hides the tool-call lines.
            The result goes to standard output; progress and the cost line go to standard error.

            """,
        "chat" => "Usage: onellm chat <model> [--system text] [--root dir] [--state dir] [--max-cost usd]\n\nReads lines from standard input and streams each answer. /exit or end of input stops. The chat is saved as an instance.\n",
        "instances" => "Usage: onellm instances list [--state-filter state] [--name text] [--limit n]\n       onellm instances show <id>\n       onellm instances resume <id> [--prompt text] [--yes] [--allow-shell[=cmds]] [--allow-net[=hosts]]\n       onellm instances fork <id> [--at messageId]\n\nTab-separated: id, state, modified (UTC), name, model.\n",
        "cost" => "Usage: onellm cost [id] [--since 24h|7d|2026-11-01]\n\nWith an id: the instance's cost by model, and with its sub-agents. Without: everything saved in the state directory, optionally since a time. Dollars to six places.\n",
        "catalog" => "Usage: onellm catalog [model]\n\nWithout a model: every catalog model and its context window. With one: each value and, in brackets, the layer that set it (shipped, user, code). User files come from ONELLM_CATALOG.\n",
        "examples" => "Usage: onellm examples [show <name>]\n\nLists the example agents built into the tool. 'show' prints one so you can copy and change it.\n",
        _ => Text,
    };
}
