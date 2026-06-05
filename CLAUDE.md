# Code Quality Standards

This project follows the code quality standards defined in [`code-standards.md`](./code-standards.md).

## Mandatory first step

Before writing any code, you MUST call `get_instructions` on the `ComplianceKit` MCP server. It returns the complete code quality standards for this project.

Key rules:
- **DRY** — do not duplicate existing abstractions. Use `find_candidates(description)` to check what already exists.
- **SRP** — each function/class does one thing.
- **Consistency** — match the surrounding code style exactly.
- **Audit** — after writing, call `audit_file(path)` to check for violations.

The `ComplianceKit` server is registered in your MCP tool list. Use it.
