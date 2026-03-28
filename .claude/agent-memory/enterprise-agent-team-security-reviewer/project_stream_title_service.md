---
name: stream-title-service security review context
description: Security patterns and findings for the stream-title-service Azure Function (.NET 9, Service Bus, Key Vault, Restream/YouTube APIs)
type: project
---

stream-title-service is a .NET 9 Azure Function (isolated worker) that receives Service Bus messages and updates stream titles on Restream and YouTube.

**Why:** Initial comprehensive security review completed 2026-03-27.

**How to apply:**
- Key Vault RBAC is properly configured via Bicep (Secrets User role only)
- Restream OAuth tokens cached in singleton with semaphore-guarded refresh
- Thread-safety issue identified: RestreamClient mutates DefaultRequestHeaders on a shared HttpClient
- Deploy workflow uses legacy `secrets.AZURE_CREDENTIALS` (service principal JSON) instead of OIDC federated credentials
- Exception messages from domain/infra are published to Service Bus failed events (minor info disclosure)
- No input validation on deserialized Service Bus message fields beyond Location
- Deprecated packages: Azure.Identity 1.12.0, Polly.Extensions.Http 3.0.0
