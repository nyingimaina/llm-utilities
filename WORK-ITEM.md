# Work Item: LLM Integration Quality Verification & Fixes
**Assignee:** Junior Developer  
**Repo:** `D:\work\nyingi\code\systems\llm-utilities`  
**Priority:** High — these are production MCP servers used daily by Claude Code

---

## Background & Current State

Seven major bug fixes were applied across the MCP server codebase (8 commits total, all on `master`). A 34-test integration test suite was added in `src/McpServers.Tests/`. All **116 tests pass** (34 integration + 82 unit). The servers are structurally sound.

**What was fixed (do not re-fix these):**
1. `get_instructions` now bypasses the `_initialized` gate — it responds even before `notifications/initialized` arrives
2. Tool calls are dispatched to a background worker — `get_instructions` responds in <500ms even while a slow `run()` command is in-flight
3. Removed `WaitForExit()` from Notifier's fire-and-forget notification path
4. PowerShell injection in notification titles fixed (Base64 encoding)
5. Rowster DB exceptions now surface as tool errors `{_e:"...", error_code:"DB_ERROR"}` instead of protocol errors
6. JSON serialization in error messages is now safe (was using string interpolation)
7. Config file load failure now logs a warning and continues with defaults instead of silently crashing

**What this work item covers:** Three categories remain unresolved:
- A) Code gaps that will cause LLM confusion or breakage
- B) LLM ergonomics gaps — the tool works but an LLM won't know how to use it correctly
- C) Verification that everything works end-to-end with a real LLM session

---

## Prerequisites — Read This Before Touching Anything

### 1. Understand the MCP Protocol
MCP (Model Context Protocol) is JSON-RPC over stdin/stdout. The handshake is:
1. Client → `initialize` request
2. Server → `initialize` response (sends capabilities, server name/version)
3. Client → `notifications/initialized` (no response expected)
4. Client → any tool calls

If you skip step 3, the server should still respond to `get_instructions` (this was one of the bugs we fixed). All other tools will return `{"error":{"code":-32000,"message":"Not initialized"}}` until step 3 is received.

### 2. Build and Run Tests First
Before making any changes, confirm the baseline is green:

```powershell
# From repo root
dotnet build
dotnet test src/McpServers.Tests/McpServers.Tests.csproj --verbosity normal
dotnet test src/FReader.Tests/FReader.Tests.csproj --verbosity quiet
```

**Expected output:**
```
McpServers.Tests: Passed: 34, Failed: 0
FReader.Tests:    Passed: 82, Failed: 0
```

If any tests fail before you touch anything, stop and escalate. Do not proceed on a broken baseline.

### 3. Understand the Test Infrastructure
`src/McpServers.Tests/McpTestClient.cs` — in-process transport using `BlockingCollection<string>`. The server runs on a background thread and communicates via two in-memory pipe analogues. This avoids spawning real processes. When a test times out (e.g., `ReadResponse(3000)` throws `TimeoutException`), it means the server never wrote a response — likely a deadlock, not a crash.

### 4. Understand McpServerConfig
Every server has a `McpServerConfig` with these fields:
- `AnnouncementDirective` — injected into the tool descriptions that Claude sees when it lists available tools. This is the first thing Claude reads.
- `HarnessInstructions` — injected into the system prompt area by Claude Code's MCP harness, if supported. Currently unclear if Claude Code injects this; treat it as "may or may not appear."
- `InstructionsToolDescription` — the description for the `get_instructions` tool itself. Must be compelling enough that Claude calls it first.

---

## Category A: Code Gaps (Must Fix)

These are concrete bugs or missing features. Fix each one, run all tests to confirm nothing broke, then commit. One commit per fix.

### A1. Rowster: `_meta PARAMS` section missing from `GetInstructions()`

**File:** `src/Rowster/McpServer.cs` — `GetInstructions()` method (around line 180)

**Problem:** Every other server (CliSilentProxy, FReader, Notifier) has an `=== _meta PARAMS ===` section explaining that the LLM can pass `_meta:{canNotify:true}` to opt in to desktop notifications when a query takes longer than `AutoNotifyThresholdMs`. Rowster's `GetInstructions()` ends with just `=== TIMEOUT ===` and no `_meta` section. Without it, an LLM using Rowster will never know to opt in for notifications on slow queries.

**Fix:** Add the following at the end of the `GetInstructions()` method, before `return new { _h = instr.ToArray() }`:

```csharp
instr.Add("=== _meta PARAMS (any tool call) ===");
instr.Add("Pass _meta in the tool call params object to opt in to advanced features:");
instr.Add("  _meta:{canNotify:true}   — server injects _shouldNotify:true when threshold exceeded;");
instr.Add("                             YOU then call Notifier.notify() with a human message.");
instr.Add("  _meta:{sender:\"Claude\"}  — LLM identity surfaced in notifications.");
instr.Add("  _meta:{project:\"name\"}   — project name surfaced in notifications.");
instr.Add("  _meta:{progressToken:N}  — enables $/progress notifications during long ops.");
instr.Add("Without canNotify the server fires a generic auto-notification (no action needed).");
instr.Add("");
```

**Test:** After fixing, add a test to `src/McpServers.Tests/RowsterTests.cs` named `GetInstructions_Contains_MetaSection` that calls `GetInstructions()` and asserts `Assert.Contains("_meta", text)` and `Assert.Contains("canNotify", text)`. Run all tests.

**Edge case:** Make sure the section is indented/aligned consistently with the other servers. Copy the exact wording from `NotifierServer.cs` (line ~400+) to stay consistent.

---

### A2. Rowster: `timeoutMs` marked `required` in schema but instructions say "defaults to 60000"

**File:** `src/Rowster/McpServer.cs` — `RegisterTools()` method

**Problem:** Every tool's `InputSchema` has `required = new[] { ..., "timeoutMs" }`. But `GetInstructions()` says "timeoutMs defaults to 60000." This contradiction means:
- Claude may try to validate the schema and complain the parameter is missing if the LLM doesn't supply it
- Or Claude will supply it anyway because it sees `required` — which is fine, but then the instruction "defaults to 60000" is misleading

Look at `McpServerBase.HandleCore()` to understand how `timeoutMs` is actually handled when missing. If the base class truly defaults it to 60000 when omitted, then `required` should be removed from the schema. If omitting it actually causes an error, then the instructions are wrong.

**Steps to diagnose:**
1. Open `src/Commons/McpServerBase.cs`
2. Search for `RequiresTimeoutMs` and `DefaultTimeoutMs`
3. Check whether Rowster overrides `DefaultTimeoutMs` (look for an override in `McpServer.cs` — if absent, check the base class default)
4. In `McpServerBase.HandleCore()`, find the code path that validates `timeoutMs`

**Expected finding:** `McpServerBase` already handles the case where `timeoutMs` is absent by using `DefaultTimeoutMs()`. Rowster does NOT override `DefaultTimeoutMs`, which means it uses the base class default.

**Fix (after diagnosis confirms):** Remove `"timeoutMs"` from the `required` array in each tool's schema in `RegisterTools()`. The `query`, `count`, `sample`, `list_tables`, `describe_table`, `list_databases`, and `connect` tools all list it as required. **Do not remove it from `ping`'s schema** — ping already has an empty required array.

**Test:** Write a test `Query_WithoutTimeoutMs_UsesDefault` that calls `query` without supplying `timeoutMs` and verifies it doesn't return a "timeoutMs required" error.

**Edge case:** After removing `required`, verify the tool call still receives a timeout internally. If a query runs forever without a timeout, that's a regression.

---

### A3. CliSilentProxy: Success path discards stdout — no way for LLM to read command output

**File:** `src/CliSilentProxy/CliSilentProxyServer.cs`

**Problem:** On success (exit code 0), CliSilentProxy returns only `{_s:'ok', _exit:0, _ms:N, _cmd:'...'}` and the stdout log is discarded. The `_parsed` field is returned for known build/test tools, but for anything else (e.g., `git status`, `git log --oneline`, `ls`, `echo`, `dotnet --version`) the LLM gets nothing back.

This means an LLM **cannot use CliSilentProxy to read output from arbitrary commands**. This is a significant design gap. Right now, `get_instructions` says this but doesn't emphasize it enough: an LLM accustomed to Bash (which always returns stdout) will call `run("git", ["log", "--oneline", "-5"])` and wonder why it gets `{_s:'ok'}` with no content.

**Investigation steps:**
1. Read the `HandleRun()` method fully
2. Confirm: is there any way to opt in to capturing stdout on success? (Look for a `captureOutput` or `returnStdout` parameter)
3. Check what `_parsed` covers (the output parser list is in `GetInstructions()`) — note the specific tools it handles

**Fix options (choose one with your lead):**
- **Option A (Recommended):** Add a `captureOutput: bool` parameter to `run()`. When `true`, include `_stdout` in the success response (compressed through the same pipeline). Update the schema and instructions. This is the safest, most explicit option.
- **Option B:** Add a sentence to `GetInstructions()` under `BEST PRACTICES` that explicitly warns: "CliSilentProxy.run() DISCARDS stdout on success. Do NOT use it when you need to read command output. Use FReader.read() or the built-in Read tool for file reads, and use Bash for commands where you need to inspect the output."

**Test for Option A:** `Run_WithCaptureOutput_ReturnsStdout` — run `echo hello` with `captureOutput:true`, assert `_stdout` contains `"hello"`.  
**Test for Option B:** No code test needed, but add a test `GetInstructions_WarnsMissingStdoutOnSuccess` that checks the instructions contain "discards stdout" or equivalent.

**Important edge case:** If you implement Option A, the `_stdout` content should still go through the compression pipeline (strip ANSI, collapse duplicates, etc.). Do NOT bypass compression or you'll regress the token-efficiency guarantee.

---

### A4. FReader: Verify `_meta PARAMS` section is present

**File:** `src/FReader/FReaderServer.cs` — `GetInstructions()` method

**Action:** Read the `GetInstructions()` output. Confirm it contains `=== _meta PARAMS ===` and `canNotify`. If missing, add it the same way as A1.

Check the test `FReaderServerTests.GetInstructions_ReturnsInstructions` — does it assert `_meta` is present? If not, add that assertion.

---

## Category B: LLM Ergonomics Gaps (Should Fix)

These won't crash anything but will cause LLMs to use the tools less effectively or need more prompting.

### B1. Rowster: No "common workflow" example in GetInstructions

**Problem:** An LLM that has never used Rowster before needs to understand the connect → query workflow. The current instructions say "Call connect(connection) once — all subsequent calls reuse the same connection" but there's no example showing what a typical session looks like.

**Fix:** Add this section to `GetInstructions()` before `=== TIMEOUT ===`:

```csharp
instr.Add("=== QUICK START EXAMPLE ===");
instr.Add("1. connect(\"Server=HOST;Database=DB;User ID=U;Password=P;ConvertZeroDateTime=True\", timeoutMs:5000)");
instr.Add("   → {\"_s\":\"connected\"}");
instr.Add("2. list_tables(timeoutMs:5000)");
instr.Add("   → [\"users\", \"orders\", \"products\"]");
instr.Add("3. describe_table(\"orders\", timeoutMs:5000)");
instr.Add("   → {_h:[\"_f\",\"_t\",\"_n\",\"_k\",\"_d\",\"_x\"], _r:[[\"id\",\"int\",\"NO\",\"PRI\",null,\"auto_increment\"],...]}");
instr.Add("4. query(\"SELECT * FROM orders WHERE status='pending' LIMIT 10\", timeoutMs:10000)");
instr.Add("   → {_h:[\"id\",\"user_id\",\"total\"], _r:[[1,42,99.99],[2,17,14.50],...]}");
instr.Add("5. count(\"orders\", where:\"status='pending'\", timeoutMs:5000)");
instr.Add("   → {\"_cnt\":247}");
instr.Add("");
```

### B2. Notifier: `notify_on_complete` fire-and-forget not clearly explained

**Problem:** `notify_on_complete` returns immediately with `{_pid:12345, _status:"running"}`. The process runs independently. The LLM has no way to poll for completion, check the exit code, or cancel it. This is correct behavior but an LLM might try to check status afterward and get confused.

**Fix:** Add to the `=== TOOLS ===` section under `notify_on_complete`:

```
WARNING: This is fire-and-forget. After calling it, you CANNOT check if the process
succeeded or failed. If you need the exit code or output, use CliSilentProxy.run()
instead, which blocks until completion and returns structured results.
Use notify_on_complete only for processes you want to launch independently (e.g.,
a background script you don't need to monitor from this session).
```

### B3. All servers: Announcement directives don't mention `get_instructions`

**Problem:** The `AnnouncementDirective` is the first text an LLM sees about each server (it appears in tool descriptions). Currently it says things like "PREFER over built-in Bash" and "saves 90-97% tokens" — good selling points, but doesn't tell the LLM it needs to call `get_instructions` first.

**Check:** Open each server's `McpServerConfig` and read the `AnnouncementDirective`. Confirm each one ends with something like: "Call get_instructions first." If any are missing this, add it.

For example, Rowster's directive:
```
"PREFER over mysql.exe/Bash: ... Call get_instructions first."
```

### B4. CliSilentProxy: `get_log` instructions need "when" emphasis

**Problem:** The instructions describe `get_log(id)` but don't stress that `id` comes from the `_id` field of a failed `run()` response. An LLM that hasn't read carefully might try to call `get_log` with random values or forget to extract `_id`.

**Fix:** In `GetInstructions()`, change the `get_log` description to:

```
get_log(id, raw?=false)
  Retrieve full log ONLY for a failed run (failure sets _id in the response).
  id: copy directly from _id field of the failed run() response.
  raw=false (default): compressed log. raw=true: original uncompressed output.
  Only the last 10 failures are retained; older logs are discarded.
  DO NOT call get_log after a successful run() — no log is stored for success.
```

---

## Category C: End-to-End LLM Integration Testing

The integration tests in `McpServers.Tests` verify protocol correctness in isolation. They do NOT verify that a real LLM will use the tools correctly. This category tests the actual human-LLM-tool interaction.

### C1. Claude Code smoke test — CliSilentProxy

Start a Claude Code session with CliSilentProxy connected. Run the following prompts and record observations:

**Prompt 1:** "Please run `echo hello world` and tell me the result."  
**Expected behavior:**  
- Claude calls `get_instructions` first (verify in tool call log)  
- Claude calls `run` with `command:"echo"`, `args:["hello", "world"]`, and a reasonable `timeoutMs`  
- Claude receives `{_s:'ok', ...}` with no stdout content  
- Claude should ideally note that stdout wasn't returned (this surfaces the A3 gap)

**What to record:** Did Claude call get_instructions? Did it provide timeoutMs? Did it seem confused by the empty success response?

**Prompt 2:** "Run `dotnet build` in `D:/work/nyingi/code/systems/llm-utilities` and tell me if there are any errors."  
**Expected behavior:**  
- Claude calls `run` with the dotnet build command  
- On success, `_parsed` should contain `{succeeded:true, error_count:0, ...}`  
- Claude should read `_parsed` rather than complaining about missing output

**What to record:** Did `_parsed` appear? Did Claude use it correctly?

**Prompt 3 (failure path):** "Run `dotnet build NonExistentProject.csproj` in that folder."  
**Expected behavior:**  
- Run fails with nonzero exit  
- Response contains `_id`, `_tail`, and `_s:"fail"`  
- Claude calls `get_log(_id)` if the tail was insufficient  
- Claude correctly reports the error

**What to record:** Did Claude extract `_id` and call `get_log`? Did it understand `_tail`?

---

### C2. Claude Code smoke test — Rowster

**Prerequisites:** You need a MySQL server running locally. If you don't have one, use Docker:
```powershell
docker run -d --name mysql-test -e MYSQL_ROOT_PASSWORD=test -e MYSQL_DATABASE=testdb -p 3306:3306 mysql:8
```

**Prompt 1:** "Connect to MySQL at 127.0.0.1 using root/test and list the databases."  
**Expected behavior:**  
- Claude calls `get_instructions`  
- Claude calls `connect` with the correct connection string (including `ConvertZeroDateTime=True` — check this!)  
- Claude calls `list_databases`  
- Claude receives a list of database names

**Red flag:** If Claude omits `ConvertZeroDateTime=True` from the connection string, the instructions are not clear enough. That instruction exists because MySQL's zero-date (`0000-00-00`) causes an exception in MySqlConnector without this flag. Add a stronger warning if it's missing.

**Prompt 2:** "How many tables are in the testdb database?"  
**Expected behavior:**  
- Claude calls `list_tables` (not `query("SHOW TABLES")`)  
- Claude returns the count

**What to record:** Did Claude use `list_tables` or fall back to raw SQL? Token-saving tool selection is the whole point of Rowster.

**Prompt 3:** "Query the first 5 rows of any table in testdb."  
**Expected behavior:**  
- Claude uses `sample(table, n:5)` — NOT `query("SELECT * FROM table LIMIT 5")`  
- Response has compact `{_h:..., _r:...}` format  
- Claude correctly interprets `_h` as column headers and `_r` as rows

**Red flag:** If Claude uses `query("SELECT * LIMIT 5")` instead of `sample()`, the token-saving instructions aren't landing. Improve the `=== TOKEN-SAVING TIPS ===` section.

---

### C3. Claude Code smoke test — Notifier

**Prompt 1:** "Send me a desktop notification saying 'Test notification' from Claude."  
**Expected behavior:**  
- Claude calls `get_instructions`  
- Claude calls `notify` with `title:"Test notification"`, `message: something useful`, `sender:"Claude"`  
- A real desktop toast appears on screen  
- Response contains `_sent:true`

**Red flags:**  
- `_sent:false` — notification failed silently; check platform support and NotifierHelper binary
- Claude doesn't set `sender` — instructions say always set sender so the user knows which LLM sent it
- Claude sends a generic message like "Tool execution finished" — instructions explicitly forbid this

**Prompt 2:** "Run a 10-second sleep command and notify me when it's done."  
**Expected behavior:**  
- Claude calls `CliSilentProxy.run("timeout", ["/t", "10"])` (Windows) with `_meta:{canNotify:true}`  
- After the command, response contains `_shouldNotify:true`  
- Claude calls `Notifier.notify()` with a human-readable completion message  
- Do NOT accept Claude calling `notify_on_complete` here — that's for when Claude doesn't need the exit code

**What to record:** Did `_meta:{canNotify:true}` appear in the tool call? Did `_shouldNotify:true` appear in the response? Did Claude follow up with a notify call?

---

### C4. Claude Code smoke test — FReader

**Prompt 1:** "What does the `OperationTracker` class in `D:/work/nyingi/code/systems/res-mon/src/ResMon/Internal/OperationTracker.cs` do?"  
**Expected behavior:**  
- Claude calls `FReader.get_instructions` first  
- Claude calls `FReader.summarize(path)` — NOT `FReader.read(path)` directly  
- Claude then drills in with `read_function` if needed  
- Uses significantly fewer tokens than reading the whole file

**Red flag:** Claude using the built-in `Read` tool instead of `FReader`. If this happens, the `AnnouncementDirective` and `HarnessInstructions` aren't compelling enough.

**Prompt 2:** "Find all methods that call `_tracker.Record(` in the llm-utilities codebase."  
**Expected behavior:**  
- Claude uses `FReader.grep` (cross-file search) — NOT the built-in `Grep` tool  
- Results come back with file paths and line numbers

---

## Edge Cases — Things Junior Devs Commonly Miss

### EC1. The `_meta` field is stripped from tool inputs in McpServerBase
The `_meta` object in tool call params is extracted and removed by `McpServerBase` before dispatching to `HandleToolCall()`. **Do not** try to read `_meta` inside any server's `HandleToolCall()` — it won't be there. All `_meta` processing happens in the base class. If you're adding `_meta` to a new feature, look at how the base class handles it.

### EC2. Notification tests don't actually send notifications
`NotifierTests.Notify_ValidPayload_ReturnsResponseWithSentField` does NOT verify a real desktop toast appeared — it checks `_sent` in the JSON response. `_sent` can be `false` if the notification system is unavailable (CI, headless environment). The test should pass even when `_sent:false`, because the server handled the call correctly. Do not change the test to require `_sent:true`.

### EC3. Rowster tests use a real network timeout
`RowsterTests.Query_WithInvalidConnection_ReturnsToolError_NotCrash` connects to `127.0.0.1:9999` with `ConnectionTimeout=1`. This test takes ~2 seconds because it waits for the TCP connection to time out. This is intentional — it's proving the server doesn't crash on a real connection failure. Do not mock the MySQL driver; that would defeat the purpose.

### EC4. CliSilentProxy `run` on Windows uses different commands than Linux/macOS
The integration test `GetInstructions_RespondsImmediately_WhileSlowToolRunning` uses `ping -n 3 127.0.0.1` (Windows) to simulate a 2-second command. If you're on Linux/macOS, this test may fail or behave differently. Check for platform guards when adding new tests that run real commands.

### EC5. Process.Threads.Count allocates on every call
`ProcessSampler.Sample()` calls `process.Threads.Count` which internally creates a `ProcessThreadCollection` snapshot every call. This was flagged in the original audit as an allocation issue. It's not fixed yet and is not part of this work item, but be aware that if you add any test that calls `Sample()` in a loop, you may see GC pressure.

### EC6. TimerLoop.DisposeAsync is not idempotent
Calling `DisposeAsync()` twice on a `TimerLoop` instance will throw `ObjectDisposedException` on the second call (from `_cts.Dispose()`). This is a known issue in the res-mon project (not llm-utilities). Do not confuse the two codebases. In llm-utilities, `TimerLoop` does not exist.

### EC7. `BlockingPipeWriter` has a bounded capacity of 256
In `McpTestClient.cs`, `BlockingPipeWriter` has `boundedCapacity: 256`. If a test writes more than 256 messages without reading them, `_q.Add(value)` will block. This shouldn't happen in any current test, but if you add a test that fires many requests without reading responses, it will deadlock.

### EC8. McpServerBase `Run()` blocks until stdin closes
The server's `Run(TextReader input, TextWriter output)` loop runs `while (input.ReadLine() != null)`. It only exits when the reader returns null. In tests, `McpTestClient.Dispose()` calls `_serverInput.Close()` which enqueues `null`. If your test disposes the client before the server thread has processed all queued messages, you may get spurious test failures. Always read all expected responses before disposing.

---

## Verification Checklist

Before marking this work item done, verify every item:

**Code fixes:**
- [ ] A1: Rowster GetInstructions contains `_meta PARAMS` section  
- [ ] A1: Test `GetInstructions_Contains_MetaSection` added and passes  
- [ ] A2: Diagnosis of `timeoutMs` required vs optional completed and documented  
- [ ] A2: Fix applied (schema or instructions corrected, not both contradicting)  
- [ ] A2: Test covering timeoutMs behavior added and passes  
- [ ] A3: Decision made on captureOutput (Option A or B), documented, implemented  
- [ ] A3: Test added for chosen option  
- [ ] A4: FReader GetInstructions confirmed to have `_meta` section  
- [ ] All 116 existing tests still pass after all changes  

**Ergonomics fixes:**
- [ ] B1: Rowster quick-start example added  
- [ ] B2: notify_on_complete fire-and-forget warning added  
- [ ] B3: All server AnnouncementDirectives mention `get_instructions`  
- [ ] B4: CliSilentProxy `get_log` description clarified  

**LLM integration tests:**
- [ ] C1: CliSilentProxy smoke test completed, observations recorded  
- [ ] C2: Rowster smoke test completed (requires MySQL), observations recorded  
- [ ] C3: Notifier smoke test completed, toast appeared on screen  
- [ ] C4: FReader smoke test completed, observations recorded  
- [ ] Any gap surfaced during smoke tests has a follow-up task created  

**Final:**
- [ ] `git log --oneline` shows one commit per fix (not a single giant commit)  
- [ ] Each commit message starts with `fix:`, `docs:`, or `refactor:` and describes the change  
- [ ] No test is disabled, skipped, or has its assertion weakened  
- [ ] This document updated with any new findings  

---

## Best Practices for MCP Tool Design (Reference)

Keep these in mind when making any changes to tool schemas or instructions:

1. **Tool descriptions are the first line of defense.** The LLM reads tool descriptions before calling `get_instructions`. If the description doesn't say "call me first," the LLM won't. Every `InstructionsToolDescription` should start with "MANDATORY FIRST STEP."

2. **Required vs optional parameters.** Only mark a parameter `required` in the JSON schema if omitting it truly causes an error. Optional parameters with good defaults reduce friction and LLM errors. Use `required` sparingly.

3. **Error messages must guide the LLM toward the fix.** `{"_e": "No connection"}` is better than `{"error": "null reference"}`. The LLM reads the error and decides what to do next. Always tell it what to do: `{"_e": "No connection. Call connect() first."}`.

4. **Compact field names need a legend.** `_h`, `_r`, `_s`, `_e` save tokens but only if the LLM knows what they mean. The legend must be in `get_instructions` AND in the tool description for any tool that returns compact fields (in case `get_instructions` wasn't called). Put the legend in both places.

5. **Idempotency.** Tool calls should be safe to retry. If a `connect()` call is retried with the same connection string, it should succeed silently — not throw "already connected." Check every mutating tool for idempotency.

6. **Never return HTML, ANSI codes, or escape sequences in tool responses.** These waste tokens and confuse the LLM. CliSilentProxy's compression pipeline strips them from captured output; make sure any new server that captures subprocess output does the same.

7. **Timeouts must be finite.** A tool that can hang indefinitely will stall the entire server (before the async dispatch fix) or stall the worker indefinitely (even after). Every tool that calls external systems (DB, shell, network) must have a timeout, and that timeout must have a maximum cap. Never trust the LLM to supply a sane timeout — use `Math.Min(timeoutMs, MAX_TIMEOUT)`.

8. **Don't crash on bad input.** An LLM will occasionally pass `null`, empty strings, or wrong types. Every tool should validate input and return a structured `{_e: "field X is required"}` rather than throwing a `NullReferenceException` that becomes a generic protocol error.

---

## Questions / Escalation

If you find something that doesn't match what's described here (e.g., a test file is missing, a method doesn't exist, behavior is different from what's documented), stop and ask. Do not guess. Do not fix a different thing than what's described. The worst outcome is a fix that looks right locally but causes a regression in a server that's already working.

Contact: Check the git log for the most recent committer — they have full context on all changes made.
