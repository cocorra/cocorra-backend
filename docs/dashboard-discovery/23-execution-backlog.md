# 23 — Execution Backlog

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 12
> **Depends on**: all preceding blueprint documents
> **Scope**: Documentation only. These are work items for a future execution phase.

---

## How to read this

Each item is implementation-ready: it names the files, the dependencies, the steps, the acceptance criteria, the tests, and the rollback position. File paths and line numbers are verified at HEAD `c13f1f6`.

**Priorities**

| Level | Meaning |
|---|---|
| **P0** | Data Trust — must land before the dashboard is used for major decisions |
| **P1** | Core Analytics Infrastructure |
| **P2** | Decision Analytics |
| **P3** | Advanced Intelligence |

**No development-hour estimates are given**, deliberately. Relative sequencing and dependency are specified; velocity is the team's to judge.

---

# P0 — DATA TRUST

---

## AN-001 — Measure runtime pipeline behaviour

| | |
|---|---|
| **Problem** | Three implementation decisions depend on measurements that cannot be made from source: current event-drop rate, flush-failure rate, and `UserEvents` volume. |
| **Goal** | Replace three inferences with three measurements before any change that depends on them. |
| **Files / Components** | None — read-only inspection of container logs and the database. |
| **Dependencies** | None. **This is the first item in the programme.** |
| **Priority** | **P0** |

**Implementation steps**
1. Count occurrences of `"Event queue full; dropped {EventType}"` in container logs over a representative week (R-1).
2. Count occurrences of `"Failed to persist batch of {BatchCount} user events."` (R-2).
3. `SELECT COUNT(*) FROM UserEvents`, plus daily counts for the last 30 days (R-3).
4. Compare distinct `SessionId` per user per day against distinct active users per day (R-4).
5. `SELECT TOP 100 PropertiesJson FROM UserEvents WHERE EventType = 'notification_opened'` (R-5).
6. Record all five in `11-current-state-validation.md` §6.

**Acceptance criteria**
- [ ] All five measurements recorded with dates and method
- [ ] R-1/R-2/R-3 explicitly signed off as inputs to AN-003 and AN-004

**Tests required** — none (observation only).

**Rollback** — n/a.

**INFERENCE** — deploying high-frequency events into a channel that is already dropping would degrade the events that currently work, and the drop is silent. This item costs a log grep and two queries; skipping it means guessing about a failure mode that leaves no trace.

---

## AN-002 — Add `EventId`, `SchemaVersion`, `CorrelationId` to `UserEvents`

| | |
|---|---|
| **Problem** | **FACT** — `UserEvent.Id` is a database identity assigned at insert, so a retried batch produces new rows with new ids. There is no deduplication anywhere in the pipeline. |
| **Goal** | Give every event a stable identity so retry becomes safe and "at most once" becomes enforceable. |
| **Files / Components** | `Cocorra.DAL/Models/UserEvent.cs`, `Cocorra.DAL/Data/AppDbContext.cs` (config at lines 251-264), new migration |
| **Dependencies** | None |
| **Priority** | **P0** |

**Implementation steps**
1. Add `EventId` (`Guid`, required), `SchemaVersion` (`byte`, default 1), `CorrelationId` (`Guid?`).
2. Configure `UX_UserEvents_EventId` unique; `IX_UserEvents_CorrelationId` **filtered** on `CorrelationId IS NOT NULL`.
3. Create the migration.
4. **Backfill existing rows in batches**, not one `UPDATE` — the table receives concurrent inserts.
5. Apply the unique constraint only after backfill completes.

**Acceptance criteria**
- [ ] Columns exist; unique constraint active
- [ ] Existing rows backfilled; no duplicates
- [ ] Correlation index is filtered, not full-table
- [ ] Existing event writes unaffected

**Tests required** — 29 (duplicate rejected), 32 (deterministic key stability), plus the SQLite provider guard. **Must run on SQLite in-memory or SQL Server** — `EFCore.InMemory` does not enforce unique indexes and these tests would pass vacuously against it.

**Rollback** — drop the constraint first, then the columns. Nothing reads them until AN-003.

**INFERENCE** — this is the first schema change because AN-003 and AN-010 both depend on it. Adding retry before this column exists would create duplicates rather than prevent loss.

---

## AN-003 — Harden `EventFlushService`

| | |
|---|---|
| **Problem** | **FACT** — `batch.Clear()` runs in a `finally`, so any transient database fault permanently discards up to 100 events. No retry, no dead-letter, no checkpoint. This is a silent loss path the prior audit did not identify. |
| **Goal** | Close the loss path without weakening the never-throw contract. |
| **Files / Components** | `Cocorra.BLL/Services/EventTracking/EventFlushService.cs`, new `EventTrackingOptions.cs`, new `Cocorra.DAL/Models/Analytics/DeadLetterEvent.cs`, `Program.cs:210-214` |
| **Dependencies** | **AN-002** (blocking), AN-001 (parameters) |
| **Priority** | **P0** |

**Implementation steps**
1. Bind `EventTrackingOptions` (batch size, max retries, backoff, channel capacity), following the `Analytics:IpHashSalt` guard pattern at `Program.cs:205-207`.
2. Classify `SaveChangesAsync` failures: duplicate-key / transient / permanent.
3. **Duplicate-key → per-row insert fallback**, discarding only the colliding rows.
4. Transient → bounded retry with exponential backoff.
5. Exhausted or permanent → append to `DeadLetterEvents`, increment counter.
6. `batch.Clear()` only after step 3, 4, or 5 has completed.
7. Add counters: `events_dropped_on_enqueue`, `flush_batches_failed`, `flush_batches_retried`, `events_dead_lettered`.
8. On cancellation, drain the remaining channel with a bounded timeout before exiting.

**Acceptance criteria**
- [ ] Transient failure retried, then succeeds, with no duplicates
- [ ] Permanent failure dead-lettered, zero events lost
- [ ] A batch with one duplicate persists 99 rows and does not fail
- [ ] `Track` still never throws on a full channel
- [ ] Shutdown drains in-flight events

**Tests required** — 28, 30, **31**, 42, 43.

**Rollback** — revert; behaviour returns to discard-on-failure. `EventId` is harmless if unused.

**INFERENCE — step 3 is the item's highest-risk detail.** `AddRange` + one `SaveChangesAsync` means a single duplicate key fails all 100 rows. Adding the unique constraint without the per-row fallback would create a **new 99-event-wide loss path** — strictly worse than the problem being fixed.

---

## AN-004 — Batch the retention delete; make retention configurable

| | |
|---|---|
| **Problem** | **FACT** — a single unbatched `ExecuteDeleteAsync` against a table with three (soon five) indexes, concurrently receiving inserts. Retention is hardcoded at 180 days. Runs immediately at every startup. |
| **Goal** | Prevent the purge from competing for locks with ingestion, and make the policy a decision. |
| **Files / Components** | `Cocorra.BLL/Services/EventTracking/EventCleanupService.cs`, `EventTrackingOptions.cs` |
| **Dependencies** | AN-001 (R-3 volume) |
| **Priority** | **P0** |

**Implementation steps**
1. Move the period to `Analytics:RawEventRetentionDays` (default 180).
2. Delete in bounded batches (default 5,000), looping until exhausted, with a short inter-batch delay.
3. Decouple from startup: apply a startup delay, then run daily at a low-traffic hour (UTC 00:00–03:00 aligns with the MENA overnight trough).
4. Log rows deleted, batches executed, duration.

**Acceptance criteria**
- [ ] Only rows past the cutoff are deleted
- [ ] No single statement exceeds the batch size
- [ ] Config change moves the cutoff
- [ ] Concurrent inserts succeed during a purge run

**Tests required** — 44, plus retention configurability. **SQL Server** — `ExecuteDeleteAsync` batching is provider-specific.

**Rollback** — revert to the unbatched delete.

**INFERENCE** — this must land *before* the P0 events, not after. Those events increase daily row volume by design, so the purge grows in step; batching a delete that is already competing with ingestion is harder than batching one that is not yet under pressure.

---

## AN-005 — Apply host exclusion; remove Top Speakers

| | |
|---|---|
| **Problem** | **FACT, TRUST-01** — a silent host accrues the room's full 2–3 hour duration as `TotalSpokenSeconds` while emitting no `mic_activated`. Top Speakers ranks coaches by room length; Active-vs-Passive counts those same coaches as passive listeners. Two panels of the same dashboard contradict each other about the same person. |
| **Goal** | Remove the only active self-contradiction in Cocorra's data. |
| **Files / Components** | `Cocorra.DAL/Repository/AnalyticsRepository/AnalyticsRepository.cs:166-231` (Participation), `:501-540` (ActiveVsPassive), `Cocorra.DAL/DTOS/AnalyticsDto/ParticipationStatsDto.cs` |
| **Dependencies** | None — **query-layer only, no schema, no events, no deployment coordination** |
| **Priority** | **P0** |

**Implementation steps**
1. Join `RoomParticipant` / `UserEvents` to `Rooms.HostId`; exclude rows where `UserId = HostId`.
2. Remove `TopSpeakers` and `UsersWhoRaisedHand` from the DTO and the response (removal, not hiding — per R-8).
3. Redefine `UsersWhoSpoke` to derive from `mic_activated`, host-excluded, rather than `TotalSpokenSeconds > 0`.
4. Prepare the user-facing note explaining the Active-vs-Passive rate shift.

**Acceptance criteria**
- [ ] A host joining their own room is excluded from every room-participation metric
- [ ] The same user joining another room is included
- [ ] No `UserId` appears in both the speaker set and the passive set for the same window
- [ ] `TopSpeakers` and `UsersWhoRaisedHand` absent from the response
- [ ] Active-vs-Passive rate is higher than before, and the change is explained

**Tests required** — 2, 3, **4**, 10, 12, 59, 60.

**Rollback** — revert the query change. **INFERENCE** — rolling back restores a self-contradicting metric, which is why this should ship early and stay.

**INFERENCE** — this is the earliest high-value item in the programme: no dependencies, no deployment risk, and it removes the defect most likely to produce a confident wrong decision.

---

## AN-006 — Replace the retention metric

| | |
|---|---|
| **Problem** | **FACT, TRUST-03** — `AnalyticsRepository.cs:324-392` matches activity on *exactly* day N (`timeDiff.Days == day`), over a cookie-based `session_started` signal on a Flutter client, with an **unbounded** activity fetch. |
| **Goal** | Replace with a metric that is server-authoritative, cookie-independent, and correctly defined. |
| **Files / Components** | `AnalyticsRepository.cs:324-392`, `AnalyticsService.cs`, `IAnalyticsService.cs`, `AnalyticsController.cs` |
| **Dependencies** | None |
| **Priority** | **P0** |

**Implementation steps**
1. Implement M-102: of users with `room_joined` in week N, the share with `room_joined` in any **later** week.
2. Bound the activity query by an explicit upper time limit.
3. Mark the old endpoint deprecated; keep it serving until cutover.
4. Add the upward-bias limitation (hard deletes) to the metric contract.

**Acceptance criteria**
- [ ] A user active on days 2 and 5 counts as returned
- [ ] The new value is ≥ the old over the same cohort
- [ ] The computation reads zero `session_started` rows
- [ ] The activity query has an upper bound

**Tests required** — 5, 48, 54.

**Rollback** — the old endpoint is still live until Phase E.

**INFERENCE** — this is a replacement, not a repair. Fixing `== day` to `>= day` while leaving `session_started` as the signal would produce a plausible number resting on an unvalidated input — the exact failure mode this programme exists to eliminate.

---

## AN-007 — Make the funnel sequential

| | |
|---|---|
| **Problem** | **FACT, TRUST-06** — `AnalyticsRepository.cs:300-322` counts each step independently with no ordering constraint, so the funnel can *widen* downward. |
| **Goal** | A true sequential funnel with per-step elapsed time. |
| **Files / Components** | `AnalyticsRepository.cs:300-322`, `AnalyticsService.cs`, `AnalyticsController.cs`, `Router.cs` |
| **Dependencies** | None |
| **Priority** | **P0** |

**Implementation steps**
1. Per user, take `MIN(OccurredAtUtc)` per step; count at step N only where every prior step precedes it.
2. Compute median elapsed time between consecutive steps.
3. Assert monotonicity before returning; a violation is an error, not a chart.
4. Expose as `GET /Analytics/Activation/Funnel` (C-1).

**Acceptance criteria**
- [ ] Each step's count ≤ the previous step's, for any input
- [ ] A user with `activation_completed` before `email_confirmed` does not count at the later step
- [ ] Median elapsed time returned per step
- [ ] Sequential counts ≤ old independent counts at every step

**Tests required** — **6**, 7, 38, 62.

**Rollback** — the old endpoint remains until Phase E.

**INFERENCE** — elapsed time is not an optional extra here. One of Cocorra's steps is a human review queue; a conversion-only funnel renders an 18-hour wait and an instant drop-off identically, and those need opposite responses.

---

## AN-008 — Split User Growth; reconstruct status history

| | |
|---|---|
| **Problem** | **FACT, TRUST-02** — users are bucketed by `CreatedAt` but counted by **current** `Status`. The distortion grows with bucket age, producing a false "our early users were worse" gradient. |
| **Goal** | Keep the sound registration count; replace the status breakdown. |
| **Files / Components** | `AnalyticsRepository.cs:21-93`, `UserGrowthDto.cs` |
| **Dependencies** | None for reconstruction; AN-011 for durable future history |
| **Priority** | **P0** |

**Implementation steps**
1. Extract registration count per bucket as its own VERIFIED metric.
2. Remove the current-status breakdown.
3. Reconstruct status-at-time from `voice_verification_result` events: per user, the most recent result at or before each bucket boundary; no event → `Pending`.
4. Return an explicit series start date bounded by the 180-day event window.
5. Relabel MBTI and average age as *"users registered in this window."*
6. Replace `.ToList()` client-side grouping with server-side aggregation.

**Acceptance criteria**
- [ ] A user registered in month 1 and banned in month 3 shows month 1's status in month 1
- [ ] Reconstructed *current* status equals `AspNetUsers.Status` for every user with a status event
- [ ] Series start date returned explicitly
- [ ] Registration counts unchanged from the old path

**Tests required** — 49, 54, 61.

**Rollback** — restore the previous DTO shape.

**INFERENCE** — the reconciliation in acceptance criterion 2 is the important one: a mismatch means a status-change emit site is missing, which is a defect the reconstruction would otherwise hide.

---

## AN-009 — `StateSnapshotService`

| | |
|---|---|
| **Problem** | **FACT, GAP-05** — `AdminService.GetDashboardStatsAsync` (`AdminService.cs:383-401`) is a bare `GroupBy(Status)` with no date filter. Yesterday's pending queue depth is unrecoverable. |
| **Goal** | Begin capturing pure-state counts before more history is lost. |
| **Files / Components** | New `Cocorra.BLL/Services/Analytics/StateSnapshotService.cs`, new `Cocorra.DAL/Models/Analytics/DailyStateSnapshot.cs`, `AppDbContext.cs`, `Program.cs` |
| **Dependencies** | None |
| **Priority** | **P0** |

**Implementation steps**
1. Create `DailyStateSnapshots (Date, MetricKey, Value, ComputedAtUtc)`, unique on `(Date, MetricKey)`.
2. `BackgroundService` running daily at 00:15 UTC with a startup delay.
3. Capture: `pending_verification_queue`, `rerecord_queue`, `active_users_total`, `fcm_token_coverage`, `open_reports`.
4. UPSERT on the natural key.
5. Same-day retry on failure.
6. Gap detection that flags missing dates and never interpolates.

**Acceptance criteria**
- [ ] Snapshot equals a direct count at capture time
- [ ] Two runs on the same date produce one row
- [ ] Missing dates flagged, not interpolated
- [ ] Failure triggers a same-day retry

**Tests required** — 36, 53, 67.

**Rollback** — remove the hosted service registration. Existing rows are harmless.

**INFERENCE — the smallest item in the programme and among the most urgent.** Five `COUNT` queries on a timer. But it is the only read model that cannot be backfilled: every day it does not run is a permanent hole in a series operations will eventually need. It is also the only guard against an FCM token regression of the `dc1c933` class.

---

## AN-010 — Fix `activation_completed` deduplication

| | |
|---|---|
| **Problem** | **FACT, TRUST-10** — `AdminService.cs:141-147` guards emission by querying the `UserEvents` **table**, but `Track` only enqueues. Two concurrent activations of the same user both observe "not yet activated" and both emit. |
| **Goal** | Replace a racing read with a database-enforced guarantee. |
| **Files / Components** | `Cocorra.BLL/Services/AdminService/AdminService.cs:141-147`, `EventTracker.cs`, `IEventTracker.cs` |
| **Dependencies** | **AN-002** (blocking) |
| **Priority** | **P0** |

**Implementation steps**
1. Add the `Track` overload accepting `eventKey`.
2. Derive `EventId` deterministically from `eventKey` when supplied.
3. Replace the `AnyAsync` guard with `eventKey = $"activation_completed:{userId}"`.
4. Run the duplicate-incidence query to quantify historical occurrences.

**Acceptance criteria**
- [ ] Two parallel activations produce exactly one persisted event
- [ ] The same `eventKey` yields the same `EventId` across processes
- [ ] The `AnyAsync` round-trip is removed from the activation path
- [ ] Historical duplicate count recorded

**Tests required** — **33**, 68.

**Rollback** — restore the `AnyAsync` guard.

**INFERENCE** — this fix is cheaper than the code it replaces (it removes a database round-trip per activation) and it establishes the general rule: an emission guard must never depend on reading a table written asynchronously. Several proposed events have the same "at most once" requirement and would fail identically if implemented by analogy with the current code.

---

## AN-011 — Emit `user_status_changed`

| | |
|---|---|
| **Problem** | **FACT** — the acting admin's identity exists in the controller and is dropped at exactly one boundary. `ApplicationUser` has no `UpdatedAt` and no history table exists, so status transitions are recorded nowhere durable. |
| **Goal** | One event closing three gaps: historical status (TRUST-02), backlog history (GAP-05), reviewer consistency (GAP-08). |
| **Files / Components** | `IAdminService.cs:13`, `AdminService.cs:77` and `:289`, `AdminController.cs:54` and `:92`, `EventTypes.cs` |
| **Dependencies** | AN-002 |
| **Priority** | **P0** |

**Implementation steps**
1. Add `Guid adminId, bool isBulk = false` to `IAdminService.ChangeUserStatusAsync`.
2. Update the implementation; capture `fromStatus` before mutation.
3. Pass `adminId` from `AdminController.ChangeStatus` (already read at line 54).
4. Forward `adminId` and `isBulk: true` from `BulkChangeUserStatusAsync` (already received at line 256).
5. Emit after `UpdateAsync` succeeds, with `{fromStatus, toStatus, changedByAdminId, isBulkOperation, reason}`.

**Acceptance criteria**
- [ ] `changedByAdminId` populated in both paths
- [ ] `isBulkOperation` correct in both paths
- [ ] `fromStatus` captured before mutation
- [ ] Emitted only after a successful update
- [ ] Existing `voice_verification_result` emission unchanged

**Tests required** — 25, 27.

**Rollback** — revert. **INFERENCE — the rollback is lossy**: status transitions during the rollback window are permanently unrecoverable, because no other record exists. This event carries the highest data-loss risk in the programme and justifies extra validation before deployment.

---

## AN-012 — `IMetricRegistry` and trust metadata

| | |
|---|---|
| **Problem** | **FACT** — `08a` grades only 1 of 12 shipped metrics as VERIFIED, yet all twelve render identically. Nothing distinguishes a sound number from a wrong one. |
| **Goal** | Make metric contracts executable and carry trust to the client. |
| **Files / Components** | New `Cocorra.BLL/Services/Analytics/MetricRegistry.cs` + `IMetricRegistry.cs`, new `MetricTrustMeta` DTO, `AnalyticsService.cs`, `AnalyticsController.cs`, `Router.cs` |
| **Dependencies** | AN-005…AN-008 (so the registry describes corrected metrics) |
| **Priority** | **P0** |

**Implementation steps**
1. Encode every contract from `14-` in `MetricRegistry`.
2. Populate `Response<T>.Meta` — **FACT**: the field already exists on every response, is accepted by `ResponseHandler.Success<T>(entity, meta)`, and is currently always null. Populating it is purely additive.
3. Include `dataAvailableFromUtc`, `computedAtUtc`, `exclusions`, `limitations`, `trustLevel`, `historicalReliability`.
4. Expose `GET /Analytics/Metrics/Registry` (G-1).
5. Fail the build if a served metric has no contract, or a contract lacks any of the four mandatory fields.

**Acceptance criteria**
- [ ] Every analytics endpoint returns populated `Meta.metrics`
- [ ] Registry output matches per-response metadata exactly
- [ ] Every contract has business purpose, technical definition, formula, validation method
- [ ] Host exclusion declared in `Exclusions` for every room metric
- [ ] A metric without a contract fails the build

**Tests required** — 45, 56, 57, 58, 59.

**Rollback** — `Meta` reverts to null; clients ignoring it are unaffected.

**INFERENCE** — the build-failure rule in step 5 is what converts `08a`'s mandatory rule from a policy into a guarantee. It is how the system avoids re-accumulating undocumented metrics.

---

## AN-013 — Soft delete for `ApplicationUser`

| | |
|---|---|
| **Problem** | **FACT, TRUST-05** — `AuthServices.DeleteAccountAsync` hard-deletes the row. Every retention rate is computed only over survivors and is biased upward. Registration history decreases retroactively. A reported user can erase their moderation history by deleting their account (`Report.ReportedUserId` is `SetNull`). |
| **Goal** | Preserve analytical continuity while honouring deletion in substance. |
| **Files / Components** | `Cocorra.DAL/Models/ApplicationUser.cs`, `AppDbContext.cs` (global query filter), `Cocorra.BLL/Services/AuthService/AuthServices.cs:565`, plus an audit of every `ApplicationUser` query |
| **Dependencies** | **BLOCKED on a data-protection decision** |
| **Priority** | **P0** |

**Implementation steps**
1. **Obtain a decision on whether scrub-in-place satisfies Cocorra's deletion obligations.** This is not an engineering decision.
2. Add `IsDeleted` and `DeletedAt`.
3. Rewrite `DeleteAccountAsync`: scrub name, email, bio, profile picture, voice path, FCM token in place; retain `Id`, `CreatedAt`, `Status`.
4. Add a global query filter excluding deleted users by default.
5. **Audit every query touching `ApplicationUser`** — including `UserManager` operations, login, role checks, friend search, admin listings — and add `IgnoreQueryFilters()` where analytics needs the full population.

**Acceptance criteria**
- [ ] Deletion leaves the row with `IsDeleted = true` and personal fields nulled
- [ ] Default queries exclude deleted users; explicit opt-out includes them
- [ ] Cumulative registration counts never decrease
- [ ] `Report.ReportedUserId` and `UserEvent.UserId` retain values after deletion
- [ ] No personal field survives (auditable privacy test)

**Tests required** — 61, plus the soft-delete suite in `13-`. **SQL Server** for the referential assertions.

**Rollback** — remove the query filter and restore hard delete. Already-soft-deleted rows would need handling.

**INFERENCE — the blast radius is wider than the analytics surface.** A global query filter changes the behaviour of *every* query touching `ApplicationUser`. The analytics benefit is large, but this must be scoped as an application-wide change, not an analytics one. **It is also the only P0 item where each day of delay permanently destroys evidence**, which is why it should be raised for decision immediately rather than at implementation time.

---

# P1 — CORE ANALYTICS INFRASTRUCTURE

---

## AN-014 — Read model tables

**Problem** — No aggregation layer exists; every metric is a live query, and raw events are purged at 180 days.
**Goal** — Give metrics history, stability, and cheap reads.
**Files** — `Cocorra.DAL/Models/Analytics/` (RM-1…RM-5, `AggregationCheckpoint`), `AppDbContext.cs`, migration
**Dependencies** — AN-002
**Steps** — Create the five tables per `17-` with natural-key unique indexes; add `WeeklyPlatformMetrics` for distinct-user grains; denormalise room dimensions onto RM-2.
**Acceptance** — Tables exist with unique natural keys; RM-2 carries `HostId`, `Category`, `SelectionMode`, `StageCapacity`; no percentages stored, only counts.
**Tests** — 51, 52.
**Rollback** — Drop the tables; nothing reads them yet.
**Priority** — **P1**

**INFERENCE** — storing counts rather than percentages is the design decision that makes weekly figures correct. A weekly rate must be computed from summed numerators and denominators; averaging seven daily rates is wrong whenever daily volumes differ, and Cocorra's volumes differ greatly between a day with three live rooms and a day with none.

---

## AN-015 — `AnalyticsAggregationService`

**Problem** — Read models need populating incrementally and idempotently.
**Goal** — Hourly rollups that are safe to re-run.
**Files** — New `Cocorra.BLL/Services/Analytics/AnalyticsAggregationService.cs`, `IAggregationCheckpointStore.cs`, `Program.cs`
**Dependencies** — AN-014
**Steps** — Hourly `BackgroundService` at :05; read `UserEvents WHERE Id > LastProcessedEventId` with a small safety lag; identify affected grains; recompute each **in full**; UPSERT; advance the watermark only after commit; recompute funnel cohorts over a 45-day trailing window.
**Acceptance** — Rollups reconcile against live queries; running twice produces byte-identical rows; a forced failure leaves the watermark unchanged; a late `activation_completed` is picked up.
**Tests** — 35, 40, 41, 64.
**Rollback** — Disable `Analytics:UseReadModels`; endpoints fall back to live queries.
**Priority** — **P1**

**INFERENCE — the watermark must be `UserEvent.Id`, not `OccurredAtUtc`.** Because the flush service persists in batches, an event that *occurred* at 10:59 may be *inserted* at 11:01. A timestamp watermark already past 11:00 would skip it permanently and silently. The identity column has no such hazard.

---

## AN-016 — Backfill job

**Problem** — Read models start empty; some history is reconstructable.
**Goal** — Populate what raw data supports, and nothing more.
**Files** — New backfill job sharing the aggregation code path
**Dependencies** — AN-015
**Steps** — Resumable, date-batched, oldest first, checkpointed per date, throttled; run against a restored copy first; per `17-`: RM-1 full (180d), RM-2 partial, **RM-3 full history**, RM-4 onboarding only.
**Acceptance** — Backfilled rows byte-identical to live-aggregated rows over the same range; non-backfillable metrics carry `dataAvailableFromUtc`; **no fabricated events**.
**Tests** — 47, 48, 50.
**Rollback** — Truncate and re-run; INV-5 guarantees recomputability.
**Priority** — **P1**

**INFERENCE — RM-3 is the standout.** Host metrics derive from `Rooms.HostId` and `Rooms.CreatedAt` — relational data never purged — so the supply-side series can be reconstructed from the platform's first day, giving Cocorra's leading indicator a longer baseline than anything else in the system.

---

## AN-017 — P0 room events, low-frequency increment

**Problem** — GAP-06: four of six core-loop funnel steps are uninstrumented.
**Goal** — Instrument the stage flow, starting with events that add little channel load.
**Files** — `Cocorra.API/Hubs/RoomHub.cs` (`ApproveToStage`, `MoveToAudience`, `KickUser`, `GrantExtraTime`, `ToggleMic` time-up path), `RoomService.cs:422-460`, `EventTypes.cs`
**Dependencies** — AN-002, AN-003, AN-004, AN-001
**Steps** — Emit E-03, E-04, E-06, E-07, E-08, E-09 behind `Analytics:EnableNewEventEmission`; emit after the domain write succeeds (except E-06, where the rejection *is* the fact).
**Acceptance** — `stage_promoted.UserId` is the **promoted participant**, not the host; `speaker_time_exhausted` emitted before the throw; `room_went_live` from both start paths; drop counter stable.
**Tests** — 20, 23, 24, 27, 28.
**Rollback** — Disable the flag.
**Priority** — **P1**

**INFERENCE** — the `stage_promoted.UserId` convention is the subtle risk. The existing `room_join_approved` uses the opposite convention (tracked against the host, `RoomService.cs:311`); an implementer following that precedent would break the M-400 funnel in a way no metric test would catch.

---

## AN-018 — P0 room events, high-frequency increment

**Problem** — Hand raises and mic segments are the highest-value and highest-volume events.
**Goal** — Complete core-loop instrumentation.
**Files** — `RoomHub.cs` (`RaiseHand`, `LowerHand`, `ToggleMic`), `RoomService.cs:526-538` and `:556-576`
**Dependencies** — **AN-017 deployed and drop counter verified stable**
**Steps** — Emit E-01, E-02, E-05 behind a separate flag; emit `mic_deactivated` from **all three** close sites; use a `COUNT` projection for stage occupancy, not a full participant load; thread a reason through the paths that reset `IsHandRaised`.
**Acceptance** — raise→lower→raise yields 2 raises and 1 lower; `mic_deactivated` from all three sites; `wasInitialHostMic` true only for the host's auto-opened mic; `hand_lowered.wasApproved` correct.
**Tests** — 17, 18, 19, 21, 22.
**Rollback** — Disable the high-frequency flag independently.
**Priority** — **P1**

**INFERENCE** — the two-increment split exists because these events scale with engagement, landing hardest on the busiest rooms. Separating them isolates the volume risk to a change that can be reverted without touching AN-017.

---

## AN-019 — Extend `room_joined` and `room_ended`

**Problem** — `room_joined` carries only `{roomId}`; `room_ended.durationHours` uses the *scheduled* `StartDate`.
**Goal** — Enable host exclusion by column and correct duration.
**Files** — `RoomHub.cs:270`, `RoomService.cs:543`
**Dependencies** — AN-017 (for `room_went_live`)
**Steps** — Add `isHost`, `isRejoin`, `entrySource` (default `"direct"`); add `actualDurationSeconds`, `endReason`, `peakParticipants`; increment `SchemaVersion` for both.
**Acceptance** — `isHost`/`isRejoin` correct; `entrySource` defaults when absent; `actualDurationSeconds` matches `room_ended.OccurredAtUtc − room_went_live.OccurredAtUtc`.
**Tests** — 24, 26, 39.
**Rollback** — Revert; properties are additive.
**Priority** — **P1**

**FACT** — both booleans are free at the emit point: `RoomHub.JoinRoom` already loads `room` and already branches on `participant.Status == Left` at line 245.

---

## AN-020 — Supply Health endpoints

**Problem** — GAP-07: host count, retention, concentration, and audience return are computable today from verified relational data, and **no endpoint computes any of them**.
**Goal** — Expose the platform's leading indicator.
**Files** — `AnalyticsRepository`, `AnalyticsService`, `AnalyticsController`, `Router.cs`, new DTOs
**Dependencies** — AN-014, AN-015, AN-012
**Steps** — Implement B-1, B-2, B-3 per `19-`; scope Coach role to own rows on B-2; return `suggestedDisplayOffsetMinutes` on B-3.
**Acceptance** — M-200/M-201/M-202/M-203/M-204 returned with trust metadata; M-202 includes total host count; M-204 excludes hosts with fewer than 2 rooms; B-3 carries the timezone offset.
**Tests** — 15, 16, 45.
**Rollback** — Additive routes; remove them.
**Priority** — **P1**

**INFERENCE — the highest value-to-effort item in the programme.** No new events, no schema change, entirely verified relational data, and it answers the platform's most consequential unwatched question.

---

## AN-021 — Report rate by room category

**Problem** — GAP-12: both inputs are verified and present; the segmentation has never been run.
**Goal** — Cocorra's highest-stakes safety analysis.
**Files** — `AnalyticsRepository.cs:233-298`, `ReportInsightsDto.cs`, `AnalyticsController.cs`
**Dependencies** — AN-014, AN-012
**Steps** — Join `user_reported.reportedRoomId` → `Rooms.Category`; normalise per 1,000 joins; **exclude** reports with no room context rather than bucketing them into `Others`; return absolute counts beside rates; restrict to `Admin`.
**Acceptance** — Per-category counts sum to the total with room context; no-room reports excluded; absolute counts present; Coach receives 403.
**Tests** — 13, 45.
**Rollback** — Remove the segmentation.
**Priority** — **P1**

**INFERENCE** — one `GROUP BY` on verified data. Two of three categories are `Relationships` and `MentalHealth`, which carry duty-of-care obligations a general social product does not.

---

## AN-022 — Admin review latency endpoint

**Problem** — GAP-08. **FACT, correcting the earlier audit** — latency **is** derivable from the event pair; `06-blind-spots.md` §3 concluded otherwise because it considered only the relational data.
**Goal** — Measure the hard serialisation point on the growth funnel.
**Files** — `AnalyticsRepository`, `AnalyticsService`, `AnalyticsController`, `Router.cs`
**Dependencies** — AN-009 (queue depth), AN-012
**Steps** — Compute p50/p90/p99 of the gap between `voice_verification_submitted` and `voice_verification_result`; segment by day-of-week and hour; return queue depth from RM-5 alongside; **return no mean**.
**Acceptance** — Exact percentiles from a known fixture; **no mean in the response**; queue depth present.
**Tests** — 8, 9.
**Rollback** — Remove the route.
**Priority** — **P1**

**INFERENCE** — excluding the mean is a contract requirement, not a presentation preference. If most reviews take 20 minutes and 15% take 3 days, the mean describes nobody and hides the users being harmed.

---

## AN-023 — Support analytics endpoint

**Problem** — GAP-10: no analytics endpoint covers support at all; the data exists and is unexposed.
**Goal** — Surface Cocorra's only systematic reliability signal.
**Files** — `AnalyticsRepository`, `AnalyticsController`, `Router.cs`, new DTOs
**Dependencies** — AN-012
**Steps** — Implement F-2; ticket volume by `SupportTicketType` normalised per 1,000 active users; first-response time from `SupportMessage.CreatedAt` + `IsFromAdmin`; chat resolution from `ClosedAt − CreatedAt`; **label M-601 "proxy — no error tracking exists"** in `Meta`.
**Acceptance** — Type filtering exact; anonymous tickets included; **proxy label present in `Meta`**.
**Tests** — 45.
**Rollback** — Remove the route.
**Priority** — **P1**

---

## AN-024 — Push delivery events

**Problem** — GAP-11: the FCM response is discarded. Commit `dc1c933` fixed *reversed FCM delivery*; an identical regression would be invisible today.
**Goal** — A regression guard for a defect class that has already occurred once.
**Files** — `Cocorra.BLL/Services/NotificationService/` (`PushNotificationService`), `EventTypes.cs`
**Dependencies** — AN-002
**Steps** — Emit `push_send_attempted` before the FCM call and `push_send_result` after, both with `CorrelationId = Notification.Id`; capture `success`, `errorCode`, `tokenInvalidated`, `latencyMs`.
**Acceptance** — A mocked failure produces `success=false` with an `errorCode`; attempt and result counts reconcile; token coverage reported alongside.
**Tests** — 27, 45, 70.
**Rollback** — Revert.
**Priority** — **P1**

---

## AN-025 — Job health endpoint

**Problem** — **FACT** — no structured logging sink, no APM, no metrics export. The dead-letter table could fill silently.
**Goal** — Make the durability work observable.
**Files** — `AnalyticsController`, `Router.cs`, `AggregationCheckpoint` reads
**Dependencies** — AN-003, AN-015
**Steps** — Implement F-3: aggregation lag, consecutive failures, dead-lettered count, dropped-on-enqueue count, snapshot gaps; restrict to `Admin`.
**Acceptance** — Reflects actual checkpoint state; stale aggregation sets `pipelineHealthy = false`.
**Tests** — 65, 66, 67.
**Rollback** — Remove the route.
**Priority** — **P1**

**INFERENCE** — this closes the loop `08a` opens. A metric's trust level is meaningless if the pipeline feeding it stopped three days ago.

---

# P2 — DECISION ANALYTICS

| ID | Title | Problem | Files | Dependencies | Priority |
|---|---|---|---|---|:--:|
| **AN-026** | Reminder events | `ToggleReminder` emits nothing; rows deleted on un-toggle, so conversion reads optimistically | `RoomService.cs:~490-510` | AN-002 | P2 |
| **AN-027** | Stage funnel endpoint | M-400 needs the new events surfaced | `AnalyticsRepository`, `Controller` | AN-018, AN-015 | P2 |
| **AN-028** | Room participation endpoint | Replaces `/Analytics/Participation`; removes deprecated fields | `AnalyticsRepository:166-231` | AN-005, AN-018 | P2 |
| **AN-029** | Social endpoints | GAP-16/17; reciprocity before volume | `AnalyticsRepository` | AN-012 | P2 |
| **AN-030** | Social origin properties | `message_sent` / `friend_request_sent` origin surface | `ChatService.cs:92`, `FriendService.cs:132` | AN-002 | P2 |
| **AN-031** | `LeftAt` + stop overwriting `JoinedAt` | GAP-14; `RoomHub.cs:245-253` destroys the original join time | `RoomParticipant.cs`, `RoomHub.cs` | — | P2 |
| **AN-032** | Group-chat existence check | GAP-15; establish whether the behaviour is material before building | Measurement only | — | P2 |
| **AN-033** | Status enums + resolution timestamps | `Report.Status` / `SupportTicket.Status` are free-form strings; analytics recognises only 3 values | `Report.cs`, `SupportTicket.cs` | — | P2 |
| **AN-034** | Moderation action event | GAP-12; enforcement outcomes unrecorded | `SupportService` | AN-002 | P2 |
| **AN-035** | Session signal replacement | TRUST-08/GAP-04; run parallel, decide on evidence | `EventsController.cs:22`, Flutter | AN-002, R-4 | P2 |
| **AN-036** | Local-time display context | GAP-18; UTC-only for a UTC+2/+3 base | `AnalyticsService` | AN-012 | P2 |
| **AN-037** | MBTI dichotomy analysis | GAP-19; test E/I vs mic activation, not 16 types | Query only | — | P2 |
| **AN-038** | Cohort grid endpoint | Needs 8 weeks of RM-1 | `AnalyticsRepository` | AN-016 + history | P2 |

**RECOMMENDATION on AN-032** — run the cheap existence check before building persistence. **INFERENCE** — if in-room chat volume is negligible the gap closes without code; if it is substantial, the Active-vs-Passive metric needs reinterpreting, because participants labelled "passive" may be actively typing.

---

# P3 — ADVANCED INTELLIGENCE

| ID | Title | Problem | Dependencies | Priority |
|---|---|---|---|:--:|
| **AN-039** | Decision Center | Change detection across signals | All P0/P1 + **4–6 weeks history** | P3 |
| **AN-040** | LiveKit webhook ingestion | GAP-20; zero media telemetry. `participantIdentity` correlation key already exists | LiveKit config, new endpoint | P3 |
| **AN-041** | Failure-path events | GAP-22; no error tracking anywhere | AN-002 | P3 |
| **AN-042** | Structured logging sink | Errors reach Docker stdout only | — | P3 |
| **AN-043** | Experiment capability | GAP-21; no flags, buckets, or experiment tables | User volume | P3 |
| **AN-044** | Acquisition attribution | GAP-23; no source field | — | P3 |
| **AN-045** | `UserEvents` partitioning | Deferred pending R-3 | AN-001 | P3 |

**RECOMMENDATION on AN-039** — do not build before the history gate. **FACT** — no baseline exists for any Cocorra metric. **INFERENCE** — detection without a baseline produces alerts on ordinary variance, and a dashboard that cries wolf in its first month is ignored permanently, which is harder to reverse than a delayed launch.

**RECOMMENDATION on AN-043** — cheapest first: exploit the approval-latency natural experiment (`07a` FI-3), then staged rollouts with a deterministic `UserId`-hash holdout. **INFERENCE** — with the manual approval gate throttling intake, Cocorra is unlikely to have volume for well-powered A/B tests on secondary features soon; treating A/B infrastructure as the answer would be premature.

---

# Backlog Summary

| Priority | Items | Character |
|---|:--:|---|
| **P0** | 13 (AN-001…AN-013) | Data trust. **6 require no application code change.** |
| **P1** | 12 (AN-014…AN-025) | Aggregation, events, endpoints |
| **P2** | 13 (AN-026…AN-038) | Decision analytics |
| **P3** | 7 (AN-039…AN-045) | Advanced |
| **Total** | **45** | |

**Three observations (INFERENCE).**

**Six P0 items need no application code change.** AN-001 (measurement), AN-005 (host exclusion), AN-006, AN-007, AN-008 (query corrections), and AN-021 in P1 are queries, removals, or observations against data already verified as correct. They carry no deployment risk and deliver the largest immediate trust improvement — and they include the two highest-value items in the programme: host exclusion and supply health.

**Two P0 items are blocking for the rest.** AN-002 (`EventId`) gates all retry and idempotency work; AN-001 (measurement) gates every decision about channel capacity and event volume. Nothing that touches the pipeline should start before both.

**One P0 item is blocked on a non-engineering decision.** AN-013 (soft delete) needs a data-protection ruling, and it is the only item where each day of delay permanently destroys evidence. It should be raised for decision at the start of the programme, not when its turn arrives in the backlog.
