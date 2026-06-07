# Restream verify-and-retry with forensic logging — design

**Status:** Drafted 2026-06-05
**Author:** Wasim Hanna (with Claude)
**Component:** `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs`
**Related incident:** Friday 2026-06-05 23:01 UTC — Arabic Bible Study went live; `StreamTitleFunction` completed successfully with two `200 OK` PATCH responses from Restream, but `GET /v2/user/channel-meta/{id}` for both channels (16826484, 16826413) returned the previous event's title (`Sunday, May 31, 2026 - Kneeling Prayers/Sagda`) rather than the expected `Friday, June 05, 2026 - Arabic Bible Study`. Restream silently dropped both writes despite returning success.

---

## 1. Scope and intent

Add an in-process verification + retry loop inside `RestreamClient.SetTitleAsync` so that:

1. When Restream silently drops a PATCH, we detect it within seconds rather than next time a human notices the wrong title on YouTube or Facebook.
2. The forensic record we leave behind is rich enough to root-cause the Restream behavior over time — frequency, time-of-day, channel skew, request-id correlation (via Cloudflare `cf-ray`), and content versioning (via `etag` divergence).
3. The function dead-letters on unrecoverable failure so the existing `stream-title-deadletter-alert` (metric alert on `DeadletteredMessages > 0` in resource group `livestream-platform-rg`) pages immediately, and *also* writes an Error log containing the literal token `StreamTitleFailed` so the existing `stream-title-failed-alert` (log query alert) fires as a redundant signal.

### Non-goals

- We do not switch Restream endpoints, change OAuth scopes, or bypass Restream for direct YouTube Data API / Facebook Graph calls. If a pattern emerges over the next ~30 days suggesting the channel-meta endpoint is the wrong contract, that is a separate design.
- We do not change the Service Bus subscription filter, topic, or message schema.
- We do not change cross-service trace propagation. The known trace-correlation gap at the consumer (see prior spec `2026-04-29-sb-trace-context-consumer-design.md`) is orthogonal.

---

## 2. Behavior

For each enabled Restream channel returned by `GET /v2/user/channel/all`, `RestreamClient.SetTitleAsync` runs the following loop instead of a single PATCH:

```
attempts = 0
while attempts < MaxAttempts:        // default 3, from RestreamOptions
    attempts += 1
    patch_response = PATCH /v2/user/channel-meta/{id}  body={"title": expectedTitle}
    record per-attempt: patch_status, patch_body, patch_cf_ray, patch_etag,
                        patch_started_at, patch_finished_at

    if patch_response not 2xx:
        // existing behavior preserved: count channel as failed, log warning, exit loop
        break with channel-level non-2xx failure

    wait InitialVerifyWaitSeconds          // default 5

    get_response = GET /v2/user/channel-meta/{id}
    record per-attempt: get_status, get_body_title, get_cf_ray, get_etag,
                        get_finished_at, elapsed_ms_patch_to_verify

    if get_response.title == expectedTitle:
        emit Information "VerifiedChannel channel={ChannelId} attempts={Attempt}"
        break with channel-level success

    // mismatch — retry after backoff
    if attempts < MaxAttempts:
        wait VerifyBackoffSeconds[attempts - 1]   // default [5, 10, 20]

if loop exhausted without verification:
    emit Error "StreamTitleFailed: RestreamVerificationExhausted channel={ChannelId} expected={Expected} actual={FinalActual} attempts={AttemptCount}"
    with all forensic fields from §3 in structured customDimensions
    count channel as failed in TitleUpdateResult
```

After all enabled channels are processed:

- **All channels verified → success.** `StreamTitleFunction` returns normally; SB message is completed by the host.
- **Any channel failed verification (or had non-2xx PATCH).** The function throws (or explicitly calls `messageActions.DeadLetterMessageAsync` — chosen during implementation per existing function shape) with reason `RestreamVerificationFailed channels=<comma-list>`. The Service Bus subscription places the message into the dead-letter queue, triggering `stream-title-deadletter-alert`.

### Why "any channel failure dead-letters the whole message"

Both Restream channels are mirror destinations for the same parish broadcast. Title drift between YouTube and Facebook is unacceptable to the business. Treating partial success as overall success would let one of the two destinations stay stale until a human notices.

### Wall-clock budget vs Service Bus lock

Worst-case time inside `SetTitleAsync` per channel:
- 3 PATCH+GET round trips (~2s each observed today) ≈ 6s
- Initial wait + 2 backoffs = 5 + 5 + 10 + 20 = 40s

So per-channel worst case ~46s. With 2 enabled channels run sequentially, ~92s. The Service Bus message lock duration on the `stream-title-service` subscription is 5 minutes (confirmed from `Trigger Details` in App Insights: `LockedUntilUtc` was `T+5m` on the 2026-06-05 run). 12-factor IX is satisfied with substantial margin.

---

## 3. Logging shape

One structured log record per channel that succeeds or fails. Severity:

- `Information` — `VerifiedChannel channel={ChannelId} attempts={Attempt}` — emitted on per-channel success, regardless of whether it succeeded first try or after retries. (`attempts=1` vs `attempts>1` is the queryable distinction for "succeeded immediately" vs "succeeded with hesitation".)
- `Error` — `StreamTitleFailed: RestreamVerificationExhausted ...` — emitted on per-channel verification exhaustion. Message text begins with the literal `StreamTitleFailed` so the existing `stream-title-failed-alert` log-query alert (which matches `message contains "StreamTitleFailed"`) fires.
- `Warning` — existing non-2xx PATCH warning is modified to begin with the literal `StreamTitleFailed` prefix so the existing `stream-title-failed-alert` fires for this pre-existing failure mode as well. The structured fields it carries today (channel name, status code, body) are preserved unchanged; only the message template is updated.

### Structured fields on the Error record (App Insights `customDimensions`)

| Field | Type | Example | Purpose |
|---|---|---|---|
| `channel_id` | string | `"16826484"` | Identify which Restream channel |
| `channel_display_name` | string | `"St Mary Coptic Orthodox Church - Columbus OH"` | Human-readable in alerts |
| `expected_title` | string | `"Friday, June 05, 2026 - Arabic Bible Study"` | What we PATCHed |
| `final_actual_title` | string | `"Sunday, May 31, 2026 - Kneeling Prayers/Sagda"` or null | What the final GET saw |
| `attempt_count` | int | `3` | How many PATCH+GET cycles |
| `patch_status_per_attempt` | string (csv) | `"200,200,200"` | HTTP status of each PATCH |
| `patch_body_per_attempt` | string (json array) | `"[\"\",\"\",\"\"]"` | Raw PATCH response body each attempt |
| `patch_cf_ray_per_attempt` | string (csv) | `"a07305...-CMH,a073...-CMH,..."` | Cloudflare ray id per PATCH, for Restream support correlation |
| `patch_etag_per_attempt` | string (csv) | `"W/\"47-Q5g...\",W/\"47-Xab...\",..."` | Weak etag returned by each PATCH |
| `get_status_per_attempt` | string (csv) | `"200,200,200"` | HTTP status of each verification GET |
| `get_body_title_per_attempt` | string (json array) | `"[\"old\",\"old\",\"old\"]"` | Title that each verification GET returned |
| `get_cf_ray_per_attempt` | string (csv) | `"a07305...-CMH,..."` | Cloudflare ray id per GET |
| `get_etag_per_attempt` | string (csv) | `"W/\"47-Q5g...\",..."` | Weak etag returned by each GET |
| `elapsed_ms_patch_to_verify_per_attempt` | string (csv) | `"5012,10018,20021"` | Wall-clock from PATCH return to GET issue per attempt |
| `verify_wait_seconds_per_attempt` | string (csv) | `"5,10,20"` | Configured wait inserted before each verification GET |
| `restream_token_refreshed_in_run` | bool | `true` | Whether OAuth token was refreshed during this run |
| `operation_Id` | string | (App Insights default) | For cross-record correlation per SB message |

### Format note

CSV-per-attempt rather than separate fields per attempt (`status_attempt_1`, `status_attempt_2`, ...) so the customDimensions schema does not have to grow with the configured `MaxAttempts`. CSV is more forward-compatible at the small cost of losing typed per-position KQL filters; pattern queries can `split()` the field at query time.

### Aspirational fields

The `cf-ray` and `etag` headers were confirmed present on a probe of `GET /v2/user/channel-meta/16826484` on 2026-06-05. If Restream's response headers change, these fields will be null and the rest of the record remains usable. The probe also confirmed that Restream does not surface a native `x-restream-request-id` header; `cf-ray` is the only per-request identifier available to us.

---

## 4. TDD plan

Strict TDD: RED → GREEN → REFACTOR, one test at a time. Each cycle has its own refactor checkbox; refactor is never implicit.

### Seams to introduce before the first test can be written

1. **HTTP control.** Inject a fake `HttpMessageHandler` into `RestreamClient`'s typed `HttpClient`. No production code change required — the existing typed client wiring in `Program.cs` already supports handler substitution in tests.
2. **Time control.** Today's code path (if it waits at all) calls `Task.Delay` directly. Introduce `IDelayProvider` with one method `Task DelayAsync(TimeSpan, CancellationToken)` and two implementations: `SystemDelayProvider` (delegates to `Task.Delay`) and `RecordingDelayProvider` (records the requested delays, returns `Task.CompletedTask` for tests). Inject `IDelayProvider` into `RestreamClient` via constructor. This is the 12-factor / SOLID-D fix for the timing concern.
3. **Logger spy.** `ILogger<RestreamClient>` is already injected. Tests use `FakeLogger<RestreamClient>` from `Microsoft.Extensions.Logging.Testing` to assert level, message template, and structured fields.

### Test list (each row is one RED → GREEN → REFACTOR cycle)

| # | Behavior under test |
|---|---|
| 1 | Happy path. PATCH 200 + GET returns expected title → succeed first try → exactly one `Information` `VerifiedChannel attempts=1` log → zero delays recorded. |
| 2 | Verify on retry. PATCH 200, GET stale, configured wait, PATCH 200, GET correct → success on attempt 2 → `Information` `VerifiedChannel attempts=2` → exactly one delay matching `InitialVerifyWaitSeconds` recorded. |
| 3 | Verification exhausted. PATCH 200 × 3, GET stale × 3 → `Error` log starting with `StreamTitleFailed: RestreamVerificationExhausted` with `attempt_count=3` and all CSV forensic fields populated → channel counted as failed in `TitleUpdateResult`. |
| 4 | Backoff schedule honored. Configured `VerifyBackoffSeconds=[2,4,8]` produces delays of exactly 2s, 4s, 8s in order between successive PATCHes. |
| 5 | PATCH non-2xx (existing-behavior regression). 500 on first PATCH → no verification GET → no retry → channel counted as failed → existing warning log preserved, prefixed with `StreamTitleFailed`. |
| 6 | Multi-channel partial failure. 2 enabled channels, A verifies first try, B exhausts → `TitleUpdateResult(updated=1, failed=1)` → `StreamTitleFailed` log only for B → caller throws so SB dead-letters. |
| 7 | Forensic fields populated correctly. cf-ray CSV, etag CSV, status CSV, elapsed-ms CSV all reflect three distinct attempts in order, with values traceable to the scripted HTTP responses. |
| 8 | Configuration override. `RestreamRetryPolicy(MaxAttempts: 3, InitialVerifyWait: 1s, BackoffSchedule: [1s, 2s, 3s])` injected at construction produces 1s/2s/3s delays — proves the policy is honored, not hardcoded. A second cycle covers `Program.cs` parsing the three env vars and producing the expected `RestreamRetryPolicy` (tests the composition-root parse with defaults and explicit overrides). |

### Function-level test

One additional cycle in `StreamTitleFunctionTests` covering: when `RestreamClient.SetTitleAsync` returns `TitleUpdateResult(failed > 0)`, the function dead-letters the Service Bus message (throws or explicitly calls `DeadLetterMessageAsync` — confirmed during implementation against the current function shape). Assert via the existing function-host test harness pattern.

---

## 5. Configuration

Three new App Settings on the `stream-title-svc-okg4gt72g4sfo` Function App, following the **existing project convention** (verified against `Program.cs:96-103` and the existing `YOUTUBE_BROADCAST_*` settings):

| Env var | Default | Format |
|---|---|---|
| `RESTREAM_VERIFY_MAX_ATTEMPTS` | `3` | integer |
| `RESTREAM_VERIFY_INITIAL_WAIT_SECONDS` | `5` | integer |
| `RESTREAM_VERIFY_BACKOFF_SECONDS` | `5,10,20` | CSV of integers, parsed in `Program.cs` |

Read in `Program.cs` using the same inline `Environment.GetEnvironmentVariable` + `int.TryParse` + baked-default pattern already in use for `YOUTUBE_BROADCAST_MAX_WAIT_SECONDS` and `YOUTUBE_BROADCAST_POLL_INTERVAL_SECONDS`. The parsed values are passed as constructor arguments to `RestreamClient` via a new immutable `RestreamRetryPolicy` record (one composition-root struct that carries `MaxAttempts`, `InitialVerifyWait`, and `BackoffSchedule`). Defaults are baked into the parse logic so the app keeps working if the settings are unset.

**Why not `IOptions<RestreamOptions>`?** The existing codebase has no `IOptions<T>` binding for runtime tunables. Introducing one for Restream and leaving the existing YouTube settings on the inline pattern would create two configuration mechanisms in the same composition root — the DRY violation flagged in the design analysis below. The inline pattern at the composition root is also consistent with 12-factor III (config from environment) — `Program.cs` *is* the legitimate place for env-var reads.

---

## 6. Operational deltas

1. **IaC update — `infra/main.bicep`.** Add the three new settings to the `siteConfig.appSettings` array on the Function App resource, alongside the existing `YOUTUBE_BROADCAST_*` entries. The three settings follow the same shape as today's entries (object literals with `name` and `value`, value as string). Regenerate `infra/main.json` (the ARM template) by recompiling with `az bicep build --file infra/main.bicep` (or whichever build path the team uses) so the JSON and Bicep stay in lockstep. Concretely, append:

   ```bicep
   {
     name: 'RESTREAM_VERIFY_MAX_ATTEMPTS'
     value: '3'
   }
   {
     name: 'RESTREAM_VERIFY_INITIAL_WAIT_SECONDS'
     value: '5'
   }
   {
     name: 'RESTREAM_VERIFY_BACKOFF_SECONDS'
     value: '5,10,20'
   }
   ```

   The deployment pipeline applies the IaC to the existing Function App, which sets these App Settings on next deploy. No manual `az functionapp config appsettings set` is required for the production rollout; manual application is only an option for an out-of-band test.
2. **`README.md` to be created at the repo root** as part of this change. The README is currently missing. Initial sections will cover: overview, local dev, deploy, and a dedicated **"DLQ recovery"** section covering:
   - When `stream-title-deadletter-alert` fires, peek the DLQ message using the `azure-servicebus` Python SDK pattern documented in the existing `/tmp/drain-dlq.py` template (path captured in the platform topology reference memory).
   - Because partial channel success can still cause dead-lettering, the operator must check both Restream channels' current titles via `GET /v2/user/channel-meta/{id}` before replaying — the already-correct channel will be re-PATCHed idempotently on replay, which is safe but should be expected.
   - Two recovery options: (a) replay the DLQ message back onto the main subscription (preferred — exercises the full code path), or (b) issue a manual PATCH directly via the Restream API to the affected channel(s) as a one-off fix when speed matters.
3. **No alert changes needed.** `stream-title-deadletter-alert` and `stream-title-failed-alert` (verified to exist on 2026-06-05 in `livestream-platform-rg`) both fire on this design without modification, because (a) dead-lettering increments the DLQ metric, and (b) the new Error log starts with the literal `StreamTitleFailed`. The keyword-based shape of `stream-title-failed-alert` is preserved deliberately; we are not generalizing it to severity-based to avoid alert noise from unrelated `Error` logs.

---

## 7. Design Analysis — SOLID + DRY + OOP + 12-factor

Per the project standing rule in `CLAUDE.md`, each lens is walked explicitly.

### S — Single Responsibility
`RestreamClient`'s responsibility broadens from "set a Restream title" to "set and confirm a Restream title." Both verbs describe the same axis of change (the Restream API contract). Splitting verification into a separate `IRestreamTitleVerifier` was considered and rejected: the verifier would have no independent reuse case, and exposing it as a public collaborator would leak the retry/verify implementation detail.

### O — Open/Closed
The retry policy (attempt count, wait, backoff schedule) is parameterized via `RestreamOptions`. Changing the policy requires no code change. Adding a fundamentally different verification strategy (e.g., HEAD with etag comparison only) would require editing the loop body — accepted trade-off given the current complexity.

### L — Liskov Substitution
Two implementations of the new `IDelayProvider` (`SystemDelayProvider`, `RecordingDelayProvider`) both accept any `TimeSpan ≥ 0`, never throw, and return a completed `Task` (the recording variant) or an actually-delayed `Task` (the system variant). No new exception types, no strengthened preconditions, no weakened postconditions. No L risk.

### I — Interface Segregation
`IDelayProvider` has exactly one method. The existing `IRestreamClient` (verified during implementation) grows no new methods — the external `SetTitleAsync` signature is unchanged; only internal behavior is enriched.

### D — Dependency Inversion
`RestreamClient` already takes `HttpClient` and `ILogger<RestreamClient>` via constructor injection. We add two more constructor dependencies: `IDelayProvider` (abstraction over `Task.Delay`) and a `RestreamRetryPolicy` immutable record (parsed once at composition root in `Program.cs` from environment variables). Both are injected, not constructed inline. No `Environment.GetEnvironmentVariable` inside the loop or anywhere else in `RestreamClient`. Hardcoded retry constants are eliminated from the business logic; defaults live only in the composition-root parse fallback.

### DRY
- The three Restream retry tunables (`RESTREAM_VERIFY_MAX_ATTEMPTS`, `RESTREAM_VERIFY_INITIAL_WAIT_SECONDS`, `RESTREAM_VERIFY_BACKOFF_SECONDS`) are read using the **existing inline `Environment.GetEnvironmentVariable` + `int.TryParse` + default pattern** already in use in `Program.cs:96-103` for `YOUTUBE_BROADCAST_*` settings. No second config mechanism is introduced. An earlier draft of this spec proposed `IOptions<RestreamOptions>`; that was rejected during design analysis because it would create two configuration mechanisms in the same composition root.
- The `StreamTitleFailed` alert keyword exists in one place — a `private const string` in `RestreamClient` used to build every error log message. The existing log-query alert references the same literal; if the constant is ever changed, the alert query must be updated in lockstep (documented in `README.md` and in the alert's description).
- The CSV-formatting of forensic fields goes through one helper method `BuildAttemptForensics` so the format is defined once.
- The IaC bicep change in §6 extends the existing `appSettings` array using the exact object-literal shape already used by neighbouring entries — no second entry shape introduced.

### OOP
- A private nested immutable `record AttemptLog` captures per-attempt state. The per-channel loop appends to a `List<AttemptLog>` (one writer, no shared mutation).
- Public surface of `RestreamClient` is unchanged. New state is encapsulated.
- Composition over inheritance: `RestreamClient` composes `HttpClient`, `IDelayProvider`, `IOptions<RestreamOptions>`, `ILogger<RestreamClient>`. No inheritance hierarchies introduced.

### 12-factor
- **III Config.** `RESTREAM_VERIFY_MAX_ATTEMPTS`, `RESTREAM_VERIFY_INITIAL_WAIT_SECONDS`, `RESTREAM_VERIFY_BACKOFF_SECONDS` are env-bound App Settings, provisioned via `infra/main.bicep`. Code reads them inline at the composition root (`Program.cs`) using the same pattern as the existing `YOUTUBE_BROADCAST_*` settings. Values flow into `RestreamClient` via a constructor-injected `RestreamRetryPolicy` record — no hardcoded constants in the retry loop.
- **IV Backing services.** Restream is the same attached resource via the same `HttpClient`. No new attached resource.
- **VI Processes.** No in-memory cache spans invocations. Each `SetTitleAsync` call is self-contained.
- **IX Disposability.** Worst-case in-loop wall-clock per channel ≈ 46s; per message with 2 channels ≈ 92s. SB message lock is 5 minutes (confirmed from 2026-06-05 trigger details). Comfortable margin.
- **XI Logs.** Structured logging via `ILogger`. No local files, no in-process rotation.

---

## 8. Pre-merge gates

The PR for this change must satisfy:

- [ ] CI green on the branch: build (`dotnet build --warnaserror`), all tests (`dotnet test`), format (`dotnet format --verify-no-changes`), and any further checks declared in `.github/workflows/`.
- [ ] Code review approved.
- [ ] This spec linked from the PR description.
- [ ] Acceptance criteria from §2 copied into the PR body as a checklist.
- [ ] Branch up-to-date with `main`.
- [ ] Project `CLAUDE.md` and global `~/.claude/CLAUDE.md` standards visibly satisfied (branch is a feature branch, not `main`; design analysis present in this spec).

Rollout begins only after these gates pass and the PR merges.

---

## 9. Rollout

1. Merge approved PR.
2. CI/CD deploys to `stream-title-svc-okg4gt72g4sfo` Function App.
3. Confirm the three new App Settings are present in the Function App configuration (set via the deployment pipeline or via `az functionapp config appsettings set` if applied manually).
4. Observe the next live event end-to-end. Expected: `VerifiedChannel channel=16826484 attempts=1` Information log, `VerifiedChannel channel=16826413 attempts=1` Information log, function `Succeeded`.
5. If `stream-title-deadletter-alert` or `stream-title-failed-alert` fires, follow the `README.md` DLQ recovery runbook.

---

## 10. Open questions resolved during brainstorm

- *How do we distinguish "PATCH dropped" from "GET stale due to read replica lag"?* — 5s initial wait plus etag and cf-ray correlation per attempt. The etag fields are the highest-information signal for telling the two apart.
- *What does "track a pattern" actually mean?* — The forensic field set in §3 is sized so a future KQL query can slice by channel, time-of-day, attempt count, etag divergence, cf-ray reuse, or token-refresh boundary without re-running the failed event. The exact query is not pre-committed.
- *What if only one channel fails?* — Whole-message dead-letter. Title drift across channels is unacceptable for the business; the alert noise of "partial fail = full fail" is worth the consistency guarantee.
- *Is there alerting we can rely on today?* — Yes. `stream-title-deadletter-alert` (metric, DLQ > 0) and `stream-title-failed-alert` (log, keyword `StreamTitleFailed`) both exist and route to `livestream-platform-alerts` action group. Both will fire under this design without modification.
