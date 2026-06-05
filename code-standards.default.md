# Code Quality Standards

Universal best practices for all languages and projects.

---

## 1. DRY — Don't Repeat Yourself

Every piece of knowledge must have a single, unambiguous representation in the system.

- Extract duplicate logic into functions, methods, classes, or modules.
- If you see copy-paste, create an abstraction.
- If a pattern appears 3+ times, it should be a shared utility.
- Use loops, generics, templates, or macros instead of manual repetition.

**Bad:** Copy-pasting the same SQL query with different table names.
**Good:** A parameterized helper function or repository method.

---

## 2. Single Responsibility Principle (SRP)

Each function, class, module, or file should do exactly one thing and do it well.

- A function should fit on one screen (typically ≤40 lines). If longer, extract sub-functions.
- A class should have one reason to change.
- A file should contain one primary type or module.
- Mixing data access, UI rendering, and business logic in one function is a violation.

**Bad:** A function that fetches data, transforms it, formats HTML, and writes to a file.
**Good:** Separate fetch, transform, render, and write into distinct units.

---

## 3. Naming Conventions

Names communicate intent. Choose clarity over brevity.

- **Variables:** descriptive nouns (`userCount`, not `uc` or `x`)
- **Functions:** verb phrases (`fetchUser()`, not `get_user_data` — be consistent with project style)
- **Classes/Interfaces:** noun phrases (`UserRepository`, `IDataStore`)
- **Booleans:** predicate forms (`isActive`, `hasPermission`, `shouldRetry`)
- **Constants:** `UPPER_SNAKE_CASE` or `PascalCase` per project conventions
- **Abbreviations:** avoid them unless universally understood (`id`, `url`, `html`)
- **Consistency:** follow the existing project naming pattern — do not mix `camelCase`, `snake_case`, and `PascalCase` arbitrarily

---

## 4. Code Organization

Structure code for readability and discoverability.

- Group related functionality together (by feature, not by type).
- Files should be small — ≤500 lines for most languages. Split large files.
- Import/using/include statements should be organized (standard lib, third-party, internal).
- Avoid deeply nested directory structures (max 3-4 levels).
- Configuration and secrets never belong in source code.

---

## 5. Error Handling

Errors are part of the API. Handle them explicitly.

- Never swallow exceptions with empty catch blocks.
- Never silently ignore error return values.
- Return structured errors, not bare strings or magic numbers.
- Fail fast for programming errors (null pointers, invalid args).
- Handle gracefully for runtime errors (network, file I/O, validation).
- Log errors with context (what failed, what input caused it).
- Clean up resources in `finally` blocks or equivalent (`using`, `defer`).

---

## 6. Testing

Test behavior, not implementation.

- Write tests alongside code.
- Test public interfaces, not private internals.
- One assertion concept per test.
- Name tests descriptively: `Feature_Scenario_ExpectedBehavior`.
- Prefer table-driven/parameterized tests over copy-paste.
- Mock external dependencies, not internal logic.
- Tests must be deterministic — no dependence on timing, random values, or network.

---

## 7. Type Safety

Let the type system do the work.

- Use specific types instead of primitive obsessions (`UserId` instead of `int`).
- Avoid `dynamic`, `any`, `object`, or untyped containers when a specific type exists.
- Prefer immutable data structures.
- Use `readonly`/`const`/`val` where values should not change.
- Nullable annotations are mandatory — do not suppress null warnings without justification.
- Pattern matching over type casting.

---

## 8. Security

Security is not optional.

- Validate all input — never trust user data, file content, or network responses.
- Use parameterized queries for databases (never string interpolation in SQL).
- Escape output appropriately for the context (HTML, JSON, SQL, shell).
- Do not log secrets, passwords, tokens, or PII.
- Use safe deserialization — validate schemas, reject unknown fields.
- Principle of least privilege: request only the permissions you need.

---

## 9. Performance

Correctness first, then performance.

- Choose the right algorithm (O(n log n) search, not O(n²)).
- Avoid premature optimization — write clear code first, profile, then optimize.
- Batch I/O operations (database queries, file reads, network calls).
- Lazy-load expensive resources.
- Cache repeated computations.
- Paginate large result sets.

---

## 10. Consistency

The most important rule: follow the existing style.

- When editing a file, match its indentation, naming style, comment style, and patterns.
- Do not refactor the entire file to your preferred style — make incremental changes that fit.
- If the project uses tabs, use tabs. If it uses 2-space indent, use 2-space.
- If the project puts braces on the same line, do the same.
- A consistent codebase is more valuable than a "correct" one that mixes styles.

---

## 11. Idiomatic Code

Write code the language expects, not a translation from another language.

- Use language-native constructs, not foreign patterns (don't write Java in Python or C# in JavaScript).
- Know the standard library — prefer built-in functions over reinventing them.
- Follow community conventions for the language (PEP 8 for Python, Effective Go for Go, etc.).
- Use language features properly: iterators, comprehensions, async/await, pattern matching.
- Do not over-engineer — a plain function is better than a class with one method.

---

## 12. Dependencies

Minimize and audit external dependencies.

- Think twice before adding a new package — can you do it with standard library?
- Pin dependency versions (no floating/latest ranges).
- Audit dependencies for known vulnerabilities.
- Prefer small, focused libraries over framework monoliths.
- Keep the dependency graph shallow.

---

## 13. Comments and Documentation

Code says *what*; comments say *why*.

- Write self-documenting code — choose descriptive names over comments.
- Comment on *why* something is done a certain way (trade-offs, edge cases, rationale).
- Do not comment on *what* the code does — the code already says that.
- Keep comments up to date with code changes.
- Document public APIs (docstrings, JSDoc, XML docs).
- Add a module/package-level overview for non-obvious components.

---

## 14. Separation of Concerns

Keep architectural layers distinct.

- Business logic should not depend on framework code.
- Data access should not leak into presentation.
- Configuration should be externalized, not hardcoded.
- Side effects (I/O, network, file system) should be isolated from pure logic.
- Use dependency injection to decouple components.

---

## 15. Immutable by Default

Prefer immutability where practical.

- Do not mutate function parameters.
- Return new objects instead of modifying inputs.
- Mark fields as `readonly`/`final`/`const` unless mutation is required.
- Pure functions (no side effects, same output for same input) are easier to test and reason about.
- Copy-on-write for collections when the context expects immutability.

---

## Enforcement

This document is enforced by the `ComplianceKit` MCP server available in your tool list.

- Call `get_instructions` first (mandatory before any code change).
- Use `find_candidates(description)` before writing new code to check for existing abstractions.
- Use `audit_file(path)` after writing or modifying a file.
- Use `audit_diff(diff)` before applying a change.

The server does not block your actions, but violations are reported.
