# BRS: FReader Improvements
**Date:** 2026-05-21  
**Source:** Benchmarking session against Jattac.Libs.Tempo (C# project)  
**Status:** Draft

---

## Issue 1 — `extract_function` silently returns only the first overload

### Observed behaviour
`extract_function` was called for `SafeCallbackAsync` in `TempoDispatcher.cs`. That file contains two overloads of the function:

- Overload A (lines 496–518): `Task SafeCallbackAsync(Func<Task> invoke, string name, Guid? tenantId)`
- Overload B (lines 521–544): `Task<T> SafeCallbackAsync(Func<Task<T>> invoke, string name, Guid? tenantId, T fallback)`

The tool returned only overload A with no indication that overload B exists. The caller had no signal to act on — the response looked like a complete answer.

### Why this is a problem
The caller's only recourse to discover the miss is independent prior knowledge that overloads exist (e.g., from `list_functions`). A caller who goes straight to `extract_function` will silently receive an incomplete picture. This is worse than an error: errors prompt corrective action; silent truncation does not.

### Required fix
When two or more functions share the requested name, the response must change shape to communicate the ambiguity. Two sub-cases:

**Sub-case A — no `parameterTypes` supplied (ambiguous request)**

Return all overloads in a list, not just the first. Shape:

```json
{
  "_name": "SafeCallbackAsync",
  "_overload_count": 2,
  "_overloads": [
    {
      "_line_start": 496,
      "_line_end": 518,
      "_sig": "Task SafeCallbackAsync(Func<Task> invoke, string name, Guid? tenantId)"
    },
    {
      "_line_start": 521,
      "_line_end": 544,
      "_sig": "Task<T> SafeCallbackAsync(Func<Task<T>> invoke, string name, Guid? tenantId, T fallback)"
    }
  ],
  "_hint": "Multiple overloads found. Use parameterTypes to select one, or use read_function which supports multi-overload responses."
}
```

The caller can then either:
- Call `extract_function` again with `parameterTypes` to pin one, or
- Call `read_function` (which already handles multi-overload correctly) instead.

**Sub-case B — `parameterTypes` supplied but no match found**

This is already handled well in `read_function` (returns `_available_overloads`). The same pattern must be applied to `extract_function`. If `parameterTypes` is supplied and no overload matches, return an error response that lists what was available:

```json
{
  "error": "No overload of 'SafeCallbackAsync' matches parameterTypes [Func<Task>, string]",
  "error_code": "NOT_FOUND",
  "_available_overloads": [
    {
      "_sig": "Task SafeCallbackAsync(Func<Task> invoke, string name, Guid? tenantId)",
      "_line_start": 496,
      "_line_end": 518
    },
    {
      "_sig": "Task<T> SafeCallbackAsync(Func<Task<T>> invoke, string name, Guid? tenantId, T fallback)",
      "_line_start": 521,
      "_line_end": 544
    }
  ]
}
```

### Acceptance criteria
1. Calling `extract_function` on a name with N > 1 overloads (no `parameterTypes`) returns `_overload_count` and `_overloads` array. It does **not** return a single `_line_start`/`_line_end` pair.
2. Calling `extract_function` with `parameterTypes` that matches exactly one overload returns the existing single-match shape unchanged.
3. Calling `extract_function` with `parameterTypes` that matches zero overloads returns an error with `_available_overloads`.
4. The `_hint` field in sub-case A is present and points the caller to `parameterTypes` or `read_function`.

---

## Issue 2 — `grep_in_file` corrupts non-ASCII characters in file content

### Observed behaviour
`grep_in_file` was called on `TempoScheduler.cs` with pattern `async Task`. One matched context window included the following comment line (line 131):

```
// ── Tick evaluation ────────────────────────────────────────────────────────
```

The `──` characters (U+2500 BOX DRAWINGS LIGHT HORIZONTAL) appeared in the FReader response as garbled/replacement characters. The native `Grep` tool returned the same line with the characters intact.

### Is this a bug?
Yes. The file is UTF-8 and the characters are valid Unicode. The tool is responsible for faithfully returning file content — a client reading FReader output must be able to trust that what it sees matches what is in the file. Silent corruption violates that contract. A downstream action taken on corrupted content (e.g., an edit that references surrounding text) will produce incorrect results.

### Root cause hypothesis (for the implementor to verify)
`grep_in_file` reads the file with a default or incorrectly specified encoding, and either:
- reads raw bytes then converts with a lossy ASCII/Latin-1 fallback, or
- the JSON serialisation path mangles multi-byte sequences by treating them as individual bytes.

The fix site is wherever the file bytes are first converted to a string in the `grep_in_file` code path. The read call must explicitly specify UTF-8:

```csharp
// Wrong — default encoding may not be UTF-8 in all environments
var lines = File.ReadAllLines(path);

// Correct — explicit UTF-8 with BOM detection
var lines = File.ReadAllLines(path, new UTF8Encoding(detectEncodingFromByteOrderMarks: true));
```

### Contrast with `read` tool
The `read` tool (which uses `File.ReadAllLines(path, Encoding.UTF8)`) returned the same line correctly in the same file. This confirms the bug is specific to the `grep_in_file` read path, not a system-wide issue.

### Acceptance criteria
1. `grep_in_file` on a UTF-8 file containing U+2500 (─), U+2014 (—), U+00E9 (é), U+03B1 (α), and U+1F600 (😀) returns each character intact and identical to what `read` returns for the same lines.
2. No replacement characters (U+FFFD) appear in output for any valid UTF-8 input.
3. If the file is not valid UTF-8 (e.g., Latin-1 encoded), the tool returns a graceful error (`READ_ERROR`) rather than silently corrupting output.

### Test case
Given a file `unicode_test.cs` containing exactly:

```csharp
// ── Section ─────────
// café résumé naïve
// α β γ δ
```

`grep_in_file(path, "Section")` with `context: 2` must return:

```json
{
  "_matches": 1,
  "_r": [
    "// --- 1 match(es) ---",
    "1\t// ── Section ─────────"
  ]
}
```

Not:

```json
{
  "_r": [
    "1\t// ?? Section ?????????"
  ]
}
```

---

## Issue 3 — `info` does not return last-modified timestamp

### Observed behaviour
`info` returns `_path`, `_size`, `_lines`, and `_encoding`. It does not return the file's last-modified time. The native `stat` command returns this as part of standard file metadata.

### Why this matters
The primary use of `info` before reading is to decide *how* to read — range selection, whether to summarise, etc. A second use case is cache invalidation: a caller who has already read a file can call `info` cheaply to detect whether the file changed since, without re-reading it. Without `_modified`, that check is impossible and the caller must re-read the whole file.

### Required fix
Add a `_modified` field to the `info` response, as an ISO 8601 UTC string:

```json
{
  "_path": "D:\\src\\TempoQueue.cs",
  "_size": 14083,
  "_lines": 279,
  "_encoding": "utf-8",
  "_modified": "2026-05-20T17:51:46Z"
}
```

Implementation:

```csharp
var modified = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-ddTHH:mm:ssZ");
```

### Acceptance criteria
1. `info` response always includes `_modified` as a UTC ISO 8601 string (no milliseconds required, seconds precision is sufficient).
2. The value matches the OS last-write time of the file.
3. No breaking change: all existing fields (`_path`, `_size`, `_lines`, `_encoding`) are still present.

---

---

## New Tool 4 — `grep`: cross-file regex search

### Motivation
`grep_in_file` requires the caller to already know which file to search. When the target file is
unknown — finding all usages of a constant, all `throw` statements, all `TODO` comments, all call
sites of a non-function symbol — the only current option is the native `Grep` tool, which returns
raw text with no line-range metadata and no integration with FReader's token-saving conventions.

`search_function` covers the cross-file case only for named function definitions. It does not
cover arbitrary regex patterns or non-function symbols.

### Implementation advice — loop vs native

**The developer must evaluate whether this tool is a loop around `grep_in_file` or a native
implementation.** The considerations are:

**Arguments for a loop around `grep_in_file`:**
- `grep_in_file` already handles context windows, match merging, ANSI stripping, and (once Issue 2
  is fixed) correct UTF-8 reading. Duplicating that logic in a new code path creates two
  maintenance surfaces.
- A loop is simple to reason about: glob files by extension, call `grep_in_file` on each, collect
  results.

**Arguments against a loop (i.e. for a native implementation):**
- A loop requires the caller to first enumerate candidate files (a glob call), then issue one
  `grep_in_file` call per file. For a directory with 40 `.cs` files that is 41 MCP round-trips.
  A native implementation is 1 round-trip.
- File enumeration, extension filtering, and generated-file exclusion logic already exists in
  `SearchEngine.cs` (used by `search_function`). A native `grep` can reuse that path enumeration
  and add a regex match step, keeping the logic in one place.
- A loop cannot enforce a global `maxMatches` cap across all files without over-fetching and
  discarding — a native implementation can stop early once the cap is reached.

**Recommendation:** Implement natively, reusing the file enumeration from `SearchEngine`. The
MCP round-trip cost of a loop makes the loop impractical for directories with more than ~5 files.

### Tool specification

**Tool name:** `grep`

**Parameters:**
```
rootPath:       string    — directory to search recursively
pattern:        string    — regex (same engine as grep_in_file)
fileExtensions: string[]  — filter by extension (default [".cs"])
context:        integer   — lines before/after each match (default 2)
maxMatches:     integer   — global cap across all files (default 50)
caseSensitive:  boolean   — (default false)
includeGenerated: boolean — include obj/bin/generated files (default false)
```

**Response:**
```json
{
  "_pattern": "ITempoJob",
  "_total_matches": 7,
  "_files_searched": 38,
  "_search_time_ms": 112,
  "_r": [
    {
      "_path": "src/Scheduling/TempoScheduler.cs",
      "_matches": 4,
      "_r": [
        "// --- 2 match(es) ---",
        "12\tusing Jattac.Libs.Tempo.Scheduling;",
        "...",
        "45\tpublic class TempoScheduler<TJob> : BackgroundService where TJob : ITempoJob"
      ]
    },
    {
      "_path": "src/Scheduling/ITempoJob.cs",
      "_matches": 3,
      "_r": [
        "// --- 1 match(es) ---",
        "5\tpublic interface ITempoJob"
      ]
    }
  ]
}
```

Each entry in `_r` follows the same format as `grep_in_file` output so callers parse both
identically. `_path` values are absolute. `_total_matches` is capped at `maxMatches` — if the cap
was hit, a `_truncated: true` field is added to the top-level response.

### Acceptance criteria
1. `grep` on a directory returns matches from all non-generated files matching `fileExtensions`.
2. Per-file results use the same context-window and merge logic as `grep_in_file`.
3. `maxMatches` is a global cap: once reached, no further files are searched and `_truncated: true`
   is set.
4. Files in `obj/`, `bin/`, `*.g.cs`, `*.Designer.cs` are excluded unless `includeGenerated: true`.
5. `_files_searched` reflects how many files were actually scanned (not just those with matches).
6. A file with zero matches for the pattern is not included in `_r`.
7. The UTF-8 encoding fix from Issue 2 applies here — `grep` must not corrupt non-ASCII characters.

---

## New Tool 5 — `summarize_dir`: structural overview of all files in a directory

### Motivation
`summarize` produces a compact structural overview of a single file (87–96% token savings, score
1.0). The first step in any unfamiliar codebase task is to understand what files exist and what
each one contains. Currently that requires calling `summarize` once per file — N round-trips
before any real work starts. For a directory with 12 files that is 12 calls returning 12 separate
responses to mentally merge.

### Implementation advice — loop vs native

**The developer must evaluate whether this is a loop around `summarize` or a native
implementation.** The considerations are:

**Arguments for a loop around `summarize`:**
- `summarize` already contains all the Roslyn parsing, aliasing, and output formatting logic.
  A loop reuses that entirely with no duplication.
- The result of looping is straightforward: a keyed collection of individual `summarize` responses.
- Maintenance cost is zero beyond the loop scaffolding.

**Arguments against a loop:**
- A directory with 20 files costs 20 MCP round-trips. The entire value of `summarize_dir` is
  collapsing those into 1.
- A native implementation can also apply cross-file deduplication — if 15 files share the same
  namespace, that can be stated once at the top rather than repeated 15 times, saving additional
  tokens.
- A native implementation can impose a single `maxChars` cap across all files and trim low-value
  files (e.g. single-method helpers) more aggressively to fit within budget.

**Recommendation:** Implement natively. The per-call overhead of a loop is the entire problem
being solved. However, the implementation should call the same internal summarize logic (not
re-implement it) so there is one code path for the actual summarization work.

### Tool specification

**Tool name:** `summarize_dir`

**Parameters:**
```
path:             string   — directory path
fileExtensions:   string[] — (default [".cs"])
includeGenerated: boolean  — (default false)
maxChars:         integer  — hard cap on total response characters (default 16000)
aliases:          boolean  — apply type aliasing (default false)
```

**Response:**
```json
{
  "_path": "D:\\src\\Scheduling",
  "_file_count": 6,
  "_r": [
    {
      "_path": "TempoScheduler.cs",
      "_lines": 210,
      "_text": "class TempoScheduler<TJob> : BackgroundService\n  ctor(...) @20-35\n  Task StartAsync(...) @80-110\n  ..."
    },
    {
      "_path": "ITempoJob.cs",
      "_lines": 12,
      "_text": "interface ITempoJob\n  Task ExecuteAsync(CancellationToken ct) @8-8"
    }
  ],
  "_truncated": false,
  "_total_chars": 4120
}
```

`_path` inside each result is relative to the `path` parameter. Files are ordered by line count
descending (largest/most complex first) so the caller encounters the most important files at the
top, and truncation (when `maxChars` is hit) cuts the least important files last.

### Acceptance criteria
1. All non-generated `.cs` files (or files matching `fileExtensions`) in the directory are
   summarized in a single response.
2. Results are ordered by line count descending.
3. When `maxChars` is reached, remaining files are omitted and `_truncated: true` is set.
4. Each result's `_text` is identical to what `summarize` would return for that file in isolation.
5. Subdirectories are not traversed — this is a flat directory operation, not recursive. A
   separate `summarize_tree` can be proposed if recursive coverage is needed.
6. `_file_count` reflects total files found, not just files included before truncation.

---

## New Tool 6 — `find_callers`: locate all call sites of a function

### Motivation
`search_function` finds where a function is **defined**. It does not find where it is **called**.
Understanding a function's callers is the most common next step after reading its body — it
answers "what triggers this?", "who owns the result?", and "is this safe to change?" Without a
dedicated tool, the only option is `grep` (once available) or the native `Grep` tool, both of
which return raw text that includes string literals, comments, and other false positives alongside
genuine call sites.

A Roslyn-based caller finder can distinguish genuine invocations from textual occurrences,
returning structured results with the enclosing method context — something a text search cannot do.

### Implementation note — not a loop

This is not a bulk variant of any existing tool. `search_function` finds definitions; `find_callers`
finds invocations. The data models are different. There is no existing single-mode tool to loop
around. This must be a native implementation using Roslyn's `InvocationExpressionSyntax` traversal
or a regex pre-scan followed by Roslyn confirmation (same two-phase pattern as `search_function`).

### Tool specification

**Tool name:** `find_callers`

**Parameters:**
```
rootPath:         string   — directory to search
name:             string   — function name to find call sites for
fileExtensions:   string[] — (default [".cs"])
limit:            integer  — max results (default 20)
includeGenerated: boolean  — (default false)
```

**Response:**
```json
{
  "_name": "SafeCallbackAsync",
  "_total_matches": 6,
  "_search_time_ms": 134,
  "_r": [
    {
      "_path": "src/Processing/TempoDispatcher.cs",
      "_line": 399,
      "_caller_class": "TempoDispatcher",
      "_caller_method": "ProcessItemAsync",
      "_snippet": "399\tawait SafeCallbackAsync("
    },
    {
      "_path": "src/Processing/TempoDispatcher.cs",
      "_line": 415,
      "_caller_class": "TempoDispatcher",
      "_caller_method": "ProcessItemAsync",
      "_snippet": "415\tvar trip = await SafeCallbackAsync("
    }
  ]
}
```

`_caller_class` and `_caller_method` are the enclosing type and method at the call site. These are
the fields that distinguish `find_callers` from a plain text search — they give immediate context
without requiring the caller to read surrounding code.

`_snippet` is the single line containing the invocation, tab-prefixed with the line number in the
same format as all other FReader tools.

### Acceptance criteria
1. Results include only genuine invocation sites (`SomeObj.SafeCallbackAsync(...)` or
   `SafeCallbackAsync(...)`), not string literals, comments, or `nameof()` expressions containing
   the name.
2. Each result includes `_caller_class`, `_caller_method`, `_line`, `_path`, and `_snippet`.
3. When the callee is overloaded, all overloads' call sites are returned — there is no
   `parameterTypes` filter on `find_callers` (the caller wants all invocations regardless of which
   overload is called; they can filter the result themselves).
4. `limit` caps the total results across all files. When hit, `_truncated: true` is added.
5. Generated files are excluded unless `includeGenerated: true`.

---

## New Tool 7 — `diff_function`: git diff scoped to a single function body

### Motivation
During code review and debugging the question is always "what changed in this function?" Getting
the answer currently requires: `git diff` (full file diff) → manually locate the function in the
diff → read the changed hunks. For a 400-line file with changes in 3 functions, the diff is
200+ lines of context you must mentally skip to find the one function you care about.

`diff_function` scopes the diff to a single function's line range, returning only the changed
lines within that range. This reduces a 200-line diff to 10 lines in the common case.

### Implementation note — no existing tool to loop around

This requires git integration. No existing FReader tool touches git. This is a genuinely new
capability. The implementation must:
1. Run `git diff [ref] -- <path>` (or `git show <ref>:<path>` for a specific commit) to get the
   raw diff.
2. Use the current function line range (from Roslyn, same as `extract_function`) as a window.
3. Filter the diff hunks to only those that intersect the function's line range.
4. Adjust hunk headers to reflect the scoped range.

### Tool specification

**Tool name:** `diff_function`

**Parameters:**
```
path:           string  — file path
name:           string  — function name
ref:            string  — git ref to diff against (default "HEAD", accepts "HEAD~1", commit SHA,
                          branch name)
parameterTypes: string[] — overload disambiguation (same semantics as read_function)
className:      string  — class disambiguation (same semantics as read_function)
```

**Response — changes found:**
```json
{
  "_name": "ProcessItemAsync",
  "_sig": "Task ProcessItemAsync(TempoWorkItem<TWork> item, CancellationToken ct)",
  "_ref": "HEAD~1",
  "_line_start": 341,
  "_line_end": 449,
  "_diff": [
    "@@ -364,7 +364,10 @@",
    "-    catch (OperationCanceledException)",
    "+    catch (OperationCanceledException) when (timeoutCts is not null && !ct.IsCancellationRequested)",
    "     {",
    "-        errorMessage = \"Processor timed out.\";",
    "+        // Processor exceeded ProcessorTimeout. Treat as failure so other tenants",
    "+        // are not starved by a single slow processor call.",
    "+        errorMessage = $\"Processor timed out after {_settings.ProcessorTimeout!.Value.TotalSeconds:F1}s.\";"
  ],
  "_hunks": 1,
  "_added": 3,
  "_removed": 2
}
```

**Response — no changes in this function:**
```json
{
  "_name": "ProcessItemAsync",
  "_ref": "HEAD~1",
  "_line_start": 341,
  "_line_end": 449,
  "_diff": [],
  "_hunks": 0,
  "_added": 0,
  "_removed": 0
}
```

`_diff` contains standard unified diff lines (`+`, `-`, ` ` prefix). Hunk headers (`@@`) are
included so the caller knows the line positions within the function. ANSI colour codes from git
are stripped.

### Acceptance criteria
1. `diff_function` with default `ref: "HEAD"` diffs the working tree against the last commit.
2. `diff_function` with `ref: "HEAD~1"` diffs the last commit against the one before it.
3. `diff_function` with a branch name or SHA diffs the current file against that ref.
4. Only diff hunks that intersect the function's line range are included. Hunks entirely outside
   the function are silently excluded.
5. When the function did not exist in `ref` (newly added), `_diff` contains the full function body
   as addition lines and `_added` equals the function line count.
6. When the function does not exist in the current file (deleted), return an error
   `NOT_FOUND` pointing to the fact that the function is gone, with a suggestion to use
   `diff_function(path, name, ref)` with the refs reversed to see the deletion.
7. `_hunks`, `_added`, `_removed` are always present even when `_diff` is empty.
8. Overload disambiguation via `parameterTypes` and `className` follows the same rules as
   `read_function`.

---

## Priority order

| # | Issue / Tool | Token impact | Effort |
|---|-------------|-------------|--------|
| 1 | `extract_function` silent overload truncation | Correctness fix | Medium |
| 2 | `grep_in_file` Unicode corruption | Correctness fix | Low |
| 3 | `info` missing mtime | Low — additive | Trivial |
| 4 | `grep` cross-file search | High — eliminates native Grep fallback | Medium (reuse SearchEngine paths) |
| 5 | `summarize_dir` bulk structural overview | High — N calls → 1 for codebase orientation | Medium (reuse summarize internals) |
| 6 | `find_callers` call-site locator | Medium — saves grep+manual parse | Medium (Roslyn InvocationExpression) |
| 7 | `diff_function` scoped git diff | Medium — saves wading through full file diffs | High (git integration, new dependency) |
