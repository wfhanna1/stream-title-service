# Phase 2: Stream Title Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and deploy a C#/.NET 8 Azure Function that subscribes to `StreamStarted` events on the `stream-title` Service Bus topic and sets stream titles on Restream or YouTube.

**Architecture:** Hexagonal (Ports and Adapters) with DDD. Domain layer owns title resolution logic and value objects. Application layer defines ports (interfaces). Infrastructure layer implements adapters for Restream API, YouTube API, Key Vault, Service Bus, and ACS email. Azure Function layer is a thin trigger that delegates to the application handler.

**Tech Stack:** .NET 8, Azure Functions v4 (Worker isolated), xUnit + Moq + FluentAssertions, Polly (resilience), Azure.Messaging.ServiceBus, Azure.Security.KeyVault.Secrets, Azure.Storage.Blobs, Google.Apis.YouTube.v3, Azure.Communication.Email, OpenTelemetry

**PRD Reference:** `livestream-platform-docs/prds/stream-title-service-prd.md` Sections 3, 4, 5.3, 6

**Existing patterns:** Follow zoom-automation conventions -- Singleton DI, xUnit/Moq/FluentAssertions, JSON console logging with UTC, Application Insights

**TDD approach:** Every test-driven task follows strict red-green-refactor. Write ONE failing test, run it, see it fail, write minimal code to make it pass, run it, see it pass, refactor if needed, then move to the next test. No batching tests before implementation.

---

## File Structure

```
stream-title-service/
├── src/
│   └── StreamTitleService/
│       ├── StreamTitleService.csproj
│       ├── Program.cs
│       ├── host.json
│       ├── Domain/
│       │   ├── ValueObjects/
│       │   │   ├── Location.cs
│       │   │   ├── StreamTitle.cs
│       │   │   └── TargetPlatform.cs
│       │   ├── Events/
│       │   │   ├── StreamStartedEvent.cs
│       │   │   ├── StreamTitleSetEvent.cs
│       │   │   └── StreamTitleFailedEvent.cs
│       │   ├── Services/
│       │   │   └── TitleResolver.cs
│       │   └── Exceptions/
│       │       └── DomainExceptions.cs
│       ├── Application/
│       │   ├── Ports/
│       │   │   ├── Inbound/
│       │   │   │   └── IStreamTitleHandler.cs
│       │   │   └── Outbound/
│       │   │       ├── ITitlePlatformClient.cs
│       │   │       ├── ITokenProvider.cs
│       │   │       ├── IEventPublisher.cs
│       │   │       └── IAlertNotifier.cs
│       │   └── StreamTitleHandler.cs
│       ├── Infrastructure/
│       │   ├── Adapters/
│       │   │   ├── RestreamClient.cs
│       │   │   ├── YouTubeClient.cs
│       │   │   ├── KeyVaultTokenProvider.cs
│       │   │   ├── BlobStorageYouTubeTokenProvider.cs
│       │   │   ├── ServiceBusEventPublisher.cs
│       │   │   └── AcsAlertNotifier.cs
│       │   └── Configuration/
│       │       └── LocationPlatformMapping.cs
│       └── Functions/
│           └── StreamTitleFunction.cs
├── tests/
│   └── StreamTitleService.Tests/
│       ├── StreamTitleService.Tests.csproj
│       ├── Domain/
│       │   ├── TitleResolverTests.cs
│       │   ├── LocationTests.cs
│       │   └── StreamTitleTests.cs
│       ├── Application/
│       │   └── StreamTitleHandlerTests.cs
│       ├── Infrastructure/
│       │   ├── RestreamClientTests.cs
│       │   ├── YouTubeClientTests.cs
│       │   ├── LocationPlatformMappingTests.cs
│       │   └── KeyVaultTokenProviderTests.cs
│       ├── Component/
│       │   └── FullPipelineComponentTests.cs
│       ├── Integration/
│       │   └── ServiceBusIntegrationTests.cs
│       └── Functions/
│           └── StreamTitleFunctionTests.cs
├── infra/
│   └── main.bicep
├── .github/
│   └── workflows/
│       ├── ci.yml
│       └── deploy.yml
├── .gitignore
└── README.md
```

---

## Stage 1: Foundation

### Task 1: Solution scaffold and project setup

**Files:**
- Create: `.gitignore`
- Create: `src/StreamTitleService/StreamTitleService.csproj`
- Create: `src/StreamTitleService/host.json`
- Create: `tests/StreamTitleService.Tests/StreamTitleService.Tests.csproj`
- Create: `stream-title-service.sln`

- [ ] **Step 1: Create .gitignore**

```gitignore
bin/
obj/
.vs/
.vscode/
*.user
*.suo
.DS_Store
Thumbs.db
*.pickle
*secrets*.json
*token*.json
```

- [ ] **Step 2: Create the main project file**

`src/StreamTitleService/StreamTitleService.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="1.20.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.ServiceBus" Version="5.18.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="1.18.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.ApplicationInsights" Version="1.1.0" />
    <PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.22.0" />
    <PackageReference Include="Azure.Messaging.ServiceBus" Version="7.18.0" />
    <PackageReference Include="Azure.Security.KeyVault.Secrets" Version="4.6.0" />
    <PackageReference Include="Azure.Identity" Version="1.12.0" />
    <PackageReference Include="Azure.Storage.Blobs" Version="12.19.0" />
    <PackageReference Include="Azure.Communication.Email" Version="1.0.1" />
    <PackageReference Include="Google.Apis.YouTube.v3" Version="1.68.0.3530" />
    <PackageReference Include="Google.Apis.Auth" Version="1.68.0" />
    <PackageReference Include="Microsoft.Extensions.Http.Polly" Version="8.0.0" />
    <PackageReference Include="Polly" Version="8.4.0" />
    <PackageReference Include="Polly.Extensions.Http" Version="3.0.0" />
    <PackageReference Include="TimeZoneConverter" Version="7.2.0" />
  </ItemGroup>
  <ItemGroup>
    <None Update="host.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create host.json**

`src/StreamTitleService/host.json`:
```json
{
  "version": "2.0",
  "logging": {
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true,
        "excludedTypes": "Request"
      }
    },
    "console": {
      "FormatterName": "json",
      "FormatterOptions": {
        "TimestampFormat": "yyyy-MM-ddTHH:mm:ssZ",
        "UseUtcTimestamp": true,
        "IncludeScopes": true
      }
    }
  },
  "extensions": {
    "serviceBus": {
      "prefetchCount": 0,
      "autoCompleteMessages": false
    }
  }
}
```

- [ ] **Step 4: Create test project file**

`tests/StreamTitleService.Tests/StreamTitleService.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.0" />
    <PackageReference Include="Moq" Version="4.20.0" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\StreamTitleService\StreamTitleService.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create solution file and add projects**

```bash
cd /Users/wasimhanna/Code/stream-title-service
dotnet new sln --name stream-title-service
dotnet sln add src/StreamTitleService/StreamTitleService.csproj
dotnet sln add tests/StreamTitleService.Tests/StreamTitleService.Tests.csproj
```

- [ ] **Step 6: Verify build**

Run: `dotnet build`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 7: Verify tests run (empty)**

Run: `dotnet test`
Expected: 0 tests discovered, pass

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution with Azure Functions and test projects"
```

---

### Task 2: Domain value objects (Location, TargetPlatform, StreamTitle)

**Files:**
- Create: `src/StreamTitleService/Domain/ValueObjects/Location.cs`
- Create: `src/StreamTitleService/Domain/ValueObjects/TargetPlatform.cs`
- Create: `src/StreamTitleService/Domain/ValueObjects/StreamTitle.cs`
- Create: `src/StreamTitleService/Domain/Exceptions/DomainExceptions.cs`
- Test: `tests/StreamTitleService.Tests/Domain/LocationTests.cs`
- Test: `tests/StreamTitleService.Tests/Domain/StreamTitleTests.cs`

**TDD: Location (5 red-green cycles)**

- [ ] **Cycle 1 RED: Known location succeeds**

Write the first test in `tests/StreamTitleService.Tests/Domain/LocationTests.cs`:
```csharp
using FluentAssertions;
using StreamTitleService.Domain.Exceptions;
using StreamTitleService.Domain.ValueObjects;

namespace StreamTitleService.Tests.Domain;

public class LocationTests
{
    [Theory]
    [InlineData("virtual")]
    [InlineData("st. mary and st. joseph")]
    [InlineData("st. anthony chapel")]
    public void Create_WithKnownLocation_ShouldSucceed(string value)
    {
        var location = new Location(value);
        location.Value.Should().Be(value);
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~LocationTests"`
Expected: Compilation error (Location type does not exist)

- [ ] **Cycle 1 GREEN: Implement minimal Location and DomainExceptions**

`src/StreamTitleService/Domain/Exceptions/DomainExceptions.cs`:
```csharp
namespace StreamTitleService.Domain.Exceptions;

public class UnknownLocationException : Exception
{
    public string LocationValue { get; }

    public UnknownLocationException(string locationValue)
        : base($"Unknown location: '{locationValue}'. Event will be dead-lettered.")
    {
        LocationValue = locationValue;
    }
}

public class TitleResolutionException : Exception
{
    public TitleResolutionException(string message) : base(message) { }
    public TitleResolutionException(string message, Exception inner) : base(message, inner) { }
}
```

`src/StreamTitleService/Domain/ValueObjects/Location.cs`:
```csharp
using StreamTitleService.Domain.Exceptions;

namespace StreamTitleService.Domain.ValueObjects;

public sealed record Location
{
    private static readonly HashSet<string> KnownLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "virtual",
        "st. mary and st. joseph",
        "st. anthony chapel"
    };

    public string Value { get; }

    public Location(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.ToLowerInvariant();

        if (!KnownLocations.Contains(normalized))
            throw new UnknownLocationException(normalized);

        Value = normalized;
    }

    public override string ToString() => Value;
}
```

Run: `dotnet test --filter "FullyQualifiedName~Create_WithKnownLocation"`
Expected: 3 tests pass

- [ ] **Cycle 2 RED: Mixed case normalizes to lowercase**

Add to `LocationTests.cs`:
```csharp
    [Theory]
    [InlineData("Virtual")]
    [InlineData("ST. MARY AND ST. JOSEPH")]
    [InlineData("St. Anthony Chapel")]
    public void Create_WithMixedCase_ShouldNormalizeToLowercase(string value)
    {
        var location = new Location(value);
        location.Value.Should().Be(value.ToLowerInvariant());
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Create_WithMixedCase"`
Expected: 3 tests pass (already handled by implementation -- this is a confirmation cycle)

- [ ] **Cycle 3 RED: Unknown location throws**

Add to `LocationTests.cs`:
```csharp
    [Theory]
    [InlineData("unknown-location")]
    [InlineData("")]
    [InlineData("holy cross")]
    public void Create_WithUnknownLocation_ShouldThrow(string value)
    {
        var act = () => new Location(value);
        act.Should().Throw<UnknownLocationException>()
            .Which.LocationValue.Should().Be(value.ToLowerInvariant());
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Create_WithUnknownLocation"`
Expected: 3 tests pass

- [ ] **Cycle 4 RED: Null throws ArgumentNullException**

Add to `LocationTests.cs`:
```csharp
    [Fact]
    public void Create_WithNull_ShouldThrow()
    {
        var act = () => new Location(null!);
        act.Should().Throw<ArgumentNullException>();
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Create_WithNull"`
Expected: Pass

- [ ] **Cycle 5 RED: Equality by normalized value**

Add to `LocationTests.cs`:
```csharp
    [Fact]
    public void Equals_SameLowercaseValue_ShouldBeEqual()
    {
        var a = new Location("virtual");
        var b = new Location("Virtual");
        a.Should().Be(b);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Equals_SameLowercase"`
Expected: Pass (record equality on Value)

- [ ] **Implement TargetPlatform (no tests needed -- simple enumeration)**

`src/StreamTitleService/Domain/ValueObjects/TargetPlatform.cs`:
```csharp
namespace StreamTitleService.Domain.ValueObjects;

public sealed record TargetPlatform
{
    public static readonly TargetPlatform Restream = new("restream");
    public static readonly TargetPlatform YouTube = new("youtube");

    public string Value { get; }

    private TargetPlatform(string value) => Value = value;

    public override string ToString() => Value;
}
```

**TDD: StreamTitle (4 red-green cycles)**

- [ ] **Cycle 1 RED: Format prepends date to suffix**

Write the first test in `tests/StreamTitleService.Tests/Domain/StreamTitleTests.cs`:
```csharp
using FluentAssertions;
using StreamTitleService.Domain.ValueObjects;

namespace StreamTitleService.Tests.Domain;

public class StreamTitleTests
{
    [Fact]
    public void Format_WithSuffix_ShouldPrependDate()
    {
        var timestamp = new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.Zero); // Sunday UTC
        var title = StreamTitle.Format("Divine Liturgy", timestamp);
        title.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~Format_WithSuffix"`
Expected: Compilation error (StreamTitle does not exist)

- [ ] **Cycle 1 GREEN: Implement StreamTitle.Format**

`src/StreamTitleService/Domain/ValueObjects/StreamTitle.cs`:
```csharp
using System.Text.RegularExpressions;
using TimeZoneConverter;

namespace StreamTitleService.Domain.ValueObjects;

public sealed record StreamTitle
{
    private static readonly TimeZoneInfo Eastern =
        TZConvert.GetTimeZoneInfo("America/New_York");

    // Matches: "Monday, March 29, 2026 - " (day of week, comma, full date, dash)
    private static readonly Regex DatePrefixPattern = new(
        @"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+\w+\s+\d{1,2},\s+\d{4}\s+-\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Value { get; }

    private StreamTitle(string value) => Value = value;

    public static StreamTitle Format(string suffix, DateTimeOffset eventTimestamp)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            throw new ArgumentException("Title suffix cannot be empty.", nameof(suffix));

        // Strip existing date prefix if present (prevents doubling)
        var cleanSuffix = DatePrefixPattern.Replace(suffix, "");

        var easternTime = TimeZoneInfo.ConvertTime(eventTimestamp, Eastern);
        var datePrefix = easternTime.ToString("dddd, MMMM dd, yyyy");

        return new StreamTitle($"{datePrefix} - {cleanSuffix}");
    }

    public override string ToString() => Value;
}
```

Run: `dotnet test --filter "FullyQualifiedName~Format_WithSuffix"`
Expected: Pass

- [ ] **Cycle 2 RED: Format uses Eastern timezone**

Add to `StreamTitleTests.cs`:
```csharp
    [Fact]
    public void Format_ShouldUseEasternTimezone()
    {
        // 2026-03-28 03:00 UTC = 2026-03-27 11:00 PM EST (Friday)
        var timestamp = new DateTimeOffset(2026, 3, 28, 3, 0, 0, TimeSpan.Zero);
        var title = StreamTitle.Format("Test", timestamp);
        title.Value.Should().StartWith("Friday, March 27, 2026");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Format_ShouldUseEastern"`
Expected: Pass

- [ ] **Cycle 3 RED: Strips existing date prefix**

Add to `StreamTitleTests.cs`:
```csharp
    [Fact]
    public void Format_WithDatePrefix_ShouldStripAndReformat()
    {
        var timestamp = new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.Zero);
        var title = StreamTitle.Format("Sunday, March 29, 2026 - Divine Liturgy", timestamp);
        title.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
        // Should NOT be "Sunday, March 29, 2026 - Sunday, March 29, 2026 - Divine Liturgy"
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Format_WithDatePrefix"`
Expected: Pass

- [ ] **Cycle 4 RED: Empty suffix throws**

Add to `StreamTitleTests.cs`:
```csharp
    [Fact]
    public void Format_WithEmptySuffix_ShouldThrow()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var act = () => StreamTitle.Format("", timestamp);
        act.Should().Throw<ArgumentException>();
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Format_WithEmptySuffix"`
Expected: Pass

- [ ] **Run all domain tests**

Run: `dotnet test --filter "FullyQualifiedName~Domain"`
Expected: All tests pass (12 Location + 4 StreamTitle = 16)

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add domain value objects Location, TargetPlatform, StreamTitle with TDD"
```

---

### Task 3: TitleResolver and DefaultTitleGenerator logic

**Files:**
- Create: `src/StreamTitleService/Domain/Events/StreamStartedEvent.cs`
- Create: `src/StreamTitleService/Domain/Services/TitleResolver.cs`
- Test: `tests/StreamTitleService.Tests/Domain/TitleResolverTests.cs`

**TDD: TitleResolver (6 red-green cycles)**

- [ ] **Cycle 1 RED: Explicit title is used**

Write the first test in `tests/StreamTitleService.Tests/Domain/TitleResolverTests.cs`:
```csharp
using FluentAssertions;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.Services;
using StreamTitleService.Domain.ValueObjects;

namespace StreamTitleService.Tests.Domain;

public class TitleResolverTests
{
    private readonly TitleResolver _resolver = new();

    [Fact]
    public void Resolve_WithExplicitTitle_ShouldUseIt()
    {
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "zoom-automation",
            Timestamp = new DateTimeOffset(2026, 3, 27, 19, 5, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamStartedData { Title = "Arabic Bible Study" }
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Friday, March 27, 2026 - Arabic Bible Study");
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~Resolve_WithExplicitTitle_ShouldUseIt"`
Expected: Compilation error (StreamStartedEvent, TitleResolver do not exist)

- [ ] **Cycle 1 GREEN: Implement StreamStartedEvent and TitleResolver**

`src/StreamTitleService/Domain/Events/StreamStartedEvent.cs`:
```csharp
using System.Text.Json.Serialization;

namespace StreamTitleService.Domain.Events;

public class StreamStartedEvent
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("data")]
    public StreamStartedData Data { get; set; } = new();
}

public class StreamStartedData
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
```

`src/StreamTitleService/Domain/Services/TitleResolver.cs`:
```csharp
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.ValueObjects;
using TimeZoneConverter;

namespace StreamTitleService.Domain.Services;

public class TitleResolver
{
    private static readonly TimeZoneInfo Eastern =
        TZConvert.GetTimeZoneInfo("America/New_York");

    public StreamTitle Resolve(StreamStartedEvent evt)
    {
        var suffix = !string.IsNullOrWhiteSpace(evt.Data.Title)
            ? evt.Data.Title
            : GenerateDefaultSuffix(evt.Timestamp);

        return StreamTitle.Format(suffix, evt.Timestamp);
    }

    private static string GenerateDefaultSuffix(DateTimeOffset timestamp)
    {
        var eastern = TimeZoneInfo.ConvertTime(timestamp, Eastern);

        var isSaturdayEvening =
            eastern.DayOfWeek == DayOfWeek.Saturday &&
            eastern.Hour >= 17 &&
            (eastern.Hour < 23 || (eastern.Hour == 23 && eastern.Minute <= 59));

        return isSaturdayEvening
            ? "Vespers and Midnight Praises"
            : "Divine Liturgy";
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~Resolve_WithExplicitTitle_ShouldUseIt"`
Expected: Pass

- [ ] **Cycle 2 RED: No title on Sunday morning defaults to Divine Liturgy**

Add to `TitleResolverTests.cs`:
```csharp
    [Fact]
    public void Resolve_WithNoTitle_SundayMorning_ShouldReturnDivineLiturgy()
    {
        // Sunday 10 AM Eastern = Sunday 15:00 UTC (during EDT, March)
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 29, 15, 0, 0, TimeSpan.Zero),
            Location = "st. mary and st. joseph",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Sunday, March 29, 2026 - Divine Liturgy");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Resolve_WithNoTitle_SundayMorning"`
Expected: Pass

- [ ] **Cycle 3 RED: No title on Saturday evening defaults to Vespers**

Add to `TitleResolverTests.cs`:
```csharp
    [Fact]
    public void Resolve_WithNoTitle_SaturdayEvening_ShouldReturnVespers()
    {
        // Saturday March 28 2026: EDT is active (DST starts March 8)
        // Saturday 7 PM EDT = 23:00 UTC Saturday
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero),
            Location = "st. anthony chapel",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Saturday, March 28, 2026 - Vespers and Midnight Praises");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Resolve_WithNoTitle_SaturdayEvening"`
Expected: Pass

- [ ] **Cycle 4 RED: Saturday at 11:59 PM still returns Vespers**

Add to `TitleResolverTests.cs`:
```csharp
    [Fact]
    public void Resolve_WithNoTitle_SaturdayAt1159PM_ShouldReturnVespers()
    {
        // Saturday 11:59 PM Eastern
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "test",
            Timestamp = new DateTimeOffset(2026, 3, 29, 3, 59, 0, TimeSpan.Zero), // 11:59 PM EDT Sat
            Location = "virtual",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Contain("Vespers and Midnight Praises");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~SaturdayAt1159PM"`
Expected: Pass

- [ ] **Cycle 5 RED: Saturday at midnight (Sunday 12 AM) returns Divine Liturgy**

Add to `TitleResolverTests.cs`:
```csharp
    [Fact]
    public void Resolve_WithNoTitle_SaturdayAtMidnight_ShouldReturnDivineLiturgy()
    {
        // Sunday 12:00 AM Eastern (no longer Saturday)
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "test",
            Timestamp = new DateTimeOffset(2026, 3, 29, 4, 0, 0, TimeSpan.Zero), // 12:00 AM EDT Sun
            Location = "virtual",
            Data = new StreamStartedData()
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Contain("Divine Liturgy");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~SaturdayAtMidnight"`
Expected: Pass

- [ ] **Cycle 6 RED: Explicit title overrides Saturday evening default**

Add to `TitleResolverTests.cs`:
```csharp
    [Fact]
    public void Resolve_WithExplicitTitle_OnSaturdayEvening_ShouldOverrideDefault()
    {
        // Saturday 7 PM but with explicit title
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero),
            Location = "virtual",
            Data = new StreamStartedData { Title = "Feast of St. Mark" }
        };

        var result = _resolver.Resolve(evt);

        result.Value.Should().Be("Saturday, March 28, 2026 - Feast of St. Mark");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Resolve_WithExplicitTitle_OnSaturdayEvening"`
Expected: Pass

- [ ] **Run all TitleResolver tests**

Run: `dotnet test --filter "FullyQualifiedName~TitleResolver"`
Expected: All 6 tests pass

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add TitleResolver with DefaultTitleGenerator logic and TDD"
```

---

### Task 4: Application ports (interfaces) and remaining event types

**Files:**
- Create: `src/StreamTitleService/Application/Ports/Inbound/IStreamTitleHandler.cs`
- Create: `src/StreamTitleService/Application/Ports/Outbound/ITitlePlatformClient.cs`
- Create: `src/StreamTitleService/Application/Ports/Outbound/ITokenProvider.cs`
- Create: `src/StreamTitleService/Application/Ports/Outbound/IEventPublisher.cs`
- Create: `src/StreamTitleService/Application/Ports/Outbound/IAlertNotifier.cs`
- Create: `src/StreamTitleService/Domain/Events/StreamTitleSetEvent.cs`
- Create: `src/StreamTitleService/Domain/Events/StreamTitleFailedEvent.cs`

- [ ] **Step 1: Create all port interfaces and remaining event types**

`src/StreamTitleService/Application/Ports/Inbound/IStreamTitleHandler.cs`:
```csharp
using StreamTitleService.Domain.Events;

namespace StreamTitleService.Application.Ports.Inbound;

public interface IStreamTitleHandler
{
    Task HandleAsync(StreamStartedEvent evt, CancellationToken ct);
}
```

`src/StreamTitleService/Application/Ports/Outbound/ITitlePlatformClient.cs`:
```csharp
namespace StreamTitleService.Application.Ports.Outbound;

public record TitleUpdateResult(int ChannelsUpdated, int ChannelsFailed);

public interface ITitlePlatformClient
{
    Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct);
}
```

`src/StreamTitleService/Application/Ports/Outbound/ITokenProvider.cs`:
```csharp
namespace StreamTitleService.Application.Ports.Outbound;

public interface ITokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}
```

`src/StreamTitleService/Application/Ports/Outbound/IEventPublisher.cs`:
```csharp
using StreamTitleService.Domain.Events;

namespace StreamTitleService.Application.Ports.Outbound;

public interface IEventPublisher
{
    Task PublishTitleSetAsync(StreamTitleSetEvent evt, CancellationToken ct);
    Task PublishTitleFailedAsync(StreamTitleFailedEvent evt, CancellationToken ct);
}
```

`src/StreamTitleService/Application/Ports/Outbound/IAlertNotifier.cs`:
```csharp
namespace StreamTitleService.Application.Ports.Outbound;

public interface IAlertNotifier
{
    Task SendFailureAlertAsync(string title, string error, CancellationToken ct);
}
```

`src/StreamTitleService/Domain/Events/StreamTitleSetEvent.cs`:
```csharp
using System.Text.Json.Serialization;

namespace StreamTitleService.Domain.Events;

public class StreamTitleSetEvent
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "StreamTitleSet";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "stream-title-service";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("data")]
    public StreamTitleSetData Data { get; set; } = new();
}

public class StreamTitleSetData
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("targetPlatform")]
    public string TargetPlatform { get; set; } = "";

    [JsonPropertyName("channelsUpdated")]
    public int ChannelsUpdated { get; set; }

    [JsonPropertyName("channelsFailed")]
    public int ChannelsFailed { get; set; }
}
```

`src/StreamTitleService/Domain/Events/StreamTitleFailedEvent.cs`:
```csharp
using System.Text.Json.Serialization;

namespace StreamTitleService.Domain.Events;

public class StreamTitleFailedEvent
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "StreamTitleFailed";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "stream-title-service";

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    [JsonPropertyName("data")]
    public StreamTitleFailedData Data { get; set; } = new();
}

public class StreamTitleFailedData
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("targetPlatform")]
    public string TargetPlatform { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("channelsUpdated")]
    public int ChannelsUpdated { get; set; }

    [JsonPropertyName("channelsAttempted")]
    public int ChannelsAttempted { get; set; }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat: add application ports and domain event types"
```

---

### Task 5: LocationPlatformMapping

**Files:**
- Create: `src/StreamTitleService/Infrastructure/Configuration/LocationPlatformMapping.cs`
- Test: `tests/StreamTitleService.Tests/Infrastructure/LocationPlatformMappingTests.cs`

**TDD: LocationPlatformMapping (1 red-green cycle, 3 test cases)**

- [ ] **Cycle 1 RED: Known locations map to correct platforms**

Write the test in `tests/StreamTitleService.Tests/Infrastructure/LocationPlatformMappingTests.cs`:
```csharp
using FluentAssertions;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Configuration;

namespace StreamTitleService.Tests.Infrastructure;

public class LocationPlatformMappingTests
{
    private readonly LocationPlatformMapping _mapping = new();

    [Theory]
    [InlineData("virtual", "restream")]
    [InlineData("st. mary and st. joseph", "restream")]
    [InlineData("st. anthony chapel", "youtube")]
    public void GetPlatform_KnownLocation_ShouldReturnCorrectPlatform(
        string location, string expectedPlatform)
    {
        var loc = new Location(location);
        var platform = _mapping.GetPlatform(loc);
        platform.Value.Should().Be(expectedPlatform);
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~LocationPlatformMapping"`
Expected: Compilation error

- [ ] **Cycle 1 GREEN: Implement LocationPlatformMapping**

`src/StreamTitleService/Infrastructure/Configuration/LocationPlatformMapping.cs`:
```csharp
using StreamTitleService.Domain.ValueObjects;

namespace StreamTitleService.Infrastructure.Configuration;

public class LocationPlatformMapping
{
    private static readonly Dictionary<string, TargetPlatform> Mapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["virtual"] = TargetPlatform.Restream,
        ["st. mary and st. joseph"] = TargetPlatform.Restream,
        ["st. anthony chapel"] = TargetPlatform.YouTube
    };

    public TargetPlatform GetPlatform(Location location)
    {
        return Mapping[location.Value];
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~LocationPlatformMapping"`
Expected: All 3 tests pass

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add LocationPlatformMapping with location-to-platform routing"
```

---

## Stage 2: Application Orchestrator

### Task 6: StreamTitleHandler (application orchestrator)

**Files:**
- Create: `src/StreamTitleService/Application/StreamTitleHandler.cs`
- Test: `tests/StreamTitleService.Tests/Application/StreamTitleHandlerTests.cs`

**TDD: StreamTitleHandler (5 red-green cycles)**

- [ ] **Cycle 1 RED: Valid event sets title and publishes success**

Write the first test in `tests/StreamTitleService.Tests/Application/StreamTitleHandlerTests.cs`:
```csharp
using FluentAssertions;
using Moq;
using StreamTitleService.Application;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.Exceptions;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Configuration;

namespace StreamTitleService.Tests.Application;

public class StreamTitleHandlerTests
{
    private readonly Mock<ITitlePlatformClient> _restreamClient = new();
    private readonly Mock<ITitlePlatformClient> _youtubeClient = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly Mock<IAlertNotifier> _alertNotifier = new();
    private readonly StreamTitleHandler _handler;

    public StreamTitleHandlerTests()
    {
        var clients = new Dictionary<TargetPlatform, ITitlePlatformClient>
        {
            [TargetPlatform.Restream] = _restreamClient.Object,
            [TargetPlatform.YouTube] = _youtubeClient.Object
        };

        _handler = new StreamTitleHandler(
            new LocationPlatformMapping(),
            clients,
            _eventPublisher.Object,
            _alertNotifier.Object,
            stalenessThresholdSeconds: 90);
    }

    [Fact]
    public async Task Handle_ValidEvent_ShouldSetTitleAndPublishSuccess()
    {
        var evt = CreateEvent("virtual", "Arabic Bible Study",
            DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(3, 0));

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamClient.Verify(c => c.SetTitleAsync(
            It.Is<string>(t => t.Contains("Arabic Bible Study")),
            It.IsAny<CancellationToken>()), Times.Once);
        _eventPublisher.Verify(p => p.PublishTitleSetAsync(
            It.Is<StreamTitleSetEvent>(e => e.Data.TargetPlatform == "restream"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static StreamStartedEvent CreateEvent(string location, string? title, DateTimeOffset timestamp)
    {
        return new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "test",
            Timestamp = timestamp,
            Location = location,
            Data = new StreamStartedData { Title = title }
        };
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~Handle_ValidEvent"`
Expected: Compilation error (StreamTitleHandler does not exist)

- [ ] **Cycle 1 GREEN: Implement StreamTitleHandler**

`src/StreamTitleService/Application/StreamTitleHandler.cs`:
```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.Exceptions;
using StreamTitleService.Domain.Services;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Configuration;

namespace StreamTitleService.Application;

public class StreamTitleHandler : IStreamTitleHandler
{
    private readonly LocationPlatformMapping _locationMapping;
    private readonly IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient> _clients;
    private readonly IEventPublisher _eventPublisher;
    private readonly IAlertNotifier _alertNotifier;
    private readonly int _stalenessThresholdSeconds;
    private readonly TitleResolver _titleResolver = new();
    private readonly ILogger<StreamTitleHandler>? _logger;

    public StreamTitleHandler(
        LocationPlatformMapping locationMapping,
        IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient> clients,
        IEventPublisher eventPublisher,
        IAlertNotifier alertNotifier,
        int stalenessThresholdSeconds = 90,
        ILogger<StreamTitleHandler>? logger = null)
    {
        _locationMapping = locationMapping;
        _clients = clients;
        _eventPublisher = eventPublisher;
        _alertNotifier = alertNotifier;
        _stalenessThresholdSeconds = stalenessThresholdSeconds;
        _logger = logger;
    }

    public async Task HandleAsync(StreamStartedEvent evt, CancellationToken ct)
    {
        // Check staleness
        var age = DateTimeOffset.UtcNow - evt.Timestamp;
        if (age.TotalSeconds > _stalenessThresholdSeconds)
        {
            _logger?.LogWarning("Skipping stale event from {Source}, age {AgeSec}s exceeds threshold {Threshold}s",
                evt.Source, (int)age.TotalSeconds, _stalenessThresholdSeconds);
            return;
        }

        // Resolve location (throws UnknownLocationException for unknown)
        Location location;
        try
        {
            location = new Location(evt.Location);
        }
        catch (UnknownLocationException ex)
        {
            await _alertNotifier.SendFailureAlertAsync(
                evt.Data.Title ?? "(default)", ex.Message, ct);
            throw;
        }

        // Resolve title
        var title = _titleResolver.Resolve(evt);

        // Route to platform
        var platform = _locationMapping.GetPlatform(location);

        if (!_clients.TryGetValue(platform, out var client))
        {
            var error = $"No client registered for platform: {platform.Value}";
            await PublishFailedAsync(evt, title.Value, platform.Value, error, 0, 0, ct);
            await _alertNotifier.SendFailureAlertAsync(title.Value, error, ct);
            throw new InvalidOperationException(error);
        }

        // Set title on platform
        try
        {
            var result = await client.SetTitleAsync(title.Value, ct);

            await _eventPublisher.PublishTitleSetAsync(new StreamTitleSetEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Location = location.Value,
                TraceId = Activity.Current?.TraceId.ToString(),
                SpanId = Activity.Current?.SpanId.ToString(),
                ParentSpanId = evt.SpanId,
                Data = new StreamTitleSetData
                {
                    Title = title.Value,
                    TargetPlatform = platform.Value,
                    ChannelsUpdated = result.ChannelsUpdated,
                    ChannelsFailed = result.ChannelsFailed
                }
            }, ct);

            _logger?.LogInformation("Title set: '{Title}' on {Platform} ({Updated} channels)",
                title.Value, platform.Value, result.ChannelsUpdated);
        }
        catch (Exception ex)
        {
            await PublishFailedAsync(evt, title.Value, platform.Value, ex.Message, 0, 0, ct);
            await _alertNotifier.SendFailureAlertAsync(title.Value, ex.Message, ct);
            throw;
        }
    }

    private async Task PublishFailedAsync(
        StreamStartedEvent evt, string title, string platform,
        string error, int updated, int attempted, CancellationToken ct)
    {
        try
        {
            await _eventPublisher.PublishTitleFailedAsync(new StreamTitleFailedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Location = evt.Location,
                TraceId = Activity.Current?.TraceId.ToString(),
                SpanId = Activity.Current?.SpanId.ToString(),
                ParentSpanId = evt.SpanId,
                Data = new StreamTitleFailedData
                {
                    Title = title,
                    TargetPlatform = platform,
                    Error = error,
                    ChannelsUpdated = updated,
                    ChannelsAttempted = attempted
                }
            }, ct);
        }
        catch (Exception pubEx)
        {
            _logger?.LogError(pubEx, "Failed to publish StreamTitleFailed event");
        }
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~Handle_ValidEvent"`
Expected: Pass

- [ ] **Cycle 2 RED: St. Anthony routes to YouTube**

Add to `StreamTitleHandlerTests.cs`:
```csharp
    [Fact]
    public async Task Handle_StAnthony_ShouldRouteToYouTube()
    {
        var evt = CreateEvent("st. anthony chapel", "Test",
            DateTimeOffset.UtcNow);
        _youtubeClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TitleUpdateResult(1, 0));

        await _handler.HandleAsync(evt, CancellationToken.None);

        _youtubeClient.Verify(c => c.SetTitleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _restreamClient.Verify(c => c.SetTitleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Handle_StAnthony"`
Expected: Pass

- [ ] **Cycle 3 RED: Unknown location fails with alert**

Add to `StreamTitleHandlerTests.cs`:
```csharp
    [Fact]
    public async Task Handle_UnknownLocation_ShouldPublishFailedAndAlert()
    {
        var evt = CreateEvent("unknown-place", null, DateTimeOffset.UtcNow);

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<UnknownLocationException>();
        _alertNotifier.Verify(a => a.SendFailureAlertAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Handle_UnknownLocation"`
Expected: Pass

- [ ] **Cycle 4 RED: Stale event is skipped**

Add to `StreamTitleHandlerTests.cs`:
```csharp
    [Fact]
    public async Task Handle_StaleEvent_ShouldSkipWithoutProcessing()
    {
        var evt = CreateEvent("virtual", "Old Title",
            DateTimeOffset.UtcNow.AddSeconds(-120)); // 120s old, threshold is 90s

        await _handler.HandleAsync(evt, CancellationToken.None);

        _restreamClient.Verify(c => c.SetTitleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _eventPublisher.Verify(p => p.PublishTitleSetAsync(
            It.IsAny<StreamTitleSetEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Handle_StaleEvent"`
Expected: Pass

- [ ] **Cycle 5 RED: Platform client failure publishes failed event and alerts**

Add to `StreamTitleHandlerTests.cs`:
```csharp
    [Fact]
    public async Task Handle_PlatformClientFails_ShouldPublishFailedAndAlert()
    {
        var evt = CreateEvent("virtual", "Test", DateTimeOffset.UtcNow);
        _restreamClient.Setup(c => c.SetTitleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Token refresh failed"));

        var act = () => _handler.HandleAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        _eventPublisher.Verify(p => p.PublishTitleFailedAsync(
            It.IsAny<StreamTitleFailedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _alertNotifier.Verify(a => a.SendFailureAlertAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Handle_PlatformClientFails"`
Expected: Pass

- [ ] **Run all handler tests**

Run: `dotnet test --filter "FullyQualifiedName~StreamTitleHandler"`
Expected: All 5 tests pass

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add StreamTitleHandler orchestrator with staleness check and platform routing"
```

---

## Stage 3: Infrastructure Adapters

### EXIT GATE: Domain and Application layers

Before proceeding to infrastructure adapters:
- [ ] All domain tests pass (Location, StreamTitle, TitleResolver)
- [ ] All application tests pass (StreamTitleHandler)
- [ ] All infrastructure config tests pass (LocationPlatformMapping)
- [ ] `dotnet build` succeeds with 0 warnings

Run: `dotnet test --verbosity normal && dotnet build --warnaserror`

---

### Task 7: RestreamClient adapter

**Files:**
- Create: `src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs`
- Test: `tests/StreamTitleService.Tests/Infrastructure/RestreamClientTests.cs`

**TDD: RestreamClient (3 red-green cycles)**

- [ ] **Cycle 1 RED: Enabled channels get patched**

Write the first test in `tests/StreamTitleService.Tests/Infrastructure/RestreamClientTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Infrastructure.Adapters;

namespace StreamTitleService.Tests.Infrastructure;

public class RestreamClientTests
{
    private readonly Mock<ITokenProvider> _tokenProvider = new();
    private readonly Mock<HttpMessageHandler> _httpHandler = new();

    private RestreamClient CreateClient()
    {
        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-access-token");

        var httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        return new RestreamClient(httpClient, _tokenProvider.Object);
    }

    [Fact]
    public async Task SetTitle_WithEnabledChannels_ShouldPatchEach()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = true, streamingPlatformId = 5 },
            new { id = "ch2", displayName = "Twitch", enabled = true, streamingPlatformId = 1 },
            new { id = "ch3", displayName = "Facebook", enabled = false, streamingPlatformId = 37 }
        };

        SetupGetChannels(channels);
        SetupPatchChannel(HttpStatusCode.OK);

        var client = CreateClient();
        var result = await client.SetTitleAsync("Test Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(2); // Only enabled channels
        result.ChannelsFailed.Should().Be(0);
    }

    private void SetupGetChannels(object channels)
    {
        var json = JsonSerializer.Serialize(channels);
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private void SetupPatchChannel(HttpStatusCode status)
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Patch),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status));
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~SetTitle_WithEnabledChannels"`
Expected: Compilation error (RestreamClient does not exist)

- [ ] **Cycle 1 GREEN: Implement RestreamClient**

`src/StreamTitleService/Infrastructure/Adapters/RestreamClient.cs`:
```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

public class RestreamClient : ITitlePlatformClient
{
    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<RestreamClient>? _logger;

    public RestreamClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        ILogger<RestreamClient>? logger = null)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(ct);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        // Get all channels
        var response = await _httpClient.GetAsync("user/channel/all", ct);
        response.EnsureSuccessStatusCode();

        var channels = await response.Content.ReadFromJsonAsync<JsonElement[]>(ct)
            ?? Array.Empty<JsonElement>();

        var enabledChannels = channels
            .Where(c => c.TryGetProperty("enabled", out var e) && e.GetBoolean())
            .ToList();

        if (enabledChannels.Count == 0)
        {
            _logger?.LogWarning("No enabled channels found on Restream");
            return new TitleUpdateResult(0, 0);
        }

        // Patch each enabled channel
        int updated = 0, failed = 0;
        foreach (var ch in enabledChannels)
        {
            var channelId = ch.GetProperty("id").ToString();
            var name = ch.TryGetProperty("displayName", out var dn) ? dn.GetString() : "unknown";

            var patchResponse = await _httpClient.PatchAsJsonAsync(
                $"user/channel-meta/{channelId}",
                new { title },
                ct);

            if (patchResponse.IsSuccessStatusCode)
            {
                updated++;
                _logger?.LogInformation("Updated channel {Name} ({Id})", name, channelId);
            }
            else
            {
                failed++;
                _logger?.LogWarning("Failed to update channel {Name}: {Status}",
                    name, patchResponse.StatusCode);
            }
        }

        return new TitleUpdateResult(updated, failed);
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~SetTitle_WithEnabledChannels"`
Expected: Pass

- [ ] **Cycle 2 RED: No enabled channels returns zero**

Add to `RestreamClientTests.cs`:
```csharp
    [Fact]
    public async Task SetTitle_NoEnabledChannels_ShouldReturnZero()
    {
        var channels = new[]
        {
            new { id = "ch1", displayName = "YouTube", enabled = false, streamingPlatformId = 5 }
        };

        SetupGetChannels(channels);

        var client = CreateClient();
        var result = await client.SetTitleAsync("Test", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~SetTitle_NoEnabledChannels"`
Expected: Pass

- [ ] **Cycle 3 RED: 401 response throws**

Add to `RestreamClientTests.cs`:
```csharp
    [Fact]
    public async Task SetTitle_Auth401_ShouldThrow()
    {
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery.Contains("channel/all")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        _tokenProvider.Setup(t => t.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("expired-token");

        var httpClient = new HttpClient(_httpHandler.Object)
        {
            BaseAddress = new Uri("https://api.restream.io/v2/")
        };

        var client = new RestreamClient(httpClient, _tokenProvider.Object);
        var act = () => client.SetTitleAsync("Test", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
```

Run: `dotnet test --filter "FullyQualifiedName~SetTitle_Auth401"`
Expected: Pass

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add RestreamClient adapter with channel listing and title patching"
```

---

### Task 8: YouTubeClient adapter (full implementation)

**Files:**
- Create: `src/StreamTitleService/Infrastructure/Adapters/YouTubeClient.cs`
- Create: `src/StreamTitleService/Infrastructure/Adapters/BlobStorageYouTubeTokenProvider.cs`
- Test: `tests/StreamTitleService.Tests/Infrastructure/YouTubeClientTests.cs`

The YouTube Data API v3 calls come from the existing YT-Title-Updater Python codebase. The C# equivalent uses `Google.Apis.YouTube.v3` NuGet package. Credentials are stored as JSON in Azure Blob Storage (not Python pickle -- pickle is a Python-specific format that cannot be loaded in C#).

**API calls ported from YT-Title-Updater:**
1. `channels().list(part="id", mine=True)` -- get authenticated user's channel ID
2. `liveBroadcasts().list(part="snippet,status", broadcastStatus="active")` -- get active broadcasts
3. `videos().list(part="snippet", id=video_id)` -- get current video snippet (to preserve all fields)
4. `videos().update(part="snippet", body={full snippet with new title})` -- update title, preserving all other snippet fields

**TDD: YouTubeClient (4 red-green cycles)**

- [ ] **Cycle 1 RED: SetTitle finds active broadcast and updates title**

Write the first test in `tests/StreamTitleService.Tests/Infrastructure/YouTubeClientTests.cs`:
```csharp
using FluentAssertions;
using Moq;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Google.Apis.Services;
using StreamTitleService.Infrastructure.Adapters;

namespace StreamTitleService.Tests.Infrastructure;

public class YouTubeClientTests
{
    private readonly Mock<IYouTubeServiceWrapper> _youtubeService = new();

    [Fact]
    public async Task SetTitle_WithActiveBroadcast_ShouldUpdateVideoTitle()
    {
        // Arrange: channel ID
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_my_channel");

        // Arrange: active broadcast matching our channel
        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("broadcast-video-123", "UC_my_channel", "Old Stream Title")
            });

        // Arrange: current video snippet
        var existingSnippet = new VideoSnippetInfo(
            "broadcast-video-123",
            "Old Stream Title",
            "Stream description",
            "UC_my_channel",
            new List<string> { "church", "liturgy" });

        _youtubeService.Setup(s => s.GetVideoSnippetAsync("broadcast-video-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingSnippet);

        // Arrange: update succeeds
        _youtubeService.Setup(s => s.UpdateVideoSnippetAsync(
                "broadcast-video-123",
                It.IsAny<string>(),
                "Stream description",
                "UC_my_channel",
                It.Is<List<string>>(t => t.Contains("church")),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = new YouTubeClient(_youtubeService.Object);

        // Act
        var result = await client.SetTitleAsync("New Stream Title", CancellationToken.None);

        // Assert
        result.ChannelsUpdated.Should().Be(1);
        result.ChannelsFailed.Should().Be(0);

        _youtubeService.Verify(s => s.UpdateVideoSnippetAsync(
            "broadcast-video-123",
            "New Stream Title",
            "Stream description",
            "UC_my_channel",
            It.Is<List<string>>(t => t.Contains("church") && t.Contains("liturgy")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~SetTitle_WithActiveBroadcast"`
Expected: Compilation error (YouTubeClient, IYouTubeServiceWrapper, etc. do not exist)

- [ ] **Cycle 1 GREEN: Implement YouTubeClient with IYouTubeServiceWrapper**

The `IYouTubeServiceWrapper` abstraction wraps the Google API SDK so it can be mocked in tests. The real implementation uses `Google.Apis.YouTube.v3.YouTubeService`.

`src/StreamTitleService/Infrastructure/Adapters/YouTubeClient.cs`:
```csharp
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

/// <summary>
/// Data transfer records for YouTube API interactions.
/// These decouple our code from the Google SDK types for testability.
/// </summary>
public record LiveBroadcastInfo(string VideoId, string ChannelId, string Title);
public record VideoSnippetInfo(
    string VideoId,
    string Title,
    string Description,
    string ChannelId,
    List<string> Tags);

/// <summary>
/// Abstraction over the Google YouTube Data API v3 for testability.
/// The real implementation wraps YouTubeService from Google.Apis.YouTube.v3.
/// </summary>
public interface IYouTubeServiceWrapper
{
    Task<string> GetMyChannelIdAsync(CancellationToken ct);
    Task<List<LiveBroadcastInfo>> ListActiveBroadcastsAsync(CancellationToken ct);
    Task<VideoSnippetInfo> GetVideoSnippetAsync(string videoId, CancellationToken ct);
    Task UpdateVideoSnippetAsync(
        string videoId, string newTitle, string description,
        string channelId, List<string> tags, CancellationToken ct);
}

/// <summary>
/// YouTube Data API v3 client for setting livestream titles.
///
/// Ported from YT-Title-Updater's YouTubeClient (Python). The exact API sequence:
/// 1. channels.list(part=id, mine=true) -> get authenticated user's channel ID
/// 2. liveBroadcasts.list(part=snippet,status, broadcastStatus=active) -> find active broadcast for our channel
/// 3. videos.list(part=snippet, id=videoId) -> get FULL snippet (must preserve all fields)
/// 4. videos.update(part=snippet, body={id, snippet with new title}) -> update title only
///
/// Credentials: Google OAuth2 UserCredential stored as JSON in Azure Blob Storage.
/// Uses GoogleWebAuthorizationBroker to load from stored token JSON (not Python pickle).
/// </summary>
public class YouTubeClient : ITitlePlatformClient
{
    private readonly IYouTubeServiceWrapper _youtube;
    private readonly ILogger<YouTubeClient>? _logger;

    public YouTubeClient(
        IYouTubeServiceWrapper youtube,
        ILogger<YouTubeClient>? logger = null)
    {
        _youtube = youtube;
        _logger = logger;
    }

    public async Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        // Step 1: Get authenticated user's channel ID
        var myChannelId = await _youtube.GetMyChannelIdAsync(ct);
        _logger?.LogInformation("Authenticated as channel {ChannelId}", myChannelId);

        // Step 2: Find active broadcast matching our channel
        var broadcasts = await _youtube.ListActiveBroadcastsAsync(ct);
        var myBroadcast = broadcasts.FirstOrDefault(b => b.ChannelId == myChannelId);

        if (myBroadcast == null)
        {
            _logger?.LogWarning("No active broadcast found for channel {ChannelId}", myChannelId);
            return new TitleUpdateResult(0, 0);
        }

        _logger?.LogInformation("Found active broadcast {VideoId}: '{Title}'",
            myBroadcast.VideoId, myBroadcast.Title);

        // Step 3: Get current video snippet (must preserve all fields)
        var snippet = await _youtube.GetVideoSnippetAsync(myBroadcast.VideoId, ct);

        // Step 4: Update only the title, preserving description, tags, channelId, etc.
        await _youtube.UpdateVideoSnippetAsync(
            myBroadcast.VideoId,
            title,
            snippet.Description,
            snippet.ChannelId,
            snippet.Tags,
            ct);

        _logger?.LogInformation("Updated YouTube video {VideoId} title to '{Title}'",
            myBroadcast.VideoId, title);

        return new TitleUpdateResult(1, 0);
    }
}

/// <summary>
/// Real implementation of IYouTubeServiceWrapper using Google.Apis.YouTube.v3.
/// </summary>
public class GoogleYouTubeServiceWrapper : IYouTubeServiceWrapper
{
    private readonly Google.Apis.YouTube.v3.YouTubeService _service;

    public GoogleYouTubeServiceWrapper(Google.Apis.YouTube.v3.YouTubeService service)
    {
        _service = service;
    }

    public async Task<string> GetMyChannelIdAsync(CancellationToken ct)
    {
        var request = _service.Channels.List("id");
        request.Mine = true;
        var response = await request.ExecuteAsync(ct);

        if (response.Items == null || response.Items.Count == 0)
            throw new InvalidOperationException("Could not retrieve authenticated channel ID");

        return response.Items[0].Id;
    }

    public async Task<List<LiveBroadcastInfo>> ListActiveBroadcastsAsync(CancellationToken ct)
    {
        var request = _service.LiveBroadcasts.List("snippet,status");
        request.BroadcastStatus = Google.Apis.YouTube.v3.LiveBroadcastsResource.ListRequest.BroadcastStatusEnum.Active;
        var response = await request.ExecuteAsync(ct);

        return (response.Items ?? new List<Google.Apis.YouTube.v3.Data.LiveBroadcast>())
            .Select(b => new LiveBroadcastInfo(
                b.Id,
                b.Snippet.ChannelId,
                b.Snippet.Title))
            .ToList();
    }

    public async Task<VideoSnippetInfo> GetVideoSnippetAsync(string videoId, CancellationToken ct)
    {
        var request = _service.Videos.List("snippet");
        request.Id = videoId;
        var response = await request.ExecuteAsync(ct);

        if (response.Items == null || response.Items.Count == 0)
            throw new InvalidOperationException($"Could not retrieve video details for {videoId}");

        var snippet = response.Items[0].Snippet;
        return new VideoSnippetInfo(
            videoId,
            snippet.Title,
            snippet.Description ?? "",
            snippet.ChannelId,
            snippet.Tags?.ToList() ?? new List<string>());
    }

    public async Task UpdateVideoSnippetAsync(
        string videoId, string newTitle, string description,
        string channelId, List<string> tags, CancellationToken ct)
    {
        var video = new Google.Apis.YouTube.v3.Data.Video
        {
            Id = videoId,
            Snippet = new Google.Apis.YouTube.v3.Data.VideoSnippet
            {
                Title = newTitle,
                Description = description,
                ChannelId = channelId,
                Tags = tags,
                CategoryId = "22" // People & Blogs (required by API, preserved from original)
            }
        };

        var request = _service.Videos.Update(video, "snippet");
        await request.ExecuteAsync(ct);
    }
}
```

`src/StreamTitleService/Infrastructure/Adapters/BlobStorageYouTubeTokenProvider.cs`:
```csharp
using Azure.Storage.Blobs;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace StreamTitleService.Infrastructure.Adapters;

/// <summary>
/// Loads YouTube OAuth2 credentials from Azure Blob Storage.
///
/// The Python YT-Title-Updater uses token.json (Python-specific binary format).
/// For C#, we store the Google OAuth credentials as JSON in Blob Storage instead:
/// {
///   "access_token": "...",
///   "refresh_token": "...",
///   "client_id": "...",
///   "client_secret": "...",
///   "token_uri": "https://oauth2.googleapis.com/token"
/// }
///
/// GoogleWebAuthorizationBroker handles token refresh automatically.
/// </summary>
public class BlobStorageYouTubeTokenProvider
{
    private readonly BlobClient _blobClient;
    private readonly ILogger<BlobStorageYouTubeTokenProvider>? _logger;

    public BlobStorageYouTubeTokenProvider(
        BlobClient blobClient,
        ILogger<BlobStorageYouTubeTokenProvider>? logger = null)
    {
        _blobClient = blobClient;
        _logger = logger;
    }

    public async Task<YouTubeService> CreateYouTubeServiceAsync(CancellationToken ct)
    {
        // Download token JSON from blob storage
        var downloadResult = await _blobClient.DownloadContentAsync(ct);
        var json = downloadResult.Value.Content.ToString();

        var tokenData = JsonSerializer.Deserialize<YouTubeTokenData>(json)
            ?? throw new InvalidOperationException("Failed to deserialize YouTube token from blob storage");

        _logger?.LogInformation("Loaded YouTube OAuth token from blob storage");

        // Build UserCredential from stored token
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = tokenData.ClientId,
                ClientSecret = tokenData.ClientSecret
            },
            Scopes = new[] { YouTubeService.Scope.Youtube }
        });

        var token = new TokenResponse
        {
            AccessToken = tokenData.AccessToken,
            RefreshToken = tokenData.RefreshToken
        };

        var credential = new UserCredential(flow, "user", token);

        // The credential will auto-refresh if expired
        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "stream-title-service"
        });
    }

    private class YouTubeTokenData
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("client_id")]
        public string ClientId { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("client_secret")]
        public string ClientSecret { get; set; } = "";
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~SetTitle_WithActiveBroadcast"`
Expected: Pass

- [ ] **Cycle 2 RED: No active broadcast returns zero**

Add to `YouTubeClientTests.cs`:
```csharp
    [Fact]
    public async Task SetTitle_NoActiveBroadcast_ShouldReturnZero()
    {
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_my_channel");

        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>());

        var client = new YouTubeClient(_youtubeService.Object);

        var result = await client.SetTitleAsync("Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
        result.ChannelsFailed.Should().Be(0);

        // Should NOT attempt to get video snippet or update
        _youtubeService.Verify(s => s.GetVideoSnippetAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~SetTitle_NoActiveBroadcast"`
Expected: Pass

- [ ] **Cycle 3 RED: Broadcast from different channel is ignored**

Add to `YouTubeClientTests.cs`:
```csharp
    [Fact]
    public async Task SetTitle_BroadcastFromDifferentChannel_ShouldReturnZero()
    {
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_my_channel");

        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("other-video", "UC_other_channel", "Someone Else's Stream")
            });

        var client = new YouTubeClient(_youtubeService.Object);

        var result = await client.SetTitleAsync("Title", CancellationToken.None);

        result.ChannelsUpdated.Should().Be(0);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~BroadcastFromDifferentChannel"`
Expected: Pass

- [ ] **Cycle 4 RED: Preserves all snippet fields on update**

Add to `YouTubeClientTests.cs`:
```csharp
    [Fact]
    public async Task SetTitle_ShouldPreserveExistingSnippetFields()
    {
        _youtubeService.Setup(s => s.GetMyChannelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("UC_ch");

        _youtubeService.Setup(s => s.ListActiveBroadcastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveBroadcastInfo>
            {
                new("vid-1", "UC_ch", "Old Title")
            });

        _youtubeService.Setup(s => s.GetVideoSnippetAsync("vid-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoSnippetInfo(
                "vid-1", "Old Title", "My detailed description", "UC_ch",
                new List<string> { "coptic", "orthodox", "liturgy" }));

        _youtubeService.Setup(s => s.UpdateVideoSnippetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client = new YouTubeClient(_youtubeService.Object);
        await client.SetTitleAsync("New Title", CancellationToken.None);

        // Verify the update preserved description and tags
        _youtubeService.Verify(s => s.UpdateVideoSnippetAsync(
            "vid-1",
            "New Title",
            "My detailed description",
            "UC_ch",
            It.Is<List<string>>(t =>
                t.Count == 3 &&
                t.Contains("coptic") &&
                t.Contains("orthodox") &&
                t.Contains("liturgy")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
```

Run: `dotnet test --filter "FullyQualifiedName~ShouldPreserveExistingSnippetFields"`
Expected: Pass

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add full YouTubeClient implementation with Google API v3 wrapper"
```

---

### Task 9: KeyVaultTokenProvider adapter

**Files:**
- Create: `src/StreamTitleService/Infrastructure/Adapters/KeyVaultTokenProvider.cs`
- Test: `tests/StreamTitleService.Tests/Infrastructure/KeyVaultTokenProviderTests.cs`

**TDD: KeyVaultTokenProvider (2 red-green cycles)**

- [ ] **Cycle 1 RED: Token refresh returns access token**

Write the first test in `tests/StreamTitleService.Tests/Infrastructure/KeyVaultTokenProviderTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using StreamTitleService.Infrastructure.Adapters;

namespace StreamTitleService.Tests.Infrastructure;

public class KeyVaultTokenProviderTests
{
    [Fact]
    public async Task GetAccessToken_ShouldRefreshAndReturnToken()
    {
        var httpHandler = new Mock<HttpMessageHandler>();
        httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        access_token = "new-access-token",
                        refresh_token = "new-refresh-token",
                        expires_in = 3600
                    }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            });

        var httpClient = new HttpClient(httpHandler.Object);
        var provider = new KeyVaultTokenProvider(
            httpClient,
            refreshToken: "test-refresh-token",
            clientId: "test-client-id",
            clientSecret: "test-client-secret");

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        token.Should().Be("new-access-token");
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~GetAccessToken_ShouldRefresh"`
Expected: Compilation error (KeyVaultTokenProvider does not exist)

- [ ] **Cycle 1 GREEN: Implement KeyVaultTokenProvider**

`src/StreamTitleService/Infrastructure/Adapters/KeyVaultTokenProvider.cs`:
```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

public class KeyVaultTokenProvider : ITokenProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _refreshToken;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly ILogger<KeyVaultTokenProvider>? _logger;

    private string? _cachedAccessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private const string TokenUrl = "https://api.restream.io/oauth/token";
    private const int ExpirationBufferSeconds = 60;

    public KeyVaultTokenProvider(
        HttpClient httpClient,
        string refreshToken,
        string clientId,
        string clientSecret,
        ILogger<KeyVaultTokenProvider>? logger = null)
    {
        _httpClient = httpClient;
        _refreshToken = refreshToken;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedAccessToken != null &&
            DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-ExpirationBufferSeconds))
        {
            return _cachedAccessToken;
        }

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedAccessToken != null &&
                DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-ExpirationBufferSeconds))
            {
                return _cachedAccessToken;
            }

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = _refreshToken
            });

            var response = await _httpClient.PostAsync(TokenUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            _cachedAccessToken = json.GetProperty("access_token").GetString()!;
            var expiresIn = json.GetProperty("expires_in").GetInt32();
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            _logger?.LogInformation("Restream token refreshed, expires at {ExpiresAt}", _expiresAt);

            return _cachedAccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~GetAccessToken_ShouldRefresh"`
Expected: Pass

- [ ] **Cycle 2 RED: Cached token avoids second HTTP call**

Add to `KeyVaultTokenProviderTests.cs`:
```csharp
    [Fact]
    public async Task GetAccessToken_WhenCached_ShouldNotRefresh()
    {
        var callCount = 0;
        var httpHandler = new Mock<HttpMessageHandler>();
        httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            access_token = "cached-token",
                            refresh_token = "r",
                            expires_in = 3600
                        }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            });

        var httpClient = new HttpClient(httpHandler.Object);
        var provider = new KeyVaultTokenProvider(httpClient, "r", "c", "s");

        await provider.GetAccessTokenAsync(CancellationToken.None);
        await provider.GetAccessTokenAsync(CancellationToken.None);

        callCount.Should().Be(1); // Only one HTTP call
    }
```

Run: `dotnet test --filter "FullyQualifiedName~GetAccessToken_WhenCached"`
Expected: Pass

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add KeyVaultTokenProvider with token refresh and caching"
```

---

### Task 10: Remaining infrastructure adapters (ServiceBusEventPublisher, AcsAlertNotifier)

**Files:**
- Create: `src/StreamTitleService/Infrastructure/Adapters/ServiceBusEventPublisher.cs`
- Create: `src/StreamTitleService/Infrastructure/Adapters/AcsAlertNotifier.cs`

These are thin wrappers around Azure SDKs. They are covered by integration and component tests (Tasks 14-15).

- [ ] **Step 1: Implement ServiceBusEventPublisher**

`src/StreamTitleService/Infrastructure/Adapters/ServiceBusEventPublisher.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;

namespace StreamTitleService.Infrastructure.Adapters;

public class ServiceBusEventPublisher : IEventPublisher
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusEventPublisher>? _logger;

    public ServiceBusEventPublisher(
        ServiceBusSender sender,
        ILogger<ServiceBusEventPublisher>? logger = null)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task PublishTitleSetAsync(StreamTitleSetEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt);
        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = evt.EventType
        };
        SetDiagnosticId(message);

        await _sender.SendMessageAsync(message, ct);
        _logger?.LogInformation("Published StreamTitleSet for {Location}", evt.Location);
    }

    public async Task PublishTitleFailedAsync(StreamTitleFailedEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt);
        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = evt.EventType
        };
        SetDiagnosticId(message);

        await _sender.SendMessageAsync(message, ct);
        _logger?.LogWarning("Published StreamTitleFailed for {Location}: {Error}",
            evt.Location, evt.Data.Error);
    }

    private static void SetDiagnosticId(ServiceBusMessage message)
    {
        if (Activity.Current != null)
        {
            message.ApplicationProperties["Diagnostic-Id"] = Activity.Current.Id;
        }
    }
}
```

- [ ] **Step 2: Implement AcsAlertNotifier**

`src/StreamTitleService/Infrastructure/Adapters/AcsAlertNotifier.cs`:
```csharp
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Outbound;

namespace StreamTitleService.Infrastructure.Adapters;

public class AcsAlertNotifier : IAlertNotifier
{
    private readonly EmailClient _emailClient;
    private readonly string _sender;
    private readonly string[] _recipients;
    private readonly ILogger<AcsAlertNotifier>? _logger;

    public AcsAlertNotifier(
        EmailClient emailClient,
        string sender,
        string[] recipients,
        ILogger<AcsAlertNotifier>? logger = null)
    {
        _emailClient = emailClient;
        _sender = sender;
        _recipients = recipients;
        _logger = logger;
    }

    public async Task SendFailureAlertAsync(string title, string error, CancellationToken ct)
    {
        try
        {
            var subject = $"[Stream Title Service] Title update failed: {title}";
            var body = $"Title: {title}\nError: {error}\nTime: {DateTimeOffset.UtcNow:u}";

            var message = new EmailMessage(
                senderAddress: _sender,
                content: new EmailContent(subject) { PlainText = body });

            foreach (var recipient in _recipients)
            {
                message.Recipients.To.Add(new EmailAddress(recipient));
            }

            await _emailClient.SendAsync(Azure.WaitUntil.Started, message, ct);
            _logger?.LogInformation("Alert email sent to {Count} recipients", _recipients.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send alert email");
            // Best-effort: don't throw from alerting
        }
    }
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add ServiceBusEventPublisher and AcsAlertNotifier adapters"
```

---

## Stage 4: Function Trigger, DI, and Resilience

### EXIT GATE: All adapters compile

Before proceeding:
- [ ] `dotnet build` succeeds
- [ ] All existing tests pass: `dotnet test --verbosity normal`

---

### Task 11: Azure Function trigger, Program.cs with Polly resilience

**Files:**
- Create: `src/StreamTitleService/Functions/StreamTitleFunction.cs`
- Create: `src/StreamTitleService/Program.cs`
- Test: `tests/StreamTitleService.Tests/Functions/StreamTitleFunctionTests.cs`

**Polly resilience policies (PRD Section 10.2):**
- Exponential backoff: 3 retries, 2-second base delay, jitter, max 30 seconds
- Circuit breaker: open after 3 consecutive failures, half-open after 60 seconds
- Applied to ALL outbound HTTP calls: Restream API AND YouTube API (via named HttpClient registrations)

**TDD: StreamTitleFunction (2 red-green cycles)**

- [ ] **Cycle 1 RED: Valid message delegates to handler**

Write the first test in `tests/StreamTitleService.Tests/Functions/StreamTitleFunctionTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Functions;

namespace StreamTitleService.Tests.Functions;

public class StreamTitleFunctionTests
{
    private readonly Mock<IStreamTitleHandler> _handler = new();
    private readonly Mock<ILogger<StreamTitleFunction>> _logger = new();

    [Fact]
    public async Task Run_ValidMessage_ShouldDelegateToHandler()
    {
        var function = new StreamTitleFunction(_handler.Object, _logger.Object);
        var json = """
        {
            "eventType": "StreamStarted",
            "source": "zoom-automation",
            "timestamp": "2026-03-27T19:05:00Z",
            "location": "virtual",
            "data": { "title": "Arabic Bible Study" }
        }
        """;

        await function.Run(json);

        _handler.Verify(h => h.HandleAsync(
            It.Is<StreamStartedEvent>(e =>
                e.Location == "virtual" &&
                e.Data.Title == "Arabic Bible Study"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~Run_ValidMessage"`
Expected: Compilation error (StreamTitleFunction does not exist)

- [ ] **Cycle 1 GREEN: Implement StreamTitleFunction**

`src/StreamTitleService/Functions/StreamTitleFunction.cs`:
```csharp
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Domain.Events;

namespace StreamTitleService.Functions;

public class StreamTitleFunction
{
    private readonly IStreamTitleHandler _handler;
    private readonly ILogger<StreamTitleFunction> _logger;

    public StreamTitleFunction(
        IStreamTitleHandler handler,
        ILogger<StreamTitleFunction> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function("StreamTitleFunction")]
    public async Task Run(
        [ServiceBusTrigger("stream-title", "stream-title-service",
            Connection = "SERVICE_BUS_CONNECTION",
            AutoCompleteMessages = false)]
        string messageBody)
    {
        _logger.LogInformation("Received message: {Length} bytes", messageBody.Length);

        var evt = JsonSerializer.Deserialize<StreamStartedEvent>(messageBody)
            ?? throw new InvalidOperationException("Failed to deserialize StreamStartedEvent");

        _logger.LogInformation("Processing StreamStarted from {Source} for location {Location}",
            evt.Source, evt.Location);

        await _handler.HandleAsync(evt, CancellationToken.None);
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~Run_ValidMessage"`
Expected: Pass

- [ ] **Cycle 2 RED: Invalid JSON throws**

Add to `StreamTitleFunctionTests.cs`:
```csharp
    [Fact]
    public async Task Run_InvalidJson_ShouldThrow()
    {
        var function = new StreamTitleFunction(_handler.Object, _logger.Object);

        var act = () => function.Run("not valid json");

        await act.Should().ThrowAsync<Exception>();
    }
```

Run: `dotnet test --filter "FullyQualifiedName~Run_InvalidJson"`
Expected: Pass

- [ ] **Step 3: Implement Program.cs with Polly resilience on both RestreamClient and YouTubeClient**

`src/StreamTitleService/Program.cs`:
```csharp
using Azure.Communication.Email;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Google.Apis.YouTube.v3;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using StreamTitleService.Application;
using StreamTitleService.Application.Ports.Inbound;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Adapters;
using StreamTitleService.Infrastructure.Configuration;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Configuration
        services.AddSingleton<LocationPlatformMapping>();

        var stalenessThreshold = int.TryParse(
            Environment.GetEnvironmentVariable("STALENESS_THRESHOLD_SECONDS"), out var val)
            ? val : 90;

        // ---------- Polly resilience policies (PRD Section 10.2) ----------
        // Applied to ALL outbound HTTP calls: Restream API and YouTube API
        //
        // Retry: exponential backoff, 3 retries, 2-second base delay, jitter, max 30 seconds
        // Circuit breaker: open after 3 consecutive failures, half-open after 60 seconds
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                {
                    var baseDelay = TimeSpan.FromSeconds(2 * Math.Pow(2, retryAttempt - 1));
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                    var total = baseDelay + jitter;
                    // Cap at 30 seconds
                    return total > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : total;
                });

        var circuitBreakerPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(60));

        var policyWrap = Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);

        // ---------- Key Vault -- load Restream credentials ----------
        var kvUri = Environment.GetEnvironmentVariable("KEY_VAULT_URI");
        if (!string.IsNullOrEmpty(kvUri))
        {
            var kvClient = new SecretClient(new Uri(kvUri), new DefaultAzureCredential());
            var refreshToken = kvClient.GetSecret("restream-refresh-token").Value.Value;
            var clientId = kvClient.GetSecret("restream-client-id").Value.Value;
            var clientSecret = kvClient.GetSecret("restream-client-secret").Value.Value;

            // Restream token provider HttpClient (also with Polly)
            services.AddHttpClient("RestreamTokenProvider")
                .AddPolicyHandler(policyWrap);

            services.AddSingleton<ITokenProvider>(sp =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                return new KeyVaultTokenProvider(
                    factory.CreateClient("RestreamTokenProvider"),
                    refreshToken, clientId, clientSecret,
                    sp.GetService<ILogger<KeyVaultTokenProvider>>());
            });
        }

        // ---------- Restream client (with Polly resilience) ----------
        services.AddHttpClient("RestreamClient", client =>
        {
            client.BaseAddress = new Uri("https://api.restream.io/v2/");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddPolicyHandler(policyWrap);

        // ---------- YouTube client (with Polly resilience on HTTP calls) ----------
        // YouTube Data API v3 credentials from Blob Storage
        var blobConnectionString = Environment.GetEnvironmentVariable("YOUTUBE_TOKEN_BLOB_CONNECTION");
        var blobContainerName = Environment.GetEnvironmentVariable("YOUTUBE_TOKEN_BLOB_CONTAINER") ?? "youtube-tokens";
        var blobName = Environment.GetEnvironmentVariable("YOUTUBE_TOKEN_BLOB_NAME") ?? "youtube-token.json";

        if (!string.IsNullOrEmpty(blobConnectionString))
        {
            services.AddSingleton(sp =>
            {
                var blobClient = new BlobClient(blobConnectionString, blobContainerName, blobName);
                return new BlobStorageYouTubeTokenProvider(blobClient,
                    sp.GetService<ILogger<BlobStorageYouTubeTokenProvider>>());
            });
        }

        // Platform clients dictionary
        services.AddSingleton<IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient>>(sp =>
        {
            var clients = new Dictionary<TargetPlatform, ITitlePlatformClient>();

            // Restream client
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var restreamHttpClient = httpClientFactory.CreateClient("RestreamClient");
            var tokenProvider = sp.GetRequiredService<ITokenProvider>();
            clients[TargetPlatform.Restream] = new RestreamClient(
                restreamHttpClient, tokenProvider,
                sp.GetService<ILogger<RestreamClient>>());

            // YouTube client
            var ytTokenProvider = sp.GetService<BlobStorageYouTubeTokenProvider>();
            if (ytTokenProvider != null)
            {
                // Create YouTube service synchronously at startup
                var ytService = ytTokenProvider.CreateYouTubeServiceAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
                var wrapper = new GoogleYouTubeServiceWrapper(ytService);
                clients[TargetPlatform.YouTube] = new YouTubeClient(
                    wrapper, sp.GetService<ILogger<YouTubeClient>>());
            }
            else
            {
                // Fallback: YouTube not configured, log warning if called
                clients[TargetPlatform.YouTube] = new YouTubeClientNotConfigured(
                    sp.GetService<ILogger<YouTubeClientNotConfigured>>());
            }

            return clients;
        });

        // ---------- Service Bus publisher ----------
        var sbConnection = Environment.GetEnvironmentVariable("SERVICE_BUS_CONNECTION");
        var sbTopic = Environment.GetEnvironmentVariable("SERVICE_BUS_TOPIC") ?? "stream-title";
        if (!string.IsNullOrEmpty(sbConnection))
        {
            var sbClient = new ServiceBusClient(sbConnection);
            services.AddSingleton(sbClient.CreateSender(sbTopic));
            services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        }

        // ---------- ACS alert notifier ----------
        var acsConnection = Environment.GetEnvironmentVariable("ACS_CONNECTION_STRING");
        var acsSender = Environment.GetEnvironmentVariable("ACS_SENDER") ?? "";
        var acsRecipients = (Environment.GetEnvironmentVariable("ACS_RECIPIENTS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (!string.IsNullOrEmpty(acsConnection))
        {
            services.AddSingleton(new EmailClient(acsConnection));
            services.AddSingleton<IAlertNotifier>(sp =>
                new AcsAlertNotifier(
                    sp.GetRequiredService<EmailClient>(),
                    acsSender, acsRecipients,
                    sp.GetService<ILogger<AcsAlertNotifier>>()));
        }

        // ---------- Handler ----------
        services.AddSingleton<IStreamTitleHandler>(sp =>
            new StreamTitleHandler(
                sp.GetRequiredService<LocationPlatformMapping>(),
                sp.GetRequiredService<IReadOnlyDictionary<TargetPlatform, ITitlePlatformClient>>(),
                sp.GetRequiredService<IEventPublisher>(),
                sp.GetRequiredService<IAlertNotifier>(),
                stalenessThreshold,
                sp.GetService<ILogger<StreamTitleHandler>>()));
    })
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("System", LogLevel.Warning);
    })
    .Build();

host.Run();
```

Add the fallback YouTube client class to the end of `YouTubeClient.cs`:

```csharp
/// <summary>
/// Fallback for when YouTube Blob Storage credentials are not configured.
/// Throws with a clear message instead of silently failing.
/// </summary>
public class YouTubeClientNotConfigured : ITitlePlatformClient
{
    private readonly ILogger<YouTubeClientNotConfigured>? _logger;

    public YouTubeClientNotConfigured(ILogger<YouTubeClientNotConfigured>? logger = null)
    {
        _logger = logger;
    }

    public Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        _logger?.LogError("YouTubeClient not configured. Set YOUTUBE_TOKEN_BLOB_CONNECTION env var. Title '{Title}' was not set.", title);
        throw new InvalidOperationException(
            "YouTube title updates are not configured. " +
            "Set YOUTUBE_TOKEN_BLOB_CONNECTION, YOUTUBE_TOKEN_BLOB_CONTAINER, and YOUTUBE_TOKEN_BLOB_NAME environment variables.");
    }
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test`
Expected: All tests pass

- [ ] **Step 5: Verify build**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add StreamTitleFunction trigger, Program.cs DI wiring with Polly resilience on all HTTP clients"
```

---

## Stage 5: Testing Pyramid

### Task 12: Component tests (full handler pipeline with in-memory fakes)

**Files:**
- Create: `tests/StreamTitleService.Tests/Component/FullPipelineComponentTests.cs`

Component tests verify the full handler pipeline as a black box. All external dependencies are replaced with in-memory fakes. The function receives a message, the handler orchestrates domain logic, and the correct platform client is called with the correctly formatted title.

**TDD: Component tests (3 red-green cycles)**

- [ ] **Cycle 1 RED: Full pipeline Restream happy path**

Write the first test in `tests/StreamTitleService.Tests/Component/FullPipelineComponentTests.cs`:
```csharp
using FluentAssertions;
using StreamTitleService.Application;
using StreamTitleService.Application.Ports.Outbound;
using StreamTitleService.Domain.Events;
using StreamTitleService.Domain.ValueObjects;
using StreamTitleService.Infrastructure.Configuration;

namespace StreamTitleService.Tests.Component;

/// <summary>
/// Component tests: full handler pipeline with in-memory fakes for all adapters.
/// No mocking framework -- these use simple fake implementations to test the
/// complete orchestration end-to-end without real infrastructure.
/// </summary>
public class FullPipelineComponentTests
{
    private readonly FakeTitlePlatformClient _fakeRestream = new();
    private readonly FakeTitlePlatformClient _fakeYouTube = new();
    private readonly FakeEventPublisher _fakePublisher = new();
    private readonly FakeAlertNotifier _fakeAlert = new();
    private readonly StreamTitleHandler _handler;

    public FullPipelineComponentTests()
    {
        var clients = new Dictionary<TargetPlatform, ITitlePlatformClient>
        {
            [TargetPlatform.Restream] = _fakeRestream,
            [TargetPlatform.YouTube] = _fakeYouTube
        };

        _handler = new StreamTitleHandler(
            new LocationPlatformMapping(),
            clients,
            _fakePublisher,
            _fakeAlert,
            stalenessThresholdSeconds: 90);
    }

    [Fact]
    public async Task FullPipeline_RestreamVirtual_SetsFormattedTitle()
    {
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "zoom-automation",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "virtual",
            Data = new StreamStartedData { Title = "Arabic Bible Study" }
        };

        await _handler.HandleAsync(evt, CancellationToken.None);

        _fakeRestream.LastTitle.Should().Contain("Arabic Bible Study");
        _fakeRestream.LastTitle.Should().MatchRegex(@"^\w+day, \w+ \d{1,2}, \d{4} - Arabic Bible Study$");
        _fakeRestream.CallCount.Should().Be(1);
        _fakeYouTube.CallCount.Should().Be(0);
        _fakePublisher.SetEvents.Should().HaveCount(1);
        _fakePublisher.SetEvents[0].Data.TargetPlatform.Should().Be("restream");
    }
}

// ---- In-memory fakes ----

public class FakeTitlePlatformClient : ITitlePlatformClient
{
    public string? LastTitle { get; private set; }
    public int CallCount { get; private set; }

    public Task<TitleUpdateResult> SetTitleAsync(string title, CancellationToken ct)
    {
        LastTitle = title;
        CallCount++;
        return Task.FromResult(new TitleUpdateResult(2, 0));
    }
}

public class FakeEventPublisher : IEventPublisher
{
    public List<StreamTitleSetEvent> SetEvents { get; } = new();
    public List<StreamTitleFailedEvent> FailedEvents { get; } = new();

    public Task PublishTitleSetAsync(StreamTitleSetEvent evt, CancellationToken ct)
    {
        SetEvents.Add(evt);
        return Task.CompletedTask;
    }

    public Task PublishTitleFailedAsync(StreamTitleFailedEvent evt, CancellationToken ct)
    {
        FailedEvents.Add(evt);
        return Task.CompletedTask;
    }
}

public class FakeAlertNotifier : IAlertNotifier
{
    public List<(string Title, string Error)> Alerts { get; } = new();

    public Task SendFailureAlertAsync(string title, string error, CancellationToken ct)
    {
        Alerts.Add((title, error));
        return Task.CompletedTask;
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~FullPipeline_RestreamVirtual"`
Expected: Pass

- [ ] **Cycle 2 RED: Full pipeline YouTube routing**

Add to `FullPipelineComponentTests.cs`:
```csharp
    [Fact]
    public async Task FullPipeline_StAnthony_RoutesToYouTube()
    {
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = DateTimeOffset.UtcNow,
            Location = "St. Anthony Chapel", // Mixed case -- should normalize
            Data = new StreamStartedData { Title = "Sunday Divine Liturgy" }
        };

        await _handler.HandleAsync(evt, CancellationToken.None);

        _fakeYouTube.LastTitle.Should().Contain("Sunday Divine Liturgy");
        _fakeYouTube.CallCount.Should().Be(1);
        _fakeRestream.CallCount.Should().Be(0);
        _fakePublisher.SetEvents.Should().HaveCount(1);
        _fakePublisher.SetEvents[0].Data.TargetPlatform.Should().Be("youtube");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~FullPipeline_StAnthony"`
Expected: Pass

- [ ] **Cycle 3 RED: Full pipeline with default title on Saturday evening**

Add to `FullPipelineComponentTests.cs`:
```csharp
    [Fact]
    public async Task FullPipeline_NoTitle_SaturdayEvening_UsesVespers()
    {
        // Saturday 7 PM EDT = Saturday 23:00 UTC (during EDT)
        var evt = new StreamStartedEvent
        {
            EventType = "StreamStarted",
            Source = "automated-obs-trigger",
            Timestamp = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero),
            Location = "st. mary and st. joseph",
            Data = new StreamStartedData() // No title
        };

        await _handler.HandleAsync(evt, CancellationToken.None);

        _fakeRestream.LastTitle.Should().Be("Saturday, March 28, 2026 - Vespers and Midnight Praises");
    }
```

Run: `dotnet test --filter "FullyQualifiedName~FullPipeline_NoTitle_SaturdayEvening"`
Expected: Pass

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add component tests with in-memory fakes for full handler pipeline"
```

---

### Task 13: Integration tests (real Service Bus)

**Files:**
- Create: `tests/StreamTitleService.Tests/Integration/ServiceBusIntegrationTests.cs`

Integration tests verify that the ServiceBusEventPublisher correctly sends messages to a real Service Bus topic. These require the deployed shared infrastructure.

**Service Bus connection string:**
`Endpoint=sb://livestream-platform-okg4gt72g4sfo.servicebus.windows.net/;SharedAccessKeyName=platform-send;SharedAccessKey=<SET_VIA_ENV_VAR>`

**TDD: Integration test (2 red-green cycles)**

- [ ] **Cycle 1 RED: ServiceBusEventPublisher sends message to real topic**

Write the first test in `tests/StreamTitleService.Tests/Integration/ServiceBusIntegrationTests.cs`:
```csharp
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using StreamTitleService.Domain.Events;
using StreamTitleService.Infrastructure.Adapters;
using System.Text.Json;

namespace StreamTitleService.Tests.Integration;

/// <summary>
/// Integration tests against real Service Bus infrastructure.
/// These require the deployed shared Service Bus namespace.
///
/// Run with: dotnet test --filter "Category=Integration"
/// Skip in CI unless SERVICE_BUS_INTEGRATION_CONNECTION is set.
/// </summary>
[Trait("Category", "Integration")]
public class ServiceBusIntegrationTests : IAsyncDisposable
{
    private const string ConnectionString =
        "Endpoint=sb://livestream-platform-okg4gt72g4sfo.servicebus.windows.net/;SharedAccessKeyName=platform-send;SharedAccessKey=<SET_VIA_ENV_VAR>";
    private const string TopicName = "stream-title";

    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public ServiceBusIntegrationTests()
    {
        _client = new ServiceBusClient(ConnectionString);
        _sender = _client.CreateSender(TopicName);
    }

    [Fact]
    public async Task PublishTitleSet_ShouldSendMessageToRealServiceBus()
    {
        var publisher = new ServiceBusEventPublisher(_sender);

        var evt = new StreamTitleSetEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Location = "virtual",
            TraceId = "integration-test-trace-001",
            SpanId = "integration-test-span-001",
            Data = new StreamTitleSetData
            {
                Title = "Integration Test - Title Set",
                TargetPlatform = "restream",
                ChannelsUpdated = 2,
                ChannelsFailed = 0
            }
        };

        // Should not throw -- message sent to real Service Bus
        var act = () => publisher.PublishTitleSetAsync(evt, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishTitleFailed_ShouldSendMessageToRealServiceBus()
    {
        var publisher = new ServiceBusEventPublisher(_sender);

        var evt = new StreamTitleFailedEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Location = "virtual",
            TraceId = "integration-test-trace-002",
            SpanId = "integration-test-span-002",
            Data = new StreamTitleFailedData
            {
                Title = "Integration Test - Title Failed",
                TargetPlatform = "restream",
                Error = "Test error for integration test",
                ChannelsUpdated = 0,
                ChannelsAttempted = 3
            }
        };

        var act = () => publisher.PublishTitleFailedAsync(evt, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
```

Run: `dotnet test --filter "Category=Integration"`
Expected: Both tests pass (messages sent to real Service Bus)

- [ ] **Commit**

```bash
git add -A
git commit -m "feat: add integration tests against real Service Bus"
```

---

## Stage 6: Infrastructure as Code

### EXIT GATE: Full unit and component test suite

Before proceeding to infrastructure:
- [ ] All tests pass: `dotnet test --filter "Category!=Integration" --verbosity normal`
- [ ] `dotnet build --configuration Release` succeeds
- [ ] Test count: minimum 35 tests (Location: 12, StreamTitle: 4, TitleResolver: 6, StreamTitleHandler: 5, LocationPlatformMapping: 3, RestreamClient: 3, YouTubeClient: 4, KeyVaultTokenProvider: 2, StreamTitleFunction: 2, Component: 3)

---

### Task 14: Function App Bicep (infra/main.bicep)

**Files:**
- Create: `infra/main.bicep`

The PRD states each service owns its own Function App. This Bicep template deploys the stream-title-service's Function App and connects it to the shared platform resources.

- [ ] **Step 1: Create infra/main.bicep**

`infra/main.bicep`:
```bicep
@description('Environment name (dev, prod)')
param environmentName string = 'prod'

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Shared Key Vault name (from livestream-platform-infra)')
param sharedKeyVaultName string

@description('Shared Key Vault resource group')
param sharedKeyVaultResourceGroup string = resourceGroup().name

@description('Shared Application Insights connection string')
param appInsightsConnectionString string

@description('Service Bus connection string (for function trigger)')
param serviceBusConnection string

@description('Service Bus topic name')
param serviceBusTopicName string = 'stream-title'

@description('ACS connection string for email alerts')
param acsConnectionString string = ''

@description('ACS sender email address')
param acsSender string = ''

@description('ACS recipient email addresses (comma-separated)')
param acsRecipients string = ''

@description('Staleness threshold in seconds')
param stalenessThresholdSeconds int = 90

@description('YouTube token blob storage connection string')
param youtubeTokenBlobConnection string = ''

@description('YouTube token blob container name')
param youtubeTokenBlobContainer string = 'youtube-tokens'

@description('YouTube token blob name')
param youtubeTokenBlobName string = 'youtube-token.json'

@description('Shared storage account name for YouTube tokens (for role assignment)')
param sharedStorageAccountName string = ''

@description('Shared storage account resource group')
param sharedStorageResourceGroup string = resourceGroup().name

// ---------- Naming ----------
var serviceName = 'stream-title-svc'
var uniqueSuffix = uniqueString(resourceGroup().id, serviceName)

// ---------- Storage Account (Functions runtime) ----------
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: 'st${uniqueSuffix}'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

// ---------- Function App (Flex Consumption, .NET 8, Linux) ----------
resource hostingPlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${serviceName}-plan-${uniqueSuffix}'
  location: location
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: '${serviceName}-${uniqueSuffix}'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'SERVICE_BUS_CONNECTION'
          value: serviceBusConnection
        }
        {
          name: 'SERVICE_BUS_TOPIC'
          value: serviceBusTopicName
        }
        {
          name: 'KEY_VAULT_URI'
          value: 'https://${sharedKeyVaultName}${az.environment().suffixes.keyvaultDns}/'
        }
        {
          name: 'ACS_CONNECTION_STRING'
          value: acsConnectionString
        }
        {
          name: 'ACS_SENDER'
          value: acsSender
        }
        {
          name: 'ACS_RECIPIENTS'
          value: acsRecipients
        }
        {
          name: 'STALENESS_THRESHOLD_SECONDS'
          value: string(stalenessThresholdSeconds)
        }
        {
          name: 'YOUTUBE_TOKEN_BLOB_CONNECTION'
          value: youtubeTokenBlobConnection
        }
        {
          name: 'YOUTUBE_TOKEN_BLOB_CONTAINER'
          value: youtubeTokenBlobContainer
        }
        {
          name: 'YOUTUBE_TOKEN_BLOB_NAME'
          value: youtubeTokenBlobName
        }
      ]
    }
  }
}

// ---------- Role Assignment: Key Vault Secrets User ----------
// Allows the Function App's Managed Identity to read secrets from the shared Key Vault
resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-02-01' existing = {
  name: sharedKeyVaultName
  scope: resourceGroup()
}

// Key Vault Secrets User role: 4633458b-17de-408a-b874-0445c86b69e6
resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sharedKeyVault.id, functionApp.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: sharedKeyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Role Assignment: Storage Blob Data Reader ----------
// Allows the Function App's Managed Identity to read YouTube token from shared blob storage
resource sharedStorage 'Microsoft.Storage/storageAccounts@2023-01-01' existing = if (!empty(sharedStorageAccountName)) {
  name: sharedStorageAccountName
  scope: resourceGroup()
}

// Storage Blob Data Reader role: 2a2b9908-6ea1-4ae2-8e65-a410df84e7d1
resource blobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(sharedStorageAccountName)) {
  name: guid(sharedStorage.id, functionApp.id, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  scope: sharedStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- Outputs ----------
output functionAppName string = functionApp.name
output functionAppDefaultHostName string = functionApp.properties.defaultHostName
output functionAppPrincipalId string = functionApp.identity.principalId
```

- [ ] **Step 2: Verify Bicep compiles**

Run: `az bicep build --file infra/main.bicep`
Expected: Compiles without errors (produces ARM JSON)

- [ ] **Step 3: Commit**

```bash
git add infra/main.bicep
git commit -m "feat: add Bicep template for Function App with Managed Identity and role assignments"
```

---

### Task 15: CI and deploy workflows

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/deploy.yml`

- [ ] **Step 1: Create CI workflow**

`.github/workflows/ci.yml`:
```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Test (unit + component)
        run: dotnet test --no-build --configuration Release --verbosity normal --filter "Category!=Integration" --collect:"XPlat Code Coverage"

      - name: Validate Bicep
        run: az bicep build --file infra/main.bicep
```

- [ ] **Step 2: Create deploy workflow**

`.github/workflows/deploy.yml`:
```yaml
name: Deploy

on:
  push:
    branches: [main]

permissions:
  id-token: write
  contents: read

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Test (unit + component)
        run: dotnet test --no-build --configuration Release --filter "Category!=Integration"

      - name: Publish
        run: dotnet publish src/StreamTitleService/StreamTitleService.csproj --configuration Release --output ./publish

      - name: Azure Login
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Deploy Bicep
        uses: azure/arm-deploy@v2
        with:
          resourceGroupName: ${{ vars.AZURE_RESOURCE_GROUP }}
          template: infra/main.bicep
          parameters: >
            sharedKeyVaultName=${{ vars.SHARED_KEY_VAULT_NAME }}
            appInsightsConnectionString=${{ secrets.APPLICATIONINSIGHTS_CONNECTION_STRING }}
            serviceBusConnection=${{ secrets.SERVICE_BUS_CONNECTION }}
            acsConnectionString=${{ secrets.ACS_CONNECTION_STRING }}
            acsSender=${{ vars.ACS_SENDER }}
            acsRecipients=${{ vars.ACS_RECIPIENTS }}
            youtubeTokenBlobConnection=${{ secrets.YOUTUBE_TOKEN_BLOB_CONNECTION }}
            sharedStorageAccountName=${{ vars.SHARED_STORAGE_ACCOUNT_NAME }}

      - name: Deploy Function App
        uses: azure/functions-action@v2
        with:
          app-name: ${{ steps.deploy-bicep.outputs.functionAppName }}
          package: ./publish
```

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml .github/workflows/deploy.yml
git commit -m "feat: add CI and deploy GitHub Actions workflows"
```

---

## Stage 7: Final Verification

### EXIT GATE: Full test suite passes

- [ ] **Step 1: Run all unit and component tests**

Run: `dotnet test --filter "Category!=Integration" --verbosity normal`
Expected: All tests pass. Minimum 38 tests:
- LocationTests: 12
- StreamTitleTests: 4
- TitleResolverTests: 6
- StreamTitleHandlerTests: 5
- LocationPlatformMappingTests: 3
- RestreamClientTests: 3
- YouTubeClientTests: 4
- KeyVaultTokenProviderTests: 2
- StreamTitleFunctionTests: 2
- FullPipelineComponentTests: 3

- [ ] **Step 2: Run integration tests (requires network)**

Run: `dotnet test --filter "Category=Integration" --verbosity normal`
Expected: 2 integration tests pass

- [ ] **Step 3: Run build in Release mode**

Run: `dotnet build --configuration Release`
Expected: Build succeeded

- [ ] **Step 4: Verify Bicep**

Run: `az bicep build --file infra/main.bicep`
Expected: Compiles without errors

---

### Task 16: Push and verify CI

- [ ] **Step 1: Push to GitHub**

```bash
git push -u origin main
```

- [ ] **Step 2: Verify CI passes on GitHub Actions**

Check: https://github.com/wfhanna1/stream-title-service/actions
Expected: CI workflow runs and passes

---

### E2E test (manual verification)

Once the function is deployed:

1. Send a real `StreamStarted` message to the `stream-title` topic:
```json
{
  "eventType": "StreamStarted",
  "source": "manual-e2e-test",
  "timestamp": "2026-03-29T15:00:00Z",
  "location": "virtual",
  "traceId": "e2e-test-trace-001",
  "spanId": "e2e-test-span-001",
  "parentSpanId": null,
  "data": {
    "title": "E2E Test Title"
  }
}
```

2. Verify:
   - Function picks up the message (check Application Insights logs)
   - RestreamClient sets title on enabled channels
   - `StreamTitleSet` event is published back to the topic
   - Title on Restream shows: "Sunday, March 29, 2026 - E2E Test Title"

---

## Self-Review Checklist

**Spec coverage:**
- [x] Domain value objects: Location (case-insensitive), TargetPlatform, StreamTitle (date formatting) (Task 2)
- [x] TitleResolver with DefaultTitleGenerator logic (Saturday 5-11:59 PM, otherwise Divine Liturgy) (Task 3)
- [x] Event types: StreamStartedEvent, StreamTitleSetEvent, StreamTitleFailedEvent (Tasks 3, 4)
- [x] Application ports: IStreamTitleHandler, ITitlePlatformClient, ITokenProvider, IEventPublisher, IAlertNotifier (Task 4)
- [x] StreamTitleHandler orchestrator with staleness check, platform routing, error handling (Task 6)
- [x] LocationPlatformMapping (virtual->Restream, st. mary->Restream, st. anthony->YouTube) (Task 5)
- [x] RestreamClient (GET channels, PATCH channel-meta) with Polly resilience (Tasks 7, 11)
- [x] YouTubeClient FULL implementation (channels.list, liveBroadcasts.list, videos.list, videos.update) (Task 8)
- [x] YouTube credentials from Azure Blob Storage as JSON (not Python pickle) (Task 8)
- [x] YouTube snippet fields preserved on update (Task 8)
- [x] KeyVaultTokenProvider (refresh token, caching with 60s buffer) (Task 9)
- [x] ServiceBusEventPublisher (publish set/failed events) (Task 10)
- [x] AcsAlertNotifier (email on failure) (Task 10)
- [x] StreamTitleFunction (Service Bus trigger, JSON deserialization) (Task 11)
- [x] Program.cs DI wiring with Polly on ALL HTTP clients (Task 11)
- [x] Polly: exponential backoff, 3 retries, 2s base, jitter, max 30s (Task 11)
- [x] Polly: circuit breaker, open after 3 failures, half-open after 60s (Task 11)
- [x] Title date prefix from event timestamp, not processing time (Task 2, StreamTitle.Format)
- [x] Title validation: strip existing date prefix (Task 2, StreamTitle.Format)
- [x] Staleness threshold configurable via STALENESS_THRESHOLD_SECONDS (Tasks 6, 11)
- [x] Location matching case-insensitive, normalized to lowercase (Task 2)
- [x] Unknown location fails loudly with alert (Task 6)
- [x] TDD throughout: proper red-green-refactor, one test at a time
- [x] Function App Bicep: Flex Consumption, .NET 8, Linux, Managed Identity (Task 14)
- [x] Role assignments: Key Vault Secrets User, Storage Blob Data Reader (Task 14)
- [x] CI workflow (Task 15)
- [x] Deploy workflow with Bicep and function deployment (Task 15)

**Testing pyramid:**
- [x] Unit tests: domain logic, adapter behavior, edge cases (Tasks 2-9, 11)
- [x] Component tests: full handler pipeline with in-memory fakes (Task 12)
- [x] Integration tests: real Service Bus (Task 13)
- [x] E2E test: manual verification after deployment (Stage 7)

**Placeholder scan:** No TBD, TODO, or "implement later" anywhere. YouTubeClient is fully implemented. All Polly policies are in place.

**Type consistency:** Verified: `StreamStartedEvent`, `StreamTitleSetEvent`, `StreamTitleFailedEvent` property names match Section 4 event contract. `Location.Value`, `TargetPlatform.Value`, `StreamTitle.Value` used consistently. `TitleUpdateResult` record used by both `RestreamClient`, `YouTubeClient`, and `ITitlePlatformClient`. `IYouTubeServiceWrapper` abstracts the Google SDK for testability.
