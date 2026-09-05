# 24 — Implementation Dependency Graph

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 13
> **Depends on**: `23-execution-backlog.md` (item IDs), `21-migration-rollout-plan.md` (phases)
> **Scope**: Documentation only.

---

# The Graph

Cocorra's dependency structure is not the textbook chain (`event contracts → storage → pipeline → aggregation → read models → API → dashboard`). It has **three roots**, and only one of them is a chain.

```
                    ┌─────────────────────────────────────┐
                    │  AN-001  Runtime measurement        │  ← BLOCKER (external)
                    │  R-1 drops · R-2 failures · R-3 vol │
                    └──────────────┬──────────────────────┘
                                   │
        ┌──────────────────────────┼──────────────────────────────┐
        │                          │                              │
        ▼                          ▼                              ▼
┌───────────────┐        ┌──────────────────┐          ┌────────────────────┐
│  ROOT A       │        │  ROOT B          │          │  ROOT C            │
│  PIPELINE     │        │  QUERY           │          │  SNAPSHOT          │
│  (the chain)  │        │  CORRECTIONS     │          │  (time-critical)   │
└───────┬───────┘        └────────┬─────────┘          └─────────┬──────────┘
        │                         │                              │
        ▼                         │                              ▼
┌───────────────┐                 │                    ┌────────────────────┐
│ AN-002        │                 │                    │ AN-009             │
│ EventId       │  ★ HARD BLOCKER │                    │ StateSnapshot      │
│ SchemaVersion │                 │                    │ Service            │
│ CorrelationId │                 │                    │ (no dependencies)  │
└───┬───────┬───┘                 │                    └─────────┬──────────┘
    │       │                     │                              │
    ▼       ▼                     ▼                              │
┌────────┐ ┌────────┐   ┌──────────────────────┐                │
│ AN-003 │ │ AN-010 │   │ AN-005 host exclusion│                │
│ Flush  │ │ dedup  │   │ AN-006 return metric │                │
│ retry  │ │ fix    │   │ AN-007 seq. funnel   │                │
└───┬────┘ └───┬────┘   │ AN-008 growth split  │                │
    │          │        └──────────┬───────────┘                │
    ▼          │                   │                             │
┌────────┐     │                   ▼                             │
│ AN-004 │     │        ┌──────────────────────┐                │
│ Batched│     └───────►│ AN-012 MetricRegistry│◄───────────────┘
│ purge  │              │ + trust metadata     │
└───┬────┘              └──────────┬───────────┘
    │                              │
    ▼                              ▼
┌────────────────┐      ┌────────────────────────────────────┐
│ AN-017 events  │      │ AN-020 Supply · AN-021 Safety      │
│ (low freq)     │      │ AN-022 Latency · AN-023 Support    │
└───┬────────────┘      └────────────────────────────────────┘
    │                              ▲
    ▼                              │
┌────────────────┐      ┌──────────┴───────────┐
│ AN-018 events  │      │ AN-014 read models   │
│ (high freq)    │─────►│ AN-015 aggregation   │
│ AN-019 extend  │      │ AN-016 backfill      │
└───┬────────────┘      └──────────┬───────────┘
    │                              │
    ▼                              ▼
┌────────────────┐      ┌──────────────────────┐
│ AN-027 stage   │      │  4–6 WEEKS HISTORY   │  ← BLOCKER (time)
│ funnel API     │      └──────────┬───────────┘
└────────────────┘                 │
                                   ▼
                        ┌──────────────────────┐
                        │ AN-039 Decision      │
                        │ Center               │
                        └──────────────────────┘

  ┌──────────────────────────────────────────────────────────┐
  │  AN-013  SOFT DELETE   ← BLOCKED on a data-protection    │
  │          decision. No engineering dependency.            │
  │          Every day of delay destroys evidence.           │
  └──────────────────────────────────────────────────────────┘
```

**INFERENCE — the shape is the finding.** Root B (query corrections) has **no dependency on Root A at all**. Six work items that fix the three UNRELIABLE metrics and expose the highest-value missing analysis can proceed in full parallel with the pipeline work. A team that reads this graph as a single chain would serialise roughly half the programme behind schema changes it does not need.

---

# Blockers

Things that stop work and are not themselves work items in the normal sense.

## B-1 — Runtime measurement (AN-001)

| | |
|---|---|
| **Blocks** | AN-003 (retry parameters), AN-004 (purge batch size), AN-017/AN-018 (event volume safety) |
| **Nature** | External observation — log inspection plus two read-only queries |
| **Why blocking** | **FACT** — the channel drops silently on overflow (`DropWrite`) and the flush discards silently on failure. **INFERENCE** — deploying high-frequency events into a channel already dropping under load would degrade the events that currently work, and nothing would report it. |
| **Unblocks by** | Recording R-1, R-2, R-3 |

## B-2 — `EventId` unique constraint (AN-002)

| | |
|---|---|
| **Blocks** | AN-003, AN-010, AN-011, AN-024, and every event-emitting item |
| **Nature** | Schema change |
| **Why blocking** | **FACT** — `UserEvent.Id` is a database identity assigned at insert, so a retried batch produces new rows. **INFERENCE** — implementing retry without this column converts a loss problem into a duplication problem, which is worse: duplicates are indistinguishable from genuine repeats and silently inflate every count. |
| **Unblocks by** | Column deployed, backfilled, unique constraint active |

## B-3 — Data-protection decision (AN-013)

| | |
|---|---|
| **Blocks** | AN-013 only |
| **Nature** | **Non-engineering.** Whether scrub-in-place satisfies Cocorra's deletion obligations. |
| **Why blocking** | Not a technical dependency. It cannot be unblocked by engineering effort. |
| **Cost of delay** | **INFERENCE — the only item in the programme where waiting has an irreversible cost.** Every hard delete permanently removes a user from all longitudinal analysis. The bias in every retention rate grows daily and can never be corrected retroactively. |
| **Unblocks by** | A decision from whoever owns data protection |

**RECOMMENDATION** — raise B-3 on day one, not when AN-013 reaches the top of the backlog. It is the only blocker whose resolution time is entirely outside engineering control, and the only one whose delay destroys data.

## B-4 — History accumulation (time)

| | |
|---|---|
| **Blocks** | AN-039 (Decision Center), AN-038 (cohort grid), Page 0 and Page 6 cutover |
| **Nature** | Elapsed time. Cannot be compressed by effort or parallelism. |
| **Why blocking** | **FACT** — no baseline exists for any Cocorra metric. **INFERENCE** — detection requires knowing what normal looks like. Shipping change detection without a baseline produces alerts on ordinary variance, and a dashboard that cries wolf in its first month is ignored permanently — an outcome harder to reverse than a delayed launch. |
| **Unblocks by** | 4–6 weeks of stable read models (Decision Center); 8 weeks (cohort grid) |

**RECOMMENDATION** — start AN-015 (aggregation) as early as its dependencies allow, purely to start the clock. **INFERENCE** — B-4 is the only blocker where *beginning earlier* is the sole available lever; once the clock is running, nothing else shortens it.

## B-5 — Test provider capability

| | |
|---|---|
| **Blocks** | Meaningful validation of AN-002, AN-003, AN-010, AN-013 |
| **Nature** | Test infrastructure |
| **Why blocking** | **FACT** — `Cocorra.Tests` uses `Microsoft.EntityFrameworkCore.InMemory`, which does **not** enforce unique indexes or `DeleteBehavior`. **INFERENCE** — every idempotency test written against it passes whether or not the constraint exists. The entire durability guarantee would be untested while appearing tested. |
| **Unblocks by** | Adding `Microsoft.EntityFrameworkCore.Sqlite` to `Cocorra.Tests.csproj` |

**INFERENCE** — B-5 is a one-line project change that determines whether eight tests mean anything. It is the cheapest blocker in the graph and the easiest to overlook, because the suite is green either way.

---

# Critical Path

The longest dependency chain. Everything on it delays the finish date one-for-one.

```
AN-001  Runtime measurement                    [external observation]
   ↓
AN-002  EventId + unique constraint            [schema]
   ↓
AN-003  Flush retry + dead-letter              [pipeline durability]
   ↓
AN-004  Batched retention purge                [must precede volume increase]
   ↓
AN-017  P0 events — low frequency              [flagged]
   ↓
AN-018  P0 events — high frequency             [flagged, gated on AN-017 stability]
   ↓
AN-015  Aggregation service                    [populates read models]
   ↓
   ══ 4–6 WEEKS ELAPSED (B-4) ══
   ↓
AN-039  Decision Center
```

**Nine items plus a fixed 4–6 week wait.**

## Why each link is genuinely required

| Link | Why it cannot be skipped or reordered |
|---|---|
| AN-001 → AN-002 | Measurement informs the backfill approach and channel-capacity decision. Weak link — could overlap. |
| **AN-002 → AN-003** | **Hard.** Retry without idempotency creates duplicates instead of preventing loss. |
| AN-003 → AN-004 | Soft. Both touch `UserEvents` operationally; sequencing avoids concurrent behavioural changes to the same table. |
| **AN-004 → AN-017** | **Hard in practice.** The P0 events increase daily row volume by design. Batching a purge that already competes with ingestion is harder than batching one that does not yet. |
| **AN-017 → AN-018** | **Hard by design.** High-frequency events scale with engagement and land hardest on the busiest rooms. Deploying them before the low-frequency increment has proven stable removes the ability to attribute a drop-rate spike. |
| AN-018 → AN-015 | Soft. Aggregation can start on existing events; it needs the new ones only for the stage-funnel grains. |
| **AN-015 → B-4** | **Hard and immovable.** History accrues in wall-clock time. |
| **B-4 → AN-039** | **Hard.** Detection without a baseline is noise. |

**INFERENCE — the shortening opportunity.** Only two links are truly rigid: `AN-002 → AN-003` and `B-4 → AN-039`. The AN-015 dependency on AN-018 is soft: **aggregation over existing events can begin immediately after AN-014**, which starts the B-4 clock roughly four items earlier. Since B-4 is a fixed 4–6 weeks that nothing can compress, starting it sooner is the single largest available reduction in total elapsed time.

**RECOMMENDATION — the revised critical path:**

```
AN-001 → AN-002 → AN-014 → AN-015 → ══ 4–6 WEEKS ══ → AN-039
                     (aggregate existing events immediately)

  in parallel: AN-003 → AN-004 → AN-017 → AN-018 → AN-027
```

This moves the event work **off** the critical path and onto a parallel track, because the Decision Center's gating dependency is *history*, not *complete instrumentation*. The stage funnel arrives later, which is correct — it is one page of nine.

---

# Parallel Work

Four independent tracks. **INFERENCE** — this is the most actionable output of the graph: with four developers, roughly two-thirds of the backlog proceeds without waiting on anything.

## Track 1 — Pipeline (the critical path)

```
AN-001 → AN-002 → AN-003 → AN-004 → AN-017 → AN-018 → AN-019
```

**Character** — schema and background services. Highest risk, highest coordination.
**Owner profile** — the developer most familiar with `EventTracking` and EF Core.

## Track 2 — Query corrections (fully independent)

```
AN-005  host exclusion            ─┐
AN-006  return metric             ─┤
AN-007  sequential funnel         ─┼─→  AN-012  MetricRegistry
AN-008  growth split              ─┤
AN-021  report rate by category   ─┤
AN-022  review latency            ─┘
```

**Dependencies on Track 1** — **none.**

**INFERENCE — this track is the programme's best early return.** Six items, all pure query work over data the audit already verified as correct, no schema change, no deployment risk. Between them they fix the three UNRELIABLE metrics and expose the two highest-value missing analyses (supply health, report rate by category). A team that serialises this behind the pipeline work delays the entire trust improvement for no technical reason.

## Track 3 — Read models and aggregation

```
AN-014 → AN-015 → AN-016 → AN-020 · AN-023 · AN-025 · AN-038
```

**Dependencies on Track 1** — AN-002 only.
**INFERENCE** — can start as soon as `EventId` lands, and starts the B-4 clock.

## Track 4 — Independent singles

```
AN-009  StateSnapshotService     — no dependencies at all
AN-013  Soft delete              — blocked only on B-3
AN-024  Push delivery events     — needs AN-002
AN-032  Group-chat existence check — measurement only
AN-037  MBTI dichotomy analysis  — query only
AN-042  Structured logging sink  — no dependencies
```

**RECOMMENDATION — AN-009 should be the very first thing deployed**, ahead of everything including AN-001.

**INFERENCE** — it has zero dependencies, it is five `COUNT` queries on a timer, and it is the only read model that **cannot be backfilled**. Every other item can be done later at the same cost; this one gets more expensive every day, because the history it would have captured is gone. Deploying it first costs nothing and stops an ongoing loss.

## Parallelisation summary

| Track | Items | Blocked by | Can start |
|---|:--:|---|---|
| 1 — Pipeline | 7 | B-1, B-5 | After AN-001 |
| 2 — Query corrections | 6 | **Nothing** | **Immediately** |
| 3 — Read models | 7 | AN-002 | After AN-002 |
| 4 — Independent | 6 | Mostly nothing; AN-013 on B-3 | **Immediately** |

**INFERENCE** — 12 of 45 items (Tracks 2 and 4) can start on day one with no prerequisites. They include the host-exclusion fix, the two replaced UNRELIABLE metrics, the sequential funnel, the safety segmentation, and the snapshot service.

---

# Recommended Execution Order

| Wave | Items | Rationale |
|:--:|---|---|
| **0** | AN-009, AN-001, B-3 raised, B-5 resolved | AN-009 stops ongoing data loss immediately. AN-001 unblocks Track 1. B-3 has the longest external lead time. B-5 is one line and makes the tests real. |
| **1** | AN-005, AN-007, AN-006, AN-008 (Track 2) · AN-002 (Track 1) | Track 2 delivers the trust corrections with zero deployment risk while AN-002 unblocks everything else. |
| **2** | AN-003, AN-004 (Track 1) · AN-014, AN-015 (Track 3) · AN-012 | Pipeline hardening and aggregation start in parallel. **AN-015 starts the B-4 clock.** |
| **3** | AN-016 backfill · AN-020, AN-021, AN-022, AN-023 (endpoints) · AN-017 (events) | Backfill and the high-value endpoints. Events begin on their own track. |
| **4** | AN-018, AN-019 · AN-024, AN-025 | High-frequency events after low-frequency stability is confirmed. |
| **5** | Dashboard cutover per `21-` Phase D · AN-026…AN-038 | Page-by-page cutover. P2 items proceed alongside. |
| **6** | AN-039 Decision Center · P3 | Only after B-4 has elapsed. |

**INFERENCE on Wave 0** — it contains no feature work at all. One tiny service, one measurement, one question, one test-project line. **That is the point**: each removes a constraint that would otherwise compound. AN-009 stops an ongoing loss, AN-001 unblocks a track, B-3 starts an external clock, B-5 makes eight later tests meaningful. Treating them as prerequisites rather than backlog items is what keeps them from being deferred.

---

# Risk Concentration

Where the graph is fragile.

| Risk | Concentration | Mitigation |
|---|---|---|
| **AN-002 is a single point of failure** | Five items depend on it | Small, additive, independently testable. Deploy and verify alone before dependents start. |
| **B-4 cannot be compressed** | Gates the Decision Center | Start AN-015 as early as possible; aggregate existing events without waiting for new ones |
| **B-3 is outside engineering control** | Gates AN-013 | Raise on day one; the interim `account_deleted` event provides a partial record |
| **AN-018 could destabilise ingestion** | High-frequency events on a bounded channel | Two-increment deployment; separate feature flag; AN-001 measurement first |
| **B-5 invalidates tests silently** | Idempotency across four items | Add SQLite plus a provider-guard test that fails if someone reverts to `EFCore.InMemory` |
| **Two repositories** | Dashboard cutover | API changes are additive; the dashboard adopts page by page with no coordinated release |

---

# Scaling Constraints (Out of Scope, Recorded)

**INFERENCE** — none of these block this programme. All would break if Cocorra moves to multiple API instances, and this is the natural place to record them so the decision is informed rather than discovered.

| Constraint | Evidence | Failure on scale-out |
|---|---|---|
| `RoomHub._connections` static dictionary | `RoomHub.cs:29` | Disconnect cleanup fails across instances — **the hardest failure of the three** |
| Session dedup in `IMemoryCache` | `SessionTrackingMiddleware` | `session_started` re-emitted per instance |
| `AnalyticsService` `IMemoryCache` + `SemaphoreSlim` | `AnalyticsService.cs:16-29` | Per-instance caching; stampede protection becomes per-instance |
| `BackgroundService` instances | `Program.cs:213-214` | Aggregation and snapshots run per replica |
| Single server clock assumption | `15-` ordering | Cross-instance timestamp skew affects event ordering |

**INFERENCE on the fourth row** — the aggregation services are the *least* severe of these. Because every write is an idempotent UPSERT on a natural key (INV-4), concurrent runs produce correct-but-duplicated work rather than corruption. `RoomHub._connections` fails hardest, and it is pre-existing rather than introduced here.

---

# Summary

| Question | Answer |
|---|---|
| **Blockers** | B-1 measurement · **B-2 `EventId`** · **B-3 data-protection decision (external)** · B-4 history (time) · B-5 test provider |
| **Critical path** | AN-001 → AN-002 → AN-014 → AN-015 → **4–6 weeks** → AN-039, with events on a parallel track |
| **Longest fixed delay** | B-4, 4–6 weeks, compressible only by starting aggregation earlier |
| **Parallel tracks** | 4 — Pipeline · Query corrections · Read models · Independent singles |
| **Can start immediately** | **12 of 45 items** (Tracks 2 and 4) |
| **First thing to deploy** | **AN-009 `StateSnapshotService`** — zero dependencies, and the only data loss that is ongoing |

**Three conclusions (INFERENCE).**

**The graph is three roots, not one chain.** Reading it as a single sequence would serialise the six query corrections — which fix all three UNRELIABLE metrics and require no schema change — behind pipeline work they do not depend on.

**The Decision Center is gated on history, not instrumentation.** That reorders the critical path: start aggregating existing events immediately after `EventId` lands, rather than waiting for the new events. The stage funnel arrives later, which is correct — it is one page of nine.

**Wave 0 contains no feature work, and that is deliberate.** One small service, one measurement, one question, one test-project line. Each removes a constraint that compounds if deferred, and the smallest of them — `StateSnapshotService` — is the one whose delay is permanently unrecoverable.
