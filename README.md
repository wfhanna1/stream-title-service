# stream-title-service

Azure Function (isolated worker, .NET 9) that consumes `StreamStarted` events from the `stream-title` Service Bus topic and updates the live stream title on Restream and YouTube destination channels.

See `docs/superpowers/specs/` for design specs and `docs/superpowers/plans/` for implementation plans. Project conventions and the SOLID + DRY + OOP + 12-factor standard live in `CLAUDE.md`.

## Local Development

```
dotnet build
dotnet test tests/StreamTitleService.Tests
```

Pre-push CI parity (matches what CI runs against `main`):

```
dotnet build --warnaserror
dotnet test
dotnet format --verify-no-changes
```

## Configuration

Tunables come from environment variables, read inline at the composition root in `Program.cs`. Defaults are baked in so the app keeps working if a setting is unset.

| Env var | Default | Purpose |
|---|---|---|
| `RESTREAM_VERIFY_MAX_ATTEMPTS` | `3` | How many PATCH+GET cycles `RestreamClient` runs per channel before declaring failure |
| `RESTREAM_VERIFY_INITIAL_WAIT_SECONDS` | `5` | How long to wait after a successful PATCH before issuing the verification GET (defeats most Restream read-replica lag) |
| `RESTREAM_VERIFY_BACKOFF_SECONDS` | `5,10,20` | CSV of seconds to wait between successive attempts when verification reports a stale title |
| `YOUTUBE_BROADCAST_MAX_WAIT_SECONDS` | `30` | Max wait for the YouTube broadcast to go active after `StreamStarted` |
| `YOUTUBE_BROADCAST_POLL_INTERVAL_SECONDS` | `2` | Poll interval during the YouTube broadcast wait |
| `STALENESS_THRESHOLD_SECONDS` | `90` | Drop `StreamStarted` events older than this on receipt |

App Settings on the Function App are provisioned from `infra/main.bicep`; modifying them in the portal is for one-off testing only.

## Deployment

```
az bicep build --file infra/main.bicep --outfile infra/main.json
# Deploy via the existing pipeline
```

`infra/main.bicep` is the source of truth; `infra/main.json` is the regenerated ARM template that must be in lockstep.

## Alerts

The platform's `livestream-platform-rg` has two relevant alerts wired to the `livestream-platform-alerts` action group (emails wasim@stmarycoc.org and nader@stmarycoc.org):

- **`stream-title-deadletter-alert`** (metric): fires when `DeadletteredMessages > 0` on the `stream-title-service` subscription. The handler dead-letters whenever any per-channel title verification fails, so this is the primary "the stream title update broke" signal.
- **`stream-title-failed-alert`** (log query): fires when any `traces` record from `cloud_RoleName has "stream-title"` contains the literal string `StreamTitleFailed`. The Warning emitted on a non-2xx PATCH and the Error emitted on `RestreamVerificationExhausted` both begin with this token.

## DLQ Recovery — `stream-title-deadletter-alert`

When the alert fires, the Service Bus subscription `stream-title-service` on namespace `livestream-platform-okg4gt72g4sfo` has at least one message in dead-letter. Follow these steps.

### 1. Peek the dead-lettered message

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

The handler emits `InvalidOperationException` with a message of the form `ChannelsFailed=N (ChannelsUpdated=M) on platform <platform>`; the SB host records that as `dead_letter_reason`. The body of the message is the original `StreamStartedEvent` JSON.

### 2. Check which channels are actually stale

Both Restream channels share the same expected title. Verify the current state of each before replaying:

```
TOK=...  # acquire via the Restream OAuth refresh script
curl -sS -H "Authorization: Bearer $TOK" https://api.restream.io/v2/user/channel-meta/16826484
curl -sS -H "Authorization: Bearer $TOK" https://api.restream.io/v2/user/channel-meta/16826413
```

If only one channel is stale, the other was updated successfully during the failing run — a replay will re-PATCH it idempotently, which is safe.

### 3. Recover

**Option A (preferred — replay):** Drain the DLQ message back onto the main subscription so the full pipeline reruns. This exercises the same code path that would run on a real event and validates that the underlying issue has resolved.

**Option B (fast manual fix):** When the live stream is in progress and speed matters more than exercising the code path, issue a direct PATCH:

```
TOK=...
curl -sS -X PATCH -H "Authorization: Bearer $TOK" -H "Content-Type: application/json" \
  -d '{"title":"<expected title>"}' \
  https://api.restream.io/v2/user/channel-meta/16826484
curl -sS -H "Authorization: Bearer $TOK" https://api.restream.io/v2/user/channel-meta/16826484
```

### 4. Investigate

Once the live stream is recovered, query App Insights to extract the forensic data for the failure. Filter `traces` where `cloud_RoleName has "stream-title"` and `message startswith "StreamTitleFailed: RestreamVerificationExhausted"`. The `customDimensions` carry per-attempt CSV fields:

- `patch_status_per_attempt`, `get_status_per_attempt` — Restream HTTP status across attempts
- `patch_cf_ray_per_attempt`, `get_cf_ray_per_attempt` — Cloudflare ray ids (for Restream support correlation)
- `patch_etag_per_attempt`, `get_etag_per_attempt` — content etags. A divergence between the PATCH-side etag and the GET-side etag is direct evidence that Restream silently dropped the write (as opposed to read-replica lag).
- `get_body_title_per_attempt` — what the verification GET actually saw across attempts
