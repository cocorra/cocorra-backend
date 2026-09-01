# 18 — Background Processing Plan

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 7
> **Depends on**: `11-current-state-validation.md`, `16-raw-event-storage-strategy.md`, `17-read-models-and-aggregation.md`
> **Scope**: Documentation only. No services were created or modified.

---

# Decision

# EXTEND EXISTING INFRASTRUCTURE

Cocorra keeps the ASP.NET `BackgroundService` pattern, hardens the two services that exist, and adds two more of the same kind.

## Existing infrastructure

**FACT** — two `BackgroundService` implementations, both registered in `Program.cs:213-214`:

| Service | File | Role |
|---|---|---|
| `EventFlushService` | `Cocorra.BLL/Services/EventTracking/EventFlushService.cs` | Drains the channel, batches ≤100, persists to `UserEvents` |
| `EventCleanupService` | `Cocorra.BLL/Services/EventTracking/EventCleanupService.cs` | Purges events older than 180 days, every 24h |

**FACT** — both run in-process in the single API container (`docker-compose.yml`). There is no external scheduler, job store, or worker process anywhere in the solution.

## Why not Hangfire, Quartz, or a separate worker

**RECOMMENDATION** — stay with `BackgroundService`.

| Option | Rejected because | What would change the decision |
|---|---|---|
| **Hangfire** | Adds a dependency and a job-store schema (its own tables in the same database) to replace a pattern already working twice in-repo. Its main advantages — persistent job queues, retry policies, a dashboard — solve problems Cocorra's workloads do not have: all four jobs below are idempotent, schedule-driven, and self-recovering from a watermark. | An operator needing to trigger, inspect, or re-run jobs without a deployment. |
| **Quartz.NET** | Rich scheduling (cron expressions, calendars, misfire policies) for workloads that need "hourly" and "daily at 00:15". | Genuinely complex scheduling requirements. |
| **Separate worker process** | Cocorra runs one container. A second deployable adds orchestration, configuration, and monitoring surface for isolation nobody currently needs. | Aggregation load measurably degrading API latency, or a move to multiple API instances. |

**INFERENCE — the decisive argument is not cost but competence surface.** Every option above is defensible in isolation. But `BackgroundService` is already understood in this codebase, already wired into DI, already scoped correctly via `IServiceScopeFactory`, and already handling cancellation. Introducing a scheduler means the next person to debug a failed rollup must first learn the scheduler. That is a real ongoing cost for a team with no dedicated data function, and it buys nothing the four jobs below actually need.

**A constraint that must be stated (FACT/INFERENCE).** `BackgroundService` instances run **in every API instance**. Cocorra runs one, so this is safe today. On horizontal scaling, `AnalyticsAggregationService` and `StateSnapshotService` would run concurrently in each replica and race on the same grains. **RECOMMENDATION** — because all writes are idempotent UPSERTs on a natural key (INV-4), concurrent runs would produce correct but wasteful duplicate work rather than corruption. That is an acceptable failure mode for now, and it is recorded in `24-dependency-graph.md` alongside the other multi-instance blockers (`RoomHub._connections`, the session-dedup `IMemoryCache`) — all of which fail *harder* than this one.

---

# Job 1 — `EventFlushService` (MODIFY)

> The highest-priority change in this document. It closes one of the two silent data-loss paths.

## Current behaviour

**FACT** — `EventFlushService.cs`:

```csharp
try   { /* AddRange + SaveChangesAsync */ }
catch (Exception dbEx) { _logger.LogError(dbEx, "Failed to persist batch of {BatchCount} user events.", batch.Count); }
finally { batch.Clear(); }
```

`batch.Clear()` runs on the failure path. Any transient database fault permanently discards up to 100 events, leaving one unaggregated log line as the only trace.

## Target design

| Aspect | Specification |
|---|---|
| **Trigger** | `WaitToReadAsync` on the channel — event-driven, not scheduled. **Unchanged.** |
| **Schedule** | Continuous. **Unchanged.** |
| **Batch strategy** | Drain up to `MaxBatchSize` (default 100, moved to configuration). **Unchanged in shape.** |
| **Checkpoint** | None needed — the channel *is* the checkpoint. Events are removed only once handled. |
| **Retry** | **NEW.** Bounded retry with exponential backoff, distinguishing transient faults (timeout, deadlock, connection failure) from permanent ones (constraint violation, schema mismatch). |
| **Idempotency** | **NEW.** Safe only because `EventId` is stamped at enqueue and uniquely constrained (`16-`). A replayed batch collides and is discarded. |
| **Failure monitoring** | **NEW.** Counters: `events_dropped_on_enqueue`, `flush_batches_failed`, `flush_batches_retried`, `events_dead_lettered`. |
| **Recovery** | **NEW.** After exhausted retries, append the batch to `DeadLetterEvents`. Never discard silently. |

## Retry semantics

**RECOMMENDATION** — the ordered decision tree on a `SaveChangesAsync` failure:

```
1. Duplicate key violation
   → fall back to per-row insert; discard only the colliding rows; succeed.
2. Transient fault (timeout, deadlock, transport failure)
   → retry with backoff, up to MaxRetries; the same EventId values make this safe.
3. Retries exhausted, or a permanent fault
   → write the batch to DeadLetterEvents, increment the counter, clear.
4. Only after 1, 2, or 3 has completed may batch.Clear() run.
```

**INFERENCE — step 1 is not optional and is easy to get wrong.** `AddRange` + a single `SaveChangesAsync` means **one** duplicate key fails the **entire** batch of 100. Without the per-row fallback, adding the unique constraint would create a *new* loss path 99 events wide — strictly worse than the problem it was introduced to fix. This is the single most important implementation detail in the flush-service change.

**Configuration** — `MaxRetries`, `InitialBackoff`, `MaxBatchSize`, and channel capacity move to a bound `EventTrackingOptions`, following the existing `Analytics:IpHashSalt` configuration pattern (`Program.cs:205-207`).

**RECOMMENDATION on capacity** — do not raise the channel bound before measuring R-1. Increasing it without knowing the current drop rate trades a known bounded loss for unbounded memory growth, and removes the signal that would tell you whether the change was warranted.

## Validation

1. A mocked context failing twice then succeeding persists all events exactly once.
2. A permanently failing context routes the batch to `DeadLetterEvents` with zero events lost.
3. A batch containing one duplicate persists 99 rows and discards 1 — **must run on SQLite in-memory or SQL Server**; `EFCore.InMemory` does not enforce unique indexes and would pass vacuously.
4. `Track` still returns without throwing when the channel is full (INV-1 regression).
5. Counters increment on forced overflow and forced failure.

---

# Job 2 — `EventCleanupService` (MODIFY)

## Current behaviour

**FACT** — a single unbatched `ExecuteDeleteAsync` for all rows older than a hardcoded 180 days, executed immediately at startup and then every 24 hours.

## Problems

| # | Problem | Consequence |
|:--:|---|---|
| 1 | Unbatched mass delete | On SQL Server, risks lock escalation on `UserEvents` — the same table `EventFlushService` is concurrently inserting into |
| 2 | Hardcoded retention | Policy is a constant, not a decision |
| 3 | Runs at startup | Purge timing is coupled to deployment frequency |
| 4 | No archive | History is destroyed rather than moved |

**INFERENCE on problem 1** — this is the operational risk that grows fastest. The P0 events (`mic_deactivated` on every mute; `hand_raised`/`hand_lowered` on every toggle) increase daily row volume by design, so the daily delete grows in step. A maintenance job that blocks ingestion is worse than one that runs slowly.

## Target design

| Aspect | Specification |
|---|---|
| **Trigger** | Scheduled. |
| **Schedule** | Daily at a low-traffic hour. **INFERENCE** — the user base is MENA (UTC+2/+3), so the local overnight trough is roughly 00:00–03:00 UTC+3. Aligning the purge to that window rather than to container start avoids competing with the evening room peak. |
| **Batch strategy** | **NEW.** Delete in bounded batches (e.g. 5,000 rows), looping until exhausted, with a short delay between batches. |
| **Checkpoint** | None needed — the cutoff predicate is self-describing; a partial run simply resumes next cycle. |
| **Retry** | Next scheduled cycle. No in-cycle retry needed. |
| **Idempotency** | Inherent — deleting already-deleted rows is a no-op. |
| **Failure monitoring** | Rows deleted per run, duration, batches executed. |
| **Recovery** | Automatic on the next cycle. |
| **Archive** | **Not required.** See below. |

**RECOMMENDATION on archiving — deliberately not building one.** **INFERENCE** — the read models (`17-`) are retained indefinitely and already preserve everything the dashboard needs beyond 180 days. An archive of raw events would be a second store that nothing reads, purely to hedge against a future analysis nobody has specified. The aggregation layer *is* the archive, in the form that gets used.

## Validation

1. Only rows past the cutoff are deleted.
2. Deletion completes in bounded batches; no single statement exceeds the batch size.
3. A configuration change moves the cutoff.
4. Concurrent inserts succeed during a purge run — the direct test for problem 1.

---

# Job 3 — `AnalyticsAggregationService` (NEW)

## Purpose

Turn raw events into the daily and weekly read models defined in `17-`.

| Aspect | Specification |
|---|---|
| **Trigger** | Timer. |
| **Schedule** | Hourly, offset from the top of the hour (e.g. :05) to avoid coinciding with other periodic work. |
| **Batch strategy** | Incremental: read `UserEvents WHERE Id > LastProcessedEventId`, bounded per run (e.g. 50,000 events). Identify affected `(Date, RoomId)` and `(Date, HostId)` grains, then recompute each in full. |
| **Checkpoint** | `AggregationCheckpoint.LastProcessedEventId`, advanced **only after** the transaction commits. |
| **Retry** | On failure, do not advance the watermark. The next cycle re-reads the same range. **INFERENCE** — no in-cycle retry is needed precisely because the watermark makes the whole job self-healing; adding one would duplicate a recovery mechanism that already exists. |
| **Idempotency** | UPSERT on the natural key, recomputing full values rather than incrementing (INV-4). |
| **Failure monitoring** | `ConsecutiveFailures` on the checkpoint row; lag in minutes between `LastSuccessAtUtc` and now. |
| **Recovery** | Automatic. A prolonged outage simply produces a larger backlog, processed in bounded batches across successive runs. |

## Why the watermark is `UserEvent.Id`, not `OccurredAtUtc`

**INFERENCE — this is the subtlest correctness decision in the aggregation design.** `EventFlushService` persists in batches, so an event that *occurred* at 10:59 may be *inserted* at 11:01. A timestamp watermark that had already advanced past 11:00 would skip it permanently and silently. The `bigint` identity has no such hazard: it is assigned at insert, so nothing can appear "before" a watermark that has already passed it.

**Residual edge case and its mitigation** — identity values are allocated at insert, so under concurrency a row with a lower id can commit marginally after one with a higher id. With a single flush service writing sequential batches this window is negligible. **RECOMMENDATION** — re-read from `LastProcessedEventId − N` (a small safety lag) and let the idempotent UPSERT absorb the overlap. This costs nothing and eliminates the case entirely.

## Trailing recompute for late-arriving data

**RECOMMENDATION** — in addition to the incremental pass, recompute funnel cohorts for a trailing window (~45 days) on each run.

**INFERENCE — without this, the onboarding funnel is guaranteed to be wrong.** Onboarding completes over days: `activation_completed` arrives only after a human review. A cohort aggregated once on its cohort date would permanently record a near-zero final step, making the funnel look catastrophic when it is merely premature. The trailing window must exceed the p99 of M-301 (admin review latency), which is why M-301 should be measured before the window is fixed.

## Validation

1. Rollup values reconcile against a direct live query over the same window.
2. Running twice over the same range produces byte-identical rows.
3. A forced failure leaves the watermark unchanged.
4. An `activation_completed` arriving 3 days late is picked up by the trailing recompute.
5. A room spanning UTC midnight produces two `DailyRoomMetrics` rows whose sums equal the room total.

---

# Job 4 — `StateSnapshotService` (NEW)

## Purpose

Capture pure-state counts that have no event trail (`17-` RM-5).

| Aspect | Specification |
|---|---|
| **Trigger** | Timer. |
| **Schedule** | Daily at 00:15 UTC — after the date boundary, before the first aggregation run of the day. |
| **Batch strategy** | None. Five `COUNT` queries against existing tables. |
| **Checkpoint** | The natural key `(Date, MetricKey)` is the checkpoint. A row's existence means the day is captured. |
| **Retry** | **NEW and necessary.** On failure, retry within the same day. **INFERENCE** — this is the one job where a missed run is *unrecoverable*: the count cannot be reconstructed after the fact. Unlike the aggregator, it cannot simply catch up next cycle. |
| **Idempotency** | UPSERT on `(Date, MetricKey)`. Two runs on the same date produce one row. |
| **Failure monitoring** | Gap detection — flag missing dates rather than interpolating them. |
| **Recovery** | **Not possible after the fact.** A missed day is a permanent hole. |

## Captured metrics

```
pending_verification_queue    COUNT(AspNetUsers WHERE Status = Pending)
rerecord_queue                COUNT(AspNetUsers WHERE Status = ReRecord)
active_users_total            COUNT(AspNetUsers WHERE Status = Active)
fcm_token_coverage            COUNT(Active AND FcmToken IS NOT NULL) / COUNT(Active)
open_reports                  COUNT(Reports WHERE Status = 'Open')
```

**INFERENCE — this is the smallest job in the programme and among the most urgent.** Five counts on a schedule. But it is the only read model that cannot be backfilled: every day it does not run is a permanent gap in a series that operations will eventually need. It is also the only guard against an FCM token regression of the class fixed in commit `dc1c933`, which would otherwise be invisible until users complained.

**RECOMMENDATION on gap handling** — never interpolate. A missing date must surface as missing. **INFERENCE** — an interpolated value in a queue-depth series would smooth over exactly the backlog spike the metric exists to detect.

## Validation

1. A snapshot equals a direct count taken at capture time.
2. Two runs on the same date produce one row, not two.
3. A missing date is flagged by gap detection, not interpolated.
4. A forced failure triggers a same-day retry.

---

# Cross-Cutting Concerns

## Startup ordering

**RECOMMENDATION** — stagger the four services:

| Service | Startup behaviour |
|---|---|
| `EventFlushService` | Immediate — ingestion must never wait |
| `AnalyticsAggregationService` | Delay ~2 minutes, then hourly |
| `StateSnapshotService` | Delay ~5 minutes, then daily at 00:15 UTC |
| `EventCleanupService` | **Delay, then scheduled** — currently runs immediately at startup |

**INFERENCE** — without staggering, a container restart fires a mass delete, a full aggregation pass, and a snapshot simultaneously while the API is also serving its first requests. The current startup-triggered purge is the worst offender because it is the heaviest.

## Observability

**FACT** — there is no structured logging sink, no APM, and no metrics export. Errors reach `ILogger` → Docker stdout with 10MB/3-file rotation (`00-repository-overview.md`).

**INFERENCE — this is a genuine problem for this plan specifically.** The dead-letter mechanism, the drop counters, and the aggregation lag are all *operational* signals. Writing them only to rotating container logs means nobody will see them, which would leave the durability work technically implemented and practically unobserved.

**RECOMMENDATION** — expose job health through the analytics surface itself, since that is what already exists:

```
AnalyticsJobHealth  (or DailyStateSnapshots rows)
    JobName, LastRunAtUtc, LastSuccessAtUtc, ConsecutiveFailures,
    ItemsProcessed, DeadLetteredCount, LagMinutes
```

Surfaced through a `GET /Api/V1/Analytics/System/Health` endpoint (`19-`), so the dashboard can display data freshness and pipeline health alongside the metrics they underpin. **INFERENCE** — this closes the loop that `08a` opens: a metric's trust level is meaningless if the pipeline feeding it failed silently three days ago.

## Cancellation and shutdown

**FACT** — `EventFlushService` handles `OperationCanceledException` and logs a clean stop. `EventCleanupService` breaks its loop on cancellation.

**INFERENCE — an unhandled gap.** On shutdown, `EventFlushService` stops reading the channel, so any events still queued are lost with the process. **RECOMMENDATION** — on cancellation, drain the remaining channel contents with a bounded timeout before exiting. Without this, every deployment silently loses whatever is in flight — a small, recurring, entirely avoidable loss that would otherwise persist after all the retry work is done.

## Configuration

**RECOMMENDATION** — one bound options class, following the existing `Analytics:IpHashSalt` pattern:

```
Analytics:
  IpHashSalt                    (existing, startup-guarded)
  EventChannelCapacity          default 10000
  EventFlushBatchSize           default 100
  EventFlushMaxRetries          default 3
  EventFlushInitialBackoffMs    default 200
  RawEventRetentionDays         default 180
  CleanupBatchSize              default 5000
  AggregationIntervalMinutes    default 60
  AggregationBatchSize          default 50000
  AggregationTrailingDays       default 45
  SnapshotHourUtc               default 0
```

**FACT** — `Program.cs:205-207` already throws at startup if `Analytics:IpHashSalt` is absent. **RECOMMENDATION** — extend that guard to validate the new numeric values are within sane bounds, so a misconfigured batch size fails at startup rather than at 3am.

---

# Summary

| Job | Disposition | Trigger | Schedule | Checkpoint | Retry | Idempotency | Recovery |
|---|:--:|---|---|---|---|---|---|
| `EventFlushService` | **MODIFY** | Channel read | Continuous | Channel itself | **Bounded + backoff** | `UNIQUE(EventId)` | **Dead-letter** |
| `EventCleanupService` | **MODIFY** | Timer | Daily, low-traffic hour | Cutoff predicate | Next cycle | Inherent | Next cycle |
| `AnalyticsAggregationService` | **NEW** | Timer | Hourly at :05 | `LastProcessedEventId` | Watermark not advanced | UPSERT on natural key | Automatic |
| `StateSnapshotService` | **NEW** | Timer | Daily 00:15 UTC | `(Date, MetricKey)` | **Same-day retry** | UPSERT | **None — gaps permanent** |

**Three conclusions (INFERENCE).**

**The flush-service retry is the highest-priority change here**, and it is blocked on `EventId`. Implementing retry without the unique constraint would create duplicates instead of preventing loss; implementing the constraint without the per-row duplicate fallback would create a 99-event-wide loss path. The two must ship together.

**`StateSnapshotService` is the smallest job and the most time-sensitive.** Five counts on a timer. But it is the only one whose missed runs are unrecoverable, which makes every day of delay a permanent hole in the series.

**Job health must be visible somewhere other than container logs.** With no structured logging sink, the entire durability programme would otherwise be unobservable — the dead-letter table would fill and nobody would know. Surfacing health through the analytics API is the cheapest fix, and it uses infrastructure that already exists.
