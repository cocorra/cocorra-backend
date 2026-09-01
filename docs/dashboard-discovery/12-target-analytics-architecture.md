# 12 — Target Analytics Architecture

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 1
> **Depends on**: `11-current-state-validation.md` (all facts here are re-verified at HEAD `c13f1f6`)
> **Scope**: Documentation only. No code, schema, or packages were modified.

---

## Design Position

**RECOMMENDATION — extend, do not replace.**

The validation pass found Cocorra's analytics infrastructure to be **well designed and under-hardened**. The channel-based non-blocking producer, the promoted indexed `RoomId` column, the three composite indexes, the never-throw tracking contract, and the client-event allowlist are all correct decisions that a rewrite would have to reinvent.

What is missing is durability (no retry, no idempotency key, no dead-letter), coverage (state transitions unemitted), and a computation layer (every metric is a live query over production tables).

This architecture therefore adds three things and replaces nothing:

1. **Durability** around the existing pipeline.
2. **Coverage** through new emit sites in existing services.
3. **An aggregation layer** between raw events and the API.

**INFERENCE — why not a dedicated analytics store (ClickHouse, TimescaleDB, a warehouse)?** At Cocorra's scale the operational cost dominates the benefit. The platform runs a single SQL Server instance and a single API container (`docker-compose.yml`), has no data engineering function, and no evidence of query-performance problems at current volume. A second datastore introduces sync, schema drift, and a new failure mode to solve a problem that indexed rollup tables in the existing database solve adequately. The architecture below keeps everything in SQL Server and notes the specific signal (R-3, `UserEvents` growth rate) that would justify revisiting this.

---

# Part 1 — Current Architecture

All component names, file paths, and line numbers below are verified at HEAD.

## 1.1 Current end-to-end flow

```
ACTUAL USER ACTION
  e.g. user taps "Join Room" in the Flutter app
        │
        ▼
CURRENT APPLICATION COMPONENTS
  RoomsController.Join            → RoomService.JoinRoomAsync
                                     └─ creates RoomParticipant (JoinedAt = UtcNow)
  RoomHub.JoinRoom (SignalR)      → re-activates Left participants
                                     └─ OVERWRITES JoinedAt  (RoomHub.cs:245-253)
                                     └─ _connections[ConnectionId] = (UserId, RoomId)   [static, in-process]
                                     └─ issues LiveKit token
        │
        ▼
CURRENT EVENT TRACKING
  IEventTracker.Track(eventType, userId, properties)        [Cocorra.BLL/Services/EventTracking/EventTracker.cs]
    ├─ enriches SessionId / IpHash / UserAgent from IHttpContextAccessor
    │     ⚠ NULL for every SignalR-emitted event
    ├─ ExtractRoomId() promotes "roomId" → indexed RoomId column
    ├─ Channel<UserEvent>.Writer.TryWrite()                  [Program.cs:210-211]
    │     bounded 10 000, FullMode = DropWrite
    │     ⚠ SILENT LOSS PATH 1 — overflow drops the event (warning logged)
    └─ try/catch swallows everything — never throws to the caller
        │
        ▼
CURRENT STORAGE
  EventFlushService : BackgroundService                      [Program.cs:213]
    ├─ drains up to 100 events
    ├─ scoped AppDbContext → AddRange → SaveChangesAsync
    └─ on DB failure: LogError, then batch.Clear() in `finally`
          ⚠ SILENT LOSS PATH 2 — up to 100 events discarded, no retry
        │
        ▼
  dbo.UserEvents
    Id (bigint identity) │ UserId │ EventType(64) │ PropertiesJson(max)
    SessionId │ RoomId │ OccurredAtUtc │ IpHash(64) │ UserAgent(256)
    IX (EventType, OccurredAtUtc) │ IX (UserId, OccurredAtUtc) │ IX (RoomId, EventType, OccurredAtUtc)
    FK UserId → AspNetUsers ON DELETE SET NULL
        │
        ├──────────────► EventCleanupService                 [Program.cs:214]
        │                  purges OccurredAtUtc < UtcNow-180d
        │                  single unbatched ExecuteDeleteAsync, runs at startup + every 24h
        ▼
CURRENT AGGREGATION
  ⚠ NONE. There is no aggregation layer.
    Every metric is computed live, per request, over production tables.
    Several materialise full result sets into memory (.ToList() then LINQ-to-Objects).
        │
        ▼
CURRENT DASHBOARD API
  AnalyticsRepository   (11 methods, raw LINQ over Users/Rooms/RoomParticipants/Reports/UserEvents)
        ▼
  AnalyticsService      (IMemoryCache, 10-min TTL, 11 static SemaphoreSlim stampede guards)
        ▼
  AnalyticsController   (11 GET routes, [Authorize(Roles="Admin,Coach")] + VerificationStatus=Active)
  AdminController       (GET /Api/V1/Admin/Dashboard/Stats → AdminService.GetDashboardStatsAsync)
        ▼
  admin.cocorraapp.com  (separate repository — not in scope for this blueprint)
```

## 1.2 Structural problems this architecture must solve

| # | Problem | Evidence | Consequence |
|:--:|---|---|---|
| **C-1** | Two silent data-loss paths | Channel `DropWrite`; `batch.Clear()` in `finally` | Event counts cannot be asserted as complete |
| **C-2** | No idempotency key | `UserEvent.Id` is a DB identity | Duplicates are indistinguishable from genuine repeats |
| **C-3** | No aggregation layer | Every metric is a live query | Cost scales with data volume and admin refresh rate |
| **C-4** | Snapshot state, no transition history | `IsHandRaised`, `IsOnStage`, deleted rows | Historical questions structurally unanswerable |
| **C-5** | SignalR events lack enrichment | `IHttpContextAccessor.HttpContext` is null in hubs | `SessionId` always NULL on room events |
| **C-6** | No trust metadata on responses | `Response<T>.Meta` unused | Wrong and right metrics look identical |
| **C-7** | In-memory correctness dependencies | `_connections` static dict; `IMemoryCache` session dedup | Breaks on restart; breaks on multi-instance |
| **C-8** | Hardcoded retention, unbatched purge | `AddDays(-180)`; single `ExecuteDeleteAsync` | No policy control; lock-escalation risk as volume grows |

---

# Part 2 — Target Architecture

```
┌────────────────────────────────────────────────────────────────────────────┐
│  1. DOMAIN / PRODUCT ACTION                                                │
│     Existing services and hubs, unchanged in behaviour                     │
│     RoomService · RoomHub · AuthServices · AdminService · SupportService    │
│     FriendService · ChatService · BlockService · PushNotificationService    │
└────────────────────────────────────────────────────────────────────────────┘
        │  MODIFY — add emit calls at existing state-transition points
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  2. ANALYTICS EVENT PRODUCER                                               │
│     IEventTracker  (REUSE, interface extended)                             │
│       • existing:  Track(eventType, userId, properties)      — unchanged   │
│       • added:     Track(eventType, userId, properties, eventKey)          │
│                    eventKey = caller-supplied idempotency key (nullable)   │
│     EventTracker   (MODIFY)                                                │
│       • stamps EventId (Guid) at enqueue                                   │
│       • stamps SchemaVersion                                               │
│       • accepts an explicit context when HttpContext is absent (C-5)       │
└────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  3. RELIABLE EVENT PIPELINE                                                │
│     Channel<UserEvent>  (REUSE — capacity configurable)                    │
│     EventFlushService   (MODIFY — durability)                              │
│       • bounded retry with backoff on transient DB failure                 │
│       • dead-letter to disk/table after exhausted retries  → closes C-1    │
│       • drop + failure counters exposed for monitoring                     │
└────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  4. RAW EVENT STORE                                                        │
│     dbo.UserEvents  (REUSE + EXTEND — see 16-raw-event-storage-strategy)    │
│       + EventId        uniqueidentifier, UNIQUE      → idempotency (C-2)   │
│       + SchemaVersion  tinyint                       → event evolution      │
│       + CorrelationId  uniqueidentifier NULL         → cross-event chains   │
│     EventCleanupService (MODIFY — batched delete, configurable retention)   │
└────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  5. AGGREGATION LAYER                              ★ NEW                   │
│     AnalyticsAggregationService : BackgroundService                        │
│       • hourly incremental rollup, watermark-checkpointed                  │
│       • idempotent upsert per (grain, date, dimension)                     │
│       • replayable backfill over the raw store                             │
│     StateSnapshotService : BackgroundService        ★ NEW                  │
│       • daily capture of pure-state counts          → closes C-4 partially │
└────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  6. ANALYTICS READ MODELS                          ★ NEW                   │
│     dbo.DailyPlatformMetrics · dbo.DailyRoomMetrics · dbo.DailyHostMetrics │
│     dbo.DailyFunnelMetrics   · dbo.DailyStateSnapshots                     │
│       small, indexed, append-mostly, survive raw-event purge  → C-3, C-8   │
└────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  7. VERIFIED METRICS LAYER                         ★ NEW                   │
│     IMetricRegistry — one contract per metric (14-metric-contracts.md)     │
│       • formula, population, inclusions/exclusions, trust level            │
│       • single source of truth for both computation and displayed metadata │
└────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  8. DASHBOARD API                                                          │
│     AnalyticsRepository v2 (MODIFY — read models; corrected formulas)      │
│     AnalyticsService      (REUSE — caching/stampede kept as-is)            │
│     AnalyticsController   (MODIFY — corrected + new routes)                │
│     Response<T>.Meta      (REUSE — carries trust metadata)      → C-6      │
└────────────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌────────────────────────────────────────────────────────────────────────────┐
│  9. DECISION DASHBOARD                                                     │
│     admin.cocorraapp.com — separate repository, contract-only in scope     │
└────────────────────────────────────────────────────────────────────────────┘
```

---

# Part 3 — Architecture Decisions

`REUSE` = keep as-is · `MODIFY` = change in place · `REPLACE` = swap implementation, keep the role · `NEW` = does not exist today

| Layer | Responsibility | Existing Component Reused? | New Component Needed? | Disposition | Why |
|---|---|---|---|:--:|---|
| **1. Domain action** | Perform the product action | `RoomService`, `RoomHub`, `AdminService`, `AuthServices`, `FriendService`, `ChatService`, `SupportService`, `BlockService`, `PushNotificationService` | No | **MODIFY** | Emit sites are added at existing state-transition points. Every target service already has `IEventTracker` injected or trivially can. No behavioural change to the product action itself. |
| **2. Event producer — interface** | Accept an event from domain code without blocking it | `IEventTracker` | No | **MODIFY** | Add an overload carrying an idempotency key and an explicit context. Existing 3-argument signature is preserved so all ~24 current call sites compile untouched. |
| **2. Event producer — implementation** | Enrich, serialise, enqueue | `EventTracker` | No | **MODIFY** | Stamp `EventId` and `SchemaVersion`; accept explicit context for the SignalR path (C-5). The never-throw contract and `ExtractRoomId` promotion are kept verbatim — both are correct. |
| **3. Queue** | Decouple the request thread from the database | `Channel<UserEvent>` singleton (`Program.cs:210`) | No | **MODIFY** | Keep the channel and `DropWrite` semantics; move capacity to configuration and add a drop counter. A durable broker (Kafka, Service Bus) is rejected: it adds infrastructure Cocorra does not run, to solve a loss path that retry plus dead-letter closes at a fraction of the cost. |
| **3. Flush** | Persist batches durably | `EventFlushService` | No | **MODIFY** | The single most important change in this layer: bounded retry with backoff, then dead-letter. Today `batch.Clear()` in a `finally` discards up to 100 events on any DB fault (C-1). |
| **3. Dead-letter sink** | Hold events that exhausted retries | — | **Yes** | **NEW** | Without it, "retry" only narrows the loss window rather than closing it. Kept deliberately minimal: an append-only table or file, never read by the API. |
| **4. Raw store** | Immutable event log | `dbo.UserEvents` | No | **MODIFY (extend)** | Schema is sound; three columns are added. Replacing it would discard three well-chosen composite indexes and a working `SetNull` policy, and would require rewriting all 11 repository methods for no measured benefit. |
| **4. Retention** | Bound raw storage | `EventCleanupService` | No | **MODIFY** | Batch the delete (currently one unbatched `ExecuteDeleteAsync` against a 3-index table concurrently receiving inserts) and make the 180-day window configurable. Aggregates, not raw rows, become the long-term record. |
| **5. Aggregation** | Turn raw events into stable daily facts | — | **Yes** | **NEW** | The central architectural addition. No aggregation exists today; every metric is a live query. This layer is what makes metrics cheap, stable, historically durable, and independent of the raw-event purge. |
| **5. State snapshots** | Give pure-state counts a time dimension | — | **Yes** | **NEW** | Closes C-4 for quantities that are genuinely state, not events (pending queue depth, FCM token coverage, active user count). Cheap: a scheduled job reading existing tables. **INFERENCE** — history not captured today is unrecoverable tomorrow, which is why this ships early despite its small size. |
| **5. Background host** | Run the above | ASP.NET `BackgroundService` (already used twice) | No | **REUSE (pattern)** | Hangfire or Quartz would add a dependency and a job store to replace a pattern already working in-repo. See `18-background-processing-plan.md` for the comparison and the conditions that would change this. |
| **6. Read models** | Serve dashboard queries cheaply | — | **Yes** | **NEW** | Five narrow tables. Small enough to index freely, stable enough to trust, and they survive the raw purge — which is what gives Cocorra a history longer than 180 days. |
| **7. Metric registry** | One definition per metric, used by both computation and display | — | **Yes** | **NEW** | Makes `08a-metric-trust-framework.md` executable rather than documentary. **INFERENCE** — if the contract lives only in a markdown file it will drift from the code within one release; binding trust metadata to the computation is what prevents that. |
| **8. Query layer** | Translate requests into reads | `AnalyticsRepository` | No | **MODIFY** | Corrected formulas (retention, funnel, host exclusion) plus read-model-backed methods. The interface shape and DI registration are preserved. |
| **8. Caching** | Absorb repeated dashboard loads | `AnalyticsService` — `IMemoryCache` + 11 `SemaphoreSlim` | No | **REUSE** | Already correct: per-metric stampede protection with double-checked locking and a 10-minute TTL. **INFERENCE** — pre-aggregation reduces what this cache protects against, but it costs nothing to keep and still absorbs repeated identical requests. No change. |
| **8. API surface** | Expose metrics | `AnalyticsController`, `Router.AnalyticsRouting` | No | **MODIFY** | Existing routes are corrected in place; new routes are added for supply, support, and moderation. The route-constant convention in `Router.cs` is followed. |
| **8. Trust transport** | Carry metric metadata to the client | `Response<T>.Meta` (`Cocorra.BLL/Base/Response.cs`) | No | **REUSE** | Already on every response, already accepted by `ResponseHandler.Success<T>(entity, meta)`, currently always null. Populating it is purely additive and cannot break a client that ignores it. |
| **9. Dashboard UI** | Present decisions | `admin.cocorraapp.com` | Out of scope | **CONTRACT ONLY** | Separate repository. This blueprint specifies the API contract and the trust-display requirements it must honour (`20-dashboard-implementation-blueprint.md`). |

---

## 3.1 Components explicitly NOT recommended

**RECOMMENDATION** — each of the following was considered and rejected. Recording the rejection prevents it being relitigated during implementation.

| Rejected | Why rejected | What would change the decision |
|---|---|---|
| Dedicated analytics database (ClickHouse, Timescale, warehouse) | Adds a datastore, a sync path, and schema drift to solve a problem indexed rollup tables solve. No data-engineering function exists. No measured query-performance problem. | `UserEvents` growth (R-3) reaching a scale where rollup jobs cannot complete inside their window, or a genuine multi-source reporting need. |
| Durable message broker (Kafka, RabbitMQ, Service Bus) | The in-process channel plus retry plus dead-letter closes the same loss path without new infrastructure. Cocorra runs a single API container. | A move to multiple API instances **and** a requirement that no event may ever be lost. |
| Event-sourcing the domain | Enormous change to `RoomService`/`RoomHub` for analytics benefit that targeted events already deliver. | Not foreseeable at this scale. |
| Replacing `IMemoryCache` with Redis | The cache is per-instance and correct for a single instance. Redis solves a multi-instance problem Cocorra does not have. | Horizontal scaling of the API, which would also force `RoomHub._connections` and the session dedup cache out of process (C-7). |
| Third-party analytics SDK (Segment, Amplitude, Mixpanel) | Sends user behavioural data to an external processor; adds cost and a data-protection question; duplicates a working pipeline. **INFERENCE** — Cocorra's data is unusually sensitive (`MentalHealth` rooms, voice recordings), which raises the bar. | An explicit product decision accepting the data-protection posture. |
| Hangfire / Quartz | Replaces a `BackgroundService` pattern already working twice in-repo with a dependency and a job store. | Need for cross-instance job coordination, or operator-facing job management. |

---

# Part 4 — Where Each Layer Lives

Target file placement follows the existing three-project layout exactly. No new projects.

```
Cocorra.API/
  Controllers/
    AnalyticsController.cs                     MODIFY  corrected + new routes
  Hubs/
    RoomHub.cs                                 MODIFY  emit sites: hand_raised, hand_lowered,
                                                        stage_promoted, stage_demoted,
                                                        mic_deactivated, speaker_time_exhausted,
                                                        extra_time_granted, user_kicked
  Middleware/
    SessionTrackingMiddleware.cs               MODIFY  see 15- (session strategy)

Cocorra.BLL/
  Services/
    EventTracking/
      IEventTracker.cs                         MODIFY  overload with eventKey + explicit context
      EventTracker.cs                          MODIFY  EventId, SchemaVersion, context fallback
      EventFlushService.cs                     MODIFY  retry, dead-letter, counters
      EventCleanupService.cs                   MODIFY  batched delete, configurable retention
      EventTrackingOptions.cs                  NEW     bound configuration
    Analytics/                                 NEW     (sibling of AnalyticsService)
      AnalyticsAggregationService.cs           NEW     BackgroundService — hourly rollups
      StateSnapshotService.cs                  NEW     BackgroundService — daily snapshots
      IAggregationCheckpointStore.cs           NEW     watermark persistence
      MetricRegistry.cs                        NEW     metric contracts in code
      IMetricRegistry.cs                       NEW
    AnalyticsService/
      AnalyticsService.cs                      MODIFY  attach trust metadata via Meta
      IAnalyticsService.cs                     MODIFY  new methods
    RoomService/RoomService.cs                 MODIFY  room_went_live, room_ended extension,
                                                        reminder_set / reminder_removed
    AdminService/AdminService.cs               MODIFY  user_status_changed (needs adminId)
    NotificationService/…                      MODIFY  push_send_attempted / push_send_result
    SupportService/SupportService.cs           MODIFY  moderation_action_taken

Cocorra.DAL/
  Models/
    UserEvent.cs                               MODIFY  EventId, SchemaVersion, CorrelationId
    EventTypes.cs                              MODIFY  new constants
    Analytics/                                 NEW
      DailyPlatformMetrics.cs                  NEW
      DailyRoomMetrics.cs                      NEW
      DailyHostMetrics.cs                      NEW
      DailyFunnelMetrics.cs                    NEW
      DailyStateSnapshot.cs                    NEW
      AggregationCheckpoint.cs                 NEW
      DeadLetterEvent.cs                       NEW
  Data/
    AppDbContext.cs                            MODIFY  DbSets + index configuration
  Repository/
    AnalyticsRepository/                       MODIFY  corrected formulas, read-model reads
    AnalyticsReadModelRepository/              NEW     rollup reads and upserts
  AppMetaData/Router.cs                        MODIFY  new route constants
  DTOS/AnalyticsDto/                           MODIFY  new DTOs + MetricTrustMeta

Cocorra.Tests/                                 MODIFY  extend EventTrackingSmokeTests pattern
```

**INFERENCE** — every new component sits inside an existing project and follows an existing convention: `BackgroundService` for background work, repository-per-aggregate under `Cocorra.DAL/Repository/`, route constants in `Router.cs`, DTOs under `DTOS/`. Nothing here requires the implementer to learn a new pattern, which is the main reason to prefer it over a structurally cleaner design that would.

---

# Part 5 — Data Flow, Target State

Traced for the highest-value new event, to make the design concrete.

```
USER ACTION — a participant raises their hand
        │
        ▼
RoomHub.RaiseHand(roomId)                              [Cocorra.API/Hubs/RoomHub.cs:381-400]
  existing behaviour, unchanged:
    participant.IsHandRaised = true
    UpdateParticipantAsync + SaveChangesAsync
    Clients.Group(roomId).SendAsync("HandRaised", …)
  ADDED — after the successful save, never before:
    _eventTracker.Track(
        EventTypes.HandRaised,
        userId,
        new { roomId, secondsSinceJoin, currentStageOccupancy, stageCapacity, selectionMode },
        eventKey: $"hand_raised:{roomGuid}:{userId}:{raiseSequence}")
        │
        ▼
EventTracker.Track(…)
  EventId       = Guid.NewGuid()
  SchemaVersion = 1
  RoomId        ← ExtractRoomId(propertiesJson)         [existing, unchanged]
  SessionId     ← explicit context (HttpContext is null in hubs — C-5)
  OccurredAtUtc = DateTime.UtcNow
  Channel.Writer.TryWrite(evt)   — non-blocking; a drop increments a counter and logs
        │
        ▼
EventFlushService
  drain ≤ 100 → AddRange → SaveChangesAsync
  on transient failure → retry with backoff (bounded)
  on exhausted retries → DeadLetterEvents, counter incremented
  UNIQUE(EventId) makes a retried batch safe to re-apply
        │
        ▼
dbo.UserEvents         (raw, retained per policy — see 16-)
        │
        ▼
AnalyticsAggregationService        hourly, watermark-checkpointed
  reads UserEvents WHERE Id > lastProcessedId
  computes daily grains, upserts by natural key (idempotent)
        │
        ▼
dbo.DailyRoomMetrics   (Date, RoomId, Joiners, Speakers, HandRaises, StagePromotions, …)
        │
        ▼
AnalyticsRepository.GetStageFunnelAsync(from, to)   — reads the rollup, not raw events
        │
        ▼
AnalyticsService                    IMemoryCache 10-min TTL + SemaphoreSlim   [unchanged]
  wraps the result with trust metadata from IMetricRegistry
        │
        ▼
Response<StageFunnelDto> {
    Data = { … },
    Meta = { trustLevel = "VERIFIED",
             historicalReliability = "HISTORICALLY_ACCURATE",
             dataFreshnessUtc = "2026-09-01T09:00:00Z",
             exclusions = ["room host"],
             limitations = ["raw events retained 180 days"] }
}
        │
        ▼
GET /Api/V1/Analytics/Rooms/StageFunnel            [Authorize(Roles="Admin,Coach")]
        │
        ▼
admin.cocorraapp.com — renders the funnel with a VERIFIED badge
```

---

# Part 6 — Architectural Invariants

**RECOMMENDATION** — these are non-negotiable constraints on the implementation. Every backlog item in `23-execution-backlog.md` is checked against them.

| # | Invariant | Rationale |
|:--:|---|---|
| **INV-1** | Analytics failure must never fail a product action. | Already the contract in `EventTracker.Track` (try/catch, explicit comment). Every new emit site must preserve it. |
| **INV-2** | Emit only **after** the domain write has succeeded. | An event for an action that was rolled back is worse than a missing event: it is a false positive that no downstream consumer can detect. |
| **INV-3** | Every event carries a stable `EventId`. | The only foundation for idempotency (C-2). Without it, retry cannot be made safe and duplicates cannot be distinguished from genuine repeats. |
| **INV-4** | Aggregation is idempotent and replayable. | Rollups must be safe to re-run after a failure, a restart, or a formula correction. Non-idempotent aggregation makes backfill impossible. |
| **INV-5** | Read models never hold data that cannot be recomputed from raw events, within the raw retention window. | Keeps the raw store authoritative and makes rollups disposable. If a formula is wrong, drop and rebuild rather than patch in place. |
| **INV-6** | Every metric served by the API carries trust metadata. | The core failure of the current dashboard is that wrong and right metrics are visually identical (C-6). |
| **INV-7** | Host exclusion is explicit in every room-participation metric. | Finding A. It must appear in the metric contract's `Exclusions` field, not be left as an implicit query detail that a later edit can silently drop. |
| **INV-8** | No metric ships without a validation method. | `08a` mandatory rule. Enforced by `14-metric-contracts.md`. |
| **INV-9** | UTC everywhere internally; timezone context returned to the client. | The user base is UTC+2/+3; converting server-side would corrupt stored data, and not surfacing offset would mislead the reader. |
| **INV-10** | No new required infrastructure. | Everything runs on the existing SQL Server, the existing container, and the existing `BackgroundService` pattern. |

---

# Part 7 — What This Architecture Does Not Solve

**INFERENCE** — stated explicitly so the plan is not read as more complete than it is.

| Not solved | Why | Where it is addressed |
|---|---|---|
| Hard deletes destroying churn evidence | A data-model and legal decision, not an architectural one | `13-data-trust-correction-plan.md`, `23-execution-backlog.md` |
| Media-layer blindness | Requires LiveKit webhook ingestion — a genuinely new inbound surface | `23-` (P3) |
| No experimentation capability | Requires assignment infrastructure and adequate user volume | `23-` (P3) |
| No error tracking | Better served by a structured logging sink than by analytics events | `23-` (P3) |
| Multi-instance correctness (`_connections`, session dedup) | Pre-existing architectural constraint (C-7), not introduced here | Noted as a scaling blocker in `24-dependency-graph.md` |
| Dashboard UI implementation | Separate repository | `20-` specifies the contract only |

**One consequence worth stating plainly (INFERENCE)** — this architecture makes Cocorra's metrics **trustworthy and durable**. It does not make them **complete**. Completeness depends on the emit-site work in `15-event-implementation-contracts.md` and on the soft-delete decision, both of which are product and data-model changes rather than architectural ones.
