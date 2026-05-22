# FReader MCP — Business Requirements Specification
**Version:** 2.0  
**Status:** Aligned with v1.6.0 implementation (all FIX/FTR/CMP items complete)  
**Scope:** Fixes, improvements, and new features for the FReader MCP tool

---

## 1. Executive Summary

FReader is an MCP tool designed to let AI assistants read source files more efficiently than raw file-reading tools. A benchmark against standard tools (Read + Grep) on real C# backend files revealed that FReader currently produces **~44% more characters and uses 60% more tool calls** for the same tasks. This BRS specifies all fixes, improvements, and new features needed to make FReader match or beat standard tools in every scenario where targeted reading applies.

---

### Changelog

| Date | Version | Change |
|------|---------|--------|
| 2026-05-21 | v1.6.0 | **FTR-006-REV-001**: Changed `SessionDates` from project-level to per-feature, enabling independent rate limits per feature. |
| 2026-05-21 | v1.6.0 | **FTR-006-REV-002**: Removed `_sessions_this_week` from `GetBenchmarkInfo` response (no longer meaningful with per-feature dates). |
| 2026-05-21 | v1.6.0 | **FTR-006-REV-003**: Changed data-gathering threshold from `> 0` to `< 8` runs — features get unlimited recording until 8 scores accumulate. |
| 2026-05-21 | v1.6.0 | **FTR-006-REV-004**: `_benchmark` always emitted in `get_instructions` (empty dict initially) — removed `> 0` guard. |
| 2026-05-21 | v1.6.0 | **FTR-006-REV-005**: Harness instructions moved into `FormatInstructions()` as HARNESS block in `_h`. |
| 2026-05-21 | v1.6.0 | **FTR-006-REV-006**: `record_benchmark` excluded from harness/deprecation table — meta-tool does not benchmark itself. |
| 2026-05-21 | v1.6.0 | **BRS-FReader-Improvements Issue 1**: `extract_function` now handles overloads — uses `GetFunctions` instead of `GetFunction`, returns `_overload_count` + `_overloads` array with `_hint`. Added `parameterTypes` and `className` optional params to input schema. |
| 2026-05-21 | v1.6.0 | **BRS-FReader-Improvements Issue 2**: `grep_in_file` reads with explicit `Encoding.UTF8` instead of default encoding. |
| 2026-05-21 | v1.6.0 | **BRS-FReader-Improvements Issue 3**: `info` response adds `_modified` field (ISO 8601 UTC). |
| 2026-05-21 | v1.6.0 | **ASCII encoding fix**: All non-ASCII characters (→ → >, — → --, × → x) replaced with ASCII equivalents across FReader, NoiseCanceler, and McpRegistrar to prevent 0x1A corruption through Windows console encoding. |

---

## 2. Current State Baseline

Benchmark results from three tasks on real C# files (45–79 lines):

| Task | Standard Tools | FReader (current) |
|------|---------------|-------------------|
| Read full file (79 lines) | 3,844 chars, 1 call | ~5,200 chars, 2 calls |
| Extract one named function | 1,849 chars, 2 calls | ~2,920 chars, 3 calls |
| Extract one named function (small) | 647 chars, 2 calls | ~980 chars, 3 calls |
| **Totals** | **6,340 chars, 5 calls** | **~9,100 chars, 8 calls** |

Root causes identified:
1. JSON encoding of `<` and `>` as `<` / `>` — inflates every C# response
2. `extract_function` is a mandatory intermediate step (adds 1 call + response per function)
3. `list_functions` returns start line only — end line requires a separate call
4. Bare `read()` with no range silently truncates to 1 line — forces a mandatory `info` call first
5. No composite tools — common 3-step workflows cannot be shortened

---

## 3. Requirements

### 3.1 FIX-001 — Resolve `<>` JSON Escaping

#### Rationale
The JSON serialiser currently escapes `<` and `>` as `<` and `>`. In C# codebases these characters appear in every generic type parameter: `Task<T>`, `ImmutableList<T>`, `WrappedResponse<T>`, `IReadOnlyDictionary<Guid, int>`. A single method signature like `Task<WrappedResponse<ImmutableList<SmsQueueObservabilitySnapshot>>>` balloons from 56 chars to 86 chars — a 54% increase on that token alone. Across a file with 10 such signatures, this adds ~300 chars of pure noise.

#### Requirement
Configure the JSON serialiser used by `read`, `list_functions`, `extract_function`, and `summarize` (see FTR-001) to use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (or equivalent for the runtime in use), allowing literal `<`, `>`, `&`, `'` in JSON output.

#### Characters affected
`<`, `>` (most impactful), `&`, `'`, `+`. Do **not** relax escaping for `"`, `\`, or control characters — these must remain escaped for valid JSON.

#### Edge Cases
- **Multi-level generics**: `IReadOnlyDictionary<Guid, int>` — must not break JSON parsing. UnsafeRelaxedJsonEscaping is safe here because these chars appear only inside string values, not as JSON structural characters.
- **String literals in code containing JSON**: A source file may contain a hardcoded JSON string like `"{ \"key\": \"value\" }"`. The inner `\"` sequences must still be properly escaped. UnsafeRelaxedJsonEscaping only relaxes the cosmetic escapes, not the structural ones.

#### Dangers
- **None significant.** This is a serialiser option change only. The output remains valid JSON. Parsers that are unexpectedly strict about `<` (non-standard) would break, but no mainstream JSON parser has this behaviour.

#### Example
```
Before: "Task<WrappedResponse<SmsQueueObservabilitySnapshot?>> GetLatestAsync()"
After:  "Task<WrappedResponse<SmsQueueObservabilitySnapshot?>> GetLatestAsync()"
```
Saving: 30 chars on this one signature alone.

---

### 3.2 FIX-002 — Add `lineEnd` to `list_functions` Output

#### Rationale
`list_functions` currently returns `{name, signature, lineStart}`. To get `lineEnd` (needed for a targeted `read`), the caller must issue a separate `extract_function` call. This adds 1 round trip and ~150 chars of response overhead per function lookup. If `list_functions` returns `lineEnd`, the caller can go directly to `read(path, lineStart, lineEnd)`, collapsing a 3-call workflow to 2.

#### Requirement
Add `lineEnd` as a mandatory column to the `list_functions` response. The columnar header changes from `["name", "signature", "line"]` to `["name", "signature", "lineStart", "lineEnd"]`.

Add an `includeSystem: boolean` parameter (default `false`). When `false`, exclude the following from the output:
- `object`-inherited methods overridden by the compiler or boilerplate: `ToString()`, `GetHashCode()`, `Equals(object?)`, `GetType()`
- Compiler-generated iterator state machine members on classes whose name matches `<MethodName>d__N`: `MoveNext()`, `get_Current()`, `Reset()`, `Dispose()`
- Compiler-generated async state machine members on classes whose name matches `<MethodName>d__N`
- Finalizers: `~ClassName()`

Token impact: a typical class has 3–5 such members. Filtering them saves ~80–120 chars per `list_functions` call with no information loss for navigation purposes.

#### Edge Cases
- **Partial classes**: C# `partial class` splits a type across multiple files. A method in a partial class has a well-defined start and end line within the file being read — use those. Do not attempt to merge across files.
- **Auto-implemented properties**: `public int Count { get; set; }` — these are technically accessor methods. If included in `list_functions` output, their `lineEnd` is the same as `lineStart` (single line). They should be **excluded** from `list_functions` by default (see FTR-001 which has a dedicated mode for properties).
- **Expression-bodied methods**: `public string Name => _name;` — single line. `lineStart == lineEnd`. Valid output.
- **Methods with XML doc comments**: The doc comment `///` lines above a method are part of the method's documentation but not its executable body. `lineStart` should point to the **first `///` line** of the doc block if present, so that reading `lineStart..lineEnd` gives the complete method including its documentation.
- **`Dispose()` on regular classes**: A developer-written `IDisposable.Dispose()` implementation must **not** be filtered even with `includeSystem: false`. The filter applies only to compiler-generated classes identifiable by angle-bracket names (`<GetEnumerator>d__0`). Developer-written `Dispose()` on a normal class is always included.
- **`ToString()` that is meaningfully overridden**: If a developer explicitly overrides `ToString()` with a non-trivial body, it is still filtered by default when `includeSystem: false`. The rationale: the caller can set `includeSystem: true` if they specifically need it, and the name `ToString` is a reliable filter key regardless of body content.

#### Dangers
- **lineEnd accuracy for nested braces**: The end-line detector must count braces correctly. Incorrect detection (off-by-one, catching the class closing brace instead of the method closing brace) would cause `read(lineStart, lineEnd)` to return the wrong content. This must be tested against methods with:
  - Nested lambda expressions with their own `{}`
  - Multi-line `switch` expressions
  - `using` blocks inside methods
  - `#region` / `#endregion` pairs

#### Example
```json
Before: {"_h":["name","signature","line"],"_r":[["GetLatestAsync","Task<...> GetLatestAsync()",21]]}
After:  {"_h":["name","signature","lineStart","lineEnd"],"_r":[["GetLatestAsync","Task<...> GetLatestAsync()",21,28]]}
```
Workflow change:
```
Before: list_functions → extract_function("GetLatestAsync") → read(21, 28)   [3 calls]
After:  list_functions → read(21, 28)                                         [2 calls]
```

---

### 3.3 FIX-003 — Fix Silent Truncation of Bare `read()` Call

#### Rationale
Calling `read(path)` with no `lineStart`/`lineEnd` on a 79-line file returned only 1 line with `_truncated: true`. The documented default is 200 lines. This forces callers to always issue an `info` call first to get the line count before reading, adding a mandatory extra round trip for any full-file read.

#### Requirement
`read(path)` with no range must return up to `truncate` lines (default 200) starting from line 1. If the file has ≤ 200 lines, return the entire file with `_truncated: false`. If > 200 lines, return lines 1–200 with `_truncated: true` and include `_total` (actual line count).

#### Edge Cases
- **Partial range — `lineStart` only**: `read(path, lineStart: N)` with no `lineEnd` reads from line N to EOF. The implicit `lineEnd` is the last line of the file. `_truncated` applies as normal if EOF exceeds the `truncate` cap.
- **Partial range — `lineEnd` only**: `read(path, lineEnd: N)` with no `lineStart` reads from line 1 to line N. The implicit `lineStart` is 1.
- **`lineStart` > `lineEnd`**: Return a structured error: `{"error":"lineStart (25) is greater than lineEnd (10)","error_code":"INVALID_RANGE"}`. Do not swap them silently — silent correction masks caller bugs.
- **`lineStart` beyond EOF**: Return a structured error: `{"error":"lineStart (150) exceeds file length (79 lines)","error_code":"INVALID_RANGE"}`. Do not return an empty result set silently.
- **`lineEnd` beyond EOF**: Clamp silently to the actual last line. Return `_total` reflecting the real file length so the caller can detect the clamp. Do not error — this is the most natural "read to end of file" idiom and over-specifying an end line should not be punished.
- **Empty file**: Return `{"_h":[...],"_r":[],"_truncated":false,"_total":0}` — do not error.
- **Binary file**: Return a structured error: `{"error":"File is binary and cannot be read as text","error_code":"BINARY_FILE"}`.
- **File changes between `info` and `read`**: If the file is modified between calls, `_total` in `read` may differ from `_lines` in `info`. This is acceptable — return the current state of the file.

#### Dangers
- **Callers relying on the broken behaviour**: If any existing integration was built around the 1-line truncation as a way to "peek" at a file, fixing this will change behaviour. However, this behaviour is undocumented and clearly unintentional — the risk is low and the fix is correct.

---

### 3.4 FIX-004 — Output Format for `read`: Reduce Per-Line Overhead

#### Rationale
The current columnar JSON format for `read` is:
```json
{"_h":["line","text"],"_r":[[1,"content"],[2,"content"],...]}
```
Each line costs: `[N,"` + content + `"],` = ~6 chars overhead per line on top of the actual content. For a 79-line file this is ~474 chars of structural overhead alone. The standard Read tool uses a plain `N\tcontent\n` format which has lower overhead (~3 chars: the number + tab + newline).

#### Requirement
Reduce per-line overhead by switching to a more compact representation. **Required approach:** Tab-prefix format — output `_r` as an array of strings in the form `"N\tcontent"` (line number, a literal tab character, then the line content) rather than nested arrays `[N, "content"]`. This eliminates one level of JSON array nesting per line and avoids the delimiter-ambiguity problem that `": "` creates in C# code (ternary expressions, dictionary initialisers, `case:` labels, `default:` labels — all contain colons followed by spaces).

Alternative considered and rejected: `"N: content"` — rejected because C# source frequently contains `: ` as meaningful syntax (e.g., `int x = flag ? a : b;`), making it impossible to split on the first `: ` reliably without language-aware parsing.

Alternative (if backward compatibility is a concern): keep the format but strip leading whitespace (see compression section §4.3) to reduce the content portion.

#### Edge Cases
- **Tabs in string literals**: A source line like `string s = "col1\tcol2";` contains a literal tab in the string value. In the output `"21\tstring s = \"col1\\tcol2\";"` the first tab is the delimiter; the `\t` inside the string value is the JSON escape for the embedded tab. Parsers must split on the **first tab character** to extract the line number. This is unambiguous because line numbers contain only digits.
- **Line numbers > 999**: A 1000-line file would have `"1000\tcontent"`. Still unambiguous — split on first `\t` regardless of number width.

#### Dangers
- **Breaking existing callers**: Any tool or integration that parses the `[N, "content"]` nested array format will break. Requires a version bump or a `format` parameter (`"columnar"` for old, `"prefixed"` for new). **Recommended**: make `"prefixed"` the default for new installations, keep `"columnar"` as an opt-in legacy mode.

---

### 3.5 FIX-005 — Standardized Error Response Behaviour

#### Rationale
Currently each tool handles errors inconsistently — some return bare strings, some throw tool-level exceptions, and some return `{}`. This forces callers to implement separate error-handling logic per tool. Worse, an LLM caller receiving `{}` cannot distinguish "empty result" from "something went wrong" without out-of-band knowledge. Standardizing error shape across all tools makes the entire suite easier to call correctly.

#### Requirement
All FReader tools must conform to the following error response contract:

**List and search tools** (`list_functions`, `search_function`, `read_functions`) — on any condition that produces zero results (file not found, no matching methods, empty directory), return an **empty success**, not an error:
```json
{"_r": [], "_total": 0}
```
These tools must never throw or return an `error` field for the zero-results case. The absence of results is valid information.

**Lookup and extract tools** (`read`, `read_function`, `grep_in_file`, `summarize`, `info`) — on a condition that means the specific requested thing does not exist or cannot be processed, return a **structured tool error**:
```json
{"error": "human-readable message", "error_code": "MACHINE_READABLE_CODE"}
```
Optional additional fields may be present (e.g., `_available_overloads` as specified in FTR-002).

#### Defined error codes

| Code | Meaning | Applicable tools |
|------|---------|-----------------|
| `NOT_FOUND` | The named method, file, or symbol does not exist at the specified path | `read`, `read_function`, `extract_function`, `info` |
| `AMBIGUOUS` | The name matches multiple items and no disambiguation parameter was supplied; the tool cannot choose | `read_function` (overloads without `parameterTypes`) |
| `INVALID_RANGE` | `lineStart > lineEnd`, or `lineStart` exceeds EOF | `read` |
| `BINARY_FILE` | The file exists but is not readable as UTF-8 text | `read`, `read_function`, `grep_in_file`, `info` |
| `TIMEOUT` | A regex or Roslyn parse exceeded the configured timeout | `grep_in_file`, `search_function` |
| `UNSUPPORTED_LANGUAGE` | The file extension is not supported for AST-based operations | `list_functions`, `extract_function`, `read_function` (note: `summarize` returns a graceful fallback object, not this error code — see FTR-001) |

#### Edge Cases
- **Error code vs graceful fallback**: `summarize` on a `.ts` file returns a graceful fallback (not `UNSUPPORTED_LANGUAGE`) because it is a common entry point and the fallback is more useful than an error. All AST-specific tools (`list_functions`, `extract_function`, `read_function`) return `UNSUPPORTED_LANGUAGE` because they have no meaningful fallback.
- **Nested errors (e.g., file not found during `search_function`)**: If one file in a batch is unreadable, continue processing the rest. Append an error entry to `_r` for the failed file: `{"path": "x.cs", "error": "...", "error_code": "BINARY_FILE"}`. Do not abort the entire search.
- **HTTP vs MCP transport**: Error codes are part of the JSON response body, not HTTP status codes. The MCP transport layer should return HTTP 200 with the error object in the body — this ensures the LLM sees the structured error rather than a transport-layer failure message.

#### Dangers
- **Changing existing error shapes breaks existing integrations**: This is a breaking change for callers that parse error strings. Require a version bump (`FReader/2.0`) in the MCP tool manifest when this change ships.

---

## 4. New Features

### 4.1 FTR-001 — `summarize(path)` Tool

#### Rationale
The most common first step when encountering a file is "what is in this file?" Currently answering that question requires `info` (metadata) + `list_functions` (methods) = 2 calls, and even then gives no information about class hierarchy, attributes, or properties. `summarize` provides a single-call structural overview of the entire file without returning any method bodies.

#### Requirement
Add a `summarize(path)` tool that returns a compact structural overview containing:
- File header: file name, line count, namespace, file-scoped attributes, using aliases
- Type-level: class/struct/interface/record name, base types, implemented interfaces, class-level attributes, partial marker
- Member-level: all method signatures with `lineStart`/`lineEnd` (``@L-`` notation), all non-auto properties (name + type only, no body), constructor signatures
- Counts: total lines, total methods, total properties (implicit from the content)

Output format: `_text` — a compact plain-text block (not JSON columnar). The format uses a compact text representation for maximum token savings, achieving **87–96% savings** over reading the full file. Lines use the format:
```
// File: filename.cs (N lines)
// Namespace: Ns.Name
class ClassName : BaseType
  ctor(params) @L-C
  ReturnType MethodName(params) @L-C
```

Non-`.cs` files: when `path` points to a file whose language cannot be parsed by the C# AST (e.g., `.ts`, `.py`, `.json`), return a tool error with `UNSUPPORTED_LANGUAGE` error code. The caller must pre-check file extensions before calling `summarize` on non-C# files.

#### Example Output
```json
{
  "_text": "// File: SmsQueueMetricsController.cs (48 lines)\n// Namespace: jattac.app.sms.gateway.SmsSending.Observability\n[Route(\"api/v1/[controller]\")][ApiController][ExcludeFromCodeCoverage]\nclass SmsQueueMetricsController : JattacController\n  ctor(ICallProxy, SmsQueueMetricsRepository) @15-19\n  [HttpGet(\"latest\")] Task<WrappedResponse<Snapshot?>> GetLatestAsync() @21-28\n  [HttpGet(\"history\")] Task<WrappedResponse<IList<Snapshot>>> GetHistoryAsync(int limit) @30-38\n  [HttpGet(\"providers\")] Task<WrappedResponse<IList<ProviderStat>>> GetProviderStatsAsync() @39-47"
}
```
Estimated size: ~480 chars. The `_text` format saves ~23% over the columnar JSON approach (~620 chars) while remaining human-readable and LLM-parseable.

#### Edge Cases
- **Multiple classes in one file**: Return rows for all classes in line-number order; the `class` row for each type acts as a header for its members.
- **Nested classes**: Include the nested class as its own `"class"` row in line-number position. The caller can infer nesting from `lineStart`/`lineEnd` containment.
- **Generic classes**: `class TempoQueue<TWork>` — include the type parameter in the `sig` column.
- **Partial classes**: Append `" (partial)"` to the class name in the `name` column. Do not attempt to merge members from other files.
- **Extension methods**: Append `" (extension)"` to the method `name` column value.
- **Enums**: Emit a single `"enum"` row with `sig` = `"[N values]"`. Do not list individual enum members — they are not navigable targets for `read_function`.
- **Non-`.cs` files**: Return a tool-level error with `error_code: UNSUPPORTED_LANGUAGE`. The caller must check the file extension before calling `summarize`.

#### Dangers
- **`summarize` + `read` can cost MORE than just `read(whole file)`**: If you call `summarize` and then decide to read all or most of the methods anyway, you have paid for both calls. `summarize` only saves tokens when you read a **minority** of the methods in the file (roughly: fewer than half). Document this clearly with a guideline: "Use `summarize` when the file has ≥ 5 methods and you expect to read ≤ 2 of them."
- **Summary accuracy for complex types**: Generating a compact `→ WrappedResponse<Snapshot?>` from a full return type requires abbreviation logic. If the abbreviation is wrong (e.g., drops a nullability annotation `?`), downstream code generation could be incorrect. The tool must abbreviate conservatively: prefer longer accurate output over shorter inaccurate output.
- **Attributes with constructor arguments**: `[Route("api/v1/[controller]")]` — include the argument. `[Authorize(Roles = "Admin")]` — include the argument. Do not strip attribute arguments.

---

### 4.2 FTR-002 — `read_function(path, name)` Composite Tool

#### Rationale
The most common FReader workflow is "read this specific function." Currently this takes 3 calls: `list_functions` → `extract_function` → `read(range)`. With FIX-002 it drops to 2 (`list_functions` → `read(range)`). `read_function` makes it 1 call when you already know the function name.

#### Requirement
Add `read_function(path, name)` that returns the complete body of the named function, equivalent to `extract_function(path, name)` + `read(path, lineStart, lineEnd)` combined. Response includes the content (in the same format as `read`) plus metadata: `_name`, `_line_start`, `_line_end`, `_sig`.

#### Parameters
- `path` (required): file path
- `name` (required): function name, case-insensitive
- `parameterTypes` (optional): array of parameter type strings for overload disambiguation (e.g., `["int", "string"]`). Type matching is by simple name only — `int` matches `Int32`, `string` matches `String`. When supplied, return only the overload whose parameter list matches. If no overload matches, return a structured error listing all available overloads (see edge case below).
- `className` (optional): class name to disambiguate when the same function name exists in multiple classes within the file. Case-insensitive.
- `includeDocs` (optional, default `false`): if `true`, include XML doc comment lines above the function

#### Edge Cases
- **Overloaded methods — no `parameterTypes`**: When `name` matches multiple overloads and `parameterTypes` is not supplied, return **all overloads** as a `_match` array in the response. Each entry is a full function response with `_name`, `_sig`, `_line_start`, `_line_end`, and `_r`. Do not silently return only the first match.
- **Overloaded methods — `parameterTypes` supplied, no match**: Return a structured error listing all available overloads: `{"error":"No overload of 'GetAsync' matches parameterTypes [\"int\"]","error_code":"NOT_FOUND","_available_overloads":[{"sig":"GetAsync()","lineStart":10},{"sig":"GetAsync(int id, string filter)","lineStart":18}]}`. This lets the caller immediately retry with a corrected `parameterTypes` without an additional `list_functions` call.
- **Constructors**: `name = "ctor"` or `name = ".ctor"` or `name = ClassName` should all match the constructor.
- **Operators**: `name = "operator+"` — support operator method lookup.
- **Lambda/anonymous functions**: If the target is a lambda passed as an argument (e.g., `.OnProcessed(async (_, work) => { ... })`), it is not a named method and cannot be found by name. Return a clear error: `{"error": "No named method 'OnProcessed' found. This may be an anonymous function or lambda — use read(path, lineStart, lineEnd) with a known range instead."}`.
- **Interface method implementations**: `void IDisposable.Dispose()` — match on `"Dispose"` even though the declaration includes the interface prefix.

#### Dangers
- **Name collision across nested classes**: If a file has two classes, each with a `Validate()` method, `read_function(path, "Validate")` is ambiguous. Return both with clear class-name headers, or add an optional `className` parameter to disambiguate.
- **Partial methods**: A `partial void OnModelCreated()` may have its declaration in one file and its implementation in another. Return what exists in the requested file and note if it is declaration-only or implementation-only.

#### Example
```
read_function("SmsQueueMetricsController.cs", "GetLatestAsync")

→ {
    "_name": "GetLatestAsync",
    "_sig": "Task<WrappedResponse<SmsQueueObservabilitySnapshot?>> GetLatestAsync()",
    "_line_start": 21,
    "_line_end": 28,
    "_r": [
      "21:         [HttpGet(\"latest\")]",
      "22:         public async Task<WrappedResponse<SmsQueueObservabilitySnapshot?>> GetLatestAsync()",
      "23:         {",
      "24:             using (CallProxy)",
      "25:             {",
      "26:                 return await CallProxy.CallAsync(async () => await _metricsRepository.GetLatestAsync());",
      "27:             }",
      "28:         }"
    ]
  }
```
Cost: 1 call, ~420 chars vs 3 calls + ~980 chars (current FReader) or 2 calls + 647 chars (standard tools).

---

### 4.3 FTR-003 — `read_functions(path, names[])` Batch Tool

#### Rationale
When two or more related methods must be read together (e.g., a method and its helper, or two overloads), the caller currently issues multiple sequential `read_function` calls. A batch tool reads all requested functions in one round trip.

#### Requirement
Add `read_functions(path, names)` where `names` is an array of function name strings. Returns an array of function results in the same format as `read_function`, ordered by line number (ascending) regardless of the order in `names`.

#### Parameters
- `path` (required): file path
- `names` (required): array of 2–10 function names
- `maxChars` (optional, default 8000): hard cap on total response characters. If the combined result would exceed this, return an error listing which functions were omitted.

#### Edge Cases
- **Duplicate names in the array**: Deduplicate silently.
- **Name not found**: Include a `{"_name": "X", "error": "not found"}` entry in the results for the missing function rather than failing the entire call.
- **Requested functions are adjacent**: If functions at lines 10–20 and 21–35 are both requested, the combined output reads lines 10–35 contiguously. The tool should detect this and return a single contiguous block with a separator comment, rather than two separate blocks with duplicate surrounding whitespace.
- **Array of 1**: Return an error with `error_code: INVALID_REQUEST` suggesting the caller use `read_function` instead. Do not auto-redirect — the caller should explicitly choose which tool to use.

#### Dangers
- **Large batch requests defeat the purpose**: Requesting 8 functions from a 100-line file likely returns the whole file minus 10 lines — at which point `read(path, 1, 100)` is cheaper (1 call vs 1 call, but with less JSON overhead). Add a warning in the response metadata when the combined line range of requested functions covers > 70% of the file: `"_hint": "You are reading 72% of the file. Consider read(path) instead."`.
- **`maxChars` silently omitting functions**: If the cap is hit, the omission must be explicit and loud in the response, not a silent truncation. The caller must know they did not get everything they asked for.

---

### 4.4 FTR-004 — `search_function(rootPath, name)` Cross-File Tool

#### Rationale
A common workflow is "find where method X is defined" when the file is unknown. Currently this requires Glob (find candidate files) → Grep (search for the method name) → Read (confirm and extract). That is 3 calls across potentially many files. `search_function` replaces this with 1 call.

#### Requirement
Add `search_function(rootPath, name)` that searches all source files under `rootPath` for a method matching `name` and returns an array of matches: `{path, className, sig, lineStart, lineEnd}`. Search is case-insensitive on the method name.

**Implementation strategy — regex-first, Roslyn-second**: Loading and parsing every `.cs` file through the Roslyn C# AST on a large codebase is slow (hundreds of milliseconds per file). The required strategy is:

1. **Regex pre-scan**: Run a fast case-insensitive text scan across all files looking for lines matching the pattern `\b<name>\s*\(` (identifier boundary + name + open paren). Collect only files that contain at least one match. This runs in milliseconds even on 10,000 files.
2. **Roslyn parse (candidate files only)**: Parse only the files identified in step 1 through the full Roslyn AST to extract precise method boundaries, class names, and signatures. This ensures accuracy while keeping Roslyn cost proportional to match density, not codebase size.

This strategy means `search_function` on a 1,000-file project where only 3 files contain the target name costs ~3 full Roslyn parses, not ~1,000.

#### Parameters
- `rootPath` (required): directory to search (recursive)
- `name` (required): method name
- `fileExtensions` (optional, default `[".cs"]`): file types to search
- `limit` (optional, default 20): maximum number of matches to return

#### Edge Cases
- **Very common names** (`Get`, `Save`, `Validate`, `ToString`): Could return hundreds of matches across a large codebase. The `limit` parameter prevents flooding, but the tool should also return `_total_matches` so the caller knows whether results were truncated. Callers should use more specific names when possible.
- **No matches**: Return `{"_r":[],"_total_matches":0}` — not an error.
- **rootPath is a file, not a directory**: Search that single file only.
- **Symlinked directories**: Follow symlinks but track visited real paths to avoid infinite loops.
- **Case-sensitive filesystems**: On Linux, `getlatestasync` and `GetLatestAsync` are distinct. The search should remain case-insensitive on the method **name** regardless of filesystem sensitivity.

#### Dangers
- **Slow on large codebases**: Searching a monorepo with 10,000 C# files could take several seconds. Add a `_search_time_ms` field to the response. If the search exceeds a configurable timeout (default 10s), return partial results with `_timed_out: true`.
- **Matches in generated code**: Auto-generated files (e.g., `*.Designer.cs`, `*.g.cs`, `obj/` folders) often contain common method names. These matches are almost never what the caller wants. Exclude `obj/`, `bin/`, `*.g.cs`, `*.Designer.cs` by default; add an `includeGenerated` parameter (default `false`) to override.
- **Matches in test files**: Test files often shadow production method names (e.g., a test helper named `GetLatestAsync`). Consider adding a `include_tests` parameter (default `true`, unlike generated files) so callers can filter these out when needed.

---

### 4.5 FTR-005 — `grep_in_file(path, pattern, context)` Tool


#### Rationale
When the target file is already known but the exact location of a symbol, string, or pattern within it is not, the current workflow is Grep (with file filter) → Read. `grep_in_file` collapses this to 1 call scoped to a single file, with built-in context lines.

#### Requirement
Add `grep_in_file(path, pattern, context)` that searches a single file for lines matching `pattern` (regex) and returns each match with `context` lines of surrounding code.

#### Parameters
- `path` (required): file path
- `pattern` (required): regex pattern
- `context` (optional, default 2): lines of context above and below each match
- `caseSensitive` (optional, default `false`): whether the regex is case-sensitive
- `maxMatches` (optional, default 20): cap on number of matches returned

#### Edge Cases
- **Overlapping context windows**: If two matches are 3 lines apart and `context=5`, their context windows overlap. Merge them into a single contiguous block with a separator line `// --- (merged N matches) ---`.
- **Match on first or last line**: Context cannot extend before line 1 or after the last line. Clamp silently.
- **Regex with catastrophic backtracking**: Pathological patterns like `(a+)+b` can hang. Apply a regex timeout (500ms) and return `{"error":"Pattern timed out. Simplify the regex."}` if exceeded.
- **Empty pattern**: Return all lines (equivalent to `read(path)`). Emit a `_hint: "Empty pattern matches all lines. Use read() instead."`.

#### Dangers
- **Overly broad patterns return the whole file**: `pattern: "."` matches every line. Combined with `context=2`, the merged windows cover the entire file — but with significant overhead. The tool should detect when matches + context cover > 80% of the file and emit a warning suggesting `read(path)` instead.
- **Regex injection**: The `pattern` parameter is user-supplied regex. Ensure it is evaluated in a sandboxed context with a timeout. Do not allow patterns that access the filesystem or execute code (relevant for some regex engines with extensions).

---

### 4.6 FTR-006 — Adaptive Feature Benchmarking (`record_benchmark` Tool)

#### Rationale
Different projects have different code patterns. A feature that is highly effective on one codebase (e.g., `summarize` on a C# project) may be useless on another (e.g., `summarize` on a JavaScript project). Rather than hardcoding feature availability per language or project type, FReader collects per-project benchmark scores adaptively. Features that consistently score low on a given project are automatically deprecated for that project, guiding the LLM caller to use alternative tools.

#### Requirement
Add a `record_benchmark(featureName, score, path)` tool and a `FeatureScoreManager` that persists per-project scores in a `.freader-benchmarks.json` file at the project root.

##### Project auto-detection
The project root is auto-detected by walking up from `path` until one of these markers is found:
- A `.git` directory
- A `*.sln` file
- A `*.csproj` file

If none is found, the tool returns an error. The benchmark file is stored as `.freader-benchmarks.json` at the project root.

##### Session rules
- **Data-gathering exemption**: While a feature has fewer than 8 recorded scores, there is no rate limit. Unlimited calls allowed per day/week.
- **Per-calendar-day**: Once a feature has 8+ scores, at most one benchmark session per calendar day per feature.
- **Max 3/week**: Once a feature has 8+ scores, at most 3 sessions in any rolling 7-day window per feature.
- **Error on violation**: If the caller exceeds these limits, `record_benchmark` returns a descriptive error with the next allowed date.

##### Recording scores
Each call to `record_benchmark` records a single feature score for the project:
- `score`: a floating-point value between 0.0 (completely useless) and 1.0 (perfect).
- `featureName`: a string identifying the feature (e.g., `"read_function"`, `"search_function"`, `"grep_in_file"`).

##### Automatic deprecation
If a feature accumulates 8 or more scores with an average < 0.25, it is marked as `deprecated`. The response from `record_benchmark` includes a clear warning. The `get_instructions` response includes the current benchmark state for each project in the `_benchmark` field.

##### Integration with `get_instructions`
The `get_instructions` response always includes a `_benchmark` field (empty dict if no data). It also includes harness instructions prompting the caller to benchmark tools on use and to prefer tools with avg >= 0.25:
```json
{
  "_h": ["=== HARNESS ===", "Use FReader for ALL file reads...", "...", "TOOL SELECTION:", "  runs < 8  → data-gathering, use freely", "  avg >= 0.25  → reliable", "  avg < 0.25 + 8+ runs  → DEPRECATED", "..."],
  "_benchmark": {
    "_project_root": "/path/to/project",
    "_benchmarks": {
      "read_function": { "avg_score": 0.92, "runs": 12, "deprecated": false },
      "search_function": { "avg_score": 0.15, "runs": 8, "deprecated": true }
    }
  }
}
```

#### Parameters
- `featureName` (required): string identifier for the feature
- `score` (required): 0.0–1.0 floating point
- `path` (required): any file path in the project (for project root detection)

#### Edge Cases
- **No project root found**: Return `error_code: INVALID_REQUEST` with a descriptive message.
- **Score out of range**: Return an error message; do not record.
- **Already benchmarked today (8+ runs only)**: Return a message with the next allowed date; do not record. Features with <8 runs are unlimited.
- **Duplicate recording of same feature in same session**: Appends another score. The averaging handles this naturally — duplicate scores pull the average toward the duplicated value.
- **Corrupt `.freader-benchmarks.json`**: Gracefully reset to an empty store. Do not crash.

#### Dangers
- **Caller could game the system**: A caller could repeatedly record score=1.0 for a bad feature to prevent deprecation. This is an intentional design trade-off — the caller is the LLM, which is optimizing for token efficiency. Gaming the system would only hurt the caller's own efficiency.
- **Benchmark file committed to version control**: Callers should add `.freader-benchmarks.json` to `.gitignore`. The tool does not enforce this.
- **Feature name collisions**: Since feature names are arbitrary strings, two different features could accidentally use the same name. Document the convention: use the exact MCP tool name (e.g., `"read_function"`, `"search_function"`, `"grep_in_file"`, `"summarize"`, `"read"`).

---

## 5. Compression Strategies

#### Rationale
When the target file is already known but the exact location of a symbol, string, or pattern within it is not, the current workflow is Grep (with file filter) → Read. `grep_in_file` collapses this to 1 call scoped to a single file, with built-in context lines.

#### Requirement
Add `grep_in_file(path, pattern, context)` that searches a single file for lines matching `pattern` (regex) and returns each match with `context` lines of surrounding code.

#### Parameters
- `path` (required): file path
- `pattern` (required): regex pattern
- `context` (optional, default 2): lines of context above and below each match
- `caseSensitive` (optional, default `false`): whether the regex is case-sensitive
- `maxMatches` (optional, default 20): cap on number of matches returned

#### Edge Cases
- **Overlapping context windows**: If two matches are 3 lines apart and `context=5`, their context windows overlap. Merge them into a single contiguous block with a separator line `// --- (merged N matches) ---`.
- **Match on first or last line**: Context cannot extend before line 1 or after the last line. Clamp silently.
- **Regex with catastrophic backtracking**: Pathological patterns like `(a+)+b` can hang. Apply a regex timeout (500ms) and return `{"error":"Pattern timed out. Simplify the regex."}` if exceeded.
- **Empty pattern**: Return all lines (equivalent to `read(path)`). Emit a `_hint: "Empty pattern matches all lines. Use read() instead."`.

#### Dangers
- **Overly broad patterns return the whole file**: `pattern: "."` matches every line. Combined with `context=2`, the merged windows cover the entire file — but with significant overhead. The tool should detect when matches + context cover > 80% of the file and emit a warning suggesting `read(path)` instead.
- **Regex injection**: The `pattern` parameter is user-supplied regex. Ensure it is evaluated in a sandboxed context with a timeout. Do not allow patterns that access the filesystem or execute code (relevant for some regex engines with extensions).

---

## 5. Compression Strategies

### 5.1 CMP-001 — Type Name Aliasing

#### Rationale
Long type names in C# (especially generics) are a major source of token waste. A type like `SmsQueueObservabilitySnapshot` is 34 chars / ~9 tokens. When it appears 8 times across a file's signatures and bodies, that is 272 chars / ~72 tokens spent on a single type name. Replacing repeated long names with short aliases at the point of reading could save 30–60% of token cost on heavily generic files.

#### Requirement
Add an `aliases: boolean` parameter (default `false`) to `read`, `read_function`, `read_functions`, and `summarize`. When `true`, the tool:
1. Scans the content to be returned for type names appearing ≥ 3 times
2. Assigns each a short alias (2–4 character abbreviation, not single letters — see Dangers)
3. Prepends a legend block to the response
4. Replaces all occurrences in the returned content

#### Alias generation rules
- Use the uppercase initials of the type name: `SmsQueueObservabilitySnapshot` → `SQOS`
- If collision: append a digit: `SQOS`, `SQOS2`
- Never alias: primitive types (`int`, `string`, `bool`, `Guid`, `DateTime`), single-word types (`Task`, `List`), types appearing < 3 times in the returned content

#### Legend format
The alias map is returned as two sibling fields alongside `_r` in the response object — **not** as an inline comment mixed into the code content. Embedding a `// ALIASES ...` comment into `_r` would corrupt code content (a caller copying a line out of `_r` to use as a code reference would get a comment prepended to their code). Instead:

```json
{
  "_alias_warning": "Aliases active — use full type names when writing code, not aliases",
  "_aliases": {
    "SQOS": "SmsQueueObservabilitySnapshot",
    "SQPS": "SmsQueueProviderStat",
    "WR":   "WrappedResponse",
    "IL":   "ImmutableList"
  },
  "_r": [...]
}
```

`_alias_warning` is a fixed string present whenever `aliases: true` and at least one alias was applied. Its presence signals to the caller that code in `_r` has been transformed. `_aliases` is the full dictionary mapping alias → original name.

#### Example
```
Without aliases: "Task<WrappedResponse<ImmutableList<SmsQueueObservabilitySnapshot>>> GetHistoryAsync(int limit)"
With aliases:    "Task<WR<IL<SQOS>>> GetHistoryAsync(int limit)"
Saving: 56 chars → 26 chars on this one line
```

#### Edge Cases
- **Aliases clashing with variable names in the code**: If the code contains a variable named `SQOS`, the alias would create ambiguity. The tool must check the returned content for existing uses of the candidate alias before assigning it, and choose a different alias if there is a collision.
- **Aliases spanning multiple calls**: If `read_function` is called twice in the same session with `aliases: true`, the second call may generate different aliases for the same types. The caller (LLM) must refer to each response's `_aliases` dictionary independently. There is no session-shared alias dictionary. This is a known limitation — session state is out of scope.
- **Non-English type names**: Type names using non-ASCII characters (uncommon but possible) — generate aliases from the ASCII-representable portions only.
- **No aliases applied** (all types appear < 3 times): Omit both `_alias_warning` and `_aliases` from the response entirely. Do not emit empty fields.

#### Dangers
- **Aliases INCREASE total tokens when the legend cost exceeds the savings**: The `_aliases` object itself costs tokens. If the content to be returned only contains a given type 3 times (the minimum threshold), the savings from aliasing 3 occurrences may be less than the dictionary entry cost. Break-even analysis:
  - Dictionary entry cost: ~`"SQOS":"SmsQueueObservabilitySnapshot"` = 38 chars per alias
  - Savings per occurrence: `SmsQueueObservabilitySnapshot` (30 chars) → `SQOS` (4 chars) = 26 chars saved
  - Break-even: ~1.5 occurrences past threshold — so the 3-occurrence threshold is safe
  - **But**: if 5 types each appear exactly 3 times, the dictionary costs 5 × 38 = 190 chars and saves 5 × 3 × 26 = 390 chars. Net save: 200 chars. OK.
  - **Actual danger**: `aliases: true` on a small function (8 lines) that mentions one long type 3 times. Dict entry = 38 chars, savings = 3 × 26 = 78 chars. Net save: 40 chars. Marginal but positive.
  - Conclusion: the 3-occurrence threshold makes aliasing safe in all tested scenarios. Document the threshold clearly.
- **LLM misreads aliases when generating code**: If the LLM sees `SQOS` and generates code using `SQOS` instead of the full type name, the generated code will not compile. The `_alias_warning` field exists precisely to flag this risk at the response level. Its value must never be omitted or shortened.
- **Short aliases are ambiguous**: Single-letter aliases (`S`, `T`, `R`) are almost always already used as generic type parameters in C# code (`Task<T>`, `IEnumerable<T>`). Using `T` as an alias for a concrete type would be extremely confusing. **Never generate single-character aliases.** Minimum alias length: 2 characters.

---

### 5.2 CMP-002 — Using/Import Stripping

#### Rationale
Every C# file begins with `using` directives — typically 5–15 lines. These are almost never needed when reading a file for logic comprehension: the type names in the code body are already fully apparent without knowing their namespaces. Stripping these lines on read reduces response size without information loss in the vast majority of cases.

#### Requirement
Add a `stripImports: boolean` parameter (default `false`) to `read`, `read_function`, and `read_functions`. When `true`:
- Omit all `using Namespace.Type;` lines from the response
- Keep `using TypeAlias = Namespace.Type;` lines (these define aliases used in the code body)
- Keep `global using` directives (C# 10+) only if they define aliases
- Do **not** insert a placeholder line — the using lines are silently removed
- All reported line numbers reflect the actual file line numbers (not the stripped output line numbers). The `N\t` prefix in the tab-prefix format retains the original line number.

#### Edge Cases
- **`using static`**: `using static System.Math;` imports static members. Stripping this hides that `Sqrt(x)` in the body is actually `Math.Sqrt(x)`. **Strip by default.** No placeholder line is inserted — the `using` lines are silently removed. The N\t prefix preserves original line numbers.
- **`using` inside method bodies**: C# allows `using (var conn = new SqlConnection())` inside methods — this is a resource management statement, not an import. **Never strip these.**
- **Line number accuracy**: If 8 `using` lines are stripped and the caller later calls `read(path, 15, 20)`, those are actual file line numbers. The tool must NOT renumber — `lineStart`/`lineEnd` always refer to real file line positions.

#### Dangers
- **Stripping `using` aliases breaks readability for unfamiliar codebases**: If a file uses `using Snapshot = jattac.app.sms.gateway.SmsSending.Observability.SmsQueueObservabilitySnapshot;`, stripping this makes `Snapshot` in the code body look like an unknown type. **Always preserve aliased `using` directives.** The requirement above already mandates this, but it must be tested explicitly.
- **LLM assumes wrong namespace**: If the LLM sees `MySqlConnection` in the body with imports stripped, it may guess the wrong namespace when generating import statements for new files. This is an acceptable trade-off when the goal is reading, not writing. Document that `stripImports` is for reading comprehension only.

---

### 5.3 CMP-003 — Indentation Normalisation

#### Rationale
C# files commonly have 3 levels of nesting before reaching a method body: namespace → class → method. Method body code is indented 12 spaces (3 × 4-space indent). A 60-char line of logic padded to 72 chars wastes 12 chars per line. On a 30-line method body, this is 360 chars of leading whitespace — pure structural noise.

#### Requirement
Add an `normalizeIndent: boolean` parameter (default `false`) to `read`, `read_function`, and `read_functions`. When `true`, strip the common leading whitespace from all returned lines (re-base indentation to 0 for the leftmost non-blank line in the returned range). Preserve relative indentation.

#### Example
```
Before (read lines 21-28 of a controller):
"        [HttpGet(\"latest\")]"
"        public async Task<...> GetLatestAsync()"
"        {"
"            using (CallProxy)"

After (normalizeIndent: true):
"[HttpGet(\"latest\")]"
"public async Task<...> GetLatestAsync()"
"{"
"    using (CallProxy)"
```
Saving: 8 chars × 8 lines = 64 chars on this small example. Scales with nesting depth.

#### Edge Cases
- **Mixed indentation (tabs and spaces)**: Normalise tabs to 4 spaces before computing the common prefix. Do not mix tabs and spaces in output — use spaces throughout.
- **Blank lines**: Blank lines have zero indentation. Do not use blank lines to compute the common prefix — skip them.
- **Lines with less indentation than the computed minimum**: This can happen with `#region` tags, attribute blocks, or misformatted code. Clamp to 0 — do not produce negative indentation (i.e., do not shift lines further left than their content starts).
- **Single-line methods**: No normalisation needed but apply it anyway for consistency.

#### Dangers
- **Relative indentation conveys structure**: Nested lambdas, LINQ chains, and complex `switch` expressions rely on relative indentation to show grouping. Normalisation preserves relative indentation (only the common base is stripped), so this is safe. However, if the returned range starts mid-method (e.g., just the inner body of a nested lambda), the common prefix computation may strip too much and make the returned code look like it is at the top level. Callers should be aware that `normalizeIndent: true` can make partial reads misleading if the range does not start at a method boundary.
- **Copy-pasting normalised code directly**: If the LLM uses normalised code as the basis for new code, it will be incorrectly indented (zero-based). Document that normalised output is for reading only. This is analogous to the alias danger — output optimised for reading is not production-ready code.

---

## 6. Non-Requirements (Explicitly Out of Scope)

The following were considered and rejected:

| Idea | Reason for exclusion |
|------|---------------------|
| **Binary compression (gzip/zstd)** | LLMs read plaintext. Binary-encoded output is unreadable and would cost more tokens to base64-encode than it saves. |
| **Session/delta state** ("return only what changed since last read") | MCP tools are stateless. Implementing session state requires server-side storage and adds correctness risk if the file changes between reads. Out of scope until MCP introduces a session concept. |
| **LLM-readable diff format** | Useful but requires session state. Same exclusion as above. |
| **Single-character aliases** | Too ambiguous with C# generic type parameters (`T`, `K`, `V`). Prohibited in CMP-001. |
| **Semantic summarisation** ("describe what this method does") | Requires LLM inference, not text processing. This is the caller's job, not the tool's. |
| **Auto-applying all compression simultaneously** | The interaction between aliasing, import stripping, and indent normalisation on the same output creates complex edge cases (e.g., what line number does the alias legend appear on?). Each must be opt-in independently. |

---

## 7. Acceptance Criteria

Each requirement is accepted when:

| ID | Criterion |
|----|-----------|
| FIX-001 | `list_functions` on a file with 10 generic signatures contains no `<` or `>` sequences |
| FIX-002 (lineEnd) | `list_functions` response includes `lineEnd` column; value matches the actual closing brace line for 5 tested methods including one with nested lambdas |
| FIX-002 (includeSystem) | `list_functions` with `includeSystem: false` on a class that inherits `object` excludes `ToString`, `GetHashCode`, `Equals`; a developer-written `Dispose()` on a normal class is **not** excluded |
| FIX-003 (bare read) | `read(path)` with no range on a 79-line file returns all 79 lines with `_truncated: false` |
| FIX-003 (partial range) | `read(path, lineStart: 10)` returns lines 10–EOF; `read(path, lineEnd: 20)` returns lines 1–20; `read(path, lineStart: 50, lineEnd: 10)` returns `error_code: "INVALID_RANGE"`; `read(path, lineEnd: 999)` on a 79-line file clamps to line 79 with no error |
| FIX-004 | `read` output uses `N\tcontent` format; response for `read(path, 1, 79)` on the benchmark file is ≤ 4,200 chars; tab-delimited lines containing colons parse correctly |
| FIX-005 | Every defined `error_code` is returned by the appropriate tool under its triggering condition; list tools return `{"_r":[],"_total":0}` (not an error) for zero-result cases |
| FTR-001 (structure) | `summarize` on a 5-method controller returns `_text` with compact format showing namespace, class hierarchy, method sigs with line ranges; response contains no `<` or `>` sequences |
| FTR-001 (fallback) | `summarize` on a `.ts` file returns a tool error with `error_code: UNSUPPORTED_LANGUAGE` — not a graceful fallback |
| FTR-002 (basic) | `read_function(path, "GetLatestAsync")` returns the correct lines in 1 call |
| FTR-002 (overloads) | `read_function(path, "GetAsync")` when 2 overloads exist returns both as `_match` array; supplying `parameterTypes: ["int"]` returns only the matching overload; supplying `parameterTypes: ["bool"]` returns `error_code: "NOT_FOUND"` with `_available_overloads` |
| FTR-003 | `read_functions(path, ["A","B"])` where A and B are adjacent returns a merged contiguous block, not two separate blocks |
| FTR-004 | `search_function(root, "GetLatestAsync")` finds the method in < 5s on a 50-file project; excludes `obj/` and `bin/` directories; regex-first scan runs before any Roslyn parse |
| FTR-005 | `grep_in_file` with overlapping context windows merges them correctly; regex timeout fires within 600ms |
| CMP-001 (_aliases field) | `aliases: true` response contains `_alias_warning` and `_aliases` fields as sibling keys of `_r`; no inline `// ALIASES` comment appears anywhere in `_r`; no single-char aliases generated |
| CMP-001 (correctness) | Every occurrence of aliased types in `_r` is substituted; `_aliases` dictionary contains exactly the substituted types and no others |
| CMP-002 | `stripImports: true` preserves `using Alias = ...` directives; all `lineStart`/`lineEnd` values in the response reflect actual file line numbers |
| CMP-003 | `normalizeIndent: true` on lines 21-28 of a controller removes exactly 8 leading spaces from all non-blank lines; relative indentation is preserved |
| Test Suite | All tools have a test file; line coverage ≥ 80%; every `error_code` asserted; benchmark regression tests pass at targets in §8 |

---

## 8. Benchmark Targets (Post-Implementation)

All fixes (FIX-001–005), features (FTR-001–005), and compression strategies (CMP-001–003) are now implemented. The original benchmark must be re-run to verify targets.

| Task | Current FReader (w/ all features) | Target | Result |
|------|-----------------------------------|--------|--------|
| Read full file (64 lines) | `read(path)` — tab-prefix format | ≤ 3,800 chars, 1 call | **1,793 chars, 1 call** ✅ |
| Extract one named function | `read_function(path, "ProcessAsync")` | ≤ 1,200 chars, 1 call | **324 chars, 1 call** ✅ |
| Extract one named function (small) | `read_function(path, "Add")` with `parameterTypes` | ≤ 450 chars, 1 call | **141 chars, 1 call** ✅ |
| **Total** | **All three tasks** | **≤ 5,450 chars, 3 calls** | **2,258 chars, 3 calls** ✅ |

All targets met. FReader beats standard tools (6,340 chars, 5 calls) by **64% fewer chars and 40% fewer calls**.

---

## 9. Implementation Priority Order

| Priority | ID | Effort | Token Impact |
|----------|----|--------|-------------|
| 1 | FIX-001 (fix `<>` escaping) | Low | High — affects every C# response |
| 2 | FIX-002 (add `lineEnd` + `includeSystem` to `list_functions`) | Low | High — eliminates `extract_function` call; filters boilerplate |
| 3 | FIX-003 (fix bare `read()` truncation + partial-range semantics) | Low | Medium — eliminates mandatory `info` call; consistent range behaviour |
| 4 | FIX-005 (standardized error codes) | Low | None (correctness) — but unblocks reliable automated callers |
| 5 | FTR-002 (`read_function` composite + `parameterTypes`) | Medium | High — 3 calls → 1 call for most workflows |
| 6 | FTR-001 (`summarize` with columnar JSON + non-.cs fallback) | Medium | High — single-call file overview |
| 7 | FIX-004 (tab-prefix output format) | Medium | Medium — 10–20% reduction per read; eliminates delimiter ambiguity |
| 8 | CMP-002 (import stripping) | Low | Low-Medium — 5–15 lines per file |
| 9 | CMP-001 (type alias dictionary with `_aliases` field) | High | High — 30–60% on deeply generic files |
| 10 | CMP-003 (indentation normalisation) | Low | Low — 5–15% per method body |
| 11 | FTR-003 (`read_functions` batch) | Medium | Medium — eliminates N-1 calls for multi-function reads |
| 12 | FTR-004 (`search_function` with regex-first strategy) | High | Medium — eliminates 2-step cross-file lookup |
| 13 | FTR-005 (`grep_in_file`) | Medium | Low-Medium — saves 1 call for known-file searches |
| 14 | Test Suite (§10) | Medium | None (quality gate) — must ship alongside each tool, not after |
| 15 | FTR-006 (`record_benchmark` + `FeatureScoreManager`) | High | Medium — enables adaptive tool selection per-project; 6 revisions applied post-implementation |

Priorities 1–5 are the minimum viable improvement set. Completing them is sufficient to make FReader match standard tools on the benchmark. Everything beyond is optimisation.

**Note on FIX-005 placement**: Error standardization (priority 4) must ship before FTR-002 (priority 5) because `read_function` is the first tool that returns structured errors with `_available_overloads`. Implementing FTR-002 before FIX-005 would mean defining the error shape twice.

**Note on Test Suite placement**: Tests for each tool must be written **at the same time** as the tool, not after. The priority-14 placement reflects that the test suite as a whole is reviewed after all tools ship, but individual tool tests are not deferred.

---

## 10. Test Suite Requirements

A test suite is a first-class deliverable alongside each tool implementation. Without automated tests, every edge case documented in this BRS is a latent regression waiting to be introduced by the next serialiser change, Roslyn version bump, or compression flag interaction.

### 10.1 Structure

- Test files by concern: `LineEngineTests.cs` (read, grep_in_file, info), `ProviderTests.cs` (list_functions, summarize), `TypeAliaserTests.cs` (aliasing), `SearchEngineTests.cs` (search_function), `McpIntegrationTests.cs` (end-to-end MCP calls for all tools)
- Test fixtures: `TestFixtures/SampleClass.cs` — a synthetic C# file containing generic types, overloaded methods, XML doc comments, expression-bodied members, nested lambdas, auto-properties, and static methods.
- Total: ~65 test cases covering all tools, error codes, compression flags, and edge cases (v1.6.0 includes a new `ExtractFunction_MultipleOverloads_ReturnsAll` test).

### 10.2 Minimum test cases per tool

| Tool | Minimum test cases | Must include |
|------|-------------------|--------------|
| `read` | 12 | Full file, range, `lineStart` only, `lineEnd` only, `lineStart > lineEnd` (error), `lineStart` beyond EOF (error), `lineEnd` beyond EOF (clamp), empty file, binary file, `truncate` cap, `normalizeIndent`, `stripImports` |
| `list_functions` | 10 | Full list, `lineEnd` column, `includeSystem: false` filters `ToString`, `includeSystem: true` includes `ToString`, overloaded methods, generic methods, expression-bodied methods, partial class, auto-property excluded, empty file |
| `read_function` | 12 | Happy path, overloads without `parameterTypes` (returns all), `parameterTypes` match, `parameterTypes` no match (error with `_available_overloads`), constructor via `"ctor"`, constructor via class name, operator lookup, lambda/anon error, cross-class ambiguity, `includeDocs: true`, interface-prefixed method |
| `read_functions` | 8 | Two functions, adjacent-function merge, missing name entry (partial error), duplicate names deduplicated, array of 1, `maxChars` cap triggers, >70% file coverage hint, all names missing |
| `search_function` | 10 | Found in one file, found in multiple files, not found (empty success), `limit` truncation + `_total_matches`, `obj/` excluded, `*.g.cs` excluded, timeout triggers `_timed_out`, symlink loop prevention, `fileExtensions` filter, regex-first + Roslyn confirm (integration test) |
| `grep_in_file` | 8 | Single match, overlapping context merge, first-line match (clamp), last-line match (clamp), no matches (empty success), regex timeout, empty pattern hint, >80% file coverage warning |
| `summarize` | 8 | Single class, multiple classes, generic class, partial class, enum, non-`.cs` error (`UNSUPPORTED_LANGUAGE`), nested class, aliases support |
| `info` | 4 | Exists, not found, binary file, directory path (error) |

### 10.3 Coverage requirements

- **Line coverage**: ≥ 80% across all tool implementations
- **Error codes**: every `error_code` string defined in FIX-005 must appear in at least one test asserting the exact code value
- **`_hint` messages**: every `_hint` string emitted by any tool must be asserted in at least one test (prevents silent removal)
- **Compression flags**: every combination of compression flags that is documented as having an interaction effect must have an explicit test (e.g., `aliases: true` + `stripImports: true` on the same call)
- **Benchmark regression test**: the three benchmark tasks from §2 must be automated as integration tests asserting total response character count ≤ the targets in §8

### 10.4 Test data policy

- Test fixture `.cs` files must be **synthetic** (no production code copied into the test suite) to avoid accidental inclusion of secrets, credentials, or proprietary logic
- Fixture files must include at least: one generic method, one overloaded pair, one XML-doc'd method, one expression-bodied method, one method with nested lambdas, one auto-property
- All fixture files must be committed to source control — no runtime code generation of fixture files
