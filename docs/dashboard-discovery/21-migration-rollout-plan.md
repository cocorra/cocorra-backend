# 21 — Migration & Rollout Plan

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 10
> **Depends on**: `13-data-trust-correction-plan.md`, `16-raw-event-storage-strategy.md`, `19-analytics-api-blueprint.md`, `20-dashboard-implementation-blueprint.md`
> **Scope**: Documentation only.

---

## The constraint that shapes this plan

**FACT** — the current dashboard is live and in use. `AnalyticsController` exposes eleven endpoints under `[Authorize(Roles = "Admin,Coach")]`, and `AdminController` exposes dashboard stats. Admins and coaches are reading these numbers today.

**INFERENCE — the resulting tension.** Three of those metrics are UNRELIABLE (`08a`: User Growth status breakdown, Participation/Top Speakers, Retention). Every day they remain visible is a day someone may act on a wrong number. But removing them abruptly breaks a UI in a separate repository that Cocorra's team relies on.

**The resolution adopted here:** correct the *worst* offender immediately because it needs no deployment coordination, run old and new in parallel for everything else, and remove deprecated metrics only after their successors are validated and the dashboard repository has cut over.

**RECOMMENDATION — one exception to the parallel-running principle.** Host exclusion (TRUST-01 stage 1) should ship in Phase A, not Phase D. It is a query-level filter, it removes the dashboard's only self-contradiction, and leaving a metric visible that reports the same person as both top speaker and passive listener is not defensible for the length of a full migration.

---

# Phase A — Preparation

**Goal**: everything that can land without changing a single user-visible number.

**Duration posture**: ships continuously; no coordination with the dashboard repository required.

## A.1 — Runtime observation (blocking)

**FACT** — `11-` records five observations (R-1…R-5) that cannot be settled from source. Three are blocking for later phases.

| Item | Measurement | Blocks |
|---|---|---|
| **R-1** | Frequency of `"Event queue full; dropped {EventType}"` in container logs over a representative week | Channel capacity decision; P0 event deployment |
| **R-2** | Frequency of `"Failed to persist batch of {BatchCount} user events."` | Sizes the silent-loss problem; sets retry parameters |
| **R-3** | `UserEvents` row count and 30-day daily growth | Retention batching, partitioning trigger |
| **R-4** | Distinct `SessionId` per user per day vs distinct active users | Confirms or refutes the cookie-unreliability hypothesis |
| **R-5** | `SELECT TOP 100 PropertiesJson FROM UserEvents WHERE EventType='notification_opened'` | Whether the client emits it, and with what properties |

**RECOMMENDATION** — R-1, R-2, R-3 are read-only (log inspection plus two `COUNT` queries) and must complete before any P0 event work begins.

**INFERENCE** — deploying high-frequency events (`mic_deactivated` on every mute, `hand_raised`/`hand_lowered` on every toggle) into a channel that is already dropping under load would degrade the events that currently work. The measurement is cheap; the alternative is guessing about a silent failure mode.

## A.2 — Schema additions (backward compatible)

| Change | Compatibility |
|---|---|
| `UserEvent.EventId` — `uniqueidentifier`, unique | Additive. Backfill existing rows with random GUIDs. |
| `UserEvent.SchemaVersion` — `tinyint`, default 1 | Additive |
| `UserEvent.CorrelationId` — `uniqueidentifier` null | Additive |
| Read model tables (RM-1…RM-5) | New tables; nothing reads them yet |
| `AggregationCheckpoint`, `DeadLetterEvents` | New tables |

**FACT** — `EventId` blocks the flush-service retry (`16-`). It must land first.

**RECOMMENDATION on the backfill** — populate `EventId` for existing rows in batches, not a single `UPDATE`. **INFERENCE** — the same lock-escalation concern that applies to `EventCleanupService`'s unbatched delete applies here, against a table receiving concurrent inserts.

## A.3 — Pipeline hardening

Ships with no visible change: flush retry with backoff, per-row duplicate fallback, dead-lettering, drop/failure counters, batched retention delete, graceful drain on shutdown.

**INFERENCE — the per-row duplicate fallback is not optional and is easy to omit.** `AddRange` + one `SaveChangesAsync` means a single duplicate key fails the entire batch of 100. Adding the unique constraint without the fallback would create a *new* 99-event-wide loss path — strictly worse than the problem it was added to fix.

## A.4 — Aggregation and snapshots

Deploy `AnalyticsAggregationService` and `StateSnapshotService`. Both write only to new tables that nothing reads yet.

**RECOMMENDATION — start `StateSnapshotService` on the first possible deployment, ahead of everything else in this phase that is not blocking.**

**INFERENCE** — it is the smallest job in the programme (five `COUNT` queries on a timer) and the only one whose missed runs are unrecoverable. Every read model except RM-5 can be backfilled from raw events; pending queue depth cannot. Each day of delay is a permanent hole in a series operations will eventually need.

## A.5 — Backfill

Run the backfill job per `17-`: RM-1, RM-2 (partial), RM-3 (full history), RM-4 (onboarding only).

**INFERENCE — RM-3 is the standout.** Because host metrics derive from `Rooms.HostId` and `Rooms.CreatedAt` — relational data that is never purged — the supply-side series can be reconstructed from the platform's first day, not just 180 days. That gives Cocorra's leading indicator a longer baseline than anything else in the system, from one backfill run.

## A.6 — Host exclusion correction (the one early user-visible change)

**RECOMMENDATION** — apply host exclusion to `Participation` and `ActiveVsPassive`, and remove Top Speakers, in Phase A rather than waiting for cutover.

**FACT** — this is a query-layer change with no schema or event dependency.

**INFERENCE — the justification for breaking the parallel-running rule here.** TRUST-01 is the only case where Cocorra's data does not merely fall silent but actively contradicts itself: the same passive host is reported as both the platform's #1 speaker and a silent listener, in two panels of the same dashboard. Absence of data is survivable; a confidently wrong leaderboard is not. The Active-vs-Passive rate will visibly shift when hosts are excluded — that change should be announced to dashboard users as a correction, with the reason stated.

## Phase A exit criteria

- [ ] R-1, R-2, R-3 measured and recorded
- [ ] `EventId` deployed, backfilled, unique constraint active
- [ ] Flush retry + per-row fallback + dead-letter verified against SQLite/SQL Server
- [ ] `StateSnapshotService` running and producing daily rows
- [ ] Aggregation running; RM-1/RM-2/RM-3/RM-4 populated
- [ ] Backfill complete and reconciled against live queries
- [ ] Host exclusion applied; Top Speakers removed
- [ ] No change to any other user-visible metric

---

# Phase B — Parallel Data Collection

**Goal**: emit new events and compute new metrics alongside the existing ones, without changing what the dashboard shows.

## B.1 — P0 room events (server-only)

Deploy E-01…E-08 in `RoomHub`, plus E-09 (`room_went_live`) and E-10 (`user_status_changed`).

**FACT** — all eight core-loop events land in `RoomHub` methods that already save to the database and already have `IEventTracker` injected. No client dependency.

**FACT** — E-10 requires the one signature change in the programme: `adminId` threaded into `IAdminService.ChangeUserStatusAsync`. The controller already has the value in both the single (`AdminController.cs:54`) and bulk (`AdminController.cs:92`) paths.

**RECOMMENDATION — deploy in two increments**, watching the channel drop counter between them:
1. Low-frequency: `stage_promoted`, `stage_demoted`, `user_kicked`, `extra_time_granted`, `speaker_time_exhausted`, `room_went_live`, `user_status_changed`
2. High-frequency: `hand_raised`, `hand_lowered`, `mic_deactivated`

**INFERENCE** — the second group scales with engagement, so it lands hardest on the busiest rooms. Splitting the deployment isolates the volume risk to a change that can be reverted independently.

## B.2 — Server-side event extensions

`room_joined` gains `isHost` and `isRejoin` (both computable in-place — `RoomHub.JoinRoom` already loads `room` and already branches on `participant.Status == Left`). `room_ended` gains `actualDurationSeconds`, `endReason`, `peakParticipants`.

`entrySource` waits for the Flutter release and defaults to `"direct"` until then.

**RECOMMENDATION** — increment `SchemaVersion` for both extended events. **INFERENCE** — without it, a pre-extension `room_joined` row is indistinguishable from one where the client failed to supply `entrySource`, and the discovery metrics would silently mix the two.

## B.3 — Session signal comparison

Deploy `app_session_started` / `app_session_ended` (client) **alongside** the existing `session_started`. Do not deprecate the cookie signal yet.

**INFERENCE — this is the one place where the plan deliberately withholds judgement.** `06-blind-spots.md` and `13-` both argue on strong grounds that cookie-based sessions are unreliable on a Flutter client, but that remains an inference. R-4 plus a parallel comparison converts it into a measurement. Deprecating on the strength of the inference alone would repeat the pattern this programme exists to correct: acting confidently on unvalidated evidence.

## B.4 — New endpoints, additive only

Deploy Groups A–G from `19-` as **new routes**. Existing routes remain untouched.

**INFERENCE** — additive routes mean the dashboard repository can adopt them page by page, at its own pace, with no coordinated release. This is what makes the two-repository split manageable rather than a scheduling problem.

## Phase B exit criteria

- [ ] P0 events emitting; drop counter stable across both increments
- [ ] New endpoints deployed and returning data with populated `Meta`
- [ ] Both session signals collecting in parallel
- [ ] Old endpoints unchanged and still serving the live dashboard
- [ ] Aggregation lag within threshold under the new event volume

---

# Phase C — Validation

**Goal**: prove the new metrics are correct before anyone depends on them.

## C.1 — Reconciliation

For every metric with both an old and new implementation, compute both over identical windows and record the difference **with an explanation**.

| Metric | Expected relationship | Why |
|---|---|---|
| Active vs Passive | New rate **higher** | Hosts removed from the denominator (TRUST-01) |
| Onboarding funnel | New counts **≤** old at every step | Sequential ordering is strictly more restrictive (TRUST-06) |
| Return rate vs retention | New **≥** old | "Any later week" is strictly more inclusive than "exactly day N" (TRUST-03) |
| Registration counts | **Identical** | Unchanged computation — a control |
| Report counts | **Identical** | Unchanged computation — a control |

**RECOMMENDATION — an unexplained difference blocks cutover.** **INFERENCE** — the two controls matter as much as the three changes. If registration or report counts differ between old and new paths, something is wrong in the read-model pipeline itself, and no amount of correctness in the changed metrics would compensate.

## C.2 — Aggregation reconciliation

Read-model values must equal a direct live query over the same window, within a documented tolerance.

**RECOMMENDATION** — run daily during Phase C and alert on divergence. **INFERENCE** — a rollup that drifts from its source is the failure mode most likely to go unnoticed, because the number stays plausible.

## C.3 — Event completeness

| Check | Method |
|---|---|
| `room_joined` events vs `RoomParticipant` rows | Divergence beyond reconnect inflation indicates loss |
| `mic_activated` / `mic_deactivated` pairing | Orphan deactivations should occur only for the host's initial mic (`wasInitialHostMic = true`) |
| `hand_raised` / `hand_lowered` pairing | Unterminated raises should be rare and explainable |
| `room_went_live` per live room | Every room reaching `Live` should have exactly one |
| Dead-letter table | Should be empty or explainable |

## C.4 — Session signal comparison

Compare `session_started` against `app_session_started` per user per day.

**Decision rule (RECOMMENDATION)** — deprecate the cookie signal only if the new one demonstrates materially better per-user consistency. If they agree closely, the cookie hypothesis was wrong and `session_started` stays.

## C.5 — Metric contract enforcement

Assert every metric served by a new endpoint has a `MetricRegistry` entry with all four mandatory fields (business purpose, technical definition, formula, validation method). A metric without one must not reach the dashboard.

## Phase C exit criteria

- [ ] Every difference between old and new reconciled and explained
- [ ] Control metrics identical across both paths
- [ ] Read models reconcile against live queries within tolerance
- [ ] Event pairing checks pass
- [ ] Dead-letter table empty or explained
- [ ] Session comparison complete; decision recorded
- [ ] Every new metric has a complete contract

---

# Phase D — Cutover

**Goal**: the new dashboard becomes authoritative.

**Prerequisite**: Phase C exit criteria met **and** 4–6 weeks of stable read-model history accumulated.

**INFERENCE — the history requirement is not padding.** `20-` gates the Decision Center and the cohort grid on baseline accumulation, and `09-` establishes that detection without a baseline produces alerts on ordinary variance. A dashboard that cries wolf in its first month is ignored permanently, and that is harder to reverse than a delayed launch.

## Cutover sequence

**RECOMMENDATION** — page by page, not all at once.

| Step | Page | Why this order |
|:--:|---|---|
| 1 | Page 9 — Trust Register | Before anyone relies on a number, they must be able to check it. Also forces every metric to have a contract. |
| 2 | Page 2 — Supply Health | Entirely new; nothing to displace. Largest single improvement over the current dashboard. |
| 3 | Page 5 — Safety | Adds the category cut to an already-VERIFIED metric. Highest-stakes analysis. |
| 4 | Page 3 — Activation | Replaces the non-sequential funnel |
| 5 | Page 1 — Platform Health | Replaces the composite Summary |
| 6 | Pages 7, 8 — Social, Reliability | Additive |
| 7 | Page 6 — Return & Repeat | Once 8 weeks of cohort history exist |
| 8 | Page 4 — Room Participation | Once P0 events have 4 weeks of stable emission |
| 9 | Page 0 — Decision Center | Last. Requires everything above. |

**INFERENCE on starting with pages 2 and 5** — both are purely additive: they replace nothing and displace no existing habit. Cutover risk is concentrated in steps 4 and 5, where numbers people already read will change. Doing the additive work first builds confidence in the new surface before asking anyone to accept a revised figure.

## Communicating changed numbers

**RECOMMENDATION** — for each replaced metric, tell dashboard users what changed and why, before the number moves.

| Metric | Message |
|---|---|
| Active vs Passive | "Room hosts were being counted as passive listeners. They are now excluded. The rate will appear higher." |
| Onboarding funnel | "Steps were counted independently, so the funnel could widen. It is now sequential; counts will be lower and monotonic." |
| Retention | "Retention counted users active on exactly day N. Replaced with weekly return based on room joins; the number will be higher and means something different." |
| Top Speakers | "Removed. It ranked hosts by how long their rooms ran, not by speaking." |
| Avg Room Duration | "Removed. It averaged the configured 2-or-3-hour setting, not actual duration." |

**INFERENCE** — an unexplained metric change destroys trust faster than a wrong metric, because the reader concludes the numbers are arbitrary. Since the entire programme is about trust, the communication is part of the deliverable.

## Phase D exit criteria

- [ ] All nine pages cut over
- [ ] Trust badges rendering correctly per `20-`
- [ ] "Not measured" states rendering as gaps, never zeros
- [ ] Freshness indicator live
- [ ] Metric changes communicated
- [ ] No unexplained discrepancies reported

---

# Phase E — Deprecation

**Goal**: remove the unreliable metrics and the code paths behind them.

**Prerequisite**: Phase D complete and stable for at least two weeks.

## Removal sequence

| Step | Action |
|:--:|---|
| 1 | Old endpoints return `410 Gone` with a pointer to the successor (per `19-`) |
| 2 | Monitor for callers over ~2 weeks |
| 3 | Remove the deprecated repository methods, DTO fields, and route constants |
| 4 | Remove `session_started` emission **only if** C.4 concluded against it |

**RECOMMENDATION on step 1** — `410 Gone` rather than silent removal. **INFERENCE** — a removed endpoint returning `404` is indistinguishable from a routing bug; `410` with a successor pointer tells the caller what happened and where to go.

**FACT — R-8 already handles the field-level case.** Deprecated *fields* were removed from responses at cutover, not hidden, so a consumer reading them fails loudly rather than silently reading a wrong value.

## What is never removed

| Kept | Reason |
|---|---|
| `/Admin/Dashboard/Stats` | Sound as a snapshot; now supplemented by an RM-5 history series |
| `/Analytics/Rooms/Active` | VERIFIED; only the guidance changes (`UniqueJoiners`, not `JoinEvents`) |
| Registration counts | Correct; only the status breakdown was replaced |
| Report counts and category mix | The one VERIFIED metric in the original dashboard |
| Raw `UserEvents` | Remains the authoritative store (`16-`) |

---

# Feature Flags

## Are they needed?

**RECOMMENDATION — yes, but narrowly. Two flags, not a framework.**

**FACT** — no feature-flag infrastructure exists anywhere in the solution: no flags, no variant assignment, no experiment table, no bucketing logic.

**INFERENCE — why most of this plan does not need flags.** The additive design already provides the safety a flag would: new tables nothing reads, new endpoints nothing calls, new events nobody queries. A flag adds a branch and a configuration surface to protect changes that are already inert until something consumes them. Building general flag infrastructure for this programme would be solving a problem the sequencing already solved.

**Two exceptions**, both where a change is *not* inert:

### Flag 1 — `Analytics:EnableNewEventEmission`

**What it gates** — the P0 room events (E-01…E-08).

**Why (INFERENCE)** — these are the only changes in the programme that add load to a shared, bounded resource: the event channel, which currently drops on overflow. If the drop counter spikes after deployment, the fix must be available in seconds, without a rollback deployment that would also revert unrelated work.

**Rollback** — set to false; emission stops; existing events continue unaffected.

**Granularity (RECOMMENDATION)** — separate the high-frequency subset (`hand_raised`, `hand_lowered`, `mic_deactivated`) so it can be disabled independently of the low-frequency events, matching the two-increment deployment in B.1.

### Flag 2 — `Analytics:UseReadModels`

**What it gates** — whether the new endpoints read from read models or fall back to live queries.

**Why (INFERENCE)** — if aggregation lags or produces a bad rollup, the endpoints can serve correct-but-slower live queries instead of stale or wrong pre-aggregated values. It converts an aggregation failure from a correctness problem into a performance problem, which is the better failure.

**Rollback** — set to false; endpoints revert to live queries; aggregation continues in the background and can be rebuilt.

## Explicitly not flagged

| Change | Why no flag |
|---|---|
| Schema additions | Additive, nullable; nothing to toggle |
| Flush retry / dead-letter | A strict improvement. **INFERENCE** — a flag here would offer the option of reverting to silent data loss, which is not a state worth being able to return to. |
| `StateSnapshotService` | Writes only to a new table; harmless if unread |
| New endpoints | Inert until called |
| Host exclusion | A correctness fix. A flag would mean keeping a self-contradicting metric one toggle away. |

**INFERENCE** — the discipline here matters. Flagging everything produces a codebase where no path is known to be live, which is its own reliability problem. Two flags on the two genuinely risky changes is the right amount.

---

# Rollback

| Failure | Detection | Rollback | Data impact |
|---|---|---|---|
| Channel drops spike after P0 events | Drop counter | Disable Flag 1 (high-frequency subset first) | New events missing for the window; existing events unaffected |
| Aggregation produces wrong values | C.2 daily reconciliation | Disable Flag 2; truncate and rebuild the affected read model | None — INV-5 guarantees rollups are recomputable from raw events |
| Flush retry causes duplicates | Duplicate-count query | Should be impossible with `UNIQUE(EventId)`; if it occurs, the constraint is missing | Duplicates are identifiable by `EventId` and removable |
| Retention batch delete blocks ingestion | Flush latency / drop counter | Reduce `CleanupBatchSize`; increase inter-batch delay | None |
| `user_status_changed` signature change breaks admin flows | Admin status changes failing | Revert the deployment | Status changes unrecorded for the window — **unrecoverable** (see below) |
| New endpoint errors | Error rate | Endpoints are additive; the old dashboard is unaffected | None |
| Dashboard cutover confusion | User reports | Revert the dashboard repository page by page; the API is additive | None |

**INFERENCE — one rollback is genuinely lossy and deserves extra care.** `user_status_changed` is the only record of a status transition: `ApplicationUser` has no `UpdatedAt` and no history table exists (`15-`, data-loss risk HIGH). A rollback window means those transitions are permanently unrecoverable. **RECOMMENDATION** — deploy this event with more validation than its size suggests, and treat the dead-letter path as a prerequisite rather than a follow-up.

---

# Summary

| Phase | Goal | User-visible change | Gate |
|---|---|---|---|
| **A — Preparation** | Schema, hardening, aggregation, backfill | **One**: host exclusion + Top Speakers removed | R-1/R-2/R-3 measured |
| **B — Parallel collection** | New events and endpoints alongside old | None | Drop counter stable |
| **C — Validation** | Prove new metrics correct | None | All differences explained |
| **D — Cutover** | New dashboard authoritative | Page by page, communicated | 4–6 weeks history |
| **E — Deprecation** | Remove unreliable metrics | Old endpoints `410 Gone` | 2 weeks stable |

**Three conclusions (INFERENCE).**

**The additive design is what makes this safe.** New tables, new endpoints, new events — all inert until something reads them. That is why two feature flags suffice where a larger programme might need a framework.

**One correction should not wait for the migration.** Host exclusion is a query change that removes the dashboard's only active self-contradiction. Running a metric that reports the same person as both top speaker and silent listener through four more phases is not defensible when the fix requires no deployment coordination.

**The gate on Phase D is history, not code.** Everything can be built, validated, and deployed and the Decision Center still should not ship until a baseline exists. Detection without a baseline generates false alarms, and a dashboard that is ignored is worse than one that is late.
