# Tool signatures and JSON Schema

This note is the contract between a C# method signature and the JSON Schema 2020-12 the model sees. Two implementations follow it and a test keeps them identical: the `[Tool]` source generator (compile time, trim/AOT-safe) and `Tool.Create(delegate)` (reflection). Other libraries that describe tools in C# can conform to the same table.

Schema dialect: [JSON Schema 2020-12](https://json-schema.org/draft/2020-12) (core and validation vocabularies). Only the keywords listed below are ever written.

## A tool

| Part | Rule |
|---|---|
| Name | `[Tool(Name = ...)]`, else the method name in snake_case with a trailing `Async` removed (`GetDocketAsync` → `get_docket`, `HTTPRequest` → `http_request`). 1–64 letters, digits, `_`, `-`. |
| Description | `[Tool(Description = ...)]`, else `[Description]` on the method, else the XML `<summary>` (generator only), else empty. |
| Input schema | One closed object, built from the parameters (below). |
| Output schema | Written when the return type is not text, a `Part`, a sequence of parts, a `ToolResult`, a `JsonNode` or void: the schema of the return type. Not sent to the model; it types the result of a tool that ends the loop. |
| Metadata | `EndsLoop`, `RequiresApproval`, `ParallelSafe` (default true), `TimeoutSeconds` (0 = none). |

### Parameters

- `ToolContext` and `CancellationToken` parameters are filled in by the runtime and never appear in the schema (at most one of each).
- Every other parameter is a property named exactly as the C# parameter (camelCase by convention; names are not rewritten).
- **Required** unless the parameter has a default value. A nullable type (`int?`, `string?`) without a default is still required, and accepts JSON `null`.
- A default value of a string, number, boolean, char or enum type is written as `"default"`. A `null` default writes nothing.
- The object has `"additionalProperties": false`. A call that names an argument the tool does not declare fails with an error result naming it. `required` is omitted when empty.
- Description: `[Description]` on the parameter, else the method's `<param>` (generator only).

## Types

Keyword order in every fragment is fixed: `type`, `format`, `description`, `enum`, `minimum`, `maximum`, `minLength`, `maxLength`, `pattern`, `contentEncoding`, `items`, `minItems`, `maxItems`, `uniqueItems`, `properties`, `required`, `additionalProperties`, `default`. (Models read schemas top to bottom.)

| C# type | Fragment |
|---|---|
| `bool` | `{"type":"boolean"}` |
| `sbyte` `byte` `short` `ushort` `int` `uint` `long` `ulong` | `{"type":"integer","minimum":MIN,"maximum":MAX}` with the type's range (`ulong`: 0 to 18446744073709551615). A JSON number that is fractional or out of range is refused. |
| `float` `double` `decimal` | `{"type":"number"}` |
| `string` | `{"type":"string"}` |
| `char` | `{"type":"string","minLength":1,"maxLength":1}` |
| `Guid` | `{"type":"string","format":"uuid"}` |
| `DateTime`, `DateTimeOffset` | `{"type":"string","format":"date-time"}` (RFC 3339) |
| `DateOnly` | `{"type":"string","format":"date"}` (`yyyy-MM-dd`) |
| `TimeOnly` | `{"type":"string","format":"time"}` (`HH:mm:ss[.fffffff]`) |
| `TimeSpan` | `{"type":"string","pattern":"^-?(\\d+\\.)?\\d{2}:\\d{2}:\\d{2}(\\.\\d{1,7})?$"}` (the invariant "c" form, `[-][d.]hh:mm:ss[.fffffff]`) |
| `Uri` | `{"type":"string","format":"uri"}` (absolute) |
| `byte[]` | `{"type":"string","contentEncoding":"base64"}` |
| enum | `{"type":"string","enum":[member names in declaration order]}`; names are matched exactly |
| `T?` (value or reference type) | the fragment of `T` with `"null"` added to `type` (`["integer","null"]`); an enum's `enum` list gains `null`. A fragment with no `type` (any) is unchanged. |
| `T[]`, `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>` | `{"type":"array","items":FRAGMENT(T)}` |
| `HashSet<T>`, `ISet<T>`, `IReadOnlySet<T>` | as above plus `"uniqueItems":true`; duplicates are refused |
| `Dictionary<string,T>`, `IDictionary<string,T>`, `IReadOnlyDictionary<string,T>` | `{"type":"object","additionalProperties":FRAGMENT(T)}` |
| `JsonElement`, `JsonNode` | `{}` (any JSON value) |
| `JsonObject` | `{"type":"object"}` |
| `JsonArray` | `{"type":"array"}` |
| record, class, struct | closed object, see below |

Not supported (generator diagnostic `OLT002`; `Tool.Create` throws `NotSupportedException`): `object`, interfaces other than the collections above, delegates, multi-dimensional arrays, dictionaries whose key is not `string`, `ref`/`out`/`in` parameters, open generic types, other collection types.

### Records and classes

```json
{"type":"object","properties":{...},"required":[...],"additionalProperties":false}
```

- **Properties:** public instance properties with a public getter, base class first, then in declaration order. `[JsonIgnore]` (always) removes one; `[JsonPropertyName]` renames it.
- **Name:** camelCase when the type's `JsonSerializerContext` uses `CamelCase` (or `JsonSerializerDefaults.Web`); as written when it uses no policy. `Tool.Create` always uses camelCase and the generator mirrors the context, so schemas agree when the context uses `CamelCase`. Other naming policies are refused (`OLT011`).
- **Required:** a `required` member, a `[JsonRequired]` property, or a parameter without a default of the type's only public constructor (a positional record's primary constructor). Non-nullable alone does not make a property required.
- **Property schema:** the fragment of its type, with the property's `[Description]` (or `<summary>`; for a positional record the `<param>` of the record) and annotations.
- A type that refers to itself, directly or through its members, is refused (`OLT006`).
- **Serialization:** reading and writing these types goes through a `System.Text.Json` source-generated `JsonSerializerContext` that lists the type with `[JsonSerializable]`; the generator finds it in the compilation (`OLT005` when none lists the type). Enum properties need the string enum converter in that context (`UseStringEnumConverter = true`), matching the `enum` of names above. Return values of record, collection and dictionary types must be listed too (`List<Item>` as itself).

### Annotations

| Attribute | Keyword |
|---|---|
| `[Description("...")]` | `description` |
| `[Range(min, max)]` (numeric) | `minimum`, `maximum` (replace the type's range) |
| `[StringLength(max, MinimumLength = n)]` | `maxLength`, `minLength` (when n > 0) |
| `[MinLength(n)]`, `[MaxLength(n)]`, `[Length(a, b)]` | `minLength`/`maxLength` on strings, `minItems`/`maxItems` on arrays |
| `[RegularExpression(p)]` | `pattern` |
| `[EmailAddress]` | `format: email` |
| `[Url]` | `format: uri` |

### Doc comments

The generator reads `///` comments from the source tree itself, so they work whether or not the project generates a documentation file. `<summary>` and `<param>` text is flattened: whitespace collapsed, `<see cref="X"/>` becomes `X`, `<paramref name="p"/>` becomes `p`, `<c>`/`<para>` keep their text. `[Description]` always wins. `Tool.Create` cannot see doc comments and uses `[Description]` only.

## Results

| Return type | Result |
|---|---|
| `void`, `Task`, `ValueTask` | no content |
| `string` | one text part |
| `Part` (and subclasses), a sequence of them | those parts |
| `ToolResult` | passed through |
| `JsonNode`, `JsonObject`, … | one JSON part |
| a number, `bool`, `Guid`, date/time, `Uri`, enum (its name) | one JSON part with that value |
| a record, collection, dictionary | one JSON part from the context's serializer |
| `T?` that is null | the text `null` |

`Task<T>` and `ValueTask<T>` are unwrapped. For a tool with `EndsLoop`, the returned value is also the typed result (`ToolResult.TypedResult`).

## Errors become results

`ToolInvoker.InvokeAsync` never throws for a tool's own failure:

| Cause | Error result |
|---|---|
| Missing, unknown, null or wrong-typed argument | `Argument 'items[2].qty' must be an integer…` (the path names the argument) |
| Exception in the tool | `Tool 'x' failed: InvalidOperationException: message` (no stack trace) |
| Timeout (`TimeoutSeconds`, measured on the context's `TimeProvider`) | `Tool 'x' timed out after 5 s.` |

Cancellation by the caller is not an error result: `OperationCanceledException` propagates so the run records itself as cancelled. Cost lines added with `ToolContext.AddCost` are kept on every path.

## Generator diagnostics

| Id | Meaning |
|---|---|
| OLT001 | The containing type (and every type around it) must be a `partial` class |
| OLT002 | A parameter type has no schema |
| OLT003 | The return type is not supported (or `async void`) |
| OLT004 | The method is generic |
| OLT005 | A record/class/collection needs a `JsonSerializerContext` that lists it |
| OLT006 | A type refers to itself |
| OLT007 | Two tools in one class have the same name |
| OLT008 | The tool name is not 1–64 letters, digits, `_` or `-` |
| OLT009 | More than one `ToolContext` or `CancellationToken` parameter |
| OLT010 | The containing class is generic |
| OLT011 | The serializer context uses a property naming policy other than `CamelCase` or none |

## What each provider rejects

OneLLM writes the schema above; each provider client down-converts for its API. Keywords below are the ones the vendors' structured-output and strict tool-use pages say are unsupported (checked against their reference pages when this note was written; vendors change them, and the provider clients are the place that follows).

| Keyword | Anthropic (strict) | OpenAI (strict) | Gemini |
|---|---|---|---|
| `minimum`, `maximum` | rejected | accepted | accepted |
| `minLength`, `maxLength` | rejected | not listed as supported | accepted but not enforced |
| `minItems`, `maxItems` | only `minItems` 0 or 1 | accepted | accepted |
| `uniqueItems` | rejected | not listed as supported | accepted but not enforced |
| `pattern` | simple regex only | accepted | not listed |
| `format` | `date-time` `time` `date` `duration` `email` `hostname` `uri` `ipv4` `ipv6` `uuid` | `date-time` `time` `date` `duration` `email` `hostname` `ipv4` `ipv6` `uuid` (no `uri`) | `date-time` `date` `time` |
| `contentEncoding` | not listed | not listed | not listed |
| `additionalProperties` | must be `false` | must be `false`; every property must be in `required` | accepted |
| `default` | accepted | not listed | not listed |
| union `"type":[…,"null"]` | accepted | accepted | accepted |

## Sources

- JSON Schema 2020-12, core and validation: https://json-schema.org/draft/2020-12 (Internet-Draft, (c) IETF Trust, read for the keyword definitions only).
- Anthropic structured outputs, JSON Schema limitations: https://platform.claude.com/docs/en/build-with-claude/structured-outputs
- OpenAI structured outputs, supported schemas: https://developers.openai.com/api/docs/guides/structured-outputs
- Gemini structured output, supported schema subset: https://ai.google.dev/gemini-api/docs/structured-output
