# 11 — Current State Validation

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 0
> **Method**: Direct source inspection of `cocorra-backend` at HEAD. Prior discovery documents were treated as hypotheses, not as truth.
> **Scope**: Read-only inspection. No application code, schema, migrations, or packages were modified.

---

## Repository State

| Item | Value | Verified by |
|---|---|---|
| Branch | `main` | `git branch --show-current` |
| HEAD | `c13f1f6` — *feat: add comprehensive unit test suite and dynamic LiveKit TURN/ICE configuration* | `git log --oneline -3` |
| Working tree | Clean except untracked `docs/` | `git status --short` |
| Target framework | `net10.0` | `Cocorra.Tests.csproj` |
| Migrations (latest 3) | `20260713171943_AddUserEventTracking`, `20260713173714_AddRoomIdToUserEvent`, `20260713175717_analytices` | `Cocorra.DAL/Migrations/` |

**FACT** — HEAD is identical to the commit the Phase 1 discovery ran against. No application code has changed between the discovery phase and this blueprint. Every prior finding was nonetheless re-verified against source rather than assumed.

---

## Classification Legend

| Status | Meaning |
|---|---|
| **CONFIRMED** | Re-verified in source at HEAD. Finding stands as written. |
| **CHANGED** | Present but materially different from the prior description. |
| **NO LONGER PRESENT** | The prior finding does not exist in the code. |
| **UNCERTAIN** | Could not be settled by static inspection alone; needs runtime observation. |

---

# 1. Event Tracking Infrastructure

## 1.1 `EventTracker` — CONFIRMED, with three additions

**File**: `Cocorra.BLL/Services/EventTracking/EventTracker.cs`
**Registration**: `Program.cs:212` — `AddSingleton<IEventTracker, EventTracker>()`
**Channel**: `Program.cs:210-211` — `Channel.CreateBounded<UserEvent>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite })`, registered as a singleton.

**CONFIRMED (FACT)** — bounded 10,000-capacity channel, `DropWrite` on full, non-blocking `TryWrite`, drop logged as a warning: `_logger.LogWarning("Event queue full; dropped {EventType}", eventType)`.

**CONFIRMED (FACT)** — `Track` is wrapped in a try/catch that swallows every exception with the comment *"Tracking must NEVER throw back to the user"*. Analytics failure cannot break a product action at the enqueue stage.

**CONFIRMED (FACT)** — `ExtractRoomId` promotes a `roomId` property from `PropertiesJson` into the indexed `RoomId` column, case-insensitively, and returns null on malformed JSON without throwing.

### Addition 1 — `IHttpContextAccessor` enrichment is absent for all SignalR-emitted events

**FACT** — `EventTracker.Track` reads `SessionId`, `IpHash`, and `UserAgent` exclusively from `_httpContextAccessor.HttpContext`, and skips all three when it is null.

**FACT — confirmed by the codebase itself.** `Cocorra.Tests/EventTrackingSmokeTests.cs:28-30` constructs the tracker with a null HttpContext and documents why:

```csharp
// No HttpContext (as when firing from a SignalR hub) → enrichment is skipped,
// but userId/roomId still flow through explicitly.
```

**Consequence (FACT)** — every event emitted from `RoomHub` (`room_joined`, `room_left`, `mic_activated`) is persisted with `SessionId = NULL`, `IpHash = NULL`, `UserAgent = NULL`.

**Why this matters for the blueprint (INFERENCE)** — the `UserEvent.SessionId` column is documented in the model as *"Groups events into a single app session for funnel analysis."* For the room events that matter most, it is always null. Any design that assumes session-scoped correlation across the room lifecycle is building on a column that is empty precisely where it would be used. This is a new finding; the Phase 1 documents did not identify it.

### Addition 2 — `Analytics:IpHashSalt` is a hard startup requirement

**FACT** — `Program.cs:205-207` throws at startup if `Analytics:IpHashSalt` is missing or whitespace. There is no hardcoded fallback, by deliberate design (the source comment notes a public salt would make hashes reversible).

**Implication (INFERENCE)** — analytics configuration is already a first-class deployment concern with a fail-fast guard. New analytics configuration keys can follow this established pattern rather than inventing a new one.

### Addition 3 — No idempotency key, correlation id, or schema version

**FACT** — `UserEvent` (`Cocorra.DAL/Models/UserEvent.cs`) has exactly these fields: `Id` (bigint identity, server-generated), `UserId`, `EventType`, `PropertiesJson`, `SessionId`, `RoomId`, `OccurredAtUtc`, `IpHash`, `UserAgent`.

There is no client-supplied event id, no deduplication key, no correlation id, and no event-schema version. `Id` is a database identity assigned at insert, so it cannot serve as a deduplication key — a duplicate enqueue produces two rows with two different ids.

---

## 1.2 `EventFlushService` — CHANGED (prior docs understated the risk)

**File**: `Cocorra.BLL/Services/EventTracking/EventFlushService.cs`
**Registration**: `Program.cs:213` — `AddHostedService<EventFlushService>()`

**CONFIRMED (FACT)** — reads from the channel, batches up to 100, resolves a scoped `AppDbContext` per batch, `AddRange` + `SaveChangesAsync`.

### CHANGED — the batch is discarded on any database failure

The prior audit described the flush service as a straightforward batching writer. It did not identify its failure behaviour, which is the most significant reliability gap in the pipeline.

**FACT** — the persistence block is:

```csharp
try
{
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.UserEvents.AddRange(batch);
    await db.SaveChangesAsync(ct);
}
catch (Exception dbEx)
{
    _logger.LogError(dbEx, "Failed to persist batch of {BatchCount} user events.", batch.Count);
}
finally
{
    batch.Clear();
}
```

**FACT** — `batch.Clear()` runs in the `finally`, so it executes on the failure path as well as the success path. There is no retry, no re-enqueue, no dead-letter, and no checkpoint.

**Consequence (INFERENCE)** — a single transient database fault — a connection blip, a deadlock, a timeout, a failover — silently and permanently destroys up to 100 events. The only trace is one log line to Docker stdout, which is not persisted or aggregated anywhere (`00-repository-overview.md` confirms no structured logging sink).

**Assessment (INFERENCE)** — this is a second, independent data-loss path alongside the known `DropWrite` overflow. The prior audit treated channel overflow as the loss risk; database failure is at least as likely and loses events in silent batches of up to 100. Both must be addressed before any decision depends on event completeness.

### Additional detail — batch loop semantics

**FACT** — the inner loop drains up to 100 events, then persists. When fewer than 100 are available it persists whatever is present rather than waiting to fill the batch, so latency is bounded by channel arrival rather than by batch size. There is no time-based flush trigger, and none is needed given this structure.

---

## 1.3 `EventCleanupService` — CONFIRMED, with two additions

**File**: `Cocorra.BLL/Services/EventTracking/EventCleanupService.cs`
**Registration**: `Program.cs:214`

**CONFIRMED (FACT)** — 180-day retention, hardcoded as `DateTime.UtcNow.AddDays(-180)`. No configuration key. No archive or export before deletion.

### Addition 1 — cleanup runs immediately at every startup

**FACT** — the loop body executes the delete *before* the 24-hour `Task.Delay`. Every application restart triggers an immediate purge scan.

**INFERENCE** — benign at current volume, but it means purge timing is coupled to deployment frequency rather than to a schedule.

### Addition 2 — the purge is a single unbatched bulk delete

**FACT** — `await db.UserEvents.Where(e => e.OccurredAtUtc < cutoff).ExecuteDeleteAsync(ct)` — one statement, no batching, no row cap.

**INFERENCE** — on the first purge after a long accumulation, or after any period where the service was not running, this becomes a single large delete against a table carrying three composite indexes. On SQL Server that risks lock escalation and extended blocking on `UserEvents` — the same table `EventFlushService` is concurrently inserting into. The blueprint should batch this. It is not currently a live problem, but it becomes one as volume grows, and the event-expansion programme increases volume by design.

---

## 1.4 `UserEvent` schema and indexes — CONFIRMED

**Configuration**: `Cocorra.DAL/Data/AppDbContext.cs:251-264`

**CONFIRMED (FACT)** — three composite indexes:

```csharp
e.HasIndex(x => new { x.EventType, x.OccurredAtUtc });
e.HasIndex(x => new { x.UserId, x.OccurredAtUtc });
e.HasIndex(x => new { x.RoomId, x.EventType, x.OccurredAtUtc });
```

**CONFIRMED (FACT)** — `HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull)`. Deleting a user anonymises their events rather than removing them.

**CONFIRMED (FACT)** — `EventType` is `[Required, MaxLength(64)]`. Every currently-defined and every proposed event name fits comfortably.

**CONFIRMED (FACT)** — `PropertiesJson` is unbounded `string?`, mapped to `nvarchar(max)`. The model carries an explicit warning comment: *"NEVER store message bodies, emails, or other PII here."*

**Assessment (INFERENCE)** — the index design is genuinely good and directly supports the room-scoped queries the target dashboard needs. This is a schema to extend, not replace.

---

## 1.5 Event catalogue — CONFIRMED

**File**: `Cocorra.DAL/Models/EventTypes.cs`

**CONFIRMED (FACT)** — 24 string constants, matching `05-event-tracking-audit.md` exactly. No additions or removals.

**CONFIRMED (FACT)** — `EventsController.cs:22-27` allowlists exactly three client events: `RoomCreateStarted`, `NotificationOpened`, `FeatureViewed`.

### Addition — client event properties are entirely unvalidated

**FACT** — `TrackEventDto.Properties` is typed `object?` (`EventsController.cs:60`). It is passed directly to `_eventTracker.Track`, which serialises it with `JsonSerializer.Serialize`. There is no schema validation, no required-property check, and no size limit.

**Consequence (INFERENCE)** — an authenticated client can write an arbitrarily large JSON document into `PropertiesJson` (`nvarchar(max)`), subject only to the global 100 req/min per-IP rate limit (`Program.cs:346-360`). This is both a storage-growth concern and the mechanism by which `notification_opened` can arrive without the `notificationId` needed to attribute it. Property validation is a prerequisite for any client event carrying a correlation identifier.

---

# 2. Analytics Query & API Layer

## 2.1 `AnalyticsRepository` — CONFIRMED

**File**: `Cocorra.DAL/Repository/AnalyticsRepository/AnalyticsRepository.cs`

| Prior finding | Status | Verification |
|---|:--:|---|
| User Growth buckets by `CreatedAt`, counts by **current** `Status` | **CONFIRMED** | Lines 21-93 |
| Room Analytics counts all participant statuses; averages *configured* duration | **CONFIRMED** | Lines 98-164 |
| Participation uses snapshot `IsHandRaised` | **CONFIRMED** | Lines 166-231 |
| Funnel counts steps independently, non-sequentially | **CONFIRMED** | Lines 300-322 |
| Retention uses exact-day matching | **CONFIRMED** | See below |
| Active vs Passive materialises the joiner list for `.Contains()` | **CONFIRMED** | Lines 501-540 |
| Peak Hours groups by `OccurredAtUtc.Hour`, UTC only | **CONFIRMED** | Lines 447-469 |

**Retention, verified precisely** — `GetRetentionCohortAsync`, the day-matching predicate:

```csharp
var timeDiff = e.OccurredAtUtc.Date - cohortDate.Date;
return timeDiff.Days == day;
```

**CONFIRMED (FACT)** — `== day`, evaluated in memory over `activityEvents`, which is itself fetched with **no upper time bound**:

```csharp
var activityEvents = await _context.UserEvents
    .Where(e => e.EventType == activeEvent
             && e.UserId != null
             && cohortUserIds.Contains(e.UserId.Value))
    .Select(...).ToListAsync();
```

**INFERENCE** — two compounding problems in one method: the exact-day predicate undercounts retention, and the unbounded activity fetch loads every matching event for every cohort user into memory. Both were identified in `07-metric-verification.md`; both are confirmed verbatim.

## 2.2 `AnalyticsService` — CONFIRMED, with one architecturally significant addition

**File**: `Cocorra.BLL/Services/AnalyticsService/AnalyticsService.cs`

**CONFIRMED (FACT)** — `IMemoryCache` with a 10-minute TTL (`CacheTtl = TimeSpan.FromMinutes(10)`), and eleven `static readonly SemaphoreSlim` instances providing per-metric stampede protection through `GetOrCreateWithLockAsync`.

**CONFIRMED (FACT)** — default window resolution: `ResolveFrom` returns `UtcNow.AddDays(-30).Date`, `ResolveTo` returns `UtcNow`.

**CONFIRMED (FACT)** — cache keys embed the resolved window, e.g. `$"analytics:summary:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}"`.

### Addition — `Response<T>` already carries a `Meta` field

**FACT** — `Cocorra.BLL/Base/Response.cs`:

```csharp
public class Response<T>
{
    public HttpStatusCode StatusCode { get; set; }
    public object? Meta { get; set; }
    public bool Succeeded { get; set; }
    public string? Message { get; set; }
    public List<string>? Errors { get; set; }
    public T? Data { get; set; }
}
```

**FACT** — `ResponseHandler.Success<T>(T entity, object? meta = null, ...)` accepts a meta payload, and every analytics method currently calls it without one.

**INFERENCE — this is the single most useful architectural discovery of this validation pass.** The trust-metadata requirement from `08a-metric-trust-framework.md` needs a transport. One already exists, is present on every analytics response, is currently unused, and is additive: populating `Meta` changes no existing field and cannot break a client that ignores it. No envelope redesign is required.

## 2.3 `AnalyticsController` — CONFIRMED

**File**: `Cocorra.API/Controllers/AnalyticsController.cs`
**Routes**: `Cocorra.DAL/AppMetaData/Router.cs:91-105`

**CONFIRMED (FACT)** — eleven `[HttpGet]` endpoints; class-level `[Authorize(Roles = "Admin,Coach")]`; the default authorization policy additionally requires `VerificationStatus=Active` (`Program.cs:330-334`).

**CONFIRMED (FACT)** — no support, host/supply, or moderation-action endpoints exist. The eleven routes are exactly those listed in `03-current-dashboard.md`.

## 2.4 `AdminService.GetDashboardStatsAsync` — CONFIRMED

**File**: `Cocorra.BLL/Services/AdminService/AdminService.cs:383-401`

**CONFIRMED (FACT)** — `_userManager.Users.GroupBy(u => u.Status)` with no date filter. Pure point-in-time snapshot.

---

# 3. Domain-Side Findings

## 3.1 Finding A — host mic open from room start — CONFIRMED

**CONFIRMED (FACT)** — `RoomService.cs:115-127` (creation as Live) and `RoomService.cs:439-449` (`StartScheduledRoomAsync`) both insert the host with `IsOnStage = true, IsMuted = false, JoinedAt = UtcNow, LastUnmutedAt = UtcNow`.

**CONFIRMED (FACT)** — `RoomHub.ToggleMic` (`RoomHub.cs:518-521`) emits `mic_activated` only on a `IsMuted: true → false` transition, so the host's initial open mic emits nothing.

**CONFIRMED (FACT)** — `RoomService.cs:73` restricts `DurationHours` via `AllowedDurations` to exactly 2 or 3.

The contradiction described in `07-decision-framework.md` Finding A stands in full.

## 3.2 Finding C — go-live untracked — CONFIRMED

**CONFIRMED (FACT)** — `StartScheduledRoomAsync` (`RoomService.cs:422-460`) sets `Status = Live`, inserts the host participant, and dispatches reminder notifications. It emits no `UserEvent` and does not write `StartDate` or `UpdatedAt`.

## 3.3 `room_joined` fires per hub connect — CONFIRMED

**CONFIRMED (FACT)** — `RoomHub.cs:270` — `_eventTracker.Track(EventTypes.RoomJoined, userId, new { roomId = roomGuid })` sits on the unconditional path of `JoinRoom`, after the pre-checks and group registration. Every SignalR (re)connect that reaches this line emits another event.

**CONFIRMED (FACT)** — `RoomHub.cs:245-253` — a participant whose status is `Left` is re-activated with `participant.JoinedAt = DateTime.UtcNow`, overwriting the original join time.

**Addition (FACT)** — immediately before the event, `JoinRoom` evicts a stale connection for the same `(UserId, RoomId)` from the static `_connections` dictionary. This means the code already recognises reconnection as a distinct case; it simply does not distinguish it in the event payload. A boolean `isRejoin` is derivable at that exact point with no additional lookup.

## 3.4 `session_started` cookie mechanism — CONFIRMED

**File**: `Cocorra.API/Middleware/SessionTrackingMiddleware.cs`

**CONFIRMED (FACT)** — cookie `CocorraSessionId`, `HttpOnly`, `Secure`, `SameSite=Strict`, 7-day expiry. `context.Items["SessionId"]` is set before `await _next(context)`; the event is tracked after the pipeline completes, gated by an `IMemoryCache` key `session_logged:{sessionId}` with a 1-day TTL.

**Addition (INFERENCE)** — `SameSite=Strict` combined with `Secure` is correct for a browser but adds nothing for a native mobile client, which must maintain its own cookie jar for the cookie to survive an app restart at all. Combined with the in-process cache dedup (lost on every restart or in any multi-instance deployment), the reliability concern from `06-blind-spots.md` §1 is confirmed and, if anything, understated.

## 3.5 `activation_completed` deduplication — CHANGED (a race exists)

**FACT** — `AdminService.cs:141-147`:

```csharp
var alreadyActivated = await _context.UserEvents
    .AnyAsync(e => e.UserId == user.Id && e.EventType == EventTypes.ActivationCompleted);
if (!alreadyActivated)
{
    _eventTracker.Track(EventTypes.ActivationCompleted, user.Id);
}
```

**INFERENCE** — the guard reads the `UserEvents` **table**, but `Track` only enqueues to an in-memory channel; the row does not exist until `EventFlushService` persists the batch. Two activations of the same user inside that window both observe `alreadyActivated == false` and both emit. `07-metric-verification.md` recorded this dedup as correct; it is correct in intent but is a read-then-write race against an asynchronous writer, not a guarantee.

**Scope (FACT)** — narrow. `BulkChangeUserStatusAsync` (`AdminService.cs:256-296`) de-duplicates ids with `.Distinct()` and processes them sequentially via `await`, so the bulk path cannot race itself. The exposure is two concurrent admin requests targeting the same user.

**Assessment (INFERENCE)** — low probability, real mechanism. It matters mainly as evidence for the general principle: **event-emission guards must not depend on reading a table that is written asynchronously.** Any new deduplicated event must use a different strategy.

## 3.6 Admin identity is available but discarded — CONFIRMED and localised

**FACT** — `AdminController.ChangeStatus` (`AdminController.cs:54`) reads `adminId` from the claims principal and uses it only for a self-change guard. `AdminController.BulkChangeStatus` (`AdminController.cs:92`) parses `adminId` and passes it to `BulkChangeUserStatusAsync`.

**FACT** — `IAdminService.ChangeUserStatusAsync(Guid userId, UserStatus newStatus)` (`IAdminService.cs:13`) has no `adminId` parameter. `BulkChangeUserStatusAsync` receives `adminId` but does not forward it when calling `ChangeUserStatusAsync` (`AdminService.cs:289`).

**INFERENCE — a precise, actionable localisation.** The acting admin's identity exists in the controller in both paths and is dropped at exactly one boundary: the `ChangeUserStatusAsync` signature. Capturing it requires one parameter added to one interface method, one implementation, and two call sites. This is materially smaller than `07-decision-framework.md` implied when it recorded reviewer identity as simply "not recorded anywhere."

## 3.7 `UpdatedAt` is written in only three places — CONFIRMED

**FACT** — a solution-wide search for assignments to `UpdatedAt` returns exactly three sites: `AuthServices.cs:538`, `SupportService.cs:140`, `SupportService.cs:275`. There is no `SaveChanges` override in `AppDbContext`. `Room.UpdatedAt`, `FriendRequest.UpdatedAt`, `Message.UpdatedAt`, and `Notification.UpdatedAt` are never assigned.

**CONFIRMED (FACT)** — `RoomRepository.cs:129` orders the ended-rooms history page by `.OrderByDescending(r => r.UpdatedAt)` over a column that is NULL for effectively every row.

## 3.8 LiveKit — no telemetry ingestion — CONFIRMED

**FACT** — `ILiveKitService` exposes exactly two members: `GenerateToken` (`LiveKitService.cs:36`) and `UpdateStagePermissionAsync` (`LiveKitService.cs:116`). A repository-wide case-insensitive search for "webhook" across `.cs`, `.json`, `.yaml`, and `.yml` returns zero matches.

## 3.9 Topic Requests — schema only — CONFIRMED

**FACT** — `RoomTopicRequest` and `TopicVote` appear only in `AppDbContext.cs` (DbSets at lines 16-17, fluent configuration at 58-97) and in their model files. No controller, service, repository, route, or event references them.

---

# 4. Test Infrastructure

**FACT** — `Cocorra.Tests` targets `net10.0` and uses **xUnit 2.9.3**, **Moq 4.20.72**, **Microsoft.EntityFrameworkCore.InMemory 10.0.2**, and **coverlet.collector 6.0.4**. It references all three production projects.

**FACT** — 20 test files exist, including two directly relevant to this programme:
- `EventTrackingSmokeTests.cs` — covers `Track` → channel → `EventFlushService` → DB → `AnalyticsRepository`, explicitly with a null `HttpContext` to simulate the SignalR path.
- `AnalyticsControllerTests.cs` — controller-level tests with a mocked `IAnalyticsService`.

**INFERENCE** — the testing conventions needed for the validation strategy already exist and are demonstrated in-repo. `EventTrackingSmokeTests` is in effect a working template for end-to-end event-pipeline tests; the testing plan should extend it rather than introduce a new harness or framework.

**Caveat (INFERENCE)** — `EFCore.InMemory` does not enforce relational constraints, unique indexes, or `DeleteBehavior`. Tests asserting idempotency via a unique index, or `SetNull` on user deletion, will pass vacuously against the in-memory provider and must use SQLite in-memory or a real SQL Server instance instead.

---

# 5. Validation Summary

| # | Prior finding | Status | Notes |
|:--:|---|:--:|---|
| 1 | Bounded 10K channel, `DropWrite` | **CONFIRMED** | `Program.cs:210-211` |
| 2 | `Track` never throws | **CONFIRMED** | try/catch, explicit intent comment |
| 3 | `roomId` promoted to indexed column | **CONFIRMED** | `ExtractRoomId`, case-insensitive |
| 4 | Batches of 100 in `EventFlushService` | **CONFIRMED** | — |
| 5 | Flush failure behaviour | **CHANGED** | **No retry; `batch.Clear()` in `finally` discards up to 100 events on any DB error.** New. |
| 6 | 180-day retention, hardcoded | **CONFIRMED** | Runs at startup; unbatched `ExecuteDeleteAsync`. |
| 7 | Three composite indexes on `UserEvents` | **CONFIRMED** | Well-suited to target queries |
| 8 | `SetNull` on user deletion | **CONFIRMED** | — |
| 9 | 24 event constants; 3 client-allowlisted | **CONFIRMED** | — |
| 10 | Client event properties unvalidated | **CHANGED** | `object?`, no schema or size limit. Sharpened. |
| 11 | SignalR events lack session/IP/UA enrichment | **NEW — CONFIRMED** | Documented in `EventTrackingSmokeTests.cs:28-30` |
| 12 | No idempotency key / correlation id / version | **CONFIRMED** | `Id` is a DB identity, unusable for dedup |
| 13 | User Growth status backdating | **CONFIRMED** | `AnalyticsRepository.cs:21-93` |
| 14 | Retention exact-day matching | **CONFIRMED** | `timeDiff.Days == day`; activity fetch unbounded |
| 15 | Funnel non-sequential | **CONFIRMED** | `AnalyticsRepository.cs:300-322` |
| 16 | Participation uses snapshot `IsHandRaised` | **CONFIRMED** | — |
| 17 | 10-min `IMemoryCache` + 11 semaphores | **CONFIRMED** | — |
| 18 | `Response<T>.Meta` unused | **NEW — CONFIRMED** | Trust-metadata transport already exists |
| 19 | Finding A — host mic open at room start | **CONFIRMED** | `RoomService.cs:115-127, 439-449` |
| 20 | Finding C — go-live untracked | **CONFIRMED** | `StartScheduledRoomAsync` |
| 21 | `room_joined` per reconnect | **CONFIRMED** | `RoomHub.cs:270`; `isRejoin` derivable in place |
| 22 | `JoinedAt` overwritten on rejoin | **CONFIRMED** | `RoomHub.cs:245-253` |
| 23 | `session_started` cookie-based | **CONFIRMED** | `SameSite=Strict`; in-process dedup |
| 24 | `activation_completed` dedup correct | **CHANGED** | **Read-then-write race against the async flush.** New. |
| 25 | Admin identity not recorded | **CONFIRMED, localised** | Dropped at one signature boundary |
| 26 | `UpdatedAt` written in 3 places only | **CONFIRMED** | No `SaveChanges` override |
| 27 | No LiveKit webhook ingestion | **CONFIRMED** | Zero matches repository-wide |
| 28 | Topic Requests schema-only | **CONFIRMED** | — |
| 29 | No experimentation infrastructure | **CONFIRMED** | No flags, buckets, or experiment tables |
| 30 | Test stack: xUnit + Moq + EFCore.InMemory | **CONFIRMED** | Extendable template exists |

**Nothing was found to be NO LONGER PRESENT.** Nothing is **UNCERTAIN** at the level of static inspection, with two items requiring runtime observation before implementation decisions are finalised (below).

---

# 6. Items Requiring Runtime Observation

These cannot be settled from source and must be measured before the corresponding blueprint decisions are locked.

| # | Question | Why it matters | How to observe |
|:--:|---|---|---|
| **R-1** | How often does the channel actually drop? | Determines whether the P0 room events can be added without displacing existing events. | Count occurrences of the `"Event queue full; dropped {EventType}"` warning in container logs over a representative week. |
| **R-2** | How often does the flush batch fail? | Sizes the silent-loss problem and sets the urgency of a retry path. | Count `"Failed to persist batch of {BatchCount} user events."` in container logs. |
| **R-3** | Current `UserEvents` row count and growth rate | Informs partitioning, retention, and whether the unbatched purge is already a risk. | `SELECT COUNT(*)`, plus daily counts over the last 30 days. |
| **R-4** | What share of `session_started` events reflect genuinely new sessions? | Confirms or refutes the cookie-unreliability hypothesis with evidence rather than inference. | Compare distinct `SessionId` per user per day against distinct active users per day. |
| **R-5** | Is `notification_opened` arriving at all, and with what properties? | Determines whether the client currently emits it and whether a correlation id can be required without breaking an existing flow. | `SELECT TOP 100 PropertiesJson FROM UserEvents WHERE EventType = 'notification_opened'`. |

**RECOMMENDATION** — R-1, R-2, and R-3 are prerequisites for the storage and background-processing decisions in `16-` and `18-`. They require only log inspection and read-only queries, and should be gathered before implementation begins. They are listed as explicit blockers in `24-dependency-graph.md`.

---

# 7. Conclusions for the Blueprint

**INFERENCE — three conclusions follow from this validation and shape every subsequent document.**

**1. The event infrastructure is sound in design and weak in durability.**
The channel/flush/cleanup pattern, the promoted indexed `RoomId`, the composite indexes, the never-throw contract, and the client allowlist are all good decisions. What is missing is durability: no retry, no idempotency key, no dead-letter. The correct move is to **harden and extend**, not replace. A replacement would discard a working design to solve a problem that three targeted changes address.

**2. Two independent silent data-loss paths exist, and only one was previously known.**
Channel overflow (`DropWrite`) was documented. Flush-batch discard on database error was not, and is arguably the more dangerous of the two because it loses events in blocks of up to 100 and leaves only an unaggregated log line. Both must be closed before event completeness can be asserted, and both are prerequisites for the aggregation layer.

**3. Two pieces of the target design already exist and are unused.**
`Response<T>.Meta` is a ready-made transport for trust metadata on every analytics response. `EventTrackingSmokeTests` is a working end-to-end pipeline test that the validation strategy can extend directly. Both materially reduce the scope of the work, and both were discovered only by inspecting code rather than by reading the prior documents.
