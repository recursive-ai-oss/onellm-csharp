# Capabilities and the built-in tools

What a tool may touch is decided by the host, never by a definition. The host passes a `Capabilities` value to the `Runtime`; every built-in tool asks it before it reads a file, starts a process or opens a connection. A definition's `references` only name tools; if a reference needs a grant the runtime lacks, the definition fails to load (strict, the default) or the reference is dropped with a warning (lenient).

## Composing grants

```csharp
var caps = Capabilities.Files("work")                          // read and write under ./work
         + Capabilities.ReadOnlyFiles("/data/reference")       // read only
         + Capabilities.ShellCommands("git status", "dotnet")   // allow-listed, no shell
         + Capabilities.Network("api.example.com", "*.docs.example.com");
```

| Grant | Default | Notes |
|---|---|---|
| Files | the working directory, read and write | read roots and write roots are separate |
| Shell | off | `Capabilities.Shell` (open), `ShellCommands(...)` (allow-list), `ShellWithApproval(...)` (every command asks the approver; the agent loop supplies the approver) |
| Network | off | `Network(entries)`; `AnyPublicHosts` allows every public host |

`a + b` unions the grants. `a.Intersect(b)` is what a sub-agent gets: never wider than either side (roots are intersected by containment, the shell is the stricter policy, hosts must be allowed by both, and with two approvers both are asked).

## Files

Every path is resolved to a real path before the root check: relative paths start at the working directory, `..` is applied to the real directory (so `link/..` is the link target's parent), symlinks are followed (loops are refused), and a path whose last components do not exist yet (a write target) is resolved through its deepest existing ancestor. A path outside the roots, a sibling that merely shares a prefix (`/work2` for `/work`), and a symlink or dangling symlink leading out are refused. On Windows and macOS comparison ignores case; Windows device names (`CON`, `NUL`, …) and alternate streams are refused; junctions and other reparse points are resolved like symlinks.

**What is guaranteed between check and use.** Files are opened through a double check: resolve, open, then resolve the opened path again and compare. A link swapped in between the first resolution and the open is detected and the handle is closed before anything is read, and a write through a swapped link does not truncate the outside file. Not covered: a link swapped and swapped back around the open, hard links (a hard link inside a root is a name for the same file), and a dangling-link swap on a write target can create an empty file outside the roots before the refusal. Hosts that face hostile local users should place the roots on a directory only the agent can write to.

## Shell

`execute_terminal_command` is off until granted.
- **Open:** the line runs in the platform shell.
- **Allow-list:** the line is never given to a shell. It is split into a program and arguments; shell syntax (`;`, `&&`, `|`, backticks, `$(`, redirection, globbing, quoting tricks) is refused before anything runs, and metacharacters inside an argument are plain text. An entry is a program (`ls`) or a program with leading arguments (`git status`), which then allows only those subcommands. The program is looked up on `PATH` only (never the working directory), and the model cannot change `PATH`, `LD_*` and similar variables. Variables that look like secrets (names ending in KEY, TOKEN, SECRET or PASSWORD, cloud credential variables) are not passed to the process unless the host sets `BuiltinOptions.ShellInheritSecrets`.
- **Output:** 10 KB per stream by default (`BuiltinOptions.ShellOutputLimitBytes`): the head and the tail are kept with an elision marker. Default timeout 30 s, capped at `ShellMaxTimeoutSeconds`. Timeout and cancellation kill the process and its children.
- Known gap: the kill is `Process.Kill(entireProcessTree)`; a child that detaches into its own session can outlive it.

## Network

Tool traffic goes through `EgressClient`, which is public so adapters can reuse it. Entries: `host`, `*.suffix` (subdomains, not the apex), `host:port`, an address or a CIDR range, or `localhost`. Rules:
- A name entry never grants loopback, private or link-local addresses, even if the name resolves to one (DNS rebinding); the address is checked when the connection is made. An address or CIDR entry, or the `localhost` entry, does grant them.
- Redirects are followed by hand, at most a fixed number of hops, and every hop is checked against the grant before it is requested. Credential-looking headers (names containing auth, cookie, key, token, secret or password) are dropped when a hop changes origin. A 303 turns a POST into a GET.
- System proxies are not used.

## Built-in tools

| Tool | Grant used |
|---|---|
| `read_file`, `list_files`, `search_files_regex` | file read (`read_file` of a URL: network) |
| `write_file`, `replace_string`, `apply_patch` | file read and write |
| `execute_terminal_command` | shell |
| `http_request`, `search_web`, `fetch_webpage_markdown` (`.Web` package) | network |
| `todo`, `memory` | write root (state under `.onellm/` of the working directory by default) |
| `ask_user` | none |

`search_files_regex` uses a linear-time regex engine, so a pattern cannot hang the tool. `search_web` is costed per query from the pricing data in `SearchPricing`; the Brave key comes from `BRAVE_API_KEY`, is held in a `Secret` and is scrubbed from errors.

`ask_user` returns one JSON part `{"type":"ask_user","question":…,"choices":[…]}`; `AskUser.TryGetRequest` recognises it so a runner can put the instance in a waiting state.

### `apply_patch` format

A unified diff, as `diff -u` or `git diff` print it:
- File headers `--- a/path`, `+++ b/path` (a leading `a/` and `b/` are removed when both are present); `/dev/null` as the old path creates a file and as the new path deletes one. Renames are not supported.
- Hunks `@@ -oldStart,oldCount +newStart,newCount @@` with lines starting with a space, `-` or `+`; `\ No newline at end of file` is honoured.
- Line numbers are hints: a hunk applies where its context and removed lines match exactly, nearest the stated line. If any hunk matches nowhere the whole patch is rejected and nothing is written. The file's line endings are kept. `dry_run` checks without writing.

## Reference loading

`ToolReferenceResolver` checks a definition's references against the registered tools and the grants. `Strict` throws `BadDefinitionException` (file, line, column, naming the tool and the grant or package to add); `Lenient` removes the reference and records a warning. A reference to a group needs every tool in the group granted to count as granted. A definition cannot grant anything.

## Needs a Windows run

Junction and reparse-point resolution, reserved-name handling against the real file system, the `cmd.exe` shell form and case-insensitive roots are tested through their logic on Linux and need a Windows run before release.
