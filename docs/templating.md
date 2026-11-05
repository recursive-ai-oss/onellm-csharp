# Templating

`OneLlm.Templating` renders prompts written in the language of Go's `text/template` package. It is a standalone engine: it knows nothing about agents or providers, takes a tree of plain values as its scope, and is used by definitions, scripted runs and deciders. Parsed templates are cached and immutable, so rendering is thread-safe.

```csharp
string s = Template.Render("Hello {{ .inputs.name | upper }}",
    new Dictionary<string, object?> { ["inputs"] = new Dictionary<string, object?> { ["name"] = "ada" } });
// "Hello ADA"
```

## The language

The syntax and semantics are those of the Go package documentation (<https://pkg.go.dev/text/template>). Where that page leaves output open, OneLLM matches what Go prints; the behaviour was settled by running Go 1.26 against the same templates, and the cases are kept in `testdata/templating/` and replayed by the tests.

- **Text and actions:** `{{ ... }}`, comments `{{/* ... */}}`, trim markers `{{- ... -}}` (a marker needs a space beside it).
- **Pipelines and variables:** `a | f b`, `$x := ...`, `$x = ...`, `$` for the scope root, parenthesised pipelines, field chains (`.a.b`), index by key.
- **Control:** `if` / `else if` / `else`, `range` (with `else`, `break`, `continue`, one- or two-variable form), `with` / `else with`, `define`, `template`, `block`.
- **Range** works over lists and arrays, maps (keys in sorted order), and integers. A string is not rangeable.
- **Builtins:** `and` and `or` (short-circuit), `not`, `len`, `index`, `slice`, `print`, `printf`, `println`, `eq` (several arguments), `ne`, `lt`, `le`, `gt`, `ge`, `call`, `html`, `js`, `urlquery`.
- **Values:** `nil` or a missing key prints `<no value>`. Integers are Go `int`, other numbers are `float64` (printed as Go prints `%v`: shortest form, with an exponent below `1e-4` and from `1e+06` upward (observed from Go 1.26); JSON text switches at `1e-6` and `1e21`). Booleans print `true`/`false`. Lists and maps print as `[a b]` and `map[k:v]` with keys sorted.
- **Scope values:** `null`, strings, booleans, numbers, `Dictionary<string, object?>` and other string-keyed maps, `List<T>`, arrays and `JsonElement`. They are read directly, without reflection.

### Known differences from Go

- `date` takes RFC 3339 times only (`Z` or `+hh:mm`, one- or two-digit hour).
- Strings are UTF-16. `slice` on a string counts UTF-8 bytes, but a cut through the middle of a multi-byte character yields U+FFFD instead of a raw byte, so the length can differ.
- `slice` of a list cannot extend past the list's length; Go allows it up to the backing array's capacity, which a host list does not have.
- Complex number constants (`1i`) are not supported.
- `%v` of a 32-bit float is not modelled (all non-integer numbers are 64-bit).
- Decimal values are converted to `float64`.
- Regular expressions use a linear-time engine, so back-references and look-around are not available.

## Standard functions

Always available, on top of the builtins:

| Function | Meaning |
|---|---|
| `raw s` | Returns `s` unchanged. Use `{{ raw "{{" }}` to print a literal delimiter outside a code region. |
| `embed ref` | Inserts another prompt, rendered in the same scope (see [Embed](#embed)). |
| `base p`, `dir p`, `ext p`, `trimExt p`, `pathJoin a b ...`, `isAbs p` | Slash-path helpers (`/` separated; `pathJoin` cleans the result). |
| `lower s`, `upper s`, `trim s` | Case mapping and whitespace trimming. |
| `trimPrefix s p`, `trimSuffix s p`, `replace s old new` | `replace` replaces every occurrence. |
| `contains s sub`, `hasPrefix s p`, `hasSuffix s p` | Substring tests. |
| `default fallback v` | `fallback` when `v` is nil or the empty string, otherwise `v`. |

## OneLLM-only extensions

These do not exist in Go's package or in any prompt host that only offers the functions above. A template that uses them is not portable.

| Function | Meaning |
|---|---|
| `toJson v`, `toPrettyJson v` | JSON text of a value. Keys sorted, no HTML escaping; pretty form indents by two spaces. |
| `toYaml v` | YAML text of a value (via OneYaml). |
| `join sep list` | Joins the items of a list with `sep`. |
| `split sep s` | Splits `s` on `sep` into a list. |
| `indent n s`, `nindent n s` | Prefix each line with `n` spaces; `nindent` also starts with a newline. |
| `truncate n s` | At most `n` characters of `s`. |
| `now` | The current local time from the render's `TimeProvider`. Rendering fails if none was given. |
| `date layout t` | Formats an RFC 3339 string or time with a Go layout (`2006-01-02`). |
| `regexMatch pattern s`, `regexReplace pattern repl s` | Regular expressions (`$1`, `${name}` in the replacement). |
| `dict k v ...`, `list a b ...` | Build a map or a list inline. |

Hosts can register their own functions with `TemplateFunctions` (delegates with declared argument kinds; no reflection). A host function can replace a standard one or a builtin by name.

## Scope

The host supplies the scope. The names OneLLM definitions use: `.inputs`, `.system`, `.time` (`now`, `today`, `timezone`), `.git` (opt-in), `.env` (empty unless the host allow-lists names), `.swarm`, `.invocationMode`, and `.turns[i].text` / `.turns[i].json` in scripted runs. The engine itself only needs a map.

## Code regions

Prompts often show template syntax or code. Before parsing, `{{` and `}}` inside Markdown code are escaped so they print literally: fenced blocks (three or more backticks or tildes, indented at most three spaces, closed by a fence at least as long) and inline spans (a run of backticks closed by a run of the same length). Text outside code regions is parsed normally. Pass `EscapeCodeRegions = false` in `TemplateParseOptions` to turn this off. Error positions refer to the original text.

## Embed

`{{ embed "ref" }}` inserts the rendered text of another prompt, in the same scope, at that point.

- **Sources, in order:** the first `IEmbedSource` that returns content wins. Put the relative-path source first (`FileEmbedSource`: relative to the including file, confined to a root, symbolic links resolved, absolute references ignored), then registered prompts (`PromptEmbedSource`), then any remote source such as an HTTP resolver. **No source is configured by default**, so a template cannot read files or the network unless the host allows it.
- **Depth and cycles:** nesting is limited (`MaxEmbedDepth`, default 16) and a prompt that includes itself, directly or through others, fails with the chain of keys. Both errors carry the position of the `embed` call.
- Embedded text is parsed and cached like any template.

## missingKey

`MissingKeyMode.Default` renders a missing map key as `<no value>`. `MissingKeyMode.Error` fails and names the key. Definitions default to `Default`; deciders always use `Error`.

## Errors

`TemplateException` carries `Kind` (`Parse`, `Execute`, `Embed`, `Limit`), the template name, 1-based `Line` and `Column`, byte `Offset` and the detail. Rendering is also limited to 64 Mi characters of output and honours a cancellation token.

## Prompt caching

Provider prompt caches match on the exact leading text. A system prompt that renders `now`, `date` of `now`, `.time.now` or anything else that changes between calls differs every time and defeats the cache. Put changing values late in the prompt (a user turn, or the end of the system prompt), or use `.time.today` when day resolution is enough.
