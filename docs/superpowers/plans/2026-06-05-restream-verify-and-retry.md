# Restream Verify-and-Retry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Spec:** `docs/superpowers/specs/2026-06-05-restream-verify-and-retry-design.md`

**Goal:** Add verify-after-PATCH with bounded retries and forensic logging to `RestreamClient`; dead-letter the SB message when any channel exhausts retries; surface verbose forensics so future runs can root-cause Restream silently dropping writes.

**Architecture:** Inline verification GET after each PATCH inside `RestreamClient.SetTitleAsync`. New `IDelayProvider` abstraction makes timing test-controllable. New immutable `RestreamRetryPolicy` record carries `MaxAttempts`, `InitialVerifyWait`, `BackoffSchedule`. Three env vars (`RESTREAM_VERIFY_MAX_ATTEMPTS`, `RESTREAM_VERIFY_INITIAL_WAIT_SECONDS`, `RESTREAM_VERIFY_BACKOFF_SECONDS`) are read inline at the composition root (`Program.cs`), following the same pattern as `YOUTUBE_BROADCAST_*`. IaC update to `infra/main.bicep` + regenerated `infra/main.json`. New `README.md` with DLQ recovery runbook.

**Tech Stack:** .NET 8 Azure Functions (isolated worker), xUnit + Moq + Moq.Protected + FluentAssertions, Bicep IaC, App Insights structured logging via `ILogger`.

**TDD discipline (project rule, restated):** RED → GREEN → REFACTOR per individual test, never batched. Each task has REFACTOR as its own explicit checkbox; if no refactor is needed, write the one-sentence justification in the checkbox itself before checking it.

---

## File Map

**Create:**
- `src/StreamTitleService/Infrastructure/Time/IDelayProvider.cs` — single-method abstraction over `Task.Delay`
- `src/StreamTitleService/Infrastructure/Time/SystemDelayProvider.cs` — prod impl
- `src/StreamTitleService/Infrastructure/Adapters/RestreamRetryPolicy.cs` — immutable record
- `tests/StreamTitleService.Tests/TestDoubles/RecordingDelayProvider.cs` — test fake
- `tests/StreamTitleService.Tests/Infrastructure/Time/SystemDelayProviderTests.cs` — single trivial test
- `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs` — all new behavior tests
- `tests/StreamTitleService.Tests/Composition/ProgramRestreamRetryPolicyParsingTests.cs` — env-var parsing test
- `README.md` (repo root) — initial README with DLQ recovery runbook

**Modify:**
- `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs` — add ctor params, verify-retry loop, structured error log
- `src/StreamTitleService/Program.cs` — parse env vars into `RestreamRetryPolicy`, wire `IDelayProvider`
- `tests/StreamTitleService.Tests/Infrastructure/RestreamClientTests.cs` — update `CreateClient` helper to script default verification GET so existing tests keep passing without behavior change
- `infra/main.bicep` — add three `appSettings` entries
- `infra/main.json` — regenerate from bicep

**Not touched:** SB subscription filter, topic, OAuth scope, YouTube path, anything outside `RestreamClient` / `Program.cs` / IaC / README / new tests.

---

## Task 0: Prep — create feature branch

**Why first:** Project CLAUDE.md (`/Users/wasimhanna/Code/stream-title-service/CLAUDE.md`) forbids commits directly to `main`. The very first commit lands on a feature branch.

- [ ] **Step 0.1: Confirm current branch is `main` and working tree state**

```bash
git -C /Users/wasimhanna/Code/stream-title-service branch --show-current
git -C /Users/wasimhanna/Code/stream-title-service status --short
```

Expected: branch is `main`; status shows the new files from brainstorming (`docs/superpowers/specs/2026-06-05-restream-verify-and-retry-design.md`, `CLAUDE.md`, untracked `docs/superpowers/plans/2026-06-05-restream-verify-and-retry.md`).

- [ ] **Step 0.2: Create feature branch**

```bash
git -C /Users/wasimhanna/Code/stream-title-service checkout -b feature/restream-verify-and-retry
git -C /Users/wasimhanna/Code/stream-title-service branch --show-current
```

Expected: branch switched to `feature/restream-verify-and-retry`.

- [ ] **Step 0.3: Stage and commit the spec, plan, and project CLAUDE.md**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add CLAUDE.md docs/superpowers/specs/2026-06-05-restream-verify-and-retry-design.md docs/superpowers/plans/2026-06-05-restream-verify-and-retry.md
git -C /Users/wasimhanna/Code/stream-title-service commit -m "docs: spec and plan for Restream verify-and-retry, add project CLAUDE.md"
```

Expected: one commit on `feature/restream-verify-and-retry` containing the three files. The working tree from here on is clean before each subsequent task.

---

## Task 1: Scaffold `IDelayProvider` + `SystemDelayProvider` + `RestreamRetryPolicy`

**Files:**
- Create: `src/StreamTitleService/Infrastructure/Time/IDelayProvider.cs`
- Create: `src/StreamTitleService/Infrastructure/Time/SystemDelayProvider.cs`
- Create: `src/StreamTitleService/Infrastructure/Adapters/RestreamRetryPolicy.cs`
- Create: `tests/StreamTitleService.Tests/Infrastructure/Time/SystemDelayProviderTests.cs`
- Create: `tests/StreamTitleService.Tests/TestDoubles/RecordingDelayProvider.cs`

**Why this is one task, not three:** `IDelayProvider`, its system impl, and `RestreamRetryPolicy` are pure scaffolding required before any behavioral test can compile. Bundling them keeps the next task focused on the first verification-loop assertion. Strict TDD still holds: each NEW behavior in subsequent tasks gets its own RED → GREEN → REFACTOR cycle.

- [ ] **Step 1.1: Write SystemDelayProviderTests (RED)**

Create `tests/StreamTitleService.Tests/Infrastructure/Time/SystemDelayProviderTests.cs`:

```csharp
using System.Diagnostics;
using FluentAssertions;
using StreamTitleService.Infrastructure.Time;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure.Time;

public class SystemDelayProviderTests
{
    [Fact]
    public async Task DelayAsync_WithPositiveTimeSpan_ShouldDelayAtLeastThatMuch()
    {
        var provider = new SystemDelayProvider();
        var sw = Stopwatch.StartNew();

        await provider.DelayAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        sw.Stop();
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
    }
}
```

(40ms tolerance accounts for timer resolution; the assertion is "actually delays" not "delays exactly 50ms".)

- [ ] **Step 1.2: Run test and confirm RED**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SystemDelayProviderTests
```

Expected: FAIL — `SystemDelayProvider` and `IDelayProvider` don't exist yet.

- [ ] **Step 1.3: Create the abstraction**

Create `src/StreamTitleService/Infrastructure/Time/IDelayProvider.cs`:

```csharp
namespace StreamTitleService.Infrastructure.Time;

public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
```

- [ ] **Step 1.4: Create the system impl**

Create `src/StreamTitleService/Infrastructure/Time/SystemDelayProvider.cs`:

```csharp
namespace StreamTitleService.Infrastructure.Time;

public sealed class SystemDelayProvider : IDelayProvider
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
}
```

- [ ] **Step 1.5: Run test and confirm GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SystemDelayProviderTests
```

Expected: PASS.

- [ ] **Step 1.6: Create the immutable retry policy record**

Create `src/StreamTitleService/Infrastructure/Adapters/RestreamRetryPolicy.cs`:

```csharp
namespace StreamTitleService.Infrastructure.Adapters;

public sealed record RestreamRetryPolicy(
    int MaxAttempts,
    TimeSpan InitialVerifyWait,
    IReadOnlyList<TimeSpan> BackoffSchedule)
{
    public static RestreamRetryPolicy Defaults { get; } = new(
        MaxAttempts: 3,
        InitialVerifyWait: TimeSpan.FromSeconds(5),
        BackoffSchedule: new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20)
        });
}
```

- [ ] **Step 1.7: Create the recording test fake**

Create `tests/StreamTitleService.Tests/TestDoubles/RecordingDelayProvider.cs`:

```csharp
using StreamTitleService.Infrastructure.Time;

namespace StreamTitleService.Tests.TestDoubles;

public sealed class RecordingDelayProvider : IDelayProvider
{
    private readonly List<TimeSpan> _recorded = new();

    public IReadOnlyList<TimeSpan> Recorded => _recorded;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        _recorded.Add(delay);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 1.8: Run full unit suite to confirm no regressions**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests
```

Expected: all existing tests still pass (we haven't touched `RestreamClient` yet, so the existing `RestreamClientTests` are unaffected).

- [ ] **Step 1.9: Refactor**

No refactor needed; the abstraction and impls are minimal and isolated, with no duplication introduced. State this in the commit message.

- [ ] **Step 1.10: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add src/StreamTitleService/Infrastructure/Time src/StreamTitleService/Infrastructure/Adapters/RestreamRetryPolicy.cs tests/StreamTitleService.Tests/Infrastructure/Time tests/StreamTitleService.Tests/TestDoubles
git -C /Users/wasimhanna/Code/stream-title-service commit -m "feat: add IDelayProvider, SystemDelayProvider, RestreamRetryPolicy, RecordingDelayProvider

Scaffolds the abstractions required for the upcoming verify-and-retry behavior in RestreamClient. No refactor in this cycle; all new types are minimal and isolated."
```

---

## Task 2: TDD #1 — Happy path (verify succeeds first try)

**Files:**
- Create: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`
- Modify: `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs`
- Modify: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientTests.cs` (update `CreateClient` helper so existing tests still pass after the verification GET is added)

- [ ] **Step 2.1: Write the new test (RED)**

Create `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Infrastructure.Adapters;
using StreamTitleService.Tests.TestDoubles;
using Xunit;

namespace StreamTitleService.Tests.Infrastructure;

public class RestreamClientVerifyRetryTests
{
    private readonly Mock<ITokenProvider> _tokenProvider = new();
    private readonly Mock<HttpMessageHandler> _httpHandler = new();
    private readonly Mock<ILogger<RestreamClient>> _logger = new();
    private readonly RecordingDelayProvider _delays = new();

    private RestreamClient CreateClient(RestreamRetryPolicy? policy = null)
    {
        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-access-token");

        var httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        return new RestreamClient(
            httpClient,
            _tokenProvider.Object,
            policy ?? FastTestPolicy(),
            _delays,
            _logger.Object);
    }

    private static RestreamRetryPolicy FastTestPolicy() => new(
        MaxAttempts: 3,
        InitialVerifyWait: TimeSpan.Zero,
        BackoffSchedule: new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero });

    private void SetupGetChannels(object channels)
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith("/user/channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(channels)
            });
    }

    private void SetupPatchChannel(string channelId, HttpStatusCode status,
        string? cfRay = "test-cf-ray", string? etag = "W/\"test-etag\"")
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Patch &&
                    r.RequestUri!.PathAndQuery.EndsWith($"/user/channel-meta/{channelId}")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(status)
                {
                    Content = new StringContent(string.Empty)
                };
                if (cfRay is not null) resp.Headers.TryAddWithoutValidation("cf-ray", cfRay);
                if (etag is not null) resp.Headers.TryAddWithoutValidation("etag", etag);
                return resp;
            });
    }

    private void SetupVerifyGetChannel(string channelId, string returnedTitle,
        HttpStatusCode status = HttpStatusCode.OK,
        string? cfRay = "test-cf-ray", string? etag = "W/\"test-etag\"")
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith($"/user/channel-meta/{channelId}")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(new { title = returnedTitle, description = (string?)null })
                };
                if (cfRay is not null) resp.Headers.TryAddWithoutValidation("cf-ray", cfRay);
                if (etag is not null) resp.Headers.TryAddWithoutValidation("etag", etag);
                return resp;
            });
    }

    [Fact]
    public async Task SetTitle_VerifyMatchesFirstTry_LogsVerifiedChannelAttempts1AndNoDelays()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);
        SetupVerifyGetChannel("ch1", returnedTitle: "Friday Bible Study");

        var client = CreateClient();

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(0);
        _delays.Recorded.Should().BeEmpty(
            "happy path verifies on first try with InitialVerifyWait=0, no backoff");

        _logger.Invocations
            .Should()
            .Contain(i =>
                i.Method.Name == nameof(ILogger.Log) &&
                (LogLevel)i.Arguments[0] == LogLevel.Information &&
                i.Arguments[2]!.ToString()!.Contains("VerifiedChannel") &&
                i.Arguments[2]!.ToString()!.Contains("ch1") &&
                i.Arguments[2]!.ToString()!.Contains("attempts=1"));
    }
}
```

- [ ] **Step 2.2: Run the test and confirm RED**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~RestreamClientVerifyRetryTests
```

Expected: COMPILE FAIL — `RestreamClient` constructor doesn't yet accept `RestreamRetryPolicy` and `IDelayProvider`. This is acceptable: the test is failing because the production code hasn't been changed. Per project rule, RED means "fails for the expected reason."

- [ ] **Step 2.3: Modify `RestreamClient` constructor and add verification loop (minimum impl)**

Open `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs`. Replace the class header, fields, constructor, and `SetTitleAsync` body with the following (keep existing using-directives and add `using StreamTitleService.Infrastructure.Time;`):

```csharp
public class RestreamClient : ITitlePlatformClient
{
    private const string FailedLogPrefix = "StreamTitleFailed";

    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokenProvider;
    private readonly RestreamRetryPolicy _retryPolicy;
    private readonly IDelayProvider _delayProvider;
    private readonly ILogger<RestreamClient>? _logger;

    public RestreamClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        RestreamRetryPolicy retryPolicy,
        IDelayProvider delayProvider,
        ILogger<RestreamClient>? logger = null)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _retryPolicy = retryPolicy;
        _delayProvider = delayProvider;
        _logger = logger;
    }
```

Replace the existing per-channel PATCH loop (currently at `RestreamClient.cs` around lines 76–100) with the verify-and-retry loop:

```csharp
        int updated = 0, failed = 0;
        foreach (var ch in enabledChannels)
        {
            var channelId = ch.GetProperty("id").ToString();
            var name = ch.TryGetProperty("displayName", out var dn) ? dn.GetString() : "unknown";

            var outcome = await TryUpdateAndVerifyChannelAsync(channelId, name ?? "unknown", title, token, ct);
            if (outcome) updated++;
            else failed++;
        }

        return new TitleUpdateResult(updated, failed);
    }

    private async Task<bool> TryUpdateAndVerifyChannelAsync(
        string channelId, string channelName, string expectedTitle, string token, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
        {
            var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"user/channel-meta/{channelId}");
            patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            patchReq.Content = JsonContent.Create(new { title = expectedTitle });
            var patchResp = await _httpClient.SendAsync(patchReq, ct);

            if (!patchResp.IsSuccessStatusCode)
            {
                var body = await patchResp.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning(
                    "{Prefix}: RestreamPatchFailed channel={Name} channel_id={ChannelId} status={Status} body={Body}",
                    FailedLogPrefix, channelName, channelId, (int)patchResp.StatusCode, body);
                return false;
            }

            await _delayProvider.DelayAsync(_retryPolicy.InitialVerifyWait, ct);

            var getReq = new HttpRequestMessage(HttpMethod.Get, $"user/channel-meta/{channelId}");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var getResp = await _httpClient.SendAsync(getReq, ct);

            string? actualTitle = null;
            if (getResp.IsSuccessStatusCode)
            {
                var meta = await getResp.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (meta.TryGetProperty("title", out var t)) actualTitle = t.GetString();
            }

            if (string.Equals(actualTitle, expectedTitle, StringComparison.Ordinal))
            {
                _logger?.LogInformation(
                    "VerifiedChannel channel={Name} channel_id={ChannelId} attempts={Attempt}",
                    channelName, channelId, attempt);
                return true;
            }

            if (attempt < _retryPolicy.MaxAttempts)
            {
                var backoffIndex = Math.Min(attempt - 1, _retryPolicy.BackoffSchedule.Count - 1);
                if (backoffIndex >= 0)
                    await _delayProvider.DelayAsync(_retryPolicy.BackoffSchedule[backoffIndex], ct);
            }
        }

        _logger?.LogError(
            "{Prefix}: RestreamVerificationExhausted channel={Name} channel_id={ChannelId} expected={Expected} attempts={Attempts}",
            FailedLogPrefix, channelName, channelId, expectedTitle, _retryPolicy.MaxAttempts);
        return false;
    }
```

Forensic CSV fields will be added in Task 8 (TDD #7). This minimal impl satisfies Task 2's single assertion.

- [ ] **Step 2.4: Update existing `RestreamClientTests` helper so previously-passing tests still pass**

Open `tests/StreamTitleService.Tests/Infrastructure/RestreamClientTests.cs`. The existing tests construct `RestreamClient` without a policy or delay provider, and don't mock the verification GET. To keep them green without changing their behavioral intent, update `CreateClient` and the existing `SetupPatchChannel` helper to also script a verification-GET mock that returns the title the test PATCHed.

Replace the existing `CreateClient` method (currently `RestreamClientTests.cs:20-31`) with:

```csharp
    private RestreamClient CreateClient(ILogger<RestreamClient>? logger = null)
    {
        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-access-token");

        _httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        // Use a fast retry policy and a no-op delay provider so the existing tests'
        // behavioral assertions are unchanged. New verification-and-retry behavior is
        // exercised by RestreamClientVerifyRetryTests.cs.
        var policy = new RestreamRetryPolicy(
            MaxAttempts: 1,
            InitialVerifyWait: TimeSpan.Zero,
            BackoffSchedule: Array.Empty<TimeSpan>());
        var delays = new StreamTitleService.Tests.TestDoubles.RecordingDelayProvider();

        return new RestreamClient(_httpClient, _tokenProvider.Object, policy, delays, logger);
    }
```

Add a default verification-GET setup to `SetupPatchChannel` so tests that don't explicitly opt out still get a matching verification (since `MaxAttempts=1`, the verification GET still happens once). Find the existing `SetupPatchChannel` method (returns `void`, configures the Mock). After it sets up the PATCH, also set up the GET:

```csharp
    private void SetupPatchChannel(HttpStatusCode status, string? body = null,
        string? verifyTitle = "default-test-title")
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Patch),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? string.Empty)
            });

        // Also script a verification GET on channel-meta that mirrors any title the
        // test PATCHed, so existing happy-path tests pass through the new verification
        // step without modification.
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.Contains("/user/channel-meta/")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { title = verifyTitle, description = (string?)null })
            });
    }
```

For each existing test that PATCHes a title and expects success, locate the `var result = await client.SetTitleAsync("<Title>", ...)` line and add a `SetupVerify` setup OR update the call so the helper knows what title to mirror. The simplest mechanical change: where each test calls `SetupPatchChannel(HttpStatusCode.OK)`, replace with `SetupPatchChannel(HttpStatusCode.OK, verifyTitle: "<the title the test PATCHes>")`. There are roughly 6–8 such tests; update each.

For tests that expect PATCH failure (non-2xx), the verification GET is unreachable — no change needed.

- [ ] **Step 2.5: Run the full unit suite and confirm GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests
```

Expected: PASS — the new `SetTitle_VerifyMatchesFirstTry_LogsVerifiedChannelAttempts1AndNoDelays` test passes, and all existing tests still pass.

- [ ] **Step 2.6: Refactor**

Review the new code for duplication: the PATCH and GET both build an `HttpRequestMessage` and set the `Authorization` header. If this is the only place, extract is premature (one repetition). Note this for Task 8 when the forensic capture adds more per-attempt code — if the duplication grows, extract a `BuildAuthorizedRequest(HttpMethod, string)` helper then. For now: **no refactor needed; only one PATCH and one GET per attempt and the duplication is two lines.**

- [ ] **Step 2.7: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs tests/StreamTitleService.Tests/Infrastructure/RestreamClientTests.cs tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "feat(restream): verify title after PATCH on first try

Adds RestreamRetryPolicy + IDelayProvider ctor parameters to RestreamClient.
Verifies the PATCHed title via GET /user/channel-meta/{id} and logs
'VerifiedChannel ... attempts=N' on success. Existing tests preserved by
updating the test helper to also script a verification GET."
```

---

## Task 3: TDD #2 — Verify on retry (succeed on attempt 2)

**Files:**
- Modify: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`

(Production code: the loop already supports this; only assertion is the delay-recording.)

- [ ] **Step 3.1: Write the test (RED)**

Append to `RestreamClientVerifyRetryTests.cs`:

```csharp
    [Fact]
    public async Task SetTitle_VerifyStaleOnceThenMatches_SucceedsOnAttempt2AndRecordsOneInitialWait()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);

        // First GET returns stale title; second GET returns the expected title.
        var calls = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith("/user/channel-meta/ch1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                calls++;
                var title = calls == 1 ? "OLD-TITLE" : "Friday Bible Study";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { title, description = (string?)null })
                };
            });

        var policy = new RestreamRetryPolicy(
            MaxAttempts: 3,
            InitialVerifyWait: TimeSpan.FromSeconds(5),
            BackoffSchedule: new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) });

        var client = CreateClient(policy);

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(0);

        // Expected delays for attempts 1 and 2 of one channel:
        //   attempt 1: InitialVerifyWait (5s) → GET stale → backoff[0] (5s)
        //   attempt 2: InitialVerifyWait (5s) → GET matches → done
        _delays.Recorded.Should().Equal(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        _logger.Invocations
            .Should()
            .Contain(i =>
                (LogLevel)i.Arguments[0] == LogLevel.Information &&
                i.Arguments[2]!.ToString()!.Contains("VerifiedChannel") &&
                i.Arguments[2]!.ToString()!.Contains("attempts=2"));
    }
```

- [ ] **Step 3.2: Run, confirm RED**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SetTitle_VerifyStaleOnceThenMatches
```

Expected: FAIL — most likely the delays-recorded assertion fails (the production code already does the loop). Inspect: if all delays are recorded correctly and the test PASSES on first run, that's an indicator the production code already covers this case, in which case write the next test instead. Per strict TDD, drive the next behavior.

- [ ] **Step 3.3: Make GREEN**

If the test passed on Step 3.2 it's already GREEN (the loop logic from Task 2 covers attempt-2 success). If not, inspect: most likely the verification GET only setup once via the simple `SetupVerifyGetChannel` helper from Task 2 captures only the first invocation. The new test above uses an inline `.Setup<>` with `calls++` to script multiple responses for the same request; if the Moq behavior is "last-setup-wins," consider switching to `.SetupSequence<>` instead. Apply minimal fix.

- [ ] **Step 3.4: Run all tests, confirm GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests
```

Expected: PASS.

- [ ] **Step 3.5: Refactor**

Look for duplicated test setup: the multi-response GET pattern will repeat in Task 4 (verification exhausted). Extract a helper `SetupVerifyGetChannelSequence(string channelId, params string[] titlesInOrder)` on the test class now. **Refactor: extract helper. Justification: the same multi-response pattern is about to appear in Task 4 — preventing duplication before it lands keeps the test file readable.**

- [ ] **Step 3.6: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "test(restream): verify succeeds on attempt 2 after one stale GET

Drives the retry-after-stale path with explicit delay-schedule assertion.
Refactors the multi-response GET setup into SetupVerifyGetChannelSequence
helper in preparation for the exhaustion test in the next task."
```

---

## Task 4: TDD #3 — Verification exhausted

**Files:**
- Modify: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`
- Modify: `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs` (only if the Error log isn't already there from Task 2)

- [ ] **Step 4.1: Write the test (RED)**

Append to `RestreamClientVerifyRetryTests.cs`:

```csharp
    [Fact]
    public async Task SetTitle_VerifyStaleThreeTimes_LogsStreamTitleFailedErrorAndCountsChannelAsFailed()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);
        SetupVerifyGetChannelSequence("ch1", "STALE", "STALE", "STALE");

        var client = CreateClient();  // FastTestPolicy: 3 attempts, zero waits

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
        result.ChannelsFailed.Should().Be(1);

        _logger.Invocations
            .Should()
            .Contain(i =>
                (LogLevel)i.Arguments[0] == LogLevel.Error &&
                i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed: RestreamVerificationExhausted") &&
                i.Arguments[2]!.ToString()!.Contains("ch1") &&
                i.Arguments[2]!.ToString()!.Contains("attempts=3"));
    }
```

- [ ] **Step 4.2: Run, confirm RED or GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SetTitle_VerifyStaleThreeTimes
```

Expected: PASS — Task 2's impl already emits this log. If GREEN on first run, that's fine — the test now guards the existing behavior against regressions.

- [ ] **Step 4.3: Refactor**

No refactor — the test pinned existing behavior added in Task 2. Note in commit.

- [ ] **Step 4.4: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "test(restream): verification exhausted logs StreamTitleFailed error

Pins the per-channel Error log emitted when all retry attempts return a
stale title."
```

---

## Task 5: TDD #4 — Backoff schedule honored

**Files:**
- Modify: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`

- [ ] **Step 5.1: Write the test (RED)**

Append:

```csharp
    [Fact]
    public async Task SetTitle_AlwaysStaleWithConfiguredBackoff_HonorsSchedule()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.OK);
        SetupVerifyGetChannelSequence("ch1", "STALE", "STALE", "STALE");

        var policy = new RestreamRetryPolicy(
            MaxAttempts: 3,
            InitialVerifyWait: TimeSpan.FromSeconds(2),
            BackoffSchedule: new[] { TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) });

        var client = CreateClient(policy);

        await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        // Expected delays for one channel that exhausts:
        //   attempt 1: InitialVerifyWait (2s) → stale → backoff[0] (4s)
        //   attempt 2: InitialVerifyWait (2s) → stale → backoff[1] (8s)
        //   attempt 3: InitialVerifyWait (2s) → stale → exhausted (no backoff after final attempt)
        _delays.Recorded.Should().Equal(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(2));
    }
```

- [ ] **Step 5.2: Run, confirm result**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SetTitle_AlwaysStaleWithConfiguredBackoff
```

Expected: PASS (Task 2 impl already honors the schedule). If FAIL, inspect the off-by-one between `attempt` and `backoffIndex` in `TryUpdateAndVerifyChannelAsync` and adjust to minimum-required change to match the assertion.

- [ ] **Step 5.3: Refactor**

No refactor.

- [ ] **Step 5.4: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "test(restream): backoff schedule honored for verify-and-retry loop"
```

---

## Task 6: TDD #5 — PATCH non-2xx warning prefixed with `StreamTitleFailed` (regression)

**Files:**
- Modify: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`
- Modify: `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs` (the prefix should already be there from Task 2; this test pins it)

- [ ] **Step 6.1: Write the test (RED)**

Append:

```csharp
    [Fact]
    public async Task SetTitle_PatchReturns500_LogsWarningPrefixedWithStreamTitleFailed_NoVerificationGet()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("ch1", HttpStatusCode.InternalServerError);

        var client = CreateClient();

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
        result.ChannelsFailed.Should().Be(1);

        _logger.Invocations
            .Should()
            .Contain(i =>
                (LogLevel)i.Arguments[0] == LogLevel.Warning &&
                i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed:"));

        // No verification GET was issued because PATCH itself failed.
        _delays.Recorded.Should().BeEmpty(
            "no InitialVerifyWait when PATCH itself failed");
    }
```

- [ ] **Step 6.2: Run, confirm GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SetTitle_PatchReturns500
```

Expected: PASS — Task 2's impl already prefixes the warning with `StreamTitleFailed:`.

- [ ] **Step 6.3: Refactor**

No refactor.

- [ ] **Step 6.4: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "test(restream): non-2xx PATCH warning prefixed with StreamTitleFailed marker"
```

---

## Task 7: TDD #6 — Multi-channel partial failure surfaces `failed > 0`

**Files:**
- Modify: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`

(Function-level dead-letter assertion is covered in Task 12 / function tests; this task pins the `RestreamClient` contract.)

- [ ] **Step 7.1: Write the test (RED)**

Append:

```csharp
    [Fact]
    public async Task SetTitle_TwoChannels_AVerifiesBExhausts_ReturnsUpdated1Failed1AndLogsBOnly()
    {
        var channels = new[]
        {
            new { id = "chA", displayName = "YouTube",  enabled = true, streamingPlatformId = 5  },
            new { id = "chB", displayName = "Facebook", enabled = true, streamingPlatformId = 37 }
        };
        SetupGetChannels(channels);
        SetupPatchChannel("chA", HttpStatusCode.OK);
        SetupPatchChannel("chB", HttpStatusCode.OK);
        SetupVerifyGetChannel("chA", returnedTitle: "Friday Bible Study");
        SetupVerifyGetChannelSequence("chB", "STALE", "STALE", "STALE");

        var client = CreateClient();

        var result = await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(1);

        var errorLogs = _logger.Invocations
            .Where(i => (LogLevel)i.Arguments[0] == LogLevel.Error &&
                        i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed: RestreamVerificationExhausted"))
            .ToList();
        errorLogs.Should().HaveCount(1);
        errorLogs[0].Arguments[2]!.ToString().Should().Contain("chB");
        errorLogs[0].Arguments[2]!.ToString().Should().NotContain("chA");
    }
```

- [ ] **Step 7.2: Run, confirm GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SetTitle_TwoChannels
```

Expected: PASS.

- [ ] **Step 7.3: Refactor**

No refactor.

- [ ] **Step 7.4: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "test(restream): multi-channel partial failure returns failed=1 and logs only failing channel"
```

---

## Task 8: TDD #7 — Forensic CSV fields populated correctly

**Files:**
- Modify: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs`
- Modify: `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs` (add per-attempt capture and structured fields)

This is the task that adds the forensic capture from spec §3.

- [ ] **Step 8.1: Write the test (RED)**

Append:

```csharp
    [Fact]
    public async Task SetTitle_VerificationExhausted_ErrorLogIncludesForensicCsvFields()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 }
        };
        SetupGetChannels(channels);

        // PATCH responses with distinct cf-ray and etag per attempt.
        var patchCalls = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Patch &&
                    r.RequestUri!.PathAndQuery.EndsWith("/user/channel-meta/ch1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                patchCalls++;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty)
                };
                resp.Headers.TryAddWithoutValidation("cf-ray", $"patch-ray-{patchCalls}");
                resp.Headers.TryAddWithoutValidation("etag", $"W/\"patch-etag-{patchCalls}\"");
                return resp;
            });

        // GETs always return STALE, with distinct cf-ray and etag per attempt.
        var getCalls = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Get &&
                    r.RequestUri!.PathAndQuery.EndsWith("/user/channel-meta/ch1")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                getCalls++;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { title = "STALE", description = (string?)null })
                };
                resp.Headers.TryAddWithoutValidation("cf-ray", $"get-ray-{getCalls}");
                resp.Headers.TryAddWithoutValidation("etag", $"W/\"get-etag-{getCalls}\"");
                return resp;
            });

        var client = CreateClient();
        await client.SetTitleAsync("Friday Bible Study", CancellationToken.None);

        var errorLog = _logger.Invocations.Single(i =>
            (LogLevel)i.Arguments[0] == LogLevel.Error &&
            i.Arguments[2]!.ToString()!.StartsWith("StreamTitleFailed: RestreamVerificationExhausted"));

        // The forensic fields are passed as structured logging arguments — assert
        // the rendered message contains the CSV values so the format is locked in.
        var rendered = errorLog.Arguments[2]!.ToString()!;
        rendered.Should().Contain("patch_status_per_attempt=200,200,200");
        rendered.Should().Contain("get_status_per_attempt=200,200,200");
        rendered.Should().Contain("patch_cf_ray_per_attempt=patch-ray-1,patch-ray-2,patch-ray-3");
        rendered.Should().Contain("get_cf_ray_per_attempt=get-ray-1,get-ray-2,get-ray-3");
        rendered.Should().Contain("patch_etag_per_attempt=W/\"patch-etag-1\",W/\"patch-etag-2\",W/\"patch-etag-3\"");
        rendered.Should().Contain("get_etag_per_attempt=W/\"get-etag-1\",W/\"get-etag-2\",W/\"get-etag-3\"");
        rendered.Should().Contain("get_body_title_per_attempt=STALE,STALE,STALE");
    }
```

- [ ] **Step 8.2: Run, confirm RED**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~SetTitle_VerificationExhausted_ErrorLogIncludesForensicCsvFields
```

Expected: FAIL — the current Error log doesn't include the CSV fields.

- [ ] **Step 8.3: Make GREEN — capture per-attempt forensics and include in Error log**

Modify `RestreamClient.TryUpdateAndVerifyChannelAsync` to capture per-attempt data into a private immutable record and pass it into the Error log.

Add at top of `RestreamClient.cs` (private nested type):

```csharp
    private sealed record AttemptLog(
        int PatchStatus,
        string PatchCfRay,
        string PatchEtag,
        int GetStatus,
        string GetBodyTitle,
        string GetCfRay,
        string GetEtag);
```

In `TryUpdateAndVerifyChannelAsync`, replace the loop with:

```csharp
        var attempts = new List<AttemptLog>();

        for (var attempt = 1; attempt <= _retryPolicy.MaxAttempts; attempt++)
        {
            var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"user/channel-meta/{channelId}");
            patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            patchReq.Content = JsonContent.Create(new { title = expectedTitle });
            var patchResp = await _httpClient.SendAsync(patchReq, ct);

            var patchCfRay = patchResp.Headers.TryGetValues("cf-ray", out var pcr) ? string.Join(";", pcr) : "";
            var patchEtag = patchResp.Headers.TryGetValues("etag", out var pet) ? string.Join(";", pet) : "";

            if (!patchResp.IsSuccessStatusCode)
            {
                var body = await patchResp.Content.ReadAsStringAsync(ct);
                _logger?.LogWarning(
                    "{Prefix}: RestreamPatchFailed channel={Name} channel_id={ChannelId} status={Status} body={Body}",
                    FailedLogPrefix, channelName, channelId, (int)patchResp.StatusCode, body);
                return false;
            }

            await _delayProvider.DelayAsync(_retryPolicy.InitialVerifyWait, ct);

            var getReq = new HttpRequestMessage(HttpMethod.Get, $"user/channel-meta/{channelId}");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var getResp = await _httpClient.SendAsync(getReq, ct);

            var getCfRay = getResp.Headers.TryGetValues("cf-ray", out var gcr) ? string.Join(";", gcr) : "";
            var getEtag = getResp.Headers.TryGetValues("etag", out var get) ? string.Join(";", get) : "";

            string actualTitle = "";
            if (getResp.IsSuccessStatusCode)
            {
                var meta = await getResp.Content.ReadFromJsonAsync<JsonElement>(ct);
                if (meta.TryGetProperty("title", out var t)) actualTitle = t.GetString() ?? "";
            }

            attempts.Add(new AttemptLog(
                PatchStatus: (int)patchResp.StatusCode,
                PatchCfRay: patchCfRay,
                PatchEtag: patchEtag,
                GetStatus: (int)getResp.StatusCode,
                GetBodyTitle: actualTitle,
                GetCfRay: getCfRay,
                GetEtag: getEtag));

            if (string.Equals(actualTitle, expectedTitle, StringComparison.Ordinal))
            {
                _logger?.LogInformation(
                    "VerifiedChannel channel={Name} channel_id={ChannelId} attempts={Attempt}",
                    channelName, channelId, attempt);
                return true;
            }

            if (attempt < _retryPolicy.MaxAttempts)
            {
                var backoffIndex = Math.Min(attempt - 1, _retryPolicy.BackoffSchedule.Count - 1);
                if (backoffIndex >= 0)
                    await _delayProvider.DelayAsync(_retryPolicy.BackoffSchedule[backoffIndex], ct);
            }
        }

        _logger?.LogError(
            "{Prefix}: RestreamVerificationExhausted channel={Name} channel_id={ChannelId} expected={Expected} attempts={Attempts} " +
            "patch_status_per_attempt={PatchStatuses} get_status_per_attempt={GetStatuses} " +
            "patch_cf_ray_per_attempt={PatchCfRays} get_cf_ray_per_attempt={GetCfRays} " +
            "patch_etag_per_attempt={PatchEtags} get_etag_per_attempt={GetEtags} " +
            "get_body_title_per_attempt={GetTitles}",
            FailedLogPrefix, channelName, channelId, expectedTitle, _retryPolicy.MaxAttempts,
            string.Join(",", attempts.Select(a => a.PatchStatus)),
            string.Join(",", attempts.Select(a => a.GetStatus)),
            string.Join(",", attempts.Select(a => a.PatchCfRay)),
            string.Join(",", attempts.Select(a => a.GetCfRay)),
            string.Join(",", attempts.Select(a => a.PatchEtag)),
            string.Join(",", attempts.Select(a => a.GetEtag)),
            string.Join(",", attempts.Select(a => a.GetBodyTitle)));
        return false;
    }
```

- [ ] **Step 8.4: Run, confirm GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests
```

Expected: all tests pass.

- [ ] **Step 8.5: Refactor**

The Error log argument list is large. **Refactor: extract `BuildForensicLogParts(IReadOnlyList<AttemptLog> attempts)` returning a `record ForensicLogParts(string PatchStatuses, string GetStatuses, string PatchCfRays, string GetCfRays, string PatchEtags, string GetEtags, string GetTitles)`. Justification: one place for CSV formatting (DRY).** Apply the extraction and re-run tests.

- [ ] **Step 8.6: Re-run tests to confirm refactor didn't break anything**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests
```

Expected: all tests pass.

- [ ] **Step 8.7: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs tests/StreamTitleService.Tests/Infrastructure/RestreamClientVerifyRetryTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "feat(restream): capture per-attempt forensic fields in verification Error log

Adds private AttemptLog record and forensic CSV fields (cf-ray, etag,
status, body title) to the RestreamVerificationExhausted Error log so
future App Insights queries can extract a pattern. Extracts BuildForensicLogParts
helper to keep CSV formatting in one place."
```

---

## Task 9: TDD #8 — `RestreamRetryPolicy` honors values + Program.cs env-var parsing

**Files:**
- Create: `tests/StreamTitleService.Tests/Composition/ProgramRestreamRetryPolicyParsingTests.cs`
- Modify: `src/StreamTitleService/Program.cs`

The first half (policy values honored at runtime) is already covered by Task 5. This task covers the composition-root parse: `Program.cs` reading the three env vars into a `RestreamRetryPolicy`.

To make the parsing testable without spinning up the full Function host, extract a `static RestreamRetryPolicy BuildRestreamRetryPolicyFromEnvironment(IDictionary<string,string?> env)` method into a new `Composition` namespace (or directly on `Program` if static partial-class is awkward — use a new static class).

- [ ] **Step 9.1: Write the test (RED)**

Create `tests/StreamTitleService.Tests/Composition/ProgramRestreamRetryPolicyParsingTests.cs`:

```csharp
using FluentAssertions;
using StreamTitleService.Composition;
using Xunit;

namespace StreamTitleService.Tests.Composition;

public class ProgramRestreamRetryPolicyParsingTests
{
    [Fact]
    public void Parse_AllEnvVarsAbsent_UsesDefaults()
    {
        var policy = RestreamRetryPolicyParser.FromEnvironment(new Dictionary<string, string?>());

        policy.MaxAttempts.Should().Be(3);
        policy.InitialVerifyWait.Should().Be(TimeSpan.FromSeconds(5));
        policy.BackoffSchedule.Should().Equal(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Parse_AllEnvVarsPresent_HonorsOverrides()
    {
        var env = new Dictionary<string, string?>
        {
            ["RESTREAM_VERIFY_MAX_ATTEMPTS"] = "5",
            ["RESTREAM_VERIFY_INITIAL_WAIT_SECONDS"] = "7",
            ["RESTREAM_VERIFY_BACKOFF_SECONDS"] = "3,6,12,24"
        };

        var policy = RestreamRetryPolicyParser.FromEnvironment(env);

        policy.MaxAttempts.Should().Be(5);
        policy.InitialVerifyWait.Should().Be(TimeSpan.FromSeconds(7));
        policy.BackoffSchedule.Should().Equal(
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(24));
    }

    [Fact]
    public void Parse_BackoffSecondsHasWhitespace_TrimsAndParses()
    {
        var env = new Dictionary<string, string?>
        {
            ["RESTREAM_VERIFY_BACKOFF_SECONDS"] = " 1 , 2 , 3 "
        };

        var policy = RestreamRetryPolicyParser.FromEnvironment(env);

        policy.BackoffSchedule.Should().Equal(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Parse_UnparseableMaxAttempts_FallsBackToDefault()
    {
        var env = new Dictionary<string, string?>
        {
            ["RESTREAM_VERIFY_MAX_ATTEMPTS"] = "not-a-number"
        };

        var policy = RestreamRetryPolicyParser.FromEnvironment(env);

        policy.MaxAttempts.Should().Be(3);
    }
}
```

- [ ] **Step 9.2: Run, confirm RED**

Expected: FAIL — `RestreamRetryPolicyParser` doesn't exist.

- [ ] **Step 9.3: Create the parser (GREEN)**

Create `src/StreamTitleService/Composition/RestreamRetryPolicyParser.cs`:

```csharp
using StreamTitleService.Infrastructure.Adapters;

namespace StreamTitleService.Composition;

public static class RestreamRetryPolicyParser
{
    private const string MaxAttemptsKey = "RESTREAM_VERIFY_MAX_ATTEMPTS";
    private const string InitialWaitKey = "RESTREAM_VERIFY_INITIAL_WAIT_SECONDS";
    private const string BackoffKey = "RESTREAM_VERIFY_BACKOFF_SECONDS";

    public static RestreamRetryPolicy FromEnvironment(IDictionary<string, string?> env)
    {
        var defaults = RestreamRetryPolicy.Defaults;

        var maxAttempts = TryGetInt(env, MaxAttemptsKey, defaults.MaxAttempts);
        var initialWaitSeconds = TryGetInt(env, InitialWaitKey, (int)defaults.InitialVerifyWait.TotalSeconds);
        var backoff = TryGetCsvSeconds(env, BackoffKey, defaults.BackoffSchedule);

        return new RestreamRetryPolicy(
            MaxAttempts: maxAttempts,
            InitialVerifyWait: TimeSpan.FromSeconds(initialWaitSeconds),
            BackoffSchedule: backoff);
    }

    private static int TryGetInt(IDictionary<string, string?> env, string key, int fallback)
        => env.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : fallback;

    private static IReadOnlyList<TimeSpan> TryGetCsvSeconds(
        IDictionary<string, string?> env, string key, IReadOnlyList<TimeSpan> fallback)
    {
        if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<TimeSpan>(parts.Length);
        foreach (var p in parts)
        {
            if (int.TryParse(p, out var s)) result.Add(TimeSpan.FromSeconds(s));
            else return fallback;
        }
        return result;
    }
}
```

- [ ] **Step 9.4: Run all tests**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests
```

Expected: all pass.

- [ ] **Step 9.5: Wire `Program.cs` to use the parser and register dependencies**

Open `src/StreamTitleService/Program.cs`. Locate the section where `RestreamClient` is registered (around line 80-90 based on grep results above). Update it to construct from the parsed policy and a `SystemDelayProvider`.

Find:

```csharp
            return new RestreamClient(httpClient, tokenProvider, logger);
```

Replace with:

```csharp
            return new RestreamClient(httpClient, tokenProvider, restreamPolicy, delayProvider, logger);
```

Above the `services.AddSingleton<RestreamClient>(sp => {...})` block (or wherever the composition lives), add:

```csharp
        // Restream verify-and-retry policy: env-bound, 12-factor III. Defaults match
        // production sane values; tests can construct RestreamRetryPolicy directly.
        var restreamEnv = new Dictionary<string, string?>
        {
            ["RESTREAM_VERIFY_MAX_ATTEMPTS"] = Environment.GetEnvironmentVariable("RESTREAM_VERIFY_MAX_ATTEMPTS"),
            ["RESTREAM_VERIFY_INITIAL_WAIT_SECONDS"] = Environment.GetEnvironmentVariable("RESTREAM_VERIFY_INITIAL_WAIT_SECONDS"),
            ["RESTREAM_VERIFY_BACKOFF_SECONDS"] = Environment.GetEnvironmentVariable("RESTREAM_VERIFY_BACKOFF_SECONDS"),
        };
        var restreamPolicy = StreamTitleService.Composition.RestreamRetryPolicyParser.FromEnvironment(restreamEnv);
        IDelayProvider delayProvider = new SystemDelayProvider();
```

Add `using StreamTitleService.Infrastructure.Time;` at the top of `Program.cs` if missing.

- [ ] **Step 9.6: Run full unit suite + build**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet build --warnaserror && dotnet test tests/StreamTitleService.Tests
```

Expected: build clean, all tests pass.

- [ ] **Step 9.7: Refactor**

**Refactor: the inline env-var dictionary could become a small helper `BuildRestreamEnvSnapshot()` at the bottom of `Program.cs`. Justification: even one snapshot is fine inline — only one consumer. No extraction.**

- [ ] **Step 9.8: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add src/StreamTitleService/Composition/RestreamRetryPolicyParser.cs src/StreamTitleService/Program.cs tests/StreamTitleService.Tests/Composition/ProgramRestreamRetryPolicyParsingTests.cs
git -C /Users/wasimhanna/Code/stream-title-service commit -m "feat(composition): parse Restream retry policy from env vars at composition root

Wires three new env vars (RESTREAM_VERIFY_MAX_ATTEMPTS,
RESTREAM_VERIFY_INITIAL_WAIT_SECONDS, RESTREAM_VERIFY_BACKOFF_SECONDS)
into RestreamClient via RestreamRetryPolicyParser. Follows the existing
YOUTUBE_BROADCAST_* env-var convention; no IOptions<T> binding is introduced
(DRY with the existing composition pattern)."
```

---

## Task 10: Function-level test — partial channel failure dead-letters the SB message

**Files:**
- Modify: `tests/StreamTitleService.Tests/Functions/StreamTitleFunctionTests.cs`
- Modify: `src/StreamTitleService/...` whichever function class today calls `RestreamClient` (to be confirmed during this task — see Step 10.1)

- [ ] **Step 10.1: Inspect the current `StreamTitleFunction` and its tests to determine how it signals dead-letter**

```bash
cd /Users/wasimhanna/Code/stream-title-service && grep -n "RestreamClient\|TitleUpdateResult\|DeadLetter\|throw" src/StreamTitleService/Functions/StreamTitleFunction.cs tests/StreamTitleService.Tests/Functions/StreamTitleFunctionTests.cs 2>/dev/null | head -40
```

Use the output to determine the existing failure mechanism (throws an exception, returns a status, or explicitly calls `messageActions.DeadLetterMessageAsync`). Then write the test to match the existing pattern (per the OCP analysis in the spec: extension via existing pattern, not new pattern).

- [ ] **Step 10.2: Write the test (RED)**

Add to `StreamTitleFunctionTests.cs` (use the same test fixture setup the file currently uses):

```csharp
    [Fact]
    public async Task Run_WhenAnyChannelFailsVerification_DeadLettersOrThrows()
    {
        // Mock RestreamClient (or the platform-client port) to return failed=1.
        // Use the same DI / mocking pattern the existing tests in this file use —
        // see existing tests for the exact construction.
        // Assert: the function either throws (SB host DLQs on MaxDeliveryCount exhaustion)
        // or explicitly calls IMessageActions.DeadLetterMessageAsync — whichever the
        // existing tests already verify for other failure paths.
    }
```

Fill in the body using the conventions discovered in Step 10.1. **Do not invent a new dead-letter mechanism — match what's already there.**

- [ ] **Step 10.3: Run, confirm RED**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests --filter FullyQualifiedName~Run_WhenAnyChannelFailsVerification
```

Expected: FAIL — `StreamTitleFunction` may today complete the message even when `TitleUpdateResult.ChannelsFailed > 0`.

- [ ] **Step 10.4: Make the minimum change in the function**

In `StreamTitleFunction.Run` (or wherever the result is processed), after the call to `RestreamClient.SetTitleAsync` (or its abstraction), check `result.ChannelsFailed > 0` and either throw a typed exception (`StreamTitleVerificationException`) or call `messageActions.DeadLetterMessageAsync` — whichever matches the existing failure pattern in the file.

- [ ] **Step 10.5: Run full suite, confirm GREEN**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet test tests/StreamTitleService.Tests
```

Expected: all pass.

- [ ] **Step 10.6: Refactor**

If a new exception type was added, ensure it follows the existing exceptions namespace/pattern. **Refactor: align exception type with existing exceptions namespace if a new one was added; otherwise no refactor.**

- [ ] **Step 10.7: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add src tests
git -C /Users/wasimhanna/Code/stream-title-service commit -m "feat(function): dead-letter SB message when any Restream channel fails verification

Per spec section 2: title drift across channels is unacceptable, so any
per-channel verification failure dead-letters the whole message via the
existing dead-letter mechanism."
```

---

## Task 11: IaC — add three App Settings to `infra/main.bicep`

**Files:**
- Modify: `infra/main.bicep`
- Modify: `infra/main.json` (regenerate)

- [ ] **Step 11.1: Add the three new App Settings entries to the appSettings array**

Open `infra/main.bicep`. Locate `siteConfig.appSettings` (around line 83 per the inspection earlier). Inside the array, after the last `YOUTUBE_BROADCAST_*` entry (and before the `APPLICATIONINSIGHTS_CONNECTION_STRING` entry), insert:

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

- [ ] **Step 11.2: Regenerate `main.json` from bicep**

```bash
cd /Users/wasimhanna/Code/stream-title-service && az bicep build --file infra/main.bicep --outfile infra/main.json
```

Expected: `infra/main.json` updated; the new App Settings entries appear in the JSON.

- [ ] **Step 11.3: Diff the two files to confirm parity**

```bash
cd /Users/wasimhanna/Code/stream-title-service && git diff infra/main.bicep infra/main.json | head -80
```

Expected: both files updated, three new entries each, no other drift.

- [ ] **Step 11.4: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add infra/main.bicep infra/main.json
git -C /Users/wasimhanna/Code/stream-title-service commit -m "chore(infra): add Restream verify-and-retry App Settings to Function App

Adds RESTREAM_VERIFY_MAX_ATTEMPTS=3, RESTREAM_VERIFY_INITIAL_WAIT_SECONDS=5,
RESTREAM_VERIFY_BACKOFF_SECONDS=5,10,20 to siteConfig.appSettings in
infra/main.bicep. Regenerates infra/main.json."
```

---

## Task 12: Create `README.md` with DLQ recovery runbook

**Files:**
- Create: `README.md`

- [ ] **Step 12.1: Create the README**

Create `/Users/wasimhanna/Code/stream-title-service/README.md`:

```markdown
# stream-title-service

Azure Function (isolated worker, .NET 8) that consumes `StreamStarted` events from the `stream-title` Service Bus topic and updates the live stream title on Restream and YouTube channels.

See `docs/superpowers/specs/` for design specs and `docs/superpowers/plans/` for implementation plans.

## Local Development

```bash
dotnet build
dotnet test tests/StreamTitleService.Tests
```

CI gates that must pass before push:

```bash
dotnet build --warnaserror
dotnet test
dotnet format --verify-no-changes
```

## Deployment

IaC lives in `infra/main.bicep` (with `infra/main.json` regenerated via `az bicep build`). Deploy via the existing pipeline. App Settings on the Function App are provisioned from the bicep template; do not modify them manually except for one-off testing.

## DLQ Recovery — `stream-title-deadletter-alert`

When the `stream-title-deadletter-alert` fires (action group `livestream-platform-alerts`, emails `wasim@stmarycoc.org` and `nader@stmarycoc.org`), the Service Bus subscription `stream-title-service` on namespace `livestream-platform-okg4gt72g4sfo` has at least one message in dead-letter.

### 1. Peek the dead-lettered message

Use the `azure-servicebus` Python SDK (template script lives at `/tmp/drain-dlq.py` on the operator's box):

```python
from azure.servicebus import ServiceBusClient

ns = "livestream-platform-okg4gt72g4sfo.servicebus.windows.net"
topic = "stream-title"
sub = "stream-title-service"

with ServiceBusClient.from_connection_string(CONN_STR) as client:
    with client.get_subscription_receiver(topic, sub, sub_queue="deadletter") as receiver:
        for msg in receiver.peek_messages(max_message_count=10):
            print(msg.message_id, msg.dead_letter_reason, msg.dead_letter_error_description)
            print(str(msg))
```

Look for `dead_letter_reason=RestreamVerificationFailed` (or whichever marker the function emits per Task 10). The body contains the original `StreamStartedEvent`.

### 2. Check whether one channel already succeeded

Before replaying, confirm which Restream channels are currently in the wrong state. Both channels share the same expected title — query both:

```bash
TOK=...  # see Restream OAuth refresh script
curl -sS -H "Authorization: Bearer $TOK" https://api.restream.io/v2/user/channel-meta/16826484
curl -sS -H "Authorization: Bearer $TOK" https://api.restream.io/v2/user/channel-meta/16826413
```

If only one channel is stale, the other was updated successfully during the failing run — replay will re-PATCH it idempotently, which is safe.

### 3. Recovery options

**Option A (preferred): Replay from DLQ.** Drain the DLQ message back onto the main subscription so the full pipeline reruns. This exercises the same code path that would run on a real event.

**Option B (fast manual fix): Direct PATCH.** When speed matters more than exercising the code path, issue a direct PATCH to the affected Restream channel:

```bash
TOK=...
curl -sS -X PATCH -H "Authorization: Bearer $TOK" -H "Content-Type: application/json" \
  -d '{"title":"Friday, June 05, 2026 - Arabic Bible Study"}' \
  https://api.restream.io/v2/user/channel-meta/16826484
# verify
curl -sS -H "Authorization: Bearer $TOK" https://api.restream.io/v2/user/channel-meta/16826484
```

### 4. Investigate

Once the live stream is recovered, query App Insights to extract the forensic data for the failure. Filter `traces` where `cloud_RoleName` has `stream-title` and `message` starts with `StreamTitleFailed: RestreamVerificationExhausted`. The `customDimensions` carry per-attempt cf-ray, etag, status, and body title CSVs — these are designed to expose Restream-side patterns over time.
```

- [ ] **Step 12.2: Commit**

```bash
git -C /Users/wasimhanna/Code/stream-title-service add README.md
git -C /Users/wasimhanna/Code/stream-title-service commit -m "docs: add README with DLQ recovery runbook for stream-title-deadletter-alert"
```

---

## Task 13: Full local CI parity and PR

**Files:** none (verification + branch push + PR open)

- [ ] **Step 13.1: Run every gate the CI pipeline runs**

```bash
cd /Users/wasimhanna/Code/stream-title-service && dotnet build --warnaserror && dotnet test tests/StreamTitleService.Tests && dotnet format --verify-no-changes
```

If the repo has a `.github/workflows/ci.yml` declaring more checks, run those too. Push only when all pass locally.

- [ ] **Step 13.2: Push the feature branch**

```bash
git -C /Users/wasimhanna/Code/stream-title-service push -u origin feature/restream-verify-and-retry
```

- [ ] **Step 13.3: Open the PR via `gh`**

```bash
cd /Users/wasimhanna/Code/stream-title-service && gh pr create --title "feat: Restream verify-and-retry with forensic logging" --body "$(cat <<'EOF'
## Summary
- Adds verify-after-PATCH with bounded retries inside `RestreamClient.SetTitleAsync` so silent Restream write drops are detected within seconds, not after a human notices.
- Captures per-attempt forensic CSV fields (cf-ray, etag, status, body title) in the `StreamTitleFailed: RestreamVerificationExhausted` Error log to make Restream-side patterns queryable in App Insights over time.
- Dead-letters the Service Bus message on any per-channel verification failure so the existing `stream-title-deadletter-alert` pages immediately.
- Adds three env-bound App Settings via `infra/main.bicep` for max attempts, initial wait, and backoff schedule; defaults match production sane values.
- Creates `README.md` with a DLQ recovery runbook for `stream-title-deadletter-alert`.

Spec: `docs/superpowers/specs/2026-06-05-restream-verify-and-retry-design.md`
Plan: `docs/superpowers/plans/2026-06-05-restream-verify-and-retry.md`

## Acceptance criteria
- [ ] All channels verified → SB message completes (today's behavior).
- [ ] Any channel exhausts verification → SB message dead-letters; `stream-title-deadletter-alert` fires.
- [ ] Per-channel `StreamTitleFailed: RestreamVerificationExhausted` Error log includes channel_id, expected_title, attempts, and CSV fields for patch/get status, cf-ray, etag, and body title across all attempts.
- [ ] Successful verification on attempt N emits `VerifiedChannel ... attempts=N` Information log.
- [ ] PATCH non-2xx (existing failure mode) logs a Warning prefixed with `StreamTitleFailed:`.
- [ ] Three App Settings (`RESTREAM_VERIFY_MAX_ATTEMPTS`, `RESTREAM_VERIFY_INITIAL_WAIT_SECONDS`, `RESTREAM_VERIFY_BACKOFF_SECONDS`) parsed at composition root, honor overrides, fall back to defaults.
- [ ] Existing `RestreamClientTests` and the rest of the unit suite still pass.
- [ ] `infra/main.bicep` and `infra/main.json` updated in lockstep.

## Test plan
- [ ] CI green (build, test, format).
- [ ] Local full-suite run passes.
- [ ] After merge + deploy, observe next Friday Arabic Bible Study event: expect two `VerifiedChannel attempts=1` Information logs and a `Succeeded` function result.
- [ ] If `stream-title-deadletter-alert` or `stream-title-failed-alert` fires post-deploy, follow the DLQ recovery runbook in the new README.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 13.4: Capture the PR URL**

Run:

```bash
gh pr view --json url --jq .url
```

Save the URL and surface it to the user. PR is now in the review gate.

---

## Self-Review checklist (filled in during plan authoring)

- **Spec coverage.** §1 scope → Tasks 2–9. §2 behavior → Tasks 2–7. §3 logging shape → Tasks 2 + 8. §4 TDD plan → Tasks 1–9. §5 configuration → Task 9 + 11. §6 operational deltas (IaC + README) → Tasks 11 + 12. §7 design analysis is documentation-only. §8 pre-merge gates + §9 rollout → Task 13.
- **Placeholder scan.** Step 10.2 intentionally defers exact dead-letter mechanism to "match what's already there" because I haven't read the existing function tests yet during plan authoring; this is a deliberate "discover then implement" step, not a TBD. Acceptable.
- **Type consistency.** `RestreamRetryPolicy` ctor signature (`MaxAttempts`, `InitialVerifyWait`, `BackoffSchedule`) used consistently in Tasks 1, 3, 5, 8, 9. `FailedLogPrefix` const used consistently. `AttemptLog` record signature consistent in Task 8.
