# BRS: CliSilentProxy Improvements
**Date:** 2026-05-21
**Source:** Benchmarking session against Jattac.Libs.Tempo (C# / dotnet project)
**Status:** Draft

---

## Issue 1 — `filterPattern` silently drops the root-cause diagnostic line

### Observed behaviour
`run` was called for `dotnet test tests --no-build --configuration Release --verbosity normal`
with `filterPattern: "(?i)(error|fail|exception|passed|skipped)"`.

The command failed. The tail returned 4 lines:

```
MSB4181: The "VSTestTask" task returned false but did not log an error.
1>Done Building Project ".\tests\tests.csproj" (VSTest target(s)) -- FAILED.
Build FAILED.
    0 Error(s)
```

The actual root-cause line — present in the full 12-line log — was silently absent:

```
The test source file ".\tests\bin\Release\net8.0\tests.dll" provided was not found.
```

That line contains none of the pattern words (`error`, `fail`, `exception`, `passed`, `skipped`),
so it was filtered out. The returned tail looked like a complete diagnosis but was not — a caller
acting on it would investigate the wrong thing (MSBuild task failure) rather than the real cause
(missing output DLL, i.e. tests were never built in Release config).

### Why this is a problem
The entire value of `filterPattern` is to surface signal and suppress noise. When it drops the
root-cause line, it inverts its purpose: it surfaces secondary noise (MSBuild wrapper messages)
and suppresses signal. The caller receives no indication this happened and has no prompt to call
`get_log`. A caller who trusts the filtered tail will diagnose incorrectly.

### Required fix — two complementary changes

#### Fix A: Add `_pattern_matches` and `_pattern_total` to failure response when filterPattern is active

The response already contains `_tail_lines` (lines shown) and `_total_lines` (total compressed
lines). When `filterPattern` is active on a failure, add:

- `_pattern_matches`: how many compressed lines matched the pattern
- `_pattern_total`: total compressed lines (same as `_total_lines` — rename or alias for clarity)

This gives the caller the ratio to judge filter coverage. Example — current response:

```json
{
  "_s": "fail",
  "_exit": 1,
  "_ms": 1417,
  "_tail": ["MSB4181: ...", "Done Building ... FAILED.", "Build FAILED.", "    0 Error(s)"],
  "_tail_lines": 4,
  "_total_lines": 12,
  "_truncated": false
}
```

Improved response:

```json
{
  "_s": "fail",
  "_exit": 1,
  "_ms": 1417,
  "_tail": ["MSB4181: ...", "Done Building ... FAILED.", "Build FAILED.", "    0 Error(s)"],
  "_tail_lines": 4,
  "_total_lines": 12,
  "_pattern_matches": 4,
  "_truncated": false,
  "_hint": "filterPattern matched 4/12 lines. Call get_log(2) for full log."
}
```

The `_hint` is only present when `filterPattern` is active on a failure. The ratio `4/12` tells
the caller immediately that 67% of the log was hidden by the filter and warrants inspection.

#### Fix B: Always append the line(s) immediately preceding the first non-zero exit trigger

Regardless of whether they match `filterPattern`, include up to 3 lines from immediately before
the process exited non-zero — these are the most likely location of the root cause. Mark them
with a prefix so the caller knows they are injected context, not filter matches:

```json
{
  "_tail": [
    "MSB4181: ...",
    "Done Building ... FAILED.",
    "Build FAILED.",
    "    0 Error(s)",
    "// -- last lines before exit (unfiltered) --",
    "    0 Warning(s)",
    "    0 Error(s)",
    "Time Elapsed 00:00:00.93"
  ]
}
```

For the test case above, this would expose the unfiltered tail which includes:

```
The test source file ".\tests\bin\Release\net8.0\tests.dll" provided was not found.
```

Note: "immediately before exit" means the last 3 lines of the compressed log, not the last 3 lines
of the raw output. Since the compressed log already has blank lines stripped, these 3 lines are
already dense signal.

### Acceptance criteria
1. When `filterPattern` is active and the run fails, `_pattern_matches` is present in the response
   and equals the count of compressed log lines that matched the regex.
2. When `_pattern_matches < _total_lines`, `_hint` is present and contains the ratio and the `get_log` call with the run ID.
3. The last 3 compressed lines of the log (the "exit context") are appended to `_tail` after a
   separator entry `"// -- exit context (unfiltered) --"` when `filterPattern` is active.
4. When `filterPattern` is NOT active, none of the above fields are present (no schema bloat for
   the common case).
5. The separator line is always the literal string `"// -- exit context (unfiltered) --"` so callers
   can detect and strip it programmatically.

---

## Issue 2 — `_tail_lines` has different semantics depending on `filterPattern` presence

### Observed behaviour
`_tail_lines` is documented as "count of lines in `_tail`". That is always true. However its
*meaning* changes silently based on whether `filterPattern` is active:

- Without `filterPattern`: `_tail_lines` = last N lines of the log (recency-ordered slice)
- With `filterPattern`: `_tail_lines` = count of pattern-matched lines (scattered through the log)

In both cases the value is the count of what is in `_tail`, but the semantic is different. A caller
reading `_tail_lines: 4` cannot tell whether they have the last 4 lines or 4 scattered matches.
This matters because the two cases imply different follow-up actions:
- Last 4 lines: the failure is probably in those 4 lines, reading more means going backwards
- 4 scattered matches: the failure could be anywhere; the 4 lines are not contiguous

### Required fix
Add a boolean field `_filtered` (present and `true` only when `filterPattern` is active on a
failure) so the caller knows which semantic applies:

```json
{
  "_s": "fail",
  "_tail_lines": 4,
  "_total_lines": 12,
  "_filtered": true
}
```

Without `filterPattern`:

```json
{
  "_s": "fail",
  "_tail_lines": 8,
  "_total_lines": 8,
  "_truncated": false
}
```

`_filtered` is absent (not `false`) when filterPattern is not active — keep the success path lean.

### Acceptance criteria
1. `_filtered: true` is present in the failure response if and only if `filterPattern` was supplied.
2. `_filtered` is never present in success responses.
3. `_filtered` is never present in failure responses where `filterPattern` was not supplied.

---

---

## Feature 3 — `extractPattern`: structured extraction on success

### Problem
On success the response is total silence — `_s: "ok"`, `_exit: 0`, `_ms`, `_cmd`. This is correct
for commands whose output is irrelevant on success (build, restore, format). But many commands
produce one summary line on success that the caller needs:

- `dotnet test` → `"42 passed, 0 failed, 3 skipped (1.4s)"`
- `dotnet pack` → `"Successfully created package ... version 1.2.3"`
- `dotnet build` → `"src -> D:\...\src.dll"`
- `npm run build` → `"webpack 5.x compiled successfully in 820 ms"`

Currently the caller must either accept total silence (loses the summary) or use Bash which returns
the full 60-line output just to get that one line.

### Required feature
Add an `extractPattern` parameter (regex string with named capture groups). When the command
succeeds and the pattern matches at least one line, return a `_extracted` field in the response
containing the named captures as key-value pairs. When the pattern does not match, return
`_extracted: {}` with no error (the command still succeeded; the absence of a match is informational).

**Parameter:**
```
extractPattern: string  — .NET regex with named groups (?<name>...)
```

**Example call:**
```json
{
  "command": "dotnet",
  "args": ["test", "tests", "--configuration", "Release"],
  "extractPattern": "(?<passed>\\d+) passed.*?(?<failed>\\d+) failed.*?(?<elapsed>[\\d.]+s)"
}
```

**Success response with match:**
```json
{
  "_s": "ok",
  "_exit": 0,
  "_ms": 3210,
  "_cmd": "dotnet test tests --configuration Release",
  "_extracted": {
    "passed": "47",
    "failed": "0",
    "elapsed": "2.1s"
  }
}
```

**Success response with no match** (pattern present but didn't match anything in the log):
```json
{
  "_s": "ok",
  "_exit": 0,
  "_ms": 3210,
  "_cmd": "...",
  "_extracted": {}
}
```

`extractPattern` is evaluated against the **compressed** log (blanks stripped, ANSI stripped,
progress lines resolved) so the caller does not need to account for ANSI escape sequences in the
regex. The pattern is matched against each line independently (not the full log as a single string),
and the first match wins. If multiple groups with the same name exist across lines (unlikely but
possible), only the first match is returned.

`extractPattern` and `filterPattern` are independent and can be combined: `filterPattern` controls
the failure tail; `extractPattern` controls success extraction.

### Acceptance criteria
1. On success with a matching `extractPattern`, `_extracted` contains all named groups from the
   first matching line. Unnamed groups are ignored.
2. On success with a non-matching `extractPattern`, `_extracted` is `{}` (empty object, not absent).
3. `extractPattern` has no effect on failure responses — `_extracted` is never present when
   `_s: "fail"`.
4. An invalid regex in `extractPattern` returns an error before the command is run, with
   `error_code: "INVALID_REQUEST"` and the regex parse error message.
5. `extractPattern` without named groups returns `_extracted: {}` (unnamed groups are not useful
   as structured output; this is not an error).

---

## Feature 4 — `filterContext`: context lines around `filterPattern` matches

### Problem
`filterPattern` returns only the lines that match the pattern. Matched lines frequently lack meaning
without the lines immediately surrounding them. Example — a dotnet build failure with
`filterPattern: "(?i)(error|fail)"` might return:

```
CS0246: The type or namespace name 'ITempoJob' could not be found
```

Without context the caller does not know which file, which line, which project. The actual output
in the log is:

```
src/Scheduling/TempoScheduler.cs(45,28): error CS0246: The type or namespace name 'ITempoJob'
    could not be found (are you missing a using directive or an assembly reference?)
```

The file and line number are on the same line as the match here, but in many tools (pytest,
MSTest, rustc) the location is on the line *before* the error message. Without context lines,
the filtered tail is technically correct but diagnostically incomplete.

### Required feature
Add a `filterContext` integer parameter (default `0`). When > 0, include that many compressed
log lines before and after each `filterPattern` match. Overlapping windows are merged, identical
to how `grep_in_file`'s `context` parameter works.

**Parameter:**
```
filterContext: integer  — lines of context before and after each match (default 0)
```

**Example call:**
```json
{
  "command": "dotnet",
  "args": ["build", "src"],
  "filterPattern": "(?i)error",
  "filterContext": 2
}
```

**Response tail (failure):**
```json
{
  "_tail": [
    "// --- 1 match(es) ---",
    "src/Scheduling/TempoScheduler.cs(45,28): error CS0246: The type or namespace",
    "    'ITempoJob' could not be found",
    "    (are you missing a using directive or an assembly reference?)"
  ]
}
```

The `"// --- N match(es) ---"` separator is the same literal format used by `grep_in_file` so
callers already familiar with that tool's output need no new parsing logic.

Context lines are drawn from the compressed log. `filterContext` does not affect the unfiltered
exit-context block added by Issue 1B — that block is always the last 3 compressed lines and is
appended after the filtered section regardless.

### Acceptance criteria
1. `filterContext: 0` (default) produces identical output to the current behaviour — no context
   lines, no separators.
2. `filterContext: N` includes up to N compressed lines before and after each match.
3. When two match windows overlap (match A's post-context includes match B's pre-context), they
   are merged into a single contiguous block under a single `"// --- N match(es) ---"` separator
   where N is the count of matches in that merged block.
4. Context lines that themselves match `filterPattern` are not double-counted — they appear once
   in the merged block.
5. `filterContext` has no effect when `filterPattern` is absent.
6. `filterContext` applies to both failure and (if `filterPattern` were hypothetically applied to
   success in a future feature) any other mode where `filterPattern` is active.

---

## Feature 5 — Tail output on timeout

### Problem
When a command times out, the response is:

```json
{
  "_s": "timeout",
  "_exit": null,
  "_ms": 120000,
  "_timed_out": true,
  "_cmd": "..."
}
```

No log, no tail, no run ID. A timeout is exactly the scenario where the caller most needs to know
what the command was doing at the moment it was killed — was it hanging on a network call, stuck
in an infinite loop, waiting for user input, or simply running slower than expected? Without the
tail, the only option is to run the command again with a longer timeout and hope, which wastes
tokens and time.

### Required feature
On timeout, produce the same tail + run ID output as a failure response. The distinction between
timeout and failure is already conveyed by `_s: "timeout"` and `_timed_out: true`; there is no
reason to withhold the log.

**Current timeout response:**
```json
{
  "_s": "timeout",
  "_timed_out": true,
  "_exit": null,
  "_ms": 120000,
  "_cmd": "dotnet test tests --verbosity detailed"
}
```

**Required timeout response:**
```json
{
  "_s": "timeout",
  "_timed_out": true,
  "_exit": null,
  "_ms": 120000,
  "_cmd": "dotnet test tests --verbosity detailed",
  "_id": 3,
  "_tail": [
    "Passed! MyNamespace.SlowTest.WhenCalledWithLargeInput",
    "  Started:  MyNamespace.HangingTest.WhenConnectionDrops",
    "[no further output before timeout]"
  ],
  "_tail_lines": 3,
  "_total_lines": 89,
  "_truncated": true
}
```

The final entry `"[no further output before timeout]"` is a literal sentinel appended by
CliSilentProxy when the process was killed mid-run, so the caller knows the log ends because of
the kill, not because the command finished. This distinguishes a genuine end-of-output from a
kill boundary.

`filterPattern` and `filterContext` apply to timeout tails the same way they apply to failure
tails. `get_log` works on timeout run IDs the same way it works on failure run IDs.

### Acceptance criteria
1. A timeout response always includes `_id`, `_tail`, `_tail_lines`, `_total_lines`, `_truncated`.
2. The last entry in `_tail` is the literal string `"[no further output before timeout]"` when the
   process was killed (i.e. did not exit naturally before the timeout).
3. `get_log(id)` works for timeout run IDs and returns the full compressed log up to the kill point.
4. `filterPattern` and `filterContext`, if supplied, apply to the timeout tail.
5. `_exit` remains `null` (or `-1` if the OS returns a kill exit code — document whichever is
   chosen) to distinguish timeout from a normal non-zero exit.

---

## Feature 6 — Error-first tail mode (`tailMode: "first_error"`)

### Problem
The default tail is the **last** N lines of the compressed log. For short commands this is fine.
For long-running commands (full test suite, large build), the root cause is often the **first**
error, which may be 300 lines above the end of the log. The last N lines in these cases are
typically the build system's summary wrapper (`Build FAILED`, `0 warnings`, `Time Elapsed`) — not
the error itself.

Example — a 400-line build log for a project with 2 compile errors and 15 downstream
"could not resolve" errors:

- **Last 50 lines** (current default): 15 downstream `CS0246` errors and the MSBuild summary.
  The root cause (the first `CS0103` that caused the cascade) is at line 12.
- **Error-first 50 lines**: lines 10–15 (first error + context) + lines 396–400 (closing summary).
  Root cause visible immediately.

### Required feature
Add a `tailMode` string parameter with two values:

- `"tail"` (default, current behaviour) — last `tailLines` lines of the compressed log.
- `"first_error"` — locate the first line matching `(?i)(error|exception|fatal|panic)`, include
  `tailLines / 2` lines of context around it, then append the final 5 lines of the log as a
  closing summary. Total lines returned ≤ `tailLines`.

**Parameter:**
```
tailMode: "tail" | "first_error"   (default "tail")
```

**Example call:**
```json
{
  "command": "dotnet",
  "args": ["build", "src", "--no-incremental"],
  "tailMode": "first_error",
  "tailLines": 30
}
```

**Response tail layout for `first_error` with `tailLines: 30`:**
```
[lines 1–15: first error ± 12 lines of context]
// -- closing summary --
[lines 396–400: final 5 lines of log]
```

The `"// -- closing summary --"` separator is a fixed literal so callers can split the two
sections programmatically.

If no line matches the error pattern (e.g. the command failed with exit code 1 but produced no
error-keyword output), `first_error` falls back to `"tail"` behaviour and adds `_hint:
"first_error: no error line found, fell back to tail"` to the response.

If the first error and the closing summary overlap (log is short enough that the error is within
the last 5 lines), the two sections are merged into one contiguous block and the separator is
omitted.

### Acceptance criteria
1. `tailMode: "tail"` (default) produces identical output to current behaviour.
2. `tailMode: "first_error"` locates the first line in the compressed log matching
   `(?i)(error|exception|fatal|panic)` and returns up to `floor(tailLines / 2)` lines of context
   around it (symmetric: equal lines before and after, capped at log boundaries).
3. The final 5 lines of the compressed log are appended after `"// -- closing summary --"` unless
   they overlap with the context block.
4. Total lines in `_tail` never exceed `tailLines`.
5. When fallback occurs, `_hint` is present with the text `"first_error: no error line found,
   fell back to tail"`.
6. `tailMode` applies to timeout tails (Feature 5) as well as failure tails.
7. `tailMode` is ignored on success responses.

---

## Feature 7 — Fuzzy repeated-line collapse

### Problem
The compression pipeline already collapses **identical** consecutive lines into `line (xN)`. This
handles spinner progress lines and exact duplicates. It does not handle the common case of
**near-identical** consecutive lines that differ only in a variable field — which is the dominant
noise pattern from package managers and build tools:

```
  Restored Newtonsoft.Json 13.0.3
  Restored Microsoft.Extensions.Logging 8.0.0
  Restored Microsoft.Extensions.DependencyInjection 8.0.1
  Restored Microsoft.AspNetCore.Http.Abstractions 2.2.0
  ... (36 more lines)
```

None of these are identical so the current pipeline keeps all 40 lines. After fuzzy collapse they
become:

```
  Restored 40 packages
```

### Required feature
Add a fuzzy collapse pass to the compression pipeline, applied after the existing identical-line
collapse. The pass works by prefix clustering:

1. Scan consecutive lines for a shared prefix of ≥ 10 characters.
2. When a run of ≥ 3 consecutive lines shares a prefix, collapse the run to:
   `"{prefix}... ({count} lines)"` — where `{prefix}` is the common prefix, trimmed of trailing
   whitespace.
3. The threshold of 3 is the minimum — fewer than 3 consecutive similar lines are kept verbatim.

**Before:**
```
  Restored Newtonsoft.Json 13.0.3
  Restored Microsoft.Extensions.Logging 8.0.0
  Restored Microsoft.Extensions.DependencyInjection 8.0.1
  Restored Microsoft.AspNetCore.Http.Abstractions 2.2.0
  Build started 21/05/2026 ...
```

**After:**
```
  Restored... (4 lines)
  Build started 21/05/2026 ...
```

The collapsed line uses `...` (three ASCII dots) as the truncation marker, not `…` (U+2026), to
avoid any encoding issues (see FReader BRS Issue 2 for precedent).

**Prefix length floor:** The shared prefix must be ≥ 10 characters after leading whitespace is
stripped. This prevents over-collapsing short lines that share only a common short word (e.g.
lines starting with `"at "` in a stack trace should NOT be collapsed — the variable part is the
meaningful part).

**Stack trace exception:** Lines matching `^\s+at ` (stack frame pattern) are exempt from fuzzy
collapse even if they share a prefix. Stack frames are individually meaningful and collapsing them
loses the call chain.

**No effect on success silence:** Fuzzy collapse is a log compression step. Since success logs are
discarded entirely, this only affects failure and timeout tails and `get_log` output.

### Acceptance criteria
1. A run of ≥ 3 consecutive lines sharing a prefix of ≥ 10 non-whitespace characters is collapsed
   to `"{prefix}... ({count} lines)"`.
2. A run of < 3 lines is kept verbatim.
3. Lines matching `^\s+at ` are never collapsed regardless of prefix sharing.
4. The collapse marker is `...` (three ASCII dots), never `…`.
5. The existing identical-line collapse (`line (xN)`) runs before fuzzy collapse and takes
   precedence — a block of identical lines is collapsed by the existing pass and not re-processed.
6. `get_log(id, raw: true)` returns the original uncompressed log, unaffected by fuzzy collapse,
   so the caller can always recover the full detail.
7. `get_log(id, raw: false)` (default) applies fuzzy collapse, consistent with what was shown in
   the tail.

---

## Priority order

| # | Issue / Feature | Token impact | Effort |
|---|----------------|-------------|--------|
| 1A | `filterPattern` ratio + hint | High — prevents misdiagnosis | Low — additive fields |
| 1B | Unfiltered exit context in tail | High — surfaces root cause | Low — slice last 3 compressed lines |
| 2 | `_filtered` semantic flag | Low — DX clarity | Trivial |
| 3 | `extractPattern` success extraction | High — eliminates Bash fallback for summary data | Medium — regex eval + named group extraction |
| 4 | `filterContext` context lines | Medium — diagnostic completeness | Low — mirrors grep_in_file context logic |
| 5 | Tail on timeout | High — timeout is currently a black box | Low — reuse failure tail path |
| 6 | `tailMode: "first_error"` | High — root cause visible without `get_log` | Medium — first-error scan + merge logic |
| 7 | Fuzzy repeated-line collapse | Medium — cuts restore/install noise 10-40x | Medium — prefix clustering pass in compressor |
