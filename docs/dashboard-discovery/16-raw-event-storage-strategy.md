# 16 — Raw Event Storage Strategy

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 5
> **Depends on**: `11-current-state-validation.md`, `13-data-trust-correction-plan.md` (TRUST-07, TRUST-10), `15-event-implementation-contracts.md`
> **Scope**: Documentation only. **No migrations were created.** Schema changes below are described conceptually for the execution phase.

---

# Decision

# YES — KEEP AND EXTEND

`dbo.UserEvents` remains the raw event store, with three added columns, two added indexes, and a revised retention policy.

## Why keep it

**FACT — the existing design is sound in the ways that matter.**

| Property | Evidence | Assessment |
|---|---|---|
| Purpose-built for analytics | `Id` is `bigint` with the model comment *"high volume"* | Correct type choice for an append-heavy log |
| Room-scoped queries are indexed | `IX (RoomId, EventType, OccurredAtUtc)` — `AppDbContext.cs:257` | Directly serves the target dashboard's most common access pattern |
| Time-series queries are indexed | `IX (EventType, OccurredAtUtc)`, `IX (UserId, OccurredAtUtc)` | Covers funnel, cohort, and per-user progression queries |
| Denormalisation already done where it counts | `RoomId` promoted from JSON by `ExtractRoomId` | Avoids JSON parsing in SQL — the single most important performance decision already made |
| Deletion policy preserves analytics | `OnDelete(DeleteBehavior.SetNull)` on `UserId` | Events survive user deletion as anonymous rows |
| Flexible payload | `PropertiesJson` as `nvarchar(max)` with an explicit no-PII warning | Accommodates every event in `15-` without schema churn |

**INFERENCE — what a replacement would cost and buy.** Replacing this store would mean rewriting all eleven `AnalyticsRepository` methods, re-establishing three well-chosen composite indexes, migrating existing history, and re-validating every metric — to solve problems that three columns solve. No measured query-performance problem exists. The gap is durability and idempotency, neither of which is a property of the storage engine.

**FACT — a bounded scale expectation.** Cocorra runs a single SQL Server instance and a single API container (`docker-compose.yml`). There is no data-engineering function. A second datastore would add a sync path, schema drift, and a new failure mode.

## What "extend" means

Three columns, two indexes, one behavioural change to retention. Nothing about the write path, the promotion logic, or the index strategy changes.

---

# Required Schema Changes

> Conceptual only. No migration files were produced.

## Column 1 — `EventId` (blocking prerequisite)

```
EventId    uniqueidentifier   NOT NULL   UNIQUE
```

**Why (FACT)** — `UserEvent.Id` is a database identity assigned at insert. A retried batch produces new ids, so the database cannot recognise a replay. There is currently no deduplication anywhere in the pipeline.

**INFERENCE — this column gates TRUST-07.** Retry without idempotency does not reduce data loss; it converts a loss problem into a duplication problem. `EventId` must therefore be the **first** schema change, before the flush-service retry work begins.

**Assignment (per `15-`)**
- Default: `Guid.NewGuid()` stamped at enqueue in `EventTracker.Track`, **not** at flush.
- EXACTLY-ONCE events: deterministic GUID derived from the caller-supplied `eventKey`.

**Stamped at enqueue, deliberately.** The flush service retries whole batches; if ids were assigned during persistence, the replayed batch would carry different ids and collide with nothing.

**Constraint behaviour** — a unique index. On collision, `EventFlushService` must swallow the duplicate-key violation for the offending row rather than failing the batch.

**INFERENCE — a real implementation hazard.** `AddRange` + a single `SaveChangesAsync` means one duplicate key fails the *entire* batch of 100. **RECOMMENDATION** — on a duplicate-key violation, fall back to per-row insert for that batch, discarding only the colliding rows. Failing the whole batch on one duplicate would turn the idempotency guarantee into a new loss path, which would be a worse outcome than the problem it was added to solve.

**Backfill** — existing rows need a value. Generate random GUIDs for historical rows: they are not duplicates of anything and only need to satisfy the constraint.

## Column 2 — `SchemaVersion`

```
SchemaVersion   tinyint   NOT NULL   DEFAULT 1
```

**Why (INFERENCE)** — `15-` extends three existing events (`room_joined`, `room_ended`, `message_sent`) with new properties. Without a version marker, a query cannot distinguish "this event predates the property" from "the property was null." For `room_joined.entrySource` this matters directly: pre-extension rows would otherwise be indistinguishable from rows where the client failed to supply a source, and the discovery metrics would silently mix the two.

**Assignment** — set by `EventTracker` from a per-event-type version table. Existing rows default to 1; extended events increment.

## Column 3 — `CorrelationId`

```
CorrelationId   uniqueidentifier   NULL
```

**Why (FACT)** — `15-` identifies exactly one chain that timestamps cannot reconstruct: `push_send_attempted → push_send_result → notification_opened`. The open can arrive hours later, from a different process, out of order relative to other events.

**Scope discipline (RECOMMENDATION)** — populate only where a chain genuinely requires it: the notification chain (`CorrelationId = Notification.Id`) and the moderation chain (`CorrelationId = Report.Id`). Leave it NULL elsewhere. **INFERENCE** — a correlation id populated speculatively becomes a contract that must be maintained across the server and the Flutter client for no consumer, and adds index cost for rows that never use it.

## Indexes

```
UX_UserEvents_EventId          UNIQUE (EventId)                      -- idempotency
IX_UserEvents_CorrelationId    (CorrelationId) WHERE CorrelationId IS NOT NULL  -- filtered
```

**INFERENCE** — the correlation index must be filtered. Most rows will have NULL, and an unfiltered index would carry the full table for the benefit of a small subset.

**Existing indexes are unchanged.** All three continue to serve the target queries; `12-` and `17-` introduce no access pattern they do not already cover.

## Explicitly NOT changing

| Considered | Rejected because |
|---|---|
| Normalising `PropertiesJson` into typed columns | Every event has a different shape. `RoomId` — the only property queried across event types — is already promoted. Further promotion would add columns NULL for most rows. |
| Splitting `UserEvents` per event type | Multiplies tables, complicates cross-type funnels, and destroys the existing composite indexes. |
| Changing `Id` from `bigint` identity | It is correct: monotonic, compact, and the natural watermark for incremental aggregation (`17-`). |
| Adding `UpdatedAt` | Events are immutable by definition. A mutable event is a bug, not a feature. |

---

# Partitioning

**RECOMMENDATION — do not partition yet. Revisit at a defined trigger.**

**FACT — the volume is currently unknown.** `11-` records this as observation item R-3: current `UserEvents` row count and daily growth rate have not been measured.

**INFERENCE — the honest position.** Partitioning `UserEvents` by month on `OccurredAtUtc` would make retention a metadata operation (switch out a partition) instead of a mass delete, and would improve scan locality for windowed queries. Both are real benefits. But partitioning adds a maintenance obligation — creating future partitions, managing the scheme — that a team without a data-engineering function will eventually forget, and a missing partition is an outage rather than a slowdown. Recommending it before the volume is known would be guessing.

**Trigger to revisit** — any of:
- `UserEvents` exceeds ~50 million rows, or
- the retention purge cannot complete inside its window, or
- windowed analytics queries degrade measurably after the P0 event expansion.

**RECOMMENDATION — the cheaper mitigation first.** Batching the retention delete (below) addresses the immediate risk without a partitioning scheme. If batched deletes prove sufficient at Cocorra's growth rate, partitioning is never needed.

---

# JSON Metadata Strategy

**RECOMMENDATION — keep `PropertiesJson` as `nvarchar(max)`, with three disciplines.**

**Discipline 1 — never query JSON in a hot path.** **FACT** — `ExtractRoomId` already promotes the one cross-cutting property. **RECOMMENDATION** — any property that becomes a common filter or `GROUP BY` key gets promoted to a column, following the same pattern. The current candidate is `isHost` on `room_joined` (E-12): it is required by INV-7 on every room metric, and promoting it turns the mandatory host exclusion into a column filter instead of a join to `Rooms`.

**Discipline 2 — bound the payload.** **FACT** — `TrackEventDto.Properties` is an unvalidated `object?` (`EventsController.cs:60`) serialised directly into `nvarchar(max)`, gated only by the 100 req/min per-IP rate limit. **RECOMMENDATION** — enforce a server-side size cap (a few kilobytes) and reject oversized client payloads. This is a storage-integrity control, and it is also the mechanism that makes required-property validation possible for correlation ids.

**Discipline 3 — enforce the no-PII rule.** **FACT** — the model already carries the warning: *"NEVER store message bodies, emails, or other PII here."* **RECOMMENDATION** — make it testable. `E-26 room_group_message_sent` carries `messageLength`, never content, and that constraint should be asserted in a test rather than trusted to reviewers.

---

# Retention Strategy

## Current behaviour

**FACT** — `EventCleanupService` deletes `UserEvents` older than 180 days, in a single unbatched `ExecuteDeleteAsync`, immediately at startup and then every 24 hours. The period is hardcoded (`AddDays(-180)`). There is no archive or export.

## Problems

| # | Problem | Evidence |
|:--:|---|---|
| **1** | 180 days is not a decision — it is a constant | No configuration key exists |
| **2** | No archive before deletion | History is destroyed, not moved |
| **3** | Unbatched mass delete | One statement against a table with 3 (soon 5) indexes, concurrently receiving inserts from `EventFlushService` |
| **4** | Startup-triggered | Purge timing is coupled to deployment frequency |

**INFERENCE on problem 3** — this is the operational risk that grows fastest. The P0 event expansion adds high-frequency events (`mic_deactivated` fires on every mute; `hand_raised`/`hand_lowered` on every toggle), so the daily delete volume rises by design. On SQL Server, a large single-statement delete risks lock escalation on the same table the flush service is writing to — turning a maintenance job into an ingestion stall.

## Target policy

### Raw events — 180 days, configurable, batched

**RECOMMENDATION — keep 180 days.** **INFERENCE** — the window is defensible: it covers two full quarters of trend analysis and comfortably exceeds any operational investigation horizon. Extending it without a stated need would grow storage and slow queries for data nobody queries. The problem was never the number; it was that the number was a constant rather than a policy, and that nothing survived it.

Three changes:
1. Move the period to configuration (`Analytics:RawEventRetentionDays`), following the existing `Analytics:IpHashSalt` pattern.
2. **Batch the delete** — bounded row count per statement, looping until exhausted, with a delay between batches. This is the single most important change in this section.
3. Decouple from startup — run on a schedule, with a startup delay.

### Aggregated analytics — indefinite

**RECOMMENDATION — never purge read models.**

**INFERENCE — this is what actually solves the history problem.** Read models are small: `DailyPlatformMetrics` is one row per day; `DailyRoomMetrics` is one row per room-day. Even at generous growth these are trivial next to raw events. Retaining them indefinitely gives Cocorra a trend history longer than 180 days for the first time — which is the real answer to U-1, rather than extending raw retention and paying for it in storage and query time.

**INV-5 restated as a retention rule** — read models must hold nothing that cannot be recomputed from raw events *within the raw window*. Once raw events age out, the rollup becomes the only record, which is exactly the intent. Recomputation is possible while it matters (formula corrections, backfills) and unnecessary afterwards.

### Historical decision data — snapshots

**RECOMMENDATION** — `DailyStateSnapshots` (specified in `17-`) retained indefinitely.

**FACT** — pending queue depth, FCM token coverage, and active-user counts are pure state with no event trail. **INFERENCE** — history not captured today is unrecoverable tomorrow. This is why the snapshot job is P0 despite being small: every day it does not run is a permanent hole in a series that cannot be backfilled.

### Dead-letter — 30 days

**RECOMMENDATION** — dead-lettered events retained ~30 days, then purged. They exist for operational diagnosis, not analysis. **INFERENCE** — a dead-letter store that grows without bound becomes a second problem; one that is emptied too fast destroys the evidence of what failed. Thirty days spans any realistic investigation.

## Retention summary

| Data | Retention | Purge method | Rationale |
|---|---|---|---|
| Raw `UserEvents` | 180 days, configurable | **Batched** delete, scheduled | Bounded storage; two quarters of raw detail |
| `Daily*Metrics` read models | Indefinite | None | Small; the actual long-term history |
| `DailyStateSnapshots` | Indefinite | None | Unrecoverable if not captured |
| `DeadLetterEvents` | ~30 days | Batched delete | Operational diagnosis only |
| `AggregationCheckpoint` | Indefinite | None | Single-digit row count |

---

# Storage Growth

**FACT** — R-3 (current row count and growth rate) has not been measured. **RECOMMENDATION** — measure before sizing anything; the note below is a shape, not an estimate.

**INFERENCE — the shape of the change.** The P0 events change the per-room event profile qualitatively, not just quantitatively. Today a room produces roughly: one `room_created`, N `room_joined`, some `mic_activated`, N `room_left`, one `room_ended`. After E-01…E-08 it additionally produces a `hand_raised`/`hand_lowered` pair per raise cycle and a `mic_deactivated` per mic segment. **The events that scale worst are exactly the ones that scale with engagement** — a lively room with many hands and much mic-switching generates the most rows. That is the correct behaviour, and it is also why R-1 (channel drop rate) must be measured first: the new load lands hardest on the busiest rooms, which are the ones whose data matters most.

**RECOMMENDATION — three controls, in order:**
1. Measure R-1 and R-3 before deploying P0 events (blocking, per `24-`).
2. Batch the retention delete before event volume rises, not after.
3. Keep read models narrow, so the indefinite-retention decision stays cheap.

---

# Idempotency Model in Storage

Restating `15-`'s three classes as storage behaviour.

| Class | `EventId` derivation | Storage behaviour | Example |
|---|---|---|---|
| **EXACTLY-ONCE** | Deterministic from `eventKey` | Unique constraint rejects the duplicate; the insert is swallowed | `room_went_live`, `activation_completed` |
| **AT-LEAST-ONCE, DEDUP-ON-REPLAY** | `Guid.NewGuid()` at enqueue | Unique constraint protects against batch replay only; genuine repeats persist | `mic_deactivated`, `room_joined` |
| **NATURALLY-UNIQUE** | `Guid.NewGuid()` | Retry safety only | `message_sent` |

**Flush-service contract (RECOMMENDATION)**
1. Attempt the batch.
2. On transient failure → bounded retry with backoff. Safe, because `EventId` values are already stamped and stable.
3. On duplicate-key violation → fall back to per-row insert, discarding only the colliding rows.
4. On exhausted retries → append the batch to `DeadLetterEvents` and increment a counter.
5. Never `batch.Clear()` on the failure path without one of steps 3 or 4 having run.

**FACT** — step 5 is the correction to `EventFlushService.cs`, where `batch.Clear()` currently executes in a `finally` and discards up to 100 events on any database error.

---

# Validation

| # | Test | Asserts | Provider |
|:--:|---|---|---|
| **1** | Duplicate `EventId` insert | Second insert is rejected; first row survives | **SQLite in-memory or SQL Server** |
| **2** | Batch replay after simulated failure | Row count unchanged after replaying an identical batch | SQLite / SQL Server |
| **3** | Mixed batch with one duplicate | 99 rows persist, 1 discarded, batch not failed | SQLite / SQL Server |
| **4** | Deterministic key stability | The same `eventKey` produces the same `EventId` across processes and restarts | Unit |
| **5** | Batched retention delete | Deletes only rows past the cutoff; completes in bounded batches | Integration |
| **6** | Retention configurability | A changed config value changes the cutoff | Unit |
| **7** | Payload size cap | An oversized client payload is rejected before serialisation | Unit |
| **8** | No-PII assertion | `room_group_message_sent` carries no message content | Unit |
| **9** | `SetNull` on user deletion | Events survive with `UserId = NULL` | **SQL Server** |

**FACT — a critical constraint on tests 1, 2, 3, and 9.** `Cocorra.Tests` uses `Microsoft.EntityFrameworkCore.InMemory` (`Cocorra.Tests.csproj`). That provider does **not** enforce unique indexes or `DeleteBehavior`. Every idempotency and referential test written against it would pass vacuously while proving nothing.

**RECOMMENDATION** — add SQLite in-memory (which does enforce unique constraints) for these tests, or run them against a real SQL Server instance. **INFERENCE** — this is not a minor testing detail. The entire idempotency guarantee rests on a database constraint, and a test suite that cannot observe that constraint would report success for a broken implementation. It is called out again in `22-testing-validation-strategy.md`.

---

# Summary

| Decision | Verdict |
|---|---|
| Keep `UserEvents` as the raw store | **YES — KEEP AND EXTEND** |
| New columns | `EventId` (unique), `SchemaVersion`, `CorrelationId` (nullable) |
| New indexes | `UX(EventId)`; filtered `IX(CorrelationId)` |
| Existing indexes | Unchanged — all three still serve target queries |
| Partitioning | Deferred, with an explicit trigger; batched deletes first |
| JSON strategy | Keep `nvarchar(max)`; promote `isHost`; cap size; enforce no-PII |
| Raw retention | 180 days, configurable, **batched**, decoupled from startup |
| Aggregate retention | **Indefinite** — this is what gives Cocorra history beyond 180 days |
| Snapshot retention | **Indefinite** — uncapturable retroactively |
| Dead-letter retention | ~30 days |

**INFERENCE — the two changes that matter most.** `EventId` with a unique constraint is the first schema change because it unblocks the retry work; without it, retry creates duplicates instead of preventing loss. Batching the retention delete is the highest-value operational change, because the P0 event expansion increases purge volume by design and the current unbatched delete competes for locks with the ingestion path it is meant to support.
