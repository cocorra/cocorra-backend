# Cocorra Analytics — Final Implementation Report

> **Generated**: 2026-09-02 | **Branch**: `main`
> **Covers**: the complete analytics programme — Waves 0 through 7 — not only the final hardening phase.
> **Build**: PASS, 0 errors, 13 warnings (all pre-existing) · **Tests**: 254 / 254 pass

---

# 1. Executive Summary

## Before

Cocorra had a working event-tracking backbone and a twelve-metric admin dashboard. An audit graded **one of the twelve as VERIFIED**. Three were UNRELIABLE. **All twelve rendered identically.**

The infrastructure was well designed — a non-blocking event producer, room-id promotion into an indexed column, three well-chosen indexes, a fail-fast startup guard on the IP-hash salt. It was also under-hardened, and nothing in the system distinguished a sound number from a wrong one.

## The problem

Five verified defects, and one structural failure that mattered more than any of them.

The sharpest case: **a silent host accrued a room's full 2–3 hours as `TotalSpokenSeconds` while emitting no `mic_activated`.** Top Speakers ranked coaches by room length. Active-vs-Passive counted those same coaches as passive listeners. **The same person appeared as the platform's #1 speaker and as a silent listener, in two panels of the same dashboard**, and both panels looked equally credible.

That is the structural failure. Not that some metrics were wrong — every analytics system has wrong metrics — but that **nothing told them apart.**

## The implementation

Nine architectural layers. **Five reused or modified in place, four new, nothing replaced.**

- Three columns added to the raw event store rather than replacing it
- Retry, per-row duplicate fallback and a dead-letter table rather than a message broker
- `BackgroundService` aggregation rather than Hangfire
- `Response<T>.Meta` — present on every response and always `null` — became the trust transport
- Five read models retained **indefinitely**, giving Cocorra trend history beyond the 180-day raw window for the first time
- 27 metric contracts in code, with a test that fails the build if a served metric lacks one

## After

| | Before | After |
|---|---|---|
| Metrics with an executable contract | 0 | **27** |
| Metrics graded VERIFIED | 1 of 12 | **14 of 27** |
| Metrics rendering identically regardless of trust | **all** | **none** — trust is on every response |
| Silent data-loss paths | 2 | **0** |
| Analytics endpoints | 11 | **28** |
| Read models | 0 | **5**, indefinite retention |
| Core-loop events instrumented | 2 of 6 steps | **6 of 6** *(2 behind flags, off)* |
| Unit tests | 0 → 209 | **254** |
| Trend history horizon | 180 days | **indefinite** |

**Trust verdict: the served metric set is now usable for major decisions, with three exceptions that are labelled as such** — M-400 is EXPERIMENTAL until four weeks of emission accumulate, and M-102-LEGACY and M-507-LEGACY are UNRELIABLE and must not be displayed (both are retained only until their routes become `410 Gone` at cutover).

---

# 2. Why This Work Was Necessary

Five defects, each verified against the code at the time.

| ID | Defect | Consequence |
|---|---|---|
| **D-1** | **Host mic open from room start.** A host's mic opens with the room; `TotalSpokenSeconds` accrued idle open-mic time | Top Speakers ranked hosts by room length. The same host appeared as top speaker *and* passive listener |
| **D-2** | **Two silent data-loss paths.** Channel `DropWrite` on overflow — where `TryWrite` returns `true` after discarding, so the drop warning **could never fire** — and `batch.Clear()` in a `finally`, discarding up to 100 events on any DB fault | Counts were lower bounds by an **unmeasurable** margin |
| **D-3** | **Growth history rewritten.** Users bucketed by `CreatedAt`, counted by **current** status | A user who registered in month 1 and was banned in month 6 appeared as banned in month 1. Distortion grew with bucket age |
| **D-4** | **Retention wrong twice.** Exact-day matching (`timeDiff.Days == day`) over a cookie-derived signal on a Flutter client, with an unbounded activity fetch | A user active on days 2 and 5 counted for neither D1 nor D7 |
| **D-5** | **Funnel was not a funnel.** Steps counted independently | The result could **widen downward** |

Plus, structurally: no aggregation layer, no idempotency key, no read models, no state history, no trust metadata, no host-side analytics, no support analytics, no media telemetry, no error tracking, no experimentation.

**And the one that made all of it worse**: `Response<T>.Meta` existed on every response and was always `null`. The transport for distinguishing good numbers from bad was already built, already accepted by `ResponseHandler`, and unused.

---

# 3. Architecture Before vs After

## Old

```
DOMAIN ACTION      RoomService · RoomHub · AdminService · AuthServices
      ↓
EVENT PRODUCER     IEventTracker / EventTracker
      ↓            (try/catch — "must NEVER throw back to the user" ✓)
      ↓
      ↓            ✗ Channel FullMode = DropWrite
      ↓              TryWrite returns TRUE after discarding, so the
      ↓              drop-warning branch was unreachable. Silent AND unmeasurable.
      ↓
PIPELINE           Channel<UserEvent> → EventFlushService
      ↓            ✗ batch.Clear() in finally — up to 100 events lost per DB fault
      ↓            ✗ no retry, no dead-letter, no idempotency key
      ↓
RAW STORE          dbo.UserEvents   (3 good indexes, roomId promoted ✓)
      ↓            ✗ unbatched ExecuteDeleteAsync purge, hardcoded 180 days
      ↓
                   ✗ NO AGGREGATION LAYER
                   ✗ NO READ MODELS  →  180-day history ceiling
                   ✗ NO METRIC CONTRACTS
      ↓
DASHBOARD API      AnalyticsRepository → AnalyticsService → AnalyticsController
      ↓            ✗ Response<T>.Meta present on every response, ALWAYS NULL
      ↓
DASHBOARD          11 endpoints · 12 metrics · 1 VERIFIED · all rendered identically
```

## New

```
DOMAIN ACTION      RoomService · RoomHub · AdminService · AuthServices
                   SupportService · FriendService · ChatService
                   PushNotificationService · LiveKitWebhookController   [MODIFIED — 12 new emit sites]
      ↓
EVENT PRODUCER     IEventTracker / EventTracker                        [MODIFIED]
                   + EventId · SchemaVersion · CorrelationId
                   + NewEventEmissionEnabled / HighFrequencyEventsEnabled  (conjunctive)
                   + EventPipelineMetrics — 6 in-process counters
      ↓
                   Channel FullMode = Wait  ← so TryWrite returns FALSE when full
                   and the drop becomes OBSERVABLE and COUNTABLE
      ↓
RELIABLE PIPELINE  EventFlushService                                   [MODIFIED]
                   ├─ classify: duplicate-key / transient / permanent
                   ├─ duplicate  → PER-ROW fallback (99 of 100 persist)
                   ├─ transient  → bounded retry, exponential backoff
                   └─ exhausted  → DeadLetterEvents  ★ NEW TABLE
      ↓
RAW STORE          dbo.UserEvents                                      [EXTENDED — 3 columns]
                   + UX_UserEvents_EventId (unique)
                   + IX_UserEvents_CorrelationId (filtered)
                   EventCleanupService — batched, configurable         [MODIFIED]
      ↓
AGGREGATION        AnalyticsAggregationService  ★ NEW
                   ├─ watermark on UserEvent.Id + 120s safety lag
                   ├─ full recompute per affected date → idempotent
                   └─ AggregationCheckpoint  ★ NEW TABLE
                   StateSnapshotService         ★ NEW  (cannot be backfilled)
                   AnalyticsBackfillService     ★ NEW  (shares the live code path)
      ↓
READ MODELS        DailyPlatformMetrics · DailyRoomMetrics              ★ NEW
                   DailyHostMetrics    · DailyFunnelMetrics             INDEFINITE RETENTION
                   DailyStateSnapshots                                 → history beyond 180 days
      ↓
VERIFIED METRICS   IMetricRegistry — 27 contracts in code              ★ NEW
                   MetricRegistryContractTests FAILS THE BUILD
                   if a served metric has no contract
      ↓
OBSERVABILITY      PipelineHealthService  ★ NEW    → GET /Analytics/System/Health
                   StructuredFileLogger   ★ NEW    (opt-in, survives restart)
      ↓
DECISION LAYER     DecisionCenterService  ★ NEW    (gated on 4 complete weeks of RM-1,
                                                    which the backfill can supply)
      ↓
DASHBOARD API      AnalyticsRepository v2 (6 partials) · AnalyticsService [MODIFIED]
                   Response<T>.Meta — POPULATED                         [REUSED as designed]
                   { trustLevel (weakest component) · metrics[] · display{offset} }
      ↓
DASHBOARD          28 endpoints · 26 contracted metrics · trust on every response
```

**The reuse rate is the design's main strength.** Nothing was replaced. A dedicated analytics database, a durable message broker, a third-party analytics SDK and Hangfire were each considered and rejected with a stated reason.

---

# 4. Every Wave

## Wave 0 — Remove the compounding constraints

**Objective**: ship the things that get more expensive every day they are deferred.

| Item | Delivered |
|---|---|
| **AN-009** `StateSnapshotService` | `StateSnapshotService.cs`, `IStateSnapshotService`, `SnapshotGapReport`, table `DailyStateSnapshots`, hosted + singleton registration |
| **AN-001** measurement | *Engineering side done differently and better* — `EventPipelineMetrics` + `PipelineHealthService` + `GET /Analytics/System/Health` replaced the log grep. **The measurement itself has not been taken** (requires production). |
| Data-protection question | **Raised. Still unanswered.** Blocks AN-013. |
| SQLite in `Cocorra.Tests` | `Microsoft.EntityFrameworkCore.Sqlite` 10.0.2 |

**DB**: `20260901080448_AddDailyStateSnapshots`
**Tests**: `DailyStateSnapshotTests`

**Discovery**: `EFCore.InMemory` does not enforce unique indexes or `DeleteBehavior`. Eight idempotency tests would have passed **vacuously**. One project line decided whether they meant anything.

**Why this wave came first**: `StateSnapshotService` is five `COUNT` queries on a timer, and it is the only read model that **cannot be backfilled**. Every other item costs the same whenever it is done. This one gets permanently more expensive daily.

---

## Wave 1 — Query corrections + the enabling schema

**Objective**: fix all three UNRELIABLE metrics. Five of six items need no schema change.

| Item | Delivered |
|---|---|
| **AN-005** Host exclusion; remove Top Speakers | Host exclusion at the query layer. `TopSpeakers` and `UsersWhoRaisedHand` **deleted from the DTO** |
| **AN-006** Replace retention | `AnalyticsRepository.Corrections.cs` → `GET /Analytics/Return/Weekly` |
| **AN-007** Sequential funnel | Same file → `GET /Analytics/Activation/Funnel` |
| **AN-008** Split growth; reconstruct status | `AnalyticsRepository.UserGrowth.cs` — `StatusAtTime` + `statusHistoryAvailableFromUtc` |
| **AN-002** `EventId` / `SchemaVersion` / `CorrelationId` | Columns, unique index, **filtered** correlation index, batched backfill, constraint applied after |

**DB**: `20260901082008_AddAnalyticsPipelineAndReadModels`
**Tests**: `QueryCorrectionsTests`, `QueryCorrectionsReplacementTests`

**Decision worth recording**: AN-006 **replaced** rather than repaired. Changing `== day` to `>= day` while leaving `session_started` as the signal would have produced a plausible number resting on a cookie-derived input never validated on the Flutter client.

---

## Wave 2 — Pipeline durability, aggregation, trust

| Item | Delivered |
|---|---|
| **AN-003** Harden flush | Failure classification, bounded retry with backoff, **per-row duplicate fallback**, dead-lettering, 6 counters, shutdown drain |
| **AN-004** Batched purge | `EventCleanupService` — `CleanupBatchSize`, `RawEventRetentionDays` |
| **AN-014** Read models | 5 tables + `AggregationCheckpoint` |
| **AN-015** Aggregation | Watermark on `UserEvent.Id` with a **120-second safety lag** |
| **AN-012** Metric registry + trust | `IMetricRegistry`, `MetricRegistry`, `BuildMeta`, weakest-component composite rule |
| **AN-010** Activation dedup | Deterministic `eventKey` → the DB constraint enforces idempotency, not a read-then-write race |
| **AN-011** `user_status_changed` | `+adminId`, `+isBulk` threaded through `IAdminService` |

**Tests**: `PipelineHardeningTests`, `PipelineClassificationTests`, `AnalyticsAggregationTests`, `MetricRegistryContractTests`

**Two discoveries, both non-obvious:**

**1. `DropWrite` made the drop unloggable.** With `FullMode = DropWrite`, the channel discards the incoming item and `TryWrite` **still returns `true`** — so the existing `if (!TryWrite(evt))` warning could never fire. The drop was not merely easy to miss in logs; it was **unlogged and unmeasurable**. Changed to `Wait`, where `TryWrite` returns `false` immediately when full (only `WriteAsync` waits), keeping the producer non-blocking while making the drop countable.

**2. The safety lag exists because identity values are assigned before commit.** A row with `Id 500` can become visible *after* `Id 501`. Advancing the watermark straight to `MAX(Id)` would skip 500 permanently and silently. Excluding the most recent 120 seconds of inserts gives in-flight transactions time to commit.

**Highest-risk detail in the whole programme**: `AddRange` + one `SaveChangesAsync` means a single duplicate key fails all 100 rows. Adding the unique constraint **without** the per-row fallback would have created a **new 99-event-wide loss path** — strictly worse than the defect being fixed.

---

## Wave 3 — Backfill, endpoints, low-frequency events

| Item | Delivered |
|---|---|
| **AN-016** Backfill | `AnalyticsBackfillService`, `POST /Analytics/System/Backfill`. **Shares the rollup code path** with live aggregation, so byte-identity is structural |
| **AN-020** Supply Health | `GET /Analytics/Supply/Health` — active hosts, retention, concentration, schedule + offset |
| **AN-021** Report rate by category | `GET /Analytics/Safety/ReportRate` — **Admin only** |
| **AN-022** Review latency | `GET /Analytics/Review/Latency` — **percentiles only, no mean** |
| **AN-023** Support analytics | `GET /Analytics/Support` — proxy label in `Meta` |
| **AN-017** Low-frequency events | `room_went_live`, `stage_promoted`, `stage_demoted`, `participant_kicked`, `speaker_time_extended`, `speaker_time_exhausted` |

**Tests**: `Wave3EndpointTests`

**The subtle risk that was avoided**: `stage_promoted.UserId` is the **promoted participant**, not the host who approved them. The existing `room_join_approved` uses the opposite convention. An implementer following that precedent would have broken M-400 **in a way no metric test would catch** — the funnel would have returned plausible numbers about the wrong population.

**Contract requirement, not a preference**: AN-022 returns **no mean**. If most reviews take 20 minutes and 15% take three days, the mean describes nobody and hides the users being harmed. A test asserts the response contains no mean field.

---

## Wave 4 — High-frequency events, richer payloads, observability

| Item | Delivered |
|---|---|
| **AN-018** High-frequency events | `hand_raised`, `hand_lowered`, `mic_deactivated` — behind a **separate** flag |
| **AN-019** Extend `room_joined` / `room_ended` | `isHost`, `isRejoin`, `entrySource`; `actualDurationSeconds`, `endReason`, `peakParticipants`. `SchemaVersion` → 2 |
| **AN-024** Push delivery | `push_send_attempted` / `push_send_result`, correlated by `CorrelationId` |
| **AN-025** Job health | `GET /Analytics/System/Health` |

**Tests**: `Wave456Tests`

**`mic_deactivated` fires from five close sites** — self-mute, demotion, kick, room end, disconnect. Emitting from only the first would have silently under-counted every segment that ended because somebody else acted.

**Why the two increments are separate flags**: high-frequency events scale with engagement and land hardest on the busiest rooms. The gate is **conjunctive in code** — `HighFrequencyEventsEnabled` returns `false` unless both flags are set — so the high-volume increment cannot be enabled before the low-frequency one has proven stable. Without that, a drop-rate spike could not be attributed to either.

---

## Wave 5 — P2 decision analytics

| Item | Delivered |
|---|---|
| **AN-026** Reminder events | `room_reminder_toggled` — the row is **deleted** on un-toggle, so the event is the only surviving record of a withdrawal |
| **AN-028** Room participation | Delivered as an **in-place correction** of `/Analytics/Participation` rather than a parallel route |
| **AN-029** Social endpoints | `GET /Analytics/Social` — reciprocity before volume |
| **AN-030** Social origin properties | `isFirstMessageToRecipient`, checked **before** the insert |
| **AN-031** `LeftAt` + preserve `JoinedAt` | `JoinedAt` is no longer overwritten on rejoin |
| **AN-033** Status enums + resolution timestamps | `ReportStatus` enum, `StatusCode`, resolution timestamps |
| **AN-034** Moderation action event | `moderation_action_taken` |
| **AN-037** MBTI dichotomies | `GET /Analytics/Mbti/Dichotomies` — **four dichotomies, not sixteen types** |
| **AN-038** Cohort grid | `GET /Analytics/Return/CohortGrid` + `hasSufficientHistory` |

**DB**: `20260901120157_AddParticipantLifecycleAndStatusCodes`

**AN-028 deviated from the plan, for the better.** The plan anticipated a new endpoint superseding `/Analytics/Participation`. What shipped was a corrected `/Analytics/Participation`. A parallel route would have left the misleading original serving traffic throughout cutover.

**AN-037 tests four dichotomies rather than sixteen types deliberately** — sixteen cells are too small at Cocorra's volume, and testing sixteen hypotheses invites a chance finding.

---

## Wave 6 — P3 advanced intelligence

| Item | Delivered |
|---|---|
| **AN-039** Decision Center | `DecisionCenterService`, `GET /Analytics/Decisions`. **Built, and gated** — must render "collecting baseline" until the endpoint reports `hasBaseline: true`. It reads RM-1, so the backfill can satisfy this without waiting; see `28-` §5 |
| **AN-040** LiveKit webhooks | `LiveKitWebhookController`, `media_session_event` |
| **AN-042** Structured log sink | `StructuredFileLogger`, registered **only** when `Analytics:StructuredLogPath` is set |
| **AN-041** Failure-path events | ⚠ **Constant declared, ZERO emission sites** — carried into Wave 7 |
| **AN-043/044/045** | Deliberately not started — see §12 |

---

## Wave 7 — Final hardening *(this phase)*

**Objective**: close every gap not blocked by an external decision; make the documentation match the code.

| # | Item | Delivered |
|:--:|---|---|
| **1** | **AN-027 stage funnel (M-400)** | `StageFunnelDto`, `AnalyticsRepository.StageFunnel.cs`, service + route + controller, M-400 contract, **15 tests** |
| **2** | **A-1 Platform Health** | `PlatformHealthDto`, `AnalyticsRepository.PlatformHealth.cs`, `GET /Analytics/Platform/Health`, **11 tests** |
| **3** | **AN-041 redefined and implemented** | `OperationFailures.cs` closed vocabularies, 5 emit sites, **10 tests**, decision record `27-` |
| **4** | **Metric registry reconciliation** | M-200 corrected, M-205 added, `14-metric-contracts.md` annotated at 22 headings, `26-` written |
| **5** | **AN-036 completed** | `AnalyticsDisplayDefaults`, `DisplayTimeZoneOffsetMinutes`, `Meta.display` on every response |
| **6** | **`MetricTrustLevel.Experimental`** | Added in the correct ordinal position |
| **7** | **TRUST-09 closed** | `AvgDurationHours` and `TopRoomDto.DurationHours` **removed** |
| **8** | **Configuration made explicit** | All 15 `Analytics` settings written into `appsettings.json` with activation pointers |
| **9** | **Clock-dependent test fixed** | `AnalyticsAggregationTests` — see the defect below |

### Defects found in Wave 7

**D-6 — A test that failed every morning and passed every afternoon.**

`AnalyticsAggregationTests.AggregationService_RollsUpPlatformMetrics_AndAdvancesWatermark` seeded events at `today.AddHours(4)` and `today.AddHours(5)` and asserted two were processed. The service correctly holds back the most recent 120 seconds of inserts. Before roughly **05:02 UTC** the seeded events sat inside that lag window, so the service correctly returned `0` and the test failed.

**The service was right; the test was wrong.** Recorded rather than fixed silently, because a test that fails on a schedule is usually read as flaky infrastructure and muted — and the assertion it was making (the watermark advances, the rollup is idempotent) is one of the more important in the suite.

**D-7 — M-200 was making a false statement about the platform's leading indicator.**

The contract read `"Rooms Gone Live" / COUNT(Rooms) WHERE Status != Scheduled`, while the constant was named `ActiveHosts` and the key was attached to **two endpoints with incompatible payloads**: `/Analytics/Supply/Health`, whose headline series is `DistinctHosts`, and `/Analytics/Rooms`, which has no host figure at all.

A reader of the trust envelope on supply health was being told Cocorra's leading indicator counts rooms. **M-200 had a complete, well-formed, internally consistent contract — it was simply about a different thing than the endpoint it was attached to, so no existing test could have caught it.**

**D-8 — The north star was unreachable on any page anyone could trust.**

M-100 had a contract but was referenced by exactly one consumer: `DecisionCenterService`, which must not be relied on until it reports `hasBaseline: true`. **The declared north star was therefore not readable anywhere trustworthy.** A-1 was specified in the API blueprint and had never been built. It now exists.

**D-10 / D-11 — two more metric keys attached to payloads they did not describe.**

Found by deliberately applying the review rule that the M-200 correction produced — *every `BuildMeta` argument must name a metric whose technical definition describes a figure actually present in that endpoint's DTO* — across all 24 call sites.

- **`GET /Analytics/Funnel` declared M-507**, the *corrected sequential* funnel contract, while computing the non-sequential version. **The trust envelope was certifying defect D-5 as fixed on the one route that still has it.** Fixed by adding `M-507-LEGACY`, graded UNRELIABLE, mirroring how `M-102-LEGACY` handles the retention route.
- **`GET /Analytics/Rooms` declared M-205 "Rooms Gone Live"** while `RoomAnalyticsDto` had no such field. Fixed by adding an explicit `RoomsGoneLive` field matching the contract's formula.

**Three instances of one defect class (M-200, M-507, M-205) is a pattern, not a coincidence.** All three had complete, well-formed, internally consistent contracts and passed every test — because a well-formed contract about the wrong thing is indistinguishable from a correct one to any check that does not compare the contract against the payload.

---

**D-9 — `AvgDurationHours` measured a form field.**

It averaged `Room.DurationHours`, declared as `public int DurationHours { get; set; } = 2;` — a host-configured **scheduling** input with a default. It never described how long a room ran; it described the number hosts typed into a form, and because most accept the default it sat close to a constant `2.0` while being presented as "average room duration". The Definition of Done and frontend contract test 7 both required its removal; it was still being served.

---

# 5. Data Model Changes

## `UserEvent` — extended, not replaced

| Column | Type | Why |
|---|---|---|
| `EventId` | `Guid`, required, **`UX_UserEvents_EventId` unique** | Stable identity across retries. **Everything in the pipeline depends on this** — retry without it creates duplicates instead of preventing loss |
| `SchemaVersion` | `byte`, default 1 | A consumer can tell which payload shape it has. `room_joined` / `room_ended` are at v2 |
| `CorrelationId` | `Guid?`, **filtered** index on `IS NOT NULL` | Ties multi-step operations together (`push_send_attempted` → `push_send_result`). Filtered because most events have none |

**Why keep the table**: three well-chosen indexes (`IX(EventType,OccurredAtUtc)`, `IX(UserId,OccurredAtUtc)`, `IX(RoomId,EventType,OccurredAtUtc)`), working room-id promotion, and `OnDelete(SetNull)` on `UserId` so events survive user deletion. A replacement would have rewritten 11 repository methods to solve problems three columns solve.

## `RoomParticipant` — lifecycle

| Change | Why |
|---|---|
| `LeftAt` added | Without it, time-in-room cannot be computed |
| `JoinedAt` **no longer overwritten** on rejoin | It was destroyed on every reconnect, so the original join time was unrecoverable |

## `Report` / `SupportTicket` — typed status

| Change | Why |
|---|---|
| `ReportStatus` enum + `StatusCode` | `Status` is a free-form string; analytics could only match three values |
| Resolution timestamps | The only record of **when** an item reached a terminal state |

Additive alongside the string column, so nothing that reads `Status` breaks.

## New tables

| Table | Grain | Retention | Backfillable? |
|---|---|---|:--:|
| `DailyPlatformMetrics` (RM-1) | Date | **Indefinite** | 180 days |
| `DailyRoomMetrics` (RM-2) | Date + Room | **Indefinite** | Partial |
| `DailyHostMetrics` (RM-3) | Date + Host | **Indefinite** | **Full history** |
| `DailyFunnelMetrics` (RM-4) | Cohort date | **Indefinite** | Partial |
| `DailyStateSnapshots` (RM-5) | Date | **Indefinite** | **NO — structurally impossible** |
| `AggregationCheckpoint` | Pipeline name | Permanent | N/A |
| `DeadLetterEvents` | Row | Until investigated | N/A |

**RM-3 is the standout.** It derives from `Rooms.HostId` / `CreatedAt` — relational and never purged — so Cocorra's leading indicator can be reconstructed from the platform's first day, giving supply health a longer baseline than any other metric, from a single backfill run.

**RM-5 is the one that cannot wait.** Pure state; no event records "how many users were Pending yesterday". Every day the snapshot job does not run is a permanent hole.

## Migrations

| Migration | Contents |
|---|---|
| `20260901080448_AddDailyStateSnapshots` | RM-5 |
| `20260901082008_AddAnalyticsPipelineAndReadModels` | 3 `UserEvent` columns + indexes + batched backfill, RM-1…RM-4, checkpoint, dead-letter |
| `20260901120157_AddParticipantLifecycleAndStatusCodes` | `LeftAt`, `StatusCode`, resolution timestamps |

**No migration was added in Wave 7** — every change was code, contract, or documentation.

---

# 6. Event Pipeline Changes

```
DOMAIN WRITE SUCCEEDS
      │                    ← emits happen AFTER the domain write, except where
      │                      the rejection IS the fact (speaker_time_exhausted,
      ▼                      operation_failed)
EventTracker.Track
      │  try/catch — NEVER throws back to the user (INV-1)
      │  + EventId (deterministic where idempotency is required)
      │  + SchemaVersion · CorrelationId · SessionId · ipHash · userAgent
      │  + roomId promoted from JSON into the indexed RoomId column
      │  + flag gates checked AT THE EMIT SITE, so a reader can see which
      │    events are new instrumentation
      ▼
Channel<UserEvent>  bounded, FullMode = Wait
      │  TryWrite false when full → producer stays non-blocking,
      │  drop is COUNTED (events_dropped_on_enqueue)
      ▼
EventFlushService   batch = 100
      │
      ├─ SaveChangesAsync OK ──────────────────► events_persisted++
      │
      ├─ DUPLICATE KEY ──► per-row insert fallback
      │                    99 of 100 persist · duplicate_events_discarded++
      │
      ├─ TRANSIENT ──────► bounded retry, exponential backoff
      │                    flush_batches_retried++
      │
      └─ PERMANENT / EXHAUSTED ──► DeadLetterEvents
                                   events_dead_lettered++
      │
      │  batch.Clear() ONLY after one of the four paths completed
      ▼
dbo.UserEvents  ──► EventCleanupService (batched, configurable)
      ▼
AnalyticsAggregationService   hourly
      │  read WHERE Id > watermark AND OccurredAtUtc <= now − 120s
      │  full recompute per affected date  → re-running is safe
      │  watermark advances ONLY after commit
      ▼
READ MODELS ──► IMetricRegistry ──► Response<T>.Meta ──► DASHBOARD
```

## Guarantees

| Guarantee | Mechanism |
|---|---|
| Tracking never breaks the product | `try/catch` in `Track`; non-blocking `TryWrite` |
| No silent loss on DB fault | Retry → dead-letter; `Clear()` only after a path completes |
| At-most-once persistence | `UX_UserEvents_EventId` |
| One duplicate does not fail 99 rows | Per-row fallback |
| Every loss is countable | 6 in-process counters + `deadLetterBacklog` |
| Aggregation is re-runnable | Full recompute per date; watermark after commit |
| Backfill matches live | Shared code path |
| No late row is skipped | 120-second safety lag |
| Shutdown does not lose in-flight events | Bounded drain on cancellation |

## Limitations, stated plainly

| Limitation | Consequence |
|---|---|
| **Channel drop is still possible** under sustained overload | Counts are lower bounds. Now **measurable**, which is the change |
| **Counters are process-local** and reset on restart | `countersSinceUtc` says when the window began; record it or the numbers mean nothing |
| **Single-instance assumptions** | The channel and the `SemaphoreSlim` cache guards are correct for one container. Horizontal scaling needs revisiting |
| **Dead-letter rows are not auto-replayed** | Deliberate — a human should look before replaying |
| **No APM, no metrics export** | `GET /Analytics/System/Health` is the only observability surface. `Analytics:StructuredLogPath` adds a durable log sink |

---

# 7. Metric Changes

| Metric | Before | After | Trust | Historical impact |
|---|---|---|---|---|
| **Top Speakers** | Ranked hosts by room length (D-1) | **REMOVED from the API** | DEPRECATED | **Cannot be recovered** — conflated speech with idle open-mic time |
| **Users Who Raised Hand** | Read a transient live flag reset on approval | **REMOVED** | DEPRECATED | Not recoverable; `hand_raised` starts fresh |
| **Avg Duration Hours** | Averaged a host-typed scheduling field defaulting to 2 | **REMOVED** *(Wave 7)* | DEPRECATED | Replaced by `room_ended.actualDurationSeconds`, no history yet |
| **Status breakdown** | Bucketed by `CreatedAt`, counted by **current** status (D-3) | **M-502** reconstructed from events | CONDITIONALLY RELIABLE | Recalculable ≤180 days; earlier unrecoverable |
| **Retention** | Exact-day match on a cookie signal (D-4) | **M-102** on server-emitted `room_joined` | VERIFIED | New series; legacy retained as M-102-LEGACY (UNRELIABLE) |
| **Funnel** | Steps counted independently; could widen (D-5) | **M-507** sequential + median/p90 | VERIFIED | Recomputable from existing events |
| **Participation** | Included hosts | **M-503** host-excluded | CONDITIONALLY RELIABLE | Recomputable |
| **Rooms Gone Live** | `M-200`, on an endpoint with no host figure | **M-205** | VERIFIED | Same formula, correct ID |
| **Distinct Active Hosts** | Contract said "rooms" (D-7) | **M-200** corrected | VERIFIED | **Full history from day one** |
| **Report rate by category** | Never computed | **M-301** per 1,000 joins | VERIFIED | Fully historical |
| **Review latency** | Not computed | **M-302** percentiles, no mean | CONDITIONALLY RELIABLE | ≤180 days |
| **Queue depth** | Not captured | **M-303** from RM-5 | VERIFIED (forward) | **No history before the job ran** |
| **Supply health** | Did not exist | **M-200/201/202** | VERIFIED | **Full history from day one** |
| **Support** | No endpoint | **M-601** | CONDITIONALLY RELIABLE | Fully historical |
| **Social graph** | No endpoint | **M-701** | CONDITIONALLY RELIABLE | Fully historical |
| **MBTI vs speaking** | Not analysed | **M-702** four dichotomies | CONDITIONALLY RELIABLE | ≤180 days |
| **Cohort grid** | Did not exist | **M-103** | CONDITIONALLY RELIABLE | Needs 8 weeks of `room_joined` history (raw events, not RM-1) |
| **Stage funnel** | Did not exist | **M-400** *(Wave 7)* | **EXPERIMENTAL** | **CANNOT be backfilled** |
| **WPU (north star)** | Contract only, unreachable (D-8) | **M-100** on A-1 *(Wave 7)* | VERIFIED | From read models |

Full contracts: `29-final-metric-trust-register.md`. ID history: `26-metric-registry-reconciliation.md`.

---

# 8. APIs Added or Changed

## Added

| Route | Purpose | Decision supported | Source | Trust |
|---|---|---|---|---|
| `GET /Analytics/Platform/Health` | North star + inputs, period comparison | Is Cocorra delivering more value, and which input constrained it? | RM-1 | Per-metric |
| `GET /Analytics/Participation/StageFunnel` | Stage funnel | Which control point in the stage flow to redesign? | `UserEvents` | EXPERIMENTAL |
| `GET /Analytics/Supply/Health` | Hosts, retention, concentration, schedule | Recruit coaches, or enable existing ones? | `Rooms`, RM-3 | VERIFIED |
| `GET /Analytics/Safety/ReportRate` | Reports per 1,000 joins by category | Category-specific safeguards? | `Reports`, `Rooms` | VERIFIED |
| `GET /Analytics/Review/Latency` | Latency percentiles + queue depth | Invest in review capacity? | `UserEvents`, RM-5 | COND. RELIABLE |
| `GET /Analytics/Support` | Ticket volume, response times | Prioritise stability? | `SupportTickets` | COND. RELIABLE |
| `GET /Analytics/Social` | Friend graph, message volume | Invest in messaging? | `FriendRequest`, `Messages` | COND. RELIABLE |
| `GET /Analytics/Mbti/Dichotomies` | MBTI vs speaking | Room format design | `AspNetUsers`, `UserEvents` | COND. RELIABLE |
| `GET /Analytics/Return/Weekly` | Weekly return rate | Prioritise retention? | `UserEvents` | VERIFIED |
| `GET /Analytics/Return/CohortGrid` | 8-week cohort grid | Is retention improving? | RM-1 | COND. RELIABLE |
| `GET /Analytics/Activation/Funnel` | Sequential funnel + elapsed | Restructure onboarding? | `UserEvents`, RM-4 | VERIFIED |
| `GET /Analytics/Decisions` | Change detection | Where should attention go? | RM-1, RM-3, RM-5 | Inherited |
| `GET /Analytics/Metrics/Registry` | All 27 contracts | Can I trust this number? | `IMetricRegistry` | N/A |
| `GET /Analytics/System/Health` | Pipeline health | Can I trust today's numbers? | Counters + checkpoint | VERIFIED |
| `POST /Analytics/System/Backfill` | Replay read models | — | RM-1…RM-4 | — |
| `POST /api/Webhooks/LiveKit` | Media telemetry | Is media working? | LiveKit | — |

## Changed

| Route | Change |
|---|---|
| `/Analytics/Participation` | Host exclusion; `TopSpeakers` + `UsersWhoRaisedHand` **removed**; `UsersWhoSpoke` from `mic_activated` |
| `/Analytics/Rooms` | **`AvgDurationHours` removed**; metric key M-200 → **M-205** |
| `/Analytics/Users/Growth` | Projected status → reconstructed `StatusAtTime` + boundary marker |
| `/Analytics/Participation/ActiveVsPassive` | Host exclusion |
| `/Analytics/Summary` | Unchanged shape; **superseded** by A-1, `410 Gone` at cutover |
| `/Analytics/Funnel`, `/Analytics/Retention` | Unchanged; superseded, M-102-LEGACY graded UNRELIABLE |
| **Every analytics route** | `Meta` populated: `trustLevel`, `metrics[]`, `display.suggestedDisplayOffsetMinutes` |

---

# 9. Mobile Impact

| Category | Detail |
|---|---|
| **Backend complete** | All core-loop instrumentation. Every event fires server-side from calls mobile already makes |
| **Mobile required** | **One check + two answers.** Confirm cookie persistence (§4); confirm no duplicate client events; answer the in-room-chat question |
| **Mobile optional** | `room_create_started` *(highest value — the only view of host-side abandonment)*, `notification_opened`, `feature_viewed` |
| **Deferred** | AN-035 session replacement — blocked on the cookie answer + R-4 |
| **Blocked** | AN-032 in-room chat materiality; AN-044 acquisition attribution |

**The one thing mobile must not do**: duplicate a server-authoritative event. It would double-count and corrupt every rate.

**The one thing the backend cannot see**: whether the Flutter client persists the `CocorraSessionId` cookie. Many Flutter HTTP setups do not by default, and if it is dropped then `session_started` fires per request burst. The backend routed around this — M-102 uses `room_joined`, not `session_started` — so nothing served today depends on it. But Cocorra has **no session-duration or app-open metric at all** as a result.

Full detail: **`docs/mobile/COCORRA-ANALYTICS-MOBILE-INTEGRATION-GUIDE.md`**

---

# 10. Production Activation

| Flag | Default | Gates |
|---|:--:|---|
| `Analytics:EnableNewEventEmission` | **`false`** | AN-017 low-frequency increment + AN-041 |
| `Analytics:EnableHighFrequencyEvents` | **`false`** | AN-018 high-frequency increment |

**Conjunctive in code**: `HighFrequencyEventsEnabled = NewEventEmissionEnabled && <flag>`. Setting the second alone does nothing.

**Activation order** — full runbook in `28-production-analytics-activation.md`:

```
Stage A  1 week, flags off   Record the baseline from /Analytics/System/Health
Stage B  EnableNewEventEmission=true              → verify 7 days
Stage C  Raise channel capacity (separate deploy)
         EnableHighFrequencyEvents=true           → AN-018 event clock starts
Stage D  Four separate clocks, not one. Relational and read-model baselines
         are satisfied by the backfill; RM-5 snapshots start at first deploy
         and are unrecoverable; only the new events start at Stage B / C.
```

**Monitoring**: `GET /Analytics/System/Health`. `pipelineHealthy` goes `false` on stale aggregation, dead letters, drops, or snapshot gaps — the thresholds are encoded, not merely documented.

**Rollback**: every stage reverts by configuration, no code change. The high-frequency flag reverts **independently**, which is the entire reason the increments are separate.

**Two things that do NOT roll back**: `user_status_changed` (transitions in the window are lost permanently — there is no `UpdatedAt` and no history table) and soft delete (not implemented; every hard delete is unrecoverable).

**Readiness: READY TO BEGIN STAGE A.** Not ready to enable either flag — that needs Stage A's output, and Stage A has not run.

---

# 11. Testing

| | |
|---|---|
| **Build** | **PASS** — 0 errors |
| **Warnings** | **13, all pre-existing.** Zero new warnings introduced by this programme |
| **Tests** | **254 / 254 PASS**, 0 skipped |
| **Test files** | 33 |
| **Provider** | **SQLite in-memory** for anything touching unique indexes or `DeleteBehavior` |

## Wave 7 additions: +45 tests (209 → 254)

| File | Tests | Covers |
|---|:--:|---|
| `StageFunnelTests` | 15 | M-400: monotonicity, ordering, duplicates, repeats, unique users, host exclusion, window and room boundaries, **the unmeasured-step states**, direct promotions, registry contract |
| `PlatformHealthTests` | 11 | A-1: empty read models, window-with-no-rows, sums, ratio recomputation, MAX-not-SUM for hosts, comparison window, growth-from-zero, drill-downs, north-star presence, partial period |
| `OperationFailureTrackingTests` | 10 | AN-041: all 5 reasons, subject convention, flag gating, **no exception message can leak**, closed vocabulary |
| `AnalyticsWiringTests` | 9 | **Route coverage and uniqueness**, layer-boundary completeness, config binding, safe defaults, **the conjunctive flag rule**, trust-ordinal ordering, reserved-ID protection |

## The regression tests that matter most

| Test | Asserts | Prevents |
|---|---|---|
| `UninstrumentedStep_ReturnsNullNotZero_AndSaysWhy` | An uninstrumented funnel step returns `null` with a reason | **A fabricated finding** — "nobody raised their hand". The single most important assertion in the suite |
| `EmptyReadModels_ReturnNullValuesWithAReason_NotZeros` | The landing page over an empty read model returns nulls | A dashboard front page stating the platform had no users |
| Per-row duplicate fallback | A 100-event batch with 1 duplicate persists **99** and does not fail | The fix becoming a 99-event-wide regression |
| `ApproveToStage_StageFull_EmitsAgainstTheParticipantNotTheHost` | Subject is the participant | Silently breaking M-400 in a way no metric test would catch |
| Host exclusion (test 4) | No `UserId` in both the speaker and passive sets | **D-1** — the executable form of the contradiction |
| Funnel monotonicity (test 6) | Each step ≤ the previous | **D-5** |
| `MetricsWithKnownCaveats_AreNotGradedVerified` | A metric stating a limitation cannot claim VERIFIED | Trust-level inflation |
| `EveryMetricKeyConstant_HasAContract` | **Fails the build** | Re-accumulating undocumented metrics |
| `FailureProperties_CarryNoExceptionMessage` | Exact property list | Exception text carrying user data into a 180-day table |
| `SpeakingConversion_IsRecomputedFromTotals_NotAveragedAcrossDays` | 14%, not 30% | A window figure matching no real period |
| `AggregationService_RollsUpPlatformMetrics_AndAdvancesWatermark` | Watermark advances; re-run is idempotent | **Fixed in Wave 7** — was clock-dependent |

## Pre-existing warnings (unchanged)

| Count | Kind |
|:--:|---|
| 4 | `CS8981` — migration class names `deploy` / `analytices` are all-lowercase |
| 6 | `CS8602` / `CS8604` — nullable dereference in `EmailService`, `ChatService`, `MessageRepository`, `SupportService`, `AnalyticsRepository:101`, `Program.cs:253` |
| 1 | `CS0618` — `GoogleCredential.FromFile` obsolete |

None is in code this programme introduced, and none was fixed here — out of scope, and touching them would obscure the analytics diff.

## What is NOT tested

Stated because a coverage claim is only useful if its limits are stated:

- **No load or soak test.** R-1 (channel saturation) cannot be verified from unit tests; that is what Stage A is for
- **No SQL Server integration test.** SQLite enforces unique indexes, but is not the production engine
- **No frontend contract test.** The ten checks in `20-` §Validation are the dashboard's obligation
- **No end-to-end SignalR test** for the five `mic_deactivated` close sites — each is unit-tested in isolation

---

# 12. Known Limitations

## Blocked on a decision outside engineering

| Item | Owner | Cost of delay |
|---|---|---|
| **AN-013 soft delete** | **Product / Legal** | **IRREVERSIBLE AND RUNNING.** `ApplicationUser` has no `IsDeleted`. Every hard delete biases M-102, M-501 and M-103 **upward** by an unknown margin, permanently. The three contracts say so in writing. **This is the only blocked item where waiting destroys evidence daily.** |
| **AN-044 acquisition attribution** | Product | Attribution impossible for the intervening period |
| **AN-032 in-room chat materiality** | Product / Mobile | Active-vs-Passive may be mis-named |

## Blocked on a mobile deliverable

| Item | Needs |
|---|---|
| **AN-035 session signal** | Whether the Flutter client persists `CocorraSessionId`. **No served metric depends on `session_started`** — but Cocorra has no session-duration or app-open metric at all |

## Blocked on time

| Item | Wait |
|---|---|
| **M-400 → VERIFIED** | 4 weeks of stable emission after Stage C |
| **Decision Center usable** | 4 complete weeks present in RM-1 — **suppliable by the backfill, not necessarily by waiting.** Read `hasBaseline` from the endpoint. Change detection against no baseline alerts on ordinary variance, and a dashboard that cries wolf in its first month is ignored permanently |
| **Cohort grid (M-103)** | 8 weeks of `room_joined` history in the raw event store |
| **RM-5 trends** | Accumulation — cannot be backfilled |

## Blocked on a production measurement

| Item | Measurement |
|---|---|
| **AN-001** baseline | R-1/R-2/R-3 from `/Analytics/System/Health` — **Stage A** |
| **AN-045 partitioning** | R-3 raw event volume. Partitioning a table whose size has never been measured would be optimising against a guess |
| **AN-035** evidence | R-4 — distinct `SessionId` per user per day vs distinct active users per day |

## Deliberately not started

| Item | Reason |
|---|---|
| **AN-043 experiment capability** | With a manual approval gate throttling intake, Cocorra is unlikely to have volume for well-powered A/B tests on secondary features soon. The cheaper first move is the approval-latency natural experiment. **Treating A/B infrastructure as the answer would be premature** |
| **General error tracking (GAP-22)** | **Not delivered.** AN-041 was narrowed to core-loop refusals; unbounded error tracking needs an APM, not a row in a 180-day analytics table. See `27-`. Owned by AN-042 + a future APM |

## Historical limitations — permanent

| Data | State |
|---|---|
| Top Speakers history | **CANNOT RECOVER** — conflated speech with idle open-mic time |
| Deleted users | **CANNOT RECOVER** — hard-deleted rows |
| `hand_raised` / `stage_promoted` before Stage C | **CANNOT RECOVER** — never captured |
| Speaking seconds per segment before Stage C | **CANNOT RECOVER** |
| State snapshots before the job ran | **STRUCTURALLY UNRECOVERABLE** |
| Status history beyond 180 days | **CANNOT RECOVER** |
| Room airtime before AN-019 | **CANNOT RECOVER** — only the scheduled length was ever stored |

**Never fabricate historical events.** Metrics with no historical source return `dataAvailableFromUtc` and must render as visible gaps.

## Architectural limitations

| Limitation | Consequence |
|---|---|
| **Single-instance assumptions** | The in-memory channel, `IMemoryCache` and `SemaphoreSlim` guards are correct for one container. Horizontal scaling needs revisiting |
| **Process-local counters** | Reset on restart. `countersSinceUtc` bounds them |
| **No APM / metrics export** | `/Analytics/System/Health` is the only observability surface |
| **`Meta` lacks `window.isPartialPeriod` and `freshness`** | The blueprint's frontend contract expects both. Partial-period is available on A-1 as a top-level field; freshness via `/Analytics/System/Health`. **Not yet in the envelope** |
| **Blueprint IDs ≠ code IDs** | Six IDs mean different things. Translate via `26-` §4 before implementing any widget |

---

# 13. What Can Be Trusted Now

**Available immediately, no flags, no baseline** — relational data that was never purged:

| Capability | Endpoint | Trust |
|---|---|---|
| **Supply health** — active hosts, second-room rate, concentration, schedule | `/Analytics/Supply/Health` | **VERIFIED, full history from day one** |
| **Report rate by room category** | `/Analytics/Safety/ReportRate` | **VERIFIED** |
| Review latency percentiles + queue depth | `/Analytics/Review/Latency` | COND. RELIABLE |
| Sequential activation funnel with elapsed time | `/Analytics/Activation/Funnel` | **VERIFIED** |
| Weekly return rate | `/Analytics/Return/Weekly` | **VERIFIED** |
| Support volume and response times | `/Analytics/Support` | COND. RELIABLE |
| Social graph reciprocity | `/Analytics/Social` | COND. RELIABLE |
| MBTI dichotomies vs speaking | `/Analytics/Mbti/Dichotomies` | COND. RELIABLE |
| Report insights | `/Analytics/Reports` | **VERIFIED** |
| Most active rooms | `/Analytics/Rooms/Active` | **VERIFIED** |
| Voice verification drop-off | `/Analytics/VoiceVerification/DropOff` | **VERIFIED** |
| **All 27 metric contracts** | `/Analytics/Metrics/Registry` | N/A |
| **Pipeline health** | `/Analytics/System/Health` | **VERIFIED** |

**Also trustworthy**: that the pipeline does not silently lose events; that a metric's trust level is on every response; that re-running aggregation is safe; that backfilled rows match live rows; that an uninstrumented period returns `null` and not `0`.

**Seven of ten dashboard pages can be built today** on the above.

---

# 14. What Cannot Be Trusted Yet

| Not trustworthy | Why | When |
|---|---|---|
| **M-400 stage funnel** | Two of four steps behind flags that are **off**. Returns `isMeasured: false` — correctly | 4 weeks after Stage C |
| **Decision Center** | Needs 4 complete weeks in RM-1; detection without a baseline alerts on ordinary variance | **After the backfill** — verify with `hasBaseline`, do not assume a wait |
| **Cohort grid** | Needs ~8 weeks of `room_joined`; a short row is missing data, not a retention collapse | Query `MIN(OccurredAtUtc)` for the real date |
| **Queue-depth / FCM-coverage trends** | RM-5 must accumulate; cannot be backfilled | Ongoing |
| **M-102-LEGACY** | Exact-day matching over a cookie signal. **Graded UNRELIABLE — must not be displayed** | Never — delete at cutover |
| **Anything from `/Analytics/Platform/Health` today** | Read models are empty until aggregation runs. Returns nulls, correctly | After Stage A backfill |
| **Any user rate, in absolute terms** | Hard deletes bias every one **upward** | AN-013 |
| **Session and app-open metrics** | Do not exist | AN-035 |
| **Room airtime** | Only the scheduled length was ever stored | AN-019 + Stage C |
| **Anything, if the pipeline is stale** | A trust badge on data whose pipeline stopped three days ago **certifies stale data as verified** | Always check `pipelineHealthy` |

**The last row governs all the others.** Trust and freshness must be displayed together or neither means anything.

---

# 15. Next Steps

## BACKEND

| Priority | Item |
|:--:|---|
| **1** | Move `Analytics:IpHashSalt` out of `appsettings.json` into an environment variable. A committed salt makes the IP pseudonymisation reversible by anyone with repo access, defeating its only purpose |
| **2** | Run **Stage A** — one week of baseline from `/Analytics/System/Health` |
| **3** | Run `POST /Analytics/System/Backfill` — RM-3 reconstructs **full history** in one run |
| **4** | Stage B, then Stage C, per `28-` |
| 5 | Implement **M-203, M-204, M-403** and activation→first-join. **All four need no new events and no schema change** — the cheapest remaining analytics work in the repository |
| 6 | Add `Meta.window.isPartialPeriod` and `Meta.freshness` to complete the frontend contract |
| 7 | Register a metric for the AN-024 push events once emission is on (M-602 is reserved) |
| 8 | `410 Gone` on `/Analytics/Summary`, `/Analytics/Funnel`, `/Analytics/Retention` after cutover |

## MOBILE

| Priority | Item |
|:--:|---|
| **1** | **Answer: does the HTTP client persist `CocorraSessionId` across requests and restarts?** One check, unblocks AN-035 |
| **2** | Confirm no client-side event duplicates a server event |
| **3** | Answer: does in-room group chat exist, and is it used? *(AN-032)* |
| 4 | If the app shows analytics: apply the display offset; render `isMeasured: false` as a gap |
| 5 | Optional: `room_create_started` — **the only view of host-side abandonment** |

## PRODUCT / BUSINESS

| Priority | Item |
|:--:|---|
| **1** | **ANSWER THE SOFT-DELETE QUESTION (AN-013).** The only blocked item whose cost of delay is irreversible and running |
| 2 | Decide whether acquisition attribution is worth instrumenting (AN-044) |
| 3 | Communicate each replaced metric to dashboard users **before** the number moves. An unexplained change destroys trust faster than a wrong metric, because the reader concludes the numbers are arbitrary |

## DASHBOARD UI

| Priority | Item |
|:--:|---|
| **1** | Build **Page 9 Trust Register first.** Before anyone relies on a number they must be able to check it — and building the register first forces every metric to have a contract before it appears anywhere |
| **2** | Pages 1, 2, 3, 5, 7, 8 — **all available now** |
| **3** | Implement the three non-negotiable UI rules: **`null` renders as a labelled gap, never 0**; CONDITIONALLY RELIABLE conditions **inline, not tooltip**; UNRELIABLE **never displayed** |
| 4 | Translate every blueprint metric ID through `26-` §4 before wiring a widget |
| 5 | Pages 4, 6, 0 — gated on events and history |

**If the dashboard ignores the trust metadata, this programme produces a faster wrong dashboard.** The backend work is necessary and insufficient.

## BASELINE COLLECTION

| | |
|---|---|
| **Status** | **NOT STARTED** |
| **Starts** | **Four separate clocks.** RM-5 snapshots at first deploy (unrecoverable); relational and read-model baselines via the backfill; AN-017 events at Stage B; AN-018 events at Stage C |
| **Record** | The Stage B and Stage C UTC timestamps. They are the `dataAvailableFromUtc` for everything resting on those events |
| **Duration** | Per clock — see `28-` §5. Only the AN-017/AN-018 event clocks require waiting |
| **Compressible** | **No** — only by having started earlier |

## P3 FUTURE WORK

| Item | Trigger |
|---|---|
| **AN-045** partitioning | R-3 shows volume warranting it |
| **AN-043** experiments | User volume supports well-powered tests — start with the approval-latency natural experiment |
| General error tracking | An APM decision. **AN-041 does not cover this** |
| Horizontal scaling | Revisit the channel, cache and counters when a second instance is planned |

---

# The Three Things That Matter Most

**1. Seven of ten dashboard pages can be built today, and they include the two highest-value analyses.**
Supply health and report-rate-by-category are queries over data the audit already verified. They need no events, no flags, no baseline, and no schema change. Supply health has no counterpart in the current dashboard at all.

**2. The soft-delete decision is the only thing whose cost of delay is irreversible.**
Every other blocked item costs the same whenever it is answered. That one destroys evidence daily, and three shipped metrics carry an upward-bias limitation naming it.

**3. If the dashboard renders `0` for an uninstrumented step, this programme has failed.**
The original failure was never that some metrics were wrong — every analytics system has wrong metrics. It was that nothing distinguished them. `null` means *unknown*; `0` means *nothing happened*. They look identical on a chart and they are opposite claims. Every server-side guard for this is in place and tested. The last one is in the UI.
