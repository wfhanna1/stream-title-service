# CLAUDE.md — stream-title-service

Project-local instructions for Claude Code when working in this repository. These extend the global `~/.claude/CLAUDE.md` and the parent `/Users/wasimhanna/Code/CLAUDE.md`. Project-local instructions take precedence when they conflict with the parent.

---

## Design Standard: SOLID + DRY + OOP + 12-factor

Every non-trivial design in this repository is evaluated against **four lenses**: SOLID (all five principles), DRY, OOP, and the 12-factor app methodology. Each design document under `docs/superpowers/specs/` must contain its own "Design Analysis" section that walks all four lenses explicitly. A lens that is genuinely N/A for a given design is called out with one sentence of justification — it is never skipped silently.

### S — Single Responsibility
For every new or modified class, write its one reason to change in one sentence. If the sentence needs "and" or "also", split the class.

### O — Open/Closed
Identify the likely future extension points (new platform, new event type, new publisher key, new retry policy). If extending requires editing existing code rather than adding a new class/strategy/handler, redesign.

### L — Liskov Substitution
For every interface or base type, list its implementations. Verify each accepts the same input set, throws no new exception types the base did not document, and preserves invariants. Subclasses cannot strengthen preconditions or weaken postconditions.

### I — Interface Segregation
For every interface with more than ~3 methods, ask whether all consumers use all methods. Split if not. Many small role interfaces beat one fat interface.

### D — Dependency Inversion
For every dependency a class has, verify it's an abstraction injected at composition root, not a concrete `new`, static call, or inline `Environment.GetEnvironmentVariable`. Configuration, clock, randomness, I/O, network — all come through abstractions.

### DRY
Grep for the concept being added (a key name, a config mechanism, a value, a contract, a schema, an alert keyword). If it lives somewhere, reference it; do not re-invent. Document the reuse in the spec. Conceptual duplication (same business rule in two systems, two config mechanisms in one codebase) is more dangerous than copy-paste — check for it.

### OOP
- Responsibilities encapsulated behind well-named classes with private state and public behavior.
- Mutable shared values hidden behind methods, not exposed as public fields.
- Value-like things modeled as immutable `record` / `struct` types, not bags of properties.
- Composition preferred over inheritance. Inheritance is reserved for true is-a relationships.

### 12-factor (load-bearing factors for this codebase)
- **III Config.** Tunables (timeouts, retry counts, backoff schedules, endpoints, feature flags) come from environment via `IConfiguration` / `IOptions<T>`. Never hardcoded constants in business logic. Never inline `Environment.GetEnvironmentVariable` inside handlers.
- **IV Backing services.** External dependencies (Restream, Service Bus, Key Vault, App Insights) are attached resources reached through abstractions; they swap by config, not by code change.
- **VI Processes.** Functions are stateless between invocations. No in-memory caches that assume the same process handles the next message.
- **IX Disposability.** Handlers are crash-safe and fast to start. Long-running retries inside a single invocation must respect the Service Bus message lock duration (5 minutes default) with budget to spare.
- **XI Logs.** Emit structured events to stdout / App Insights via `ILogger`. Do not write to local files or rotate logs in-process.

---

## Branching: Always Use Feature Branches

**Never commit directly to `main`. Never merge straight into `main`.** Every change ships through a feature branch and a pull request.

- Branch naming: `feature/`, `fix/`, `chore/`, `docs/`, `refactor/`, or `test/` prefix.
- Before any `git add && git commit`, run `git branch --show-current`. If the output is `main`, stop and run `git checkout -b <branch-name>` first. No exceptions.
- Merges into `main` happen via pull request after CI passes. Direct `git merge` into `main` from a local feature branch is not allowed even when it would fast-forward — the PR is the audit trail.
- Pull requests must satisfy the project pre-merge gates before they can be merged: CI green (build, test, lint, typecheck), code review approved, linked spec under `docs/superpowers/specs/`, acceptance criteria documented in the PR body, branch up-to-date with `main`.
- Rollout / deployment sections in any spec start *after* "merge approved PR" — never with "merge PR" as step 1.

---

## TDD Discipline

Strict TDD per the global rule: RED → GREEN → REFACTOR, **per test, not per batch**. One failing test at a time, confirm it fails for the expected reason, write the minimum code to flip *that one test* green, then refactor (or write the one-sentence "no refactor needed because…" justification), then write the next test. Plans must include refactor as its own explicit checkbox in every cycle — never as a parenthetical.

---

## Standing Build Quality Gates (run before pushing)

Before pushing a branch or opening a PR, run every check that CI runs:

```
dotnet build --warnaserror
dotnet test
dotnet format --verify-no-changes
```

If the workflow file under `.github/workflows/` adds further checks (security scan, coverage threshold, integration tests), run those too. Push only when all gates pass locally.
