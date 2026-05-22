# LLM Utilities

A suite of MCP (Model Context Protocol) servers for LLM-powered coding assistants. Each server exposes a JSON-RPC 2.0 interface over stdio, providing structured, token-efficient access to databases, filesystems, code analysis, and CLI tooling.

## Servers

| Server | Purpose |
|---|---|
| **Rowster** | MySQL database queries and schema inspection |
| **FReader** | Token-efficient file reading with AST-level extraction |
| **CliSilentProxy** | CLI tool execution with structured output parsing |
| **FWriter** | File creation and function-level code editing |
| **ContractGenerator** | C# to TypeScript contract generation |
| **CodeNavigator** | Semantic code navigation via compiler APIs |
| **McpRegistrar** | MCP server registration across LLM configs |

A shared **Commons** library provides JSON-RPC infrastructure, timeout enforcement, progress reporting, and self-improvement tracking.

## Installation

Run the Windows installer (`LLMUtilitiesSetup.exe`) built with Inno Setup. The installer registers all servers into the MCP configuration files for Claude Code, Google Gemini CLI, and OpenCode. Individual servers can be deselected during installation.

### Building

```powershell
.\build.ps1
```

Publishes all servers to `publish/` and produces the installer at `installer/LLMUtilitiesSetup.exe`. Requires .NET 9 SDK and optionally Inno Setup 6 for the installer build.

## Server Details

### Rowster — MySQL Database Access

Provides MySQL operations: connect, query, count, sample, describe table, list tables/databases, and ping.

**Key features:**
- Columnar and pipe-table output formats (`format: "json"` or `format: "table"`)
- Automatic row limiting (`maxRows`, default 200) with truncation signal
- Persistent default connection via `connect` tool
- Supports `ConvertZeroDateTime=True` for MySQL zero-date compatibility

### FReader — File Reading

Token-efficient file reading via AST-level extraction for C# and TypeScript/JavaScript, structural summaries, and regex search.

**Key features:**
- `summarize` — namespace, class hierarchy, method signatures (87-96% token savings)
- `read_function` / `read_functions` — target specific functions without reading entire files
- `search_function` — locate function definitions across files
- `grep_in_file` — regex search within a single file
- Read truncation with `_truncated`/`_total`/`_next` continuation signals

### CliSilentProxy — CLI Execution

Executes arbitrary commands with structured output parsing for 11 common tools. Replaces raw process execution with token-optimized responses.

**Key features:**
- Built-in parsers: `dotnet build`, `dotnet test`, `flutter/dart analyze`, `tsc`, `eslint`, `ruff`, test runners (jest/vitest/pytest/cargo/go/flutter), `cargo build/clippy`, `npm audit`, `mypy`, `go vet`
- Structured `_parsed` results with issue counts, locations, and error codes
- `surfaceWarnings` — compact warning digest on successful commands
- `maxOutputTokens` — intelligent failure log truncation
- `extractPattern` — .NET regex extraction with flavor validation (rejects Python `(?P<name>...)`)
- `extraPaths` — PATH augmentation for version-manager tool resolution

### FWriter — File Writing

File creation and function-level code editing with integrated syntax validation.

**Key features:**
- `create_file` — write files with optional AST syntax validation (`skipValidation` escape hatch)
- `edit_function_body` — replace function bodies by name, with backup/rollback on failure
- Supports C# via Roslyn, with extensible `IWriteProvider` interface

### ContractGenerator — C# to TypeScript Contracts

Roslyn-based C# to TypeScript type translation. Generates TypeScript interfaces, enums, and type aliases from C# source files.

**Key features:**
- `generate` — translate a single C# file to TypeScript
- `generate_batch` — translate multiple files in one call
- `sync` — incremental sync that only updates changed files
- Deterministic output — same input always produces same output

### CodeNavigator — Semantic Code Navigation

Compiler API-based code navigation using Roslyn for C# and the TypeScript compiler API for TS/JS, with zero LSP dependency.

**Key features:**
- C#: `symbols`, `definition`, `hover`, `references`, `workspace_symbols`, `implementations`, `incoming_calls`, `outgoing_calls`
- TS/JS: `symbols`, `definition`, `hover`, `references` via Node.js worker
- Cross-file workspace analysis via `WorkspaceAnalyzer` (auto-discovers `.sln`/`.csproj` root)
- Compact response format — ~15 tokens per definition, ~40 tokens per reference set

### McpRegistrar — Configuration Management

Manages MCP server registration across LLM tool configuration files. Reads, validates, and updates JSON configurations for Claude Code, Google Gemini CLI, and OpenCode.

**Key features:**
- Register/unregister individual servers
- Backup before write, validate after write, rollback on failure
- Supports `%APPDATA%`, `%USERPROFILE%`, and `%ProgramData%` config locations

## Architecture

All servers share a common base (`Commons/LLMUtilities.Commons.dll`) that provides:

- **JSON-RPC 2.0** over stdio with compact field names
- **Timeout enforcement** — `timeoutMs` parameter on every hanging tool, server-side cancellation via `CancellationTokenSource`
- **Progress reporting** — `$/progress` notifications via `_meta.progressToken`
- **Self-improvement** — every 100th tool call injects an optional `_brs_request` for benchmark data collection
- **Resiliency guidance** — standardized retry/blacklist instructions in `get_instructions`
- **Version centralization** — single `<Version>` in `.csproj`, read via `GetEntryVersion()`

## Conventions

- Tool names use `snake_case`
- Response field names use compact underscore prefixes (`_h`, `_r`, `_cnt`, etc.)
- Errors use `error` and `error_code` fields (no underscore prefix)
- All hanging tools require a `timeoutMs` parameter
- Instructions tool (`get_instructions`) returns field mappings plus per-server conventions

## Requirements

- .NET 9 runtime (framework-dependent publish)
- Windows 10/11 (primary target; cross-platform possible with Mono)
- MySQL 5.7+ for Rowster
- Node.js 18+ for TypeScript/JavaScript features in FReader and CodeNavigator
