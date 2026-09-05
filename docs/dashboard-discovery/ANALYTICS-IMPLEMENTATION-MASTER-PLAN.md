# Cocorra — Analytics Implementation Master Plan

> **Generated**: 2026-09-01 | **Repository state**: `main` @ `c13f1f6`, working tree clean except untracked `docs/`
> **Scope**: Planning and architecture. **No application code, database schema, migrations, or packages were modified.**
> **Audience**: the engineer or team executing the implementation phase.

---

# 1. Current State

## What exists and works

**FACT** — Cocorra's analytics infrastructure is well designed and under-hardened. These are genuinely good decisions already in place:

| Component | Evidence | Assessment |
|---|---|---|
| Non-blocking event producer | `EventTracker.Track` — try/catch with *"Tracking must NEVER throw back to the user"* | Correct contract |
| Room-id promotion | `ExtractRoomId` promotes `roomId` from JSON into an indexed column | The single most important performance decision, already made |
| Index design | `IX(EventType,OccurredAtUtc)`, `IX(UserId,OccurredAtUtc)`, `IX(RoomId,EventType,OccurredAtUtc)` | Directly serves the target queries |
| Deletion policy for events | `OnDelete(DeleteBehavior.SetNull)` on `UserEvent.UserId` | Events survive user deletion |
| Client-event allowlist | `EventsController.cs:22` — 3 permitted events | Prevents clients forging the funnel |
| Cache stampede protection | 11 `SemaphoreSlim` guards + double-checked locking | Correct for a single instance |
| Startup config guard | `Program.cs:205-207` throws if `Analytics:IpHashSalt` is missing | Fail-fast, no insecure fallback |
| Trust transport | `Response<T>.Meta` — present on every response, **currently always null** | A ready-made vehicle for metric metadata |

## What is broken

**FACT** — five defects, verified at HEAD:

| # | Defect | Location |
|:--:|---|---|
| **D-1** | **Host mic open from room start.** A silent host accrues the room's full 2–3 hours as `TotalSpokenSeconds` while emitting no `mic_activated`. Top Speakers ranks coaches by room length; Active-vs-Passive counts those same coaches as passive listeners. | `RoomService.cs:115-127, 439-449`; `RoomHub.cs:518-521` |
| **D-2** | **Two silent data-loss paths.** Channel `DropWrite` on overflow, **and** `batch.Clear()` in a `finally` discarding up to 100 events on any DB fault, with no retry or dead-letter. | `Program.cs:210-211`; `EventFlushService.cs` |
| **D-3** | **Growth history rewritten.** Users bucketed by `CreatedAt`, counted by **current** `Status`. Distortion grows with bucket age. | `AnalyticsRepository.cs:21-93` |
| **D-4** | **Retention wrong twice.** Exact-day matching (`timeDiff.Days == day`) over a cookie-based signal on a Flutter client, with an unbounded activity fetch. | `AnalyticsRepository.cs:324-392` |
| **D-5** | **Funnel is not a funnel.** Steps counted independently; the result can widen downward. | `AnalyticsRepository.cs:300-322` |

## What is missing

**FACT** — no aggregation layer, no idempotency key, no read models, no state history, no trust metadata, no host-side analytics, no support analytics, no media telemetry, no error tracking, no experimentation.

## Trust verdict

**NOT TRUSTED FOR MAJOR DECISIONS.** Of twelve shipped metrics, **one** (Report Insights) is VERIFIED. Three are UNRELIABLE. All twelve render identically.

**INFERENCE** — the failure is not that some metrics are wrong; every analytics system has wrong metrics. It is that **nothing distinguishes them**. D-1 is the sharpest case: the same passive host appears as both the platform's #1 speaker and a silent listener, in two panels of the same dashboard.

---

# 2. Target State

A decision-driven analytics platform where:

- Every metric has an executable contract with a formula, exclusions, limitations, and a validation method.
- Every API response carries trust metadata, so a reader can tell a sound number from a caveated one.
- Metrics are pre-aggregated into read models retained **indefinitely** — giving Cocorra trend history beyond the 180-day raw window for the first time.
- The event pipeline is durable: retry, dead-letter, idempotency key, observable counters.
- The core loop is instrumented end to end, so *why* a conversion moved is answerable, not just *that* it moved.
- Uninstrumented periods render as **visible gaps**, never as zero.

## The North Star

**Weekly Participating Users (WPU)** — distinct non-host users with a `room_joined` event in a rolling 7-day window. Reported alongside Speaking Conversion, Rooms Gone Live, Distinct Active Hosts, and Weekly Return Rate.

**INFERENCE** — chosen because it is the only candidate that both represents Cocorra's actual value event and rests on a source the audit independently verified: server-emitted, indexed, cookie-independent, and untouched by any of D-1 through D-5.

---

# 3. Architecture

```
DOMAIN ACTION            RoomService · RoomHub · AdminService · AuthServices
                         SupportService · FriendService · ChatService          [MODIFY — emit sites]
      ↓
EVENT PRODUCER           IEventTracker / EventTracker                          [MODIFY — EventId, context]
      ↓
RELIABLE PIPELINE        Channel<UserEvent> + EventFlushService                [MODIFY — retry, dead-letter]
      ↓
RAW EVENT STORE          dbo.UserEvents (+ EventId, SchemaVersion,             [EXTEND — 3 columns]
                         CorrelationId) · EventCleanupService                  [MODIFY — batched purge]
      ↓
AGGREGATION              AnalyticsAggregationService · StateSnapshotService    ★ NEW
      ↓
READ MODELS              DailyPlatformMetrics · DailyRoomMetrics               ★ NEW
                         DailyHostMetrics · DailyFunnelMetrics                 (indefinite retention)
                         DailyStateSnapshots
      ↓
VERIFIED METRICS         IMetricRegistry — contracts in code                   ★ NEW
      ↓
DASHBOARD API            AnalyticsRepository v2 · AnalyticsService             [MODIFY]
                         AnalyticsController · Response<T>.Meta                [REUSE — trust transport]
      ↓
DECISION DASHBOARD       admin.cocorraapp.com                                  [separate repo, contract only]
```

## Key architectural decisions

| Decision | Verdict | Why |
|---|:--:|---|
| Keep `UserEvents` as the raw store | **KEEP AND EXTEND** | Three well-chosen indexes, working promotion logic, sound deletion policy. A replacement would rewrite 11 repository methods to solve problems three columns solve. |
| Dedicated analytics database | **REJECTED** | Adds a datastore, sync path, and schema drift. No data-engineering function; no measured performance problem. |
| Durable message broker | **REJECTED** | Retry + dead-letter closes the same loss path without new infrastructure. Single API container. |
| Third-party analytics SDK | **REJECTED** | Sends behavioural data to an external processor. **INFERENCE** — `MentalHealth` rooms and voice recordings raise the bar considerably. |
| Hangfire / Quartz | **REJECTED** | Replaces a `BackgroundService` pattern working twice in-repo with a dependency and a job store. |
| Trust metadata transport | **`Response<T>.Meta`** | Already on every response, already accepted by `ResponseHandler`, currently null. Purely additive. |

**INFERENCE — the reuse rate is the plan's main strength.** Of nine architectural layers, five are reused or modified in place and four are new. Nothing is replaced.

---

# 4. P0 Fixes

Thirteen items. **Six require no application code change.**

| ID | Fix | Code change? | Blocks |
|---|---|:--:|---|
| **AN-001** | Measure R-1/R-2/R-3 (drops, failures, volume) | **No** | Pipeline decisions |
| **AN-002** | `EventId` + `SchemaVersion` + `CorrelationId` | Schema | **Everything in the pipeline** |
| **AN-003** | Flush retry + per-row duplicate fallback + dead-letter | Yes | — |
| **AN-004** | Batched retention purge, configurable | Yes | Precedes event volume increase |
| **AN-005** | **Host exclusion; remove Top Speakers** | **Query only** | — |
| **AN-006** | Replace retention with room-join-based return | **Query only** | — |
| **AN-007** | Sequential funnel | **Query only** | — |
| **AN-008** | Split growth; reconstruct status history | **Query only** | — |
| **AN-009** | `StateSnapshotService` | New service | — |
| **AN-010** | Fix `activation_completed` dedup race | Yes | AN-002 |
| **AN-011** | Emit `user_status_changed` | Yes (+1 signature) | AN-002 |
| **AN-012** | `IMetricRegistry` + trust metadata | Yes | AN-005…008 |
| **AN-013** | **Soft delete** | Yes | **Data-protection decision** |

**RECOMMENDATION — AN-005 ships first among the corrections.** It is a query-layer filter with no schema, event, or deployment dependency, and it removes the only place where Cocorra's data actively contradicts itself. Running a metric that reports the same person as both top speaker and silent listener through a multi-phase migration is not defensible when the fix requires no coordination.

---

# 5. Implementation Phases

| Phase | Goal | User-visible change | Exit gate |
|---|---|---|---|
| **A — Preparation** | Measurement, schema, pipeline hardening, aggregation, backfill | **One**: host exclusion applied, Top Speakers removed | R-1/R-2/R-3 recorded; `EventId` live; backfill reconciled |
| **B — Parallel collection** | New events and endpoints alongside existing ones | None | Drop counter stable across both event increments |
| **C — Validation** | Prove new metrics correct | None | Every old-vs-new difference explained; controls identical |
| **D — Cutover** | New dashboard authoritative | Page by page, communicated in advance | 4–6 weeks of stable history |
| **E — Deprecation** | Remove unreliable metrics | Old endpoints → `410 Gone` | 2 weeks stable |

## Wave-level execution order

| Wave | Items |
|:--:|---|
| **0** | **AN-009** · AN-001 · raise the data-protection question · add SQLite to `Cocorra.Tests` |
| **1** | AN-005, AN-006, AN-007, AN-008 (query corrections) · AN-002 (schema) |
| **2** | AN-003, AN-004 (pipeline) · AN-014, AN-015 (aggregation — **starts the history clock**) · AN-012 |
| **3** | AN-016 backfill · AN-020…AN-023 endpoints · AN-017 low-frequency events |
| **4** | AN-018 high-frequency events · AN-019 · AN-024, AN-025 |
| **5** | Dashboard cutover · P2 items |
| **6** | AN-039 Decision Center · P3 |

**INFERENCE on Wave 0** — it contains no feature work. One tiny service, one measurement, one question, one project-file line. Each removes a constraint that compounds if deferred, which is precisely why they must not sit in a backlog queue.

---

# 6. Critical Path

```
AN-001 → AN-002 → AN-014 → AN-015 → ══ 4–6 WEEKS ══ → AN-039
                    (aggregate existing events immediately)

parallel:  AN-003 → AN-004 → AN-017 → AN-018 → AN-027
```

**INFERENCE — the important reordering.** The Decision Center is gated on **history**, not on complete instrumentation. Starting aggregation over *existing* events immediately after `EventId` lands begins the 4–6 week clock roughly four items earlier than a naive chain would. Since that wait cannot be compressed by effort, starting it sooner is the single largest available reduction in total elapsed time. The event work moves onto a parallel track, and the stage funnel arrives later — which is correct, as it is one page of nine.

## Blockers

| ID | Blocker | Nature | Notes |
|---|---|---|---|
| **B-1** | Runtime measurement (AN-001) | External observation | Log grep + 2 read-only queries |
| **B-2** | `EventId` unique constraint | Schema | **Hard.** Retry without it creates duplicates instead of preventing loss |
| **B-3** | **Data-protection decision** | **Non-engineering** | **Cost of delay is irreversible** — raise on day one |
| **B-4** | History accumulation | Time | 4–6 weeks; compressible only by starting earlier |
| **B-5** | Test provider | One project line | `EFCore.InMemory` does not enforce unique indexes — idempotency tests would pass vacuously |

---

# 7. Parallel Work

Four independent tracks. **12 of 45 items can start on day one.**

| Track | Items | Blocked by | Character |
|---|:--:|---|---|
| **1 — Pipeline** | 7 | B-1, B-5 | Schema + background services; highest risk |
| **2 — Query corrections** | 6 | **Nothing** | Pure queries over verified data; zero deployment risk |
| **3 — Read models** | 7 | AN-002 | Aggregation, backfill, endpoints |
| **4 — Independent singles** | 6 | Mostly nothing | AN-009, AN-013, AN-024, AN-032, AN-037, AN-042 |

**INFERENCE — the dependency graph has three roots, not one chain.** Track 2 has **no dependency on Track 1 at all**. Reading the plan as a single sequence would serialise the six items that fix all three UNRELIABLE metrics and expose the two highest-value missing analyses behind schema work they do not need.

---

# 8. Data Migration

| Data | Treatment | Justification |
|---|---|---|
| Existing `UserEvents` | **KEEP.** Backfill `EventId` in batches | Additive; unique constraint applied after backfill |
| `DailyHostMetrics` (RM-3) | **FULL BACKFILL — entire history** | **FACT** — derives from `Rooms.HostId`/`CreatedAt`, relational and never purged |
| `DailyPlatformMetrics` (RM-1) | **FULL BACKFILL — 180 days** | Source events exist |
| `DailyRoomMetrics` (RM-2) | **PARTIAL** | Joiners/speakers/reports yes; hand raises, promotions, speaking seconds **no** — never captured |
| `DailyFunnelMetrics` (RM-4) | **PARTIAL** | Onboarding funnel yes; stage funnel **no** |
| `DailyStateSnapshots` (RM-5) | **NO BACKFILL** | Pure state, structurally unrecoverable |
| Historical user status | **RECALCULATE ≤180 days** from `voice_verification_result` | Beyond that, unrecoverable |
| Top Speakers history | **CANNOT RECOVER** | Conflates real speaking with idle open-mic time |
| Deleted users | **CANNOT RECOVER** | Hard-deleted rows |

**RECOMMENDATION — never fabricate historical events.** Metrics with no historical source return `dataAvailableFromUtc` and render as visible gaps.

**INFERENCE** — RM-3 is the standout: Cocorra's leading indicator can be reconstructed from the platform's first day, giving supply health a longer baseline than any other metric, from a single backfill run.

---

# 9. Validation

**70 tests across seven categories**, traceable from each defect to the tests that prove it fixed.

## The decisive tests

| Test | Assertion | Proves |
|---|---|---|
| **#4** | No `UserId` appears in both the speaker set and the passive set for the same window | **D-1** — the executable form of the contradiction; fails today by construction |
| **#6** | Each funnel step's count ≤ the previous step's | **D-5** — impossible to satisfy under the current implementation |
| **#31** | A 100-event batch with 1 duplicate persists 99 rows and does **not** fail | **D-2** — prevents the fix becoming a 99-event-wide regression |
| **#5** | A user active on days 2 and 5 counts as returned | **D-4** |
| **#61** | Cumulative registrations never decrease | **Hard deletes** — works even before soft delete ships |
| **#46** | An uninstrumented period returns `null`, **never `0`** | Prevents fabricated findings |

## Continuous production invariants

Ten checks running permanently, surfaced through `GET /Analytics/System/Health`.

**INFERENCE** — this matters more for Cocorra than it would elsewhere. **FACT** — no structured logging sink, no APM, no metrics export; errors reach Docker stdout with 10MB/3-file rotation. A failing invariant written only to container logs is one nobody sees. Routing these through the analytics API uses the one observability surface that exists and that people already look at.

## Test provider constraint

**FACT** — `Microsoft.EntityFrameworkCore.InMemory` does not enforce unique indexes or `DeleteBehavior`.

**INFERENCE** — the entire idempotency guarantee rests on a database constraint the default test provider cannot observe. Eight tests would pass vacuously. Adding SQLite is one line and determines whether those tests mean anything. A provider-guard test should fail loudly if anyone reverts.

---

# 10. Rollout

**Page-by-page cutover**, ordered so additive work builds confidence before any existing number changes:

1. Trust Register — before anyone relies on a number, they must be able to check it
2. **Supply Health** — entirely new; displaces nothing; largest single improvement
3. Safety — adds the category cut to an already-VERIFIED metric
4. Activation — replaces the non-sequential funnel
5. Platform Health — replaces the composite Summary
6. Social, Reliability — additive
7. Return & Repeat — after 8 weeks of cohort history
8. Room Participation — after 4 weeks of stable P0 events
9. Decision Center — last

## Feature flags

**Two, not a framework.**

| Flag | Gates | Why |
|---|---|---|
| `Analytics:EnableNewEventEmission` | P0 room events, split high/low frequency | The only changes adding load to a shared bounded resource |
| `Analytics:UseReadModels` | Read-model vs live-query reads | Converts an aggregation failure from a correctness problem into a performance one |

**INFERENCE** — most of this plan needs no flags because the additive design already provides the safety: new tables nothing reads, new endpoints nothing calls, new events nobody queries. Flagging everything produces a codebase where no path is known to be live, which is its own reliability problem.

## Communicating changed numbers

**RECOMMENDATION** — for each replaced metric, tell dashboard users what changed and why **before** the number moves. **INFERENCE** — an unexplained metric change destroys trust faster than a wrong metric, because the reader concludes the numbers are arbitrary. Since the whole programme is about trust, the communication is part of the deliverable.

---

# 11. Risks

| # | Risk | Severity | Mitigation |
|:--:|---|:--:|---|
| **R-1** | High-frequency events overwhelm the bounded channel | **HIGH** | AN-001 measurement first; two-increment deployment; separate flag |
| **R-2** | `EventId` unique constraint fails whole batches | **HIGH** | Per-row duplicate fallback (test #31) — **without it the fix is worse than the defect** |
| **R-3** | Soft delete's global query filter breaks unrelated flows | **HIGH** | Audit every `ApplicationUser` query including `UserManager`; scope as an application-wide change |
| **R-4** | `user_status_changed` rollback loses transitions permanently | **HIGH** | Only record of the transition — no `UpdatedAt`, no history table. Extra validation; dead-letter as prerequisite |
| **R-5** | Backfilled rows diverge from live-aggregated rows | **MEDIUM** | Share the code path; test #47 asserts byte-identity |
| **R-6** | Idempotency tests pass vacuously | **MEDIUM** | SQLite + a provider-guard test |
| **R-7** | Batched purge still blocks ingestion | **MEDIUM** | Tunable batch size and delay; test #44 |
| **R-8** | Decision Center ships without a baseline | **MEDIUM** | Hard gate on 4–6 weeks; a dashboard that cries wolf is ignored permanently |
| **R-9** | Frontend ignores trust metadata | **HIGH** | **INFERENCE** — if the API returns `trustLevel` and the UI renders every number identically, this programme produces a *faster wrong dashboard*. Test #3 (not-measured state) is the minimum bar |
| **R-10** | Data-protection decision never arrives | **MEDIUM** | Raise day one; evidence loss compounds daily |

---

# 12. Rollback Strategy

| Failure | Detection | Rollback | Data impact |
|---|---|---|---|
| Channel drops spike | Drop counter | Disable event flag (high-frequency subset first) | New events missing for the window |
| Aggregation produces wrong values | Daily reconciliation | Disable `UseReadModels`; truncate and rebuild | **None** — INV-5 guarantees recomputability from raw events |
| Retry causes duplicates | Duplicate-count query | Should be impossible with `UNIQUE(EventId)`; if it occurs the constraint is missing | Duplicates identifiable by `EventId` |
| Purge blocks ingestion | Flush latency | Reduce batch size; increase delay | None |
| `user_status_changed` breaks admin flows | Status changes failing | Revert deployment | **Transitions in the window unrecoverable** |
| Soft delete breaks login/roles | Auth failures | Remove query filter; restore hard delete | Soft-deleted rows need handling |
| New endpoint errors | Error rate | Additive — old dashboard unaffected | None |
| Cutover confusion | User reports | Revert dashboard page by page | None |

**INFERENCE** — the additive design means most rollbacks are free. The two exceptions are `user_status_changed` (lossy) and soft delete (wide blast radius), and both are flagged for extra validation.

---

# 13. Definition of Done

The analytics platform is complete when:

- [ ] **Critical metrics have contracts** — all 22 in `IMetricRegistry` with business purpose, technical definition, formula, and validation method. A metric without one fails the build.
- [ ] **P0 metrics are verified** — D-1 through D-5 corrected; tests #4 and #6 green; no metric in the served set is UNRELIABLE.
- [ ] **Event contracts are implemented** — P0 core-loop events emitting; `stage_promoted.UserId` is the promoted participant; `mic_deactivated` fires from all three close sites.
- [ ] **Duplicate handling is tested** — tests #29–#36 green **on SQLite or SQL Server**, including the 99-of-100 mixed-batch case.
- [ ] **Event failure does not break product functionality** — INV-1 asserted; `Track` never throws on a full channel; all emits occur after the domain write succeeds.
- [ ] **Aggregations are reliable** — read models reconcile against live queries; re-running produces byte-identical rows; the watermark only advances after commit.
- [ ] **Dashboard metrics have traceable sources** — every widget traces to a metric, an endpoint, and a read model or query.
- [ ] **Analytics APIs are tested** — every endpoint returns populated `Meta.metrics`; registry and per-response metadata agree.
- [ ] **Historical limitations are explicitly visible** — `dataAvailableFromUtc` returned; uninstrumented periods render as **visible gaps, never zero**.
- [ ] **Unreliable metrics are deprecated or marked** — Top Speakers, hand-raise count, `AvgDurationHours`, exact-day retention, and the status breakdown removed from responses; old endpoints `410 Gone`.
- [ ] **The dashboard supports real Cocorra decisions** — supply health, listener→speaker conversion, review latency, report rate by category, and activation→first-join are all answerable.

---

# Appendix — Document Index

| Document | Contents |
|---|---|
| `11-current-state-validation.md` | 30 findings re-verified at HEAD; 5 runtime observations required |
| `12-target-analytics-architecture.md` | Current vs target; REUSE/MODIFY/REPLACE/NEW per layer; 10 invariants |
| `13-data-trust-correction-plan.md` | 10 trust corrections with root cause, strategy, historical impact, validation |
| `14-metric-contracts.md` | 22 metric contracts; 7 deprecations |
| `15-event-implementation-contracts.md` | 28 events; idempotency classes; ordering; failure behaviour |
| `16-raw-event-storage-strategy.md` | KEEP AND EXTEND; 3 columns; retention policy |
| `17-read-models-and-aggregation.md` | 5 read models; hourly aggregation; backfill matrix |
| `18-background-processing-plan.md` | 4 jobs: 2 modified, 2 new |
| `19-analytics-api-blueprint.md` | 16 endpoints across 7 groups; trust metadata contract |
| `20-dashboard-implementation-blueprint.md` | 10 pages; widget tables; trust UX rules |
| `21-migration-rollout-plan.md` | Phases A–E; 2 feature flags; rollback matrix |
| `22-testing-validation-strategy.md` | 70 tests; provider matrix; continuous invariants |
| `23-execution-backlog.md` | 45 implementation-ready work items |
| `24-dependency-graph.md` | 3 roots; 5 blockers; critical path; 4 parallel tracks |

---

# The Three Things That Matter Most

**1. Six P0 items require no application code change, and they include the two highest-value corrections.**
Host exclusion, the return metric, the sequential funnel, the growth split, report-rate-by-category, and supply health are all queries over data the audit already verified as correct. They fix all three UNRELIABLE metrics, expose the platform's unwatched leading indicator, and carry no deployment risk. They depend on nothing and should start immediately.

**2. `StateSnapshotService` should be deployed before anything else, including the measurement item.**
It has zero dependencies and is five `COUNT` queries on a timer. It is also the only read model that **cannot be backfilled**: every day it does not run is a permanent hole in a series operations will eventually need. Every other item costs the same whenever it is done; this one gets more expensive daily.

**3. If the dashboard ignores the trust metadata, this programme produces a faster wrong dashboard.**
The current failure was never that some metrics were wrong — every analytics system has wrong metrics. It was that nothing distinguished them. The backend work is necessary and insufficient. `20-`'s UI rules — inline conditions for CONDITIONALLY RELIABLE metrics, and visible gaps rather than zeros for uninstrumented periods — are part of the deliverable, not a follow-up.
