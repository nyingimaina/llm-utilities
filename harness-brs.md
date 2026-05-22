# BRS: MCP Server Auto-Harness — Zero Cold-Start Prompting
**Date:** 2026-05-21
**Scope:** FReader, CliSilentProxy (pattern applies to any future MCP server)
**Status:** Draft

---

## Problem statement

Every new Claude Code session starts cold. The MCP servers (FReader, CliSilentProxy) are already
registered and their tools are pre-approved in `.claude/settings.local.json` — the permission
prompt is gone. But the *conventions* are not loaded: which tool to prefer over a native
equivalent, the benchmark mandate, compact field names, tool selection rules. These only exist in
`get_instructions` output and a thin MEMORY.md entry.

The current workflow requires the user to either:
- explicitly call `get_instructions` at session start, or
- rely on a brief memory hint and hope Claude infers the rest correctly.

Neither is reliable. The goal is: Claude picks up FReader and CliSilentProxy conventions
automatically from the moment a session starts, with no user prompting, across all projects where
the servers are registered.

---

## Available mechanisms — evaluated

There are four independent mechanisms. They are not mutually exclusive; the recommended solution
uses three of them in layers.

---

### Mechanism A — `InitializeResult.instructions` (MCP protocol)

**What it is:**
The MCP protocol's `initialize` handshake includes an optional `instructions` string in the
server's response. Claude Code reads this field and inserts it directly into the system prompt
for the session. No user action, no CLAUDE.md, no memory — it is automatic on every session where
the server is connected.

**Server response shape:**
```json
{
  "protocolVersion": "2024-11-05",
  "capabilities": { "tools": {} },
  "serverInfo": { "name": "FReader", "version": "1.0.0" },
  "instructions": "...harness rules here..."
}
```

**What must change in the server (developer action):**
Locate the `InitializeResult` construction in each server's startup code and populate the
`instructions` field. The content must be injected there — not read from a file at runtime, not
returned by a tool. It is part of the protocol handshake.

**What the `instructions` field should contain:**
The `instructions` content is injected into the system prompt verbatim and consumed on every
token of every turn. It must be:
- **Compact** — under 400 tokens. The full `get_instructions` output is a reference document;
  `instructions` is a harness directive.
- **Imperative** — rules, not descriptions. "Use X instead of Y" not "X can do Y".
- **Self-contained** — no references to external docs. Everything a model needs to behave
  correctly must be present.

**Recommended `instructions` content for FReader:**

```
=== FReader — mandatory harness ===
Use FReader for ALL file reads in this project. Do not use Read, Grep, or Bash for file content.

TOOL SELECTION (in priority order):
  summarize       > first look at any file (87-96% token savings)
  read_function   > reading a named function (1 call vs 3)
  read_functions  > reading multiple functions (1 call, merges adjacent)
  search_function > finding a function across files (definitions only)
  grep_in_file    > regex search when file is known
  list_functions  > enumerate all functions in a file
  extract_function > get line range only (no body)
  read            > line-range read when no function tool applies
  info            > file metadata before reading

BENCHMARK MANDATE:
After first use of any tool category in a session, call record_benchmark(featureName, score, path).
score: 0.0 (useless) to 1.0 (perfect). Only record once per category per session.
Skip record_benchmark itself.

COMPACT FIELDS: _r=rows _h=headers _line_start/_line_end=range _sig=signature _text=text output
```

**Recommended `instructions` content for CliSilentProxy:**

```
=== CliSilentProxy — mandatory harness ===
Use CliSilentProxy run() instead of Bash for ALL shell commands where output on success is not needed.
Use Bash only when you need to capture and process success output inline.

WHEN TO USE run():
  - Build commands (dotnet build, npm run build, docker build)
  - Test runners (dotnet test, pytest, jest) — use filterPattern for large suites
  - Package operations (dotnet restore, npm install)
  - Git operations (push, fetch, rebase) where you only care about failure
  - Any script where success = "it worked", failure = "show me why"

FAILURE WORKFLOW:
  1. run() fails → inspect _tail
  2. _tail insufficient → get_log(id) for full compressed log
  3. get_log(id, raw:true) if you need original unprocessed output

filterPattern applies to failure tails only. Use filterContext (if available) for context lines.
```

**Limitations of this mechanism:**
- Content is static — changing the harness rules requires redeploying the server.
- There is no per-project customisation — `instructions` is global to the server, not scoped.
- If the server is unavailable at session start, instructions are not injected (though neither
  are the tools, so this is not a new problem).

**Verdict: implement this for both servers. It is the primary mechanism.**

---

### Mechanism B — CLAUDE.md (global and project-level)

**What it is:**
Claude Code loads CLAUDE.md files from two locations on every session start:
1. `~/.claude/CLAUDE.md` — global, applies to all projects on the machine
2. `<project-root>/CLAUDE.md` — project-level, applies only to the current project

Both files are loaded into the system context before the first user turn. They are the
authoritative place to put instructions that override Claude's defaults.

**Relationship to Mechanism A:**
`InitializeResult.instructions` (A) and CLAUDE.md (B) are additive — both are loaded. CLAUDE.md
is the right place for user-level preferences and project-specific rules that the server itself
cannot know. Mechanism A tells Claude how the server works; Mechanism B tells Claude how the
*user* wants it used.

**What to write in global `~/.claude/CLAUDE.md`:**

Create this file if it does not exist. It should contain cross-project rules that apply wherever
these servers are registered:

```markdown
# Global tooling conventions

## MCP servers
When FReader and CliSilentProxy MCP servers are available (check the tool list), their use is
mandatory over native equivalents:

- FReader replaces: Read, Grep (single-file), Bash file reads
- CliSilentProxy replaces: Bash for commands where success output is noise

Both servers inject their own harness rules via server instructions. This section reinforces
user-level preferences that the servers cannot express:

- Always benchmark FReader tool categories on first use per session via record_benchmark.
- Do not use filterPattern on CliSilentProxy without also noting that it can drop diagnostic lines
  not matching the pattern — prefer get_log when the tail is ambiguous.
- After any FReader or CliSilentProxy tool call, do not repeat the tool output back to the user
  verbatim. Summarise what was found and act on it.
```

**What to write in project-level `<project>/CLAUDE.md`:**

Project CLAUDE.md should contain only what is genuinely project-specific — not a repeat of the
global file or the server instructions:

```markdown
# Project conventions

## File reading
Source files are in `src/`. Tests are in `tests/`. Generated files are in `src/obj/` and
`src/bin/` — exclude these from all FReader searches (they are excluded by default).

## Build and test
- Build: `dotnet build src`
- Test: `dotnet test tests`
- Use CliSilentProxy for both. Tests require a prior build — if --no-build fails, build first.

## Benchmark data
Benchmark scores are stored in `.freader-benchmarks.json` at the project root.
```

**Limitations of this mechanism:**
- Static — changes require editing a file, not redeploying a server.
- Global CLAUDE.md applies to all projects including ones where the servers are not installed —
  write the global file defensively (check tool availability before assuming servers are present).
- CLAUDE.md is read by humans too; keep it readable, not a wall of directives.

**Verdict: implement both levels. Global for cross-project conventions, project-level for
project-specific build/test patterns.**

---

### Mechanism C — Memory system (`MEMORY.md` + memory files)

**What it is:**
The memory system at `~/.claude/projects/<project-slug>/memory/` is loaded into every session
for that project. `MEMORY.md` is the index (always loaded, truncated after 200 lines). Individual
memory files are loaded on relevance.

**Current state:**
`MEMORY.md` already has entries for `feedback_freader_workflow.md` and `feedback_callback_isolation.md`.
`feedback_freader_workflow.md` already contains: "Use FReader MCP tools by default; benchmark each
tool category once per session against native before committing to it."

**What to add:**
The memory system is the right place for *per-project state* that changes over time — benchmark
scores, user preferences discovered during sessions, known quirks. It is not the right place for
static tool conventions (that's A and B). The gap to fill:

Add a reference memory for CliSilentProxy, equivalent to the existing FReader entry:

```markdown
---
name: feedback-clisilentproxy-workflow
description: CliSilentProxy is the mandatory shell runner for this project — replaces Bash for
             build/test/git commands where success output is noise
metadata:
  type: feedback
---
Use CliSilentProxy run() instead of Bash for all shell commands where the success output is not
needed inline. Covers: dotnet build, dotnet test, dotnet pack, git operations, PowerShell scripts.

**Why:** Bash success output (build logs, test runner verbosity) is 90-97% noise tokens that
add no value. CliSilentProxy surfaces only failure tails.

**How to apply:** Default to run(). Only use Bash when the command's stdout on success is
directly needed (e.g. capturing a version string, reading script output for further processing).
```

Add this to `MEMORY.md` as a new entry in the Entries table.

**Limitations of this mechanism:**
- MEMORY.md is project-scoped — global conventions must also live in A or B.
- Memory entries can become stale; they carry a date and should be verified against current
  server behaviour when the benchmark scores update.
- The 200-line MEMORY.md truncation limit means this mechanism is supplementary, not primary.

**Verdict: use memory for project-specific state and user preferences. Not the primary harness
mechanism.**

---

### Mechanism D — MCP `prompts` capability

**What it is:**
The MCP protocol supports a `prompts` capability: servers expose named prompt templates via
`prompts/list` and `prompts/get`. A CLAUDE.md instruction can tell Claude to call a specific
named prompt at session start, which injects the server's full instructions on demand.

**Example server prompt definition:**
```json
{
  "name": "freader-harness",
  "description": "Full FReader harness rules and tool selection guide",
  "arguments": []
}
```

CLAUDE.md would then say:
```
At the start of each session, if FReader is available, call the prompt "freader-harness" to
load the full harness rules.
```

**Why this is not recommended as primary:**
- It requires Claude to remember to call the prompt before doing anything else — unreliable on
  cold start, especially in automated or agentic contexts.
- `InitializeResult.instructions` (Mechanism A) achieves the same auto-injection without relying
  on Claude's compliance.
- Prompts are useful as an *upgrade path*: if the compact `instructions` text isn't enough for a
  complex task, the user can explicitly invoke the full harness prompt mid-session. Keep
  `get_instructions` for this — it already serves this role.

**Verdict: do not use as primary. Retain `get_instructions` as the on-demand equivalent. Consider
implementing the MCP `prompts` capability as a formal alias for `get_instructions` if the MCP
client ecosystem improves.**

---

## Recommended implementation — three layers

```
Layer 1 (server-side, automatic):   InitializeResult.instructions
                                    → compact harness rules injected every session
                                    → developer action: populate instructions field in server startup

Layer 2 (client-side, static):      ~/.claude/CLAUDE.md  (global)
                                    <project>/CLAUDE.md   (project)
                                    → user preferences and project conventions
                                    → user action: create/edit these files

Layer 3 (client-side, stateful):    MEMORY.md + memory files
                                    → per-project benchmark state and session-learned preferences
                                    → maintained automatically by Claude during sessions
```

No single layer is sufficient alone:
- A without B/C: harness rules present but user preferences and project context absent.
- B without A: CLAUDE.md is editable by anyone and may drift from server reality; no auto-sync.
- C without A/B: memory is too sparse and too project-scoped to carry full conventions.

---

## Developer implementation checklist

### FReader server

- [ ] Locate `InitializeResult` construction in `FReaderServer.cs` (or `Program.cs` / startup).
- [ ] Add `instructions` field to the response using the compact content specified in Mechanism A.
- [ ] Keep `get_instructions` tool unchanged — it remains the on-demand full reference.
- [ ] Verify: start a fresh Claude Code session with no CLAUDE.md and no memory; confirm the
      tool selection rules are visible in Claude's behaviour without any user prompting.

### CliSilentProxy server

- [ ] Same as FReader: locate `InitializeResult` and populate `instructions`.
- [ ] Use the compact CliSilentProxy content from Mechanism A.
- [ ] Keep `get_instructions` tool unchanged.
- [ ] Verify: same fresh-session test.

### `instructions` field length budget

The `instructions` field for both servers combined must stay under 800 tokens total. Claude Code
injects both into the same system prompt. Exceeding ~800 tokens risks crowding out user context
on short-context sessions and defeats the token-saving purpose of the tools themselves.

Current draft sizes:
- FReader `instructions`: ~220 tokens
- CliSilentProxy `instructions`: ~150 tokens
- Total: ~370 tokens — well within budget.

Do not expand the `instructions` content with examples, full parameter lists, or error codes.
Those belong in `get_instructions`. The `instructions` field is a decision tree and a mandate,
not a manual.

---

## User configuration checklist

### Global CLAUDE.md

- [ ] Create `~/.claude/CLAUDE.md` if it does not exist.
- [ ] Add the cross-project MCP conventions from Mechanism B.
- [ ] Defensive wording: wrap FReader/CliSilentProxy rules in a check — "when these servers are
      available" — so the global file does not confuse Claude in projects where they are not
      installed.

### Project CLAUDE.md

- [ ] Create `<project-root>/CLAUDE.md` for each project using these servers.
- [ ] Include only project-specific content: directory layout, build commands, test commands.
- [ ] Do not repeat the server harness rules — those come from Mechanism A.

### Memory files

- [ ] Add `feedback_clisilentproxy_workflow.md` memory file (content in Mechanism C above).
- [ ] Add pointer to `MEMORY.md` entries table.
- [ ] Review existing `feedback_freader_workflow.md` — after Mechanism A is implemented, the
      "benchmark each tool category once per session" rule moves to `instructions`; the memory
      file should be updated to remove duplication and focus on user preferences not expressible
      in server instructions (e.g. "do not summarise tool output back verbatim").

---

## Verification test

After all three layers are in place, run this cold-start test for each project:

1. Close Claude Code completely.
2. Delete or rename `MEMORY.md` temporarily (to isolate layer 1 and 2 from layer 3).
3. Open a new session in the project.
4. Without any user prompting, ask Claude to read a source file.
5. Confirm Claude uses `summarize` or `read_function`, not `Read`.
6. Ask Claude to run the project's build command.
7. Confirm Claude uses `CliSilentProxy run()`, not `Bash`.
8. Restore `MEMORY.md`.

If step 5 or 7 fails, layer 1 (`InitializeResult.instructions`) is not working — check the
server's initialize handler. If they pass only when CLAUDE.md is present, layer 1 is absent and
layer 2 is doing the work — acceptable but fragile (CLAUDE.md can be deleted; server instructions
cannot).
