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
| Shell | off | `Capabilities.Shell` (open), `ShellCommands(...)` (allow-list), `ShellWithApproval(approver)` (every command asks that delegate inside the tool), `ShellWithLoopApproval` (every command is held in the agent loop's saved `waiting_for_approval` gate until a person approves it, also after a restart) |
| Network | off | `Network(entries)`; `AnyPublicHosts` allows every public host |

`a + b` unions the grants. The grants an agent started with are saved with its instance and re-applied (intersected with the resuming runtime's) on resume; network entries are saved only for the built-in `Capabilities`, so a custom `ICapabilities` that allows network resumes with it off. Agents reach only their own sub-agents through the agent tools (see the agents notes). A saved instance whose grants were recorded but are missing or malformed fails closed: it gets none and does not run (see the agents notes).

The saved `spawnerInstanceId` and grants are trusted when an instance is loaded; they carry no signature. They are protected by keeping the store out of the model's reach (the protected paths above), not by checking them, so a host that puts an instance store somewhere the file tools can write has no isolation between agents. Host-registered definitions (`Runtime.AgentDefinitions`) keep their own tool sets, bounded only by the parent's grants: registering a definition lets any agent that can name it use its tools.

`a.Intersect(b)` is what a sub-agent gets: never wider than either side (roots are intersected by containment, the shell is the stricter policy, hosts must be allowed by both, and with two approvers both are asked). An intersection never reaches a kind of address one side lacks: `AnyPublicHosts` intersected with `Network("localhost")` allows nothing, because public-only access never covers loopback, private or link-local addresses, and entries that can (addresses, ranges, `localhost` names) are dropped. Write protections (below) are the union of both sides'.

## Files

Every path is resolved to a real path before the root check: relative paths start at the working directory, `..` is applied to the real directory (so `link/..` is the link target's parent), symlinks are followed (loops are refused), and a path whose last components do not exist yet (a write target) is resolved through its deepest existing ancestor. A path outside the roots, a sibling that merely shares a prefix (`/work2` for `/work`), and a symlink or dangling symlink leading out are refused. On Windows and macOS comparison ignores case; Windows device names (`CON`, `NUL`, …) and alternate streams are refused; junctions and other reparse points are resolved like symlinks.

**What is guaranteed between check and use.** Files are opened through a double check: resolve, open, then resolve the opened path again and compare. A link swapped in between the first resolution and the open is detected and the handle is closed before anything is read, and a write through a swapped link does not truncate the outside file. Not covered: a link swapped and swapped back around the open, hard links (a hard link inside a root is a name for the same file), and a dangling-link swap on a write target can create an empty file outside the roots before the refusal. Hosts that face hostile local users should place the roots on a directory only the agent can write to.

**Files the model cannot write, or read, even under a root.** Two kinds of path are refused by the file tools:
- *Repository control paths*: any `.git`, `.hg`, `.svn`, `.bzr`, `.jj`, `_darcs`, `.fslckout`, `.sl` or `CVS` component (case-insensitive, trailing dots and spaces ignored) and `.gitmodules`. A root that is itself one of these, or itself a git directory, is protected too. A program on the shell allow-list (`git`, a build tool) reads configuration and hooks from there, so a write is a way to run any command. Writes only; reads are fine. A git directory is also recognised by what it holds, not by its name: any directory below a root that has `HEAD`, `objects` and `refs` (a bare repository, or the target of a working tree's `.git` file made with `--separate-git-dir`) refuses writes to everything in it, and the write that would complete that set is refused whatever order the files arrive in. (A `config` planted in a plain directory that a later `git init` adopts is not covered; do not allow-list `git init` next to a model that writes files.) `Capabilities.WithRepoControlWrites()` allows all of this when the host has no shell granted or trusts the model. `apply_patch` checks every target, counting the files the patch itself creates, before writing any, so a refused file leaves all files untouched; only a failure while writing (a full disk, a concurrent change) can leave a partial result.
- *The runtime's own state*: the `.onellm` directory under a `Capabilities.Files(root)` grant, and the directories of a `FileInstanceStore` and `JsonlRecordSink` the `Runtime` uses (read `Runtime.Capabilities` to get the protected value), plus anything added with `Capabilities.WithProtectedPaths(...)`. These are neither readable nor writable by `read_file`, `write_file`, `replace_string`, `apply_patch`, `list_files` (the directory is left out of listings), `search_files_regex` and `run_decider`: the saved instances hold other agents' transcripts and tool results, which would undo the subtree scoping of the agent tools, and the model could rewrite its own transcript, approvals and audit records. `ReadOnlyFiles` protects the default `.onellm` too. The `todo` and `memory` tools still read and write their files in the default state directory; if you move it with `BuiltinOptions.StateDirectory`, protect it too.

## Shell

`execute_terminal_command` is off until granted.
- **Open:** the line runs in the platform shell.
- **Allow-list:** the line is never given to a shell. It is split into a program and arguments; shell syntax (`;`, `&&`, `|`, backticks, `$(`, redirection, globbing, quoting tricks) is refused before anything runs, and metacharacters inside an argument are plain text. An entry is a program (`ls`) or a program with leading arguments (`git status`), which then allows only those subcommands. The program is looked up on `PATH` only (never the working directory).
- **Environment.** Allow-listed and approval-gated commands start from a minimal environment (`PATH`, `HOME`, `USER`, `LOGNAME`, `LANG`, `LC_*`, `TERM`, `TZ`, `TMPDIR` and the Windows system variables); nothing else of the host's environment reaches them. The host adds names with `BuiltinOptions.ShellInheritedEnvironmentNames`. The model may not set any variable (`GIT_CONFIG_*`, `PAGER`, `HOME`, `PYTHON*` and the like change what an allowed program runs) unless the host names it in `BuiltinOptions.ShellModelEnvironmentNames`. With an open shell the child inherits the host environment minus variables that look like secrets (names ending in KEY, TOKEN, SECRET or PASSWORD, cloud credential variables; `BuiltinOptions.ShellInheritSecrets` keeps them), which is a name heuristic, not a guarantee; the model may set any variable except those that replace the program search path or loader (`PATH`, `LD_*`, ...).
- **An allow-list entry is not a sandbox.** It grants everything that program can do. `git`, `dotnet`, `make`, `npm`, `find` and interpreters can run arbitrary code from files in the tree or from their arguments; list only programs and subcommands whose full behaviour you accept. The file grants do not apply to what a listed program opens: `cat /abs/path` reads any file the operating-system user can, and `working_dir` needs only read access.
- **Terminal approval** (the `onellm` tool) prints the program, arguments and working directory with control characters shown as `\uXXXX` escapes.
- **Output:** 10 KB per stream by default (`BuiltinOptions.ShellOutputLimitBytes`): the head and the tail are kept with an elision marker. Default timeout 30 s, capped at `ShellMaxTimeoutSeconds`. Timeout and cancellation kill the process and its children.
- Known gap: the kill is `Process.Kill(entireProcessTree)`; a child that detaches into its own session (`setsid`) can outlive a timeout. It needs an open shell, because a program on an allow-list cannot be given shell syntax.
- Known gap: hard links. A hard link inside a root to a file outside it reads and writes the same file; .NET has no portable link count without native calls. Keep roots on a directory only the agent writes to.

## Network

Tool traffic goes through `EgressClient`, which is public so adapters can reuse it. Entries: `host`, `*.suffix` (subdomains, not the apex), `host:port`, an address or a CIDR range, or `localhost`. Rules:
- A name entry never grants loopback, private or link-local addresses, even if the name resolves to one (DNS rebinding); the address is checked when the connection is made. An address or CIDR entry, or the `localhost` entry, does grant them.
- Redirects are followed by hand, at most a fixed number of hops, and every hop is checked against the grant before it is requested. When a hop changes origin, every caller header except Accept, Accept-Language, Accept-Encoding, Content-Type and User-Agent is dropped, and a 307 or 308 that would resend a request body there is refused. A 303 turns a POST into a GET.
- Connection pools are kept per network policy. The address check runs when a connection is opened, so a pool shared between grant sets would let a narrower grant reuse a loopback connection a wider one opened; grants with the same entries share a pool, different ones never do.
- Loopback, private and link-local ranges include the IPv6 forms that embed or translate to IPv4 (`::ffff:a.b.c.d`, `::a.b.c.d`, `::ffff:0:a.b.c.d`, `64:ff9b::/96`, `64:ff9b:1::/48`, `2002::/16`).
- The media tools (`generate_image`, `transcribe`, ...) read granted files and send them to the provider the host configured; the network grant is not consulted for that provider.
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

`search_files_regex` uses a linear-time regex engine, so a pattern cannot hang the tool. Glob patterns (`inclusionPatterns`, `exclusionPatterns`, `.gitignore` lines) are matched in time proportional to pattern times name, however many `*` they hold. `list_files` marks a symlink as `(symlink)` and does not print its target, which may lie outside the roots. `run_decider` resolves every link in the decider's directory against the read roots before loading it. `search_web` is costed per query from the pricing data in `SearchPricing`; the Brave key comes from `BRAVE_API_KEY`, is held in a `Secret` and is scrubbed from errors.

`ask_user` returns one JSON part `{"type":"ask_user","question":…,"choices":[…]}`; `AskUser.TryGetRequest` recognises it so a runner can put the instance in a waiting state.

### `apply_patch` format

A unified diff, as `diff -u` or `git diff` print it:
- File headers `--- a/path`, `+++ b/path` (a leading `a/` and `b/` are removed when both are present); `/dev/null` as the old path creates a file and as the new path deletes one. Renames are not supported.
- Hunks `@@ -oldStart,oldCount +newStart,newCount @@` with lines starting with a space, `-` or `+`; `\ No newline at end of file` is honoured.
- Line numbers are hints: a hunk applies where its context and removed lines match exactly, nearest the stated line. If any hunk matches nowhere the whole patch is rejected and nothing is written. The file's line endings are kept. `dry_run` checks without writing.

## Reference loading

`ToolReferenceResolver` checks a definition's references against the registered tools and the grants. `Strict` throws `BadDefinitionException` (file, line, column, naming the tool and the grant or package to add); `Lenient` removes the reference and records a warning. A reference to a group counts as granted only when every tool in the group is granted; in lenient mode the warning says the source was left out, and the tools of the group that are granted are still registered individually (they hold only grants the runtime gave). A definition cannot grant anything.

## On Windows

The same rules hold on Windows, with these forms handled explicitly (each has a test that runs there):
- **Junctions and symbolic links** are followed like any link before the root check, for reads, writes, listings, searches and `apply_patch`.
- **Case.** Roots, protected directories and the version-control names (`.GIT`, `.Git`, `CVS`) compare without regard to case.
- **8.3 short names.** Windows can answer to a generated short name (`GIT~1` for `.git`, `ONELLM~1` for `.onellm`), which would walk around name-based protection. A path component below a root that contains `~` must be the exact name of an entry in its directory; one that exists only through its alias is refused. A short alias of the root itself is a different spelling than the grant and is refused as outside; grant the root by its long name. Volumes with short names switched off have no aliases to refuse.
- **Trailing dots and spaces** are dropped from each name, as Windows does, before any comparison (`.onellm.\instances` is `.onellm\instances`); a name of only dots and spaces is refused.
- **Drive-relative, device, extended and network forms** (`C:foo`, `\foo`, `/foo`, `\\?\`, `\\.\`, `\??\`, `\\server\share`) are refused. A path on a drive or share no root is on is refused before anything is probed, so no network connection is made. Forward and back slashes both separate names.
- **Alternate data streams** (`file.txt:stream`) and **device names** (`NUL`, `CON`, `COM1`..`COM9`, `LPT1`..`LPT9`, the superscript forms, with or without an extension) are refused.
- **cmd.exe, batch files and PowerShell.** With an allow-list, a program that re-reads its arguments as commands (`cmd`, a `.bat` or `.cmd` file, `powershell`, `pwsh`) is refused any argument holding the characters that start another command or expand a variable (`& | < > ^ % !` and quotes for cmd; `; & | $` backtick, parentheses, braces and quotes for PowerShell). An entry that lists `cmd /c echo` therefore cannot be extended to `cmd /c echo hi & whoami`. Listing `cmd`, `powershell` or an interpreter without leading arguments still grants everything that program can do: an allow-list entry is not a sandbox. `.bat` and `.cmd` are not found by bare name in allow-list mode (PATHEXT entries for them are skipped); name the file by its full path.
- **Environment.** Allow-listed and approval-gated commands start with `PATH`, `PATHEXT`, `ComSpec`, `SystemRoot`, `SystemDrive`, `windir`, the user-profile and program-files variables, `TEMP`/`TMP` and `NUMBER_OF_PROCESSORS`; nothing else of the host's environment is passed.
- **Timeouts** kill the process and its children (`Process.Kill` of the tree).

Not covered on any platform: hard links (see above), a detached process that survives its parent, and an allow-listed program that is itself an interpreter.
