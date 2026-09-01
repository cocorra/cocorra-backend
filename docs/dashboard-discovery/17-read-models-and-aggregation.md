# 17 — Read Models & Aggregation

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 6
> **Depends on**: `14-metric-contracts.md` (metric definitions), `16-raw-event-storage-strategy.md` (raw store), `12-target-analytics-architecture.md`
> **Scope**: Documentation only. No tables, migrations, or services were created.

---

# The Problem This Layer Solves

**FACT** — Cocorra has **no aggregation layer**. Every one of the eleven analytics endpoints computes its metric live, per request, over production tables. Several materialise full result sets into memory before aggregating in LINQ-to-Objects:

- `GetUserGrowthAsync` (`AnalyticsRepository.cs:21-93`) — `.ToList()` of every user in the window, then client-side grouping.
- `GetRetentionCohortAsync` (`AnalyticsRepository.cs:324-392`) — **unbounded** activity fetch for every cohort user.
- `GetActiveVsPassiveAsync` (`AnalyticsRepository.cs:501-540`) — materialises the joiner list, then uses `.Contains()`, producing a large `IN (...)` clause.

**FACT** — the only mitigation is `IMemoryCache` with a 10-minute TTL and eleven `SemaphoreSlim` stampede guards (`AnalyticsService.cs:16-29`).

**INFERENCE — why caching is not sufficient.** The cache prevents *repeated* expensive queries; it does not make the first one cheap, and it does nothing after a restart or a window change. More importantly, it cannot solve the problem that actually matters here: **raw events are purged at 180 days**. A cache expiring in 10 minutes cannot preserve history that the cleanup service deletes. Pre-aggregation is the only mechanism that gives Cocorra a trend longer than its raw retention window.

So this layer exists for three reasons, in order of importance:

1. **History** — rollups survive the raw purge (`16-`: indefinite retention).
2. **Stability** — a metric computed once and stored cannot silently change when a formula is edited.
3. **Cost** — dashboard reads hit small indexed tables instead of scanning the event log.

---

# Live Query vs Read Model

**RECOMMENDATION** — the decision rule, applied consistently:

| Use a **READ MODEL** when | Use a **LIVE QUERY** when |
|---|---|
| The metric is a time series that must outlive the 180-day raw window | The metric is inherently current-state |
| The computation scans a large event range | The computation touches a small relational table |
| The metric appears on a default dashboard view | The metric is drill-down, reached deliberately |
| Values for a closed day never change | Values must reflect the last few minutes |

## Assignment for all 22 metrics

| Metric | Method | Rationale |
|---|:--:|---|
| M-100 Weekly Participating Users | **READ MODEL** | North Star; needs history past 180 days; scans `room_joined` |
| M-101 Speaking Conversion Rate | **READ MODEL** | Two event scans; shown beside M-100 |
| M-102 Weekly Return Rate | **READ MODEL** | Cross-week cohort join — expensive live |
| M-200 Distinct Active Hosts | **READ MODEL** | Leading indicator; must outlive raw retention |
| M-201 Host Retention | **READ MODEL** | Month-over-month set intersection |
| M-202 Supply Concentration | **READ MODEL** | Derived from the same host rollup |
| M-203 Non-Host Speakers per Room | **READ MODEL** | Per-room grain; feeds several views |
| M-204 Audience Return per Host | **READ MODEL** | Cross-room join per host |
| M-300 Sequential Onboarding Funnel | **HYBRID** | Read model for closed days; live for the current partial day |
| M-301 Admin Review Latency | **LIVE QUERY** | Percentiles over a 30-day window on an indexed pair; small result set |
| M-302 Activation → First Room Join | **READ MODEL** | 7-day forward window per cohort |
| M-303 Pending Queue Depth | **READ MODEL (snapshot)** | Pure state — no event trail exists |
| M-400 Stage Funnel | **READ MODEL** | Four-step sequential join per room-user |
| M-401 Non-Host Speaking Minutes | **READ MODEL** | Sums over high-frequency `mic_deactivated` |
| M-402 Hand-Raise → Promotion Rate | **READ MODEL** | Paired-event join |
| M-403 Conversion by Room Config | **READ MODEL** | Segmented; 30-day window |
| M-500 Report Rate per 1,000 Joins | **READ MODEL** | Event-derived denominator |
| M-501 Report Rate by Category | **READ MODEL** | Same, segmented |
| M-502 Repeat-Reported Users | **LIVE QUERY** | Small relational table, indexed `CreatedAt`, drill-down only |
| M-600 Message Reciprocity | **LIVE QUERY** | `Messages` is indexed on `(SenderId, ReceiverId, CreatedAt)`; relational, so no raw-purge exposure |
| M-601 Technical Ticket Rate | **LIVE QUERY** | `SupportTickets` is small |
| M-602 Push Send Success Rate | **READ MODEL** | High-frequency events; needs trend |

**Count**: 17 read model · 4 live query · 1 hybrid.

**INFERENCE — why the live-query four are safe to leave live.** M-502, M-600, and M-601 all read **relational** tables (`Reports`, `Messages`, `SupportTickets`), not the event log. They are therefore immune to the 180-day purge and are already indexed for their access pattern. M-301 reads events but over a bounded 30-day window on an indexed `(EventType, OccurredAtUtc)` pair, returning a handful of percentiles. Pre-aggregating these would add machinery for no benefit.

---

# Read Model Definitions

Five tables. All live in `Cocorra.DAL/Models/Analytics/` and are registered as `DbSet`s on `AppDbContext`.

**Design rules applied to all five (RECOMMENDATION)**
- **Natural key** on the grain, uniquely indexed — this is what makes aggregation idempotent (INV-4).
- **Additive counts only.** Store numerators and denominators, never percentages. Rates are computed at read time.
- **`ComputedAtUtc`** on every row, so staleness is reportable through `Response<T>.Meta`.
- **Nothing that cannot be recomputed** from raw events within the raw window (INV-5).

**INFERENCE — why "counts, never percentages" matters here specifically.** M-101 is `speakers ÷ joiners`. If the daily rollup stored the *rate*, a weekly figure would have to average seven daily rates — which is wrong whenever daily volumes differ, and Cocorra's volumes differ a great deal between a day with three live rooms and a day with none. Storing both counts lets any period be computed correctly by summing and then dividing.

---

## RM-1 — `DailyPlatformMetrics`

**Grain**: one row per UTC date.

```
Date                    date        PK
ParticipatingUsers      int         distinct non-host room_joined users
SpeakingUsers           int         distinct non-host mic_activated users
JoinEvents              int         raw count — reconnect-inflated, kept for diagnosis
RoomsWithActivity       int         distinct RoomId with ≥1 non-host joiner
DistinctActiveHosts     int
RoomsCreated            int
RoomsWentLive           int         from room_went_live (NULL before that event ships)
NewRegistrations        int
NewActivations          int
ReportsFiled            int
MessagesSent            int
ComputedAtUtc           datetime2
SchemaVersion           tinyint

UNIQUE (Date)
```

**Supports**: M-100, M-101, M-200, M-500, plus platform trend lines.

**INFERENCE — a deliberate subtlety about weekly metrics.** M-100 is a *rolling 7-day distinct-user count*. Distinct counts are **not additive**: summing seven daily distinct counts double-counts anyone who participated on more than one day. This table therefore supports the daily series directly, but the weekly North Star must be computed either from raw events over the 7-day window, or from a separate weekly grain. **RECOMMENDATION** — add a `WeeklyPlatformMetrics` table keyed on ISO week, populated by the same job. Attempting to derive WPU by summing daily rows would silently overstate it, and the error would grow with engagement — the worst possible direction.

**`JoinEvents` is stored deliberately.** It is the reconnect-inflated raw count and must never be shown as engagement. **INFERENCE** — its diagnostic value is real: a rising ratio of `JoinEvents` to `ParticipatingUsers` indicates reconnection churn, which is the only proxy Cocorra currently has for connection instability given the absence of LiveKit telemetry.

---

## RM-2 — `DailyRoomMetrics`

**Grain**: one row per `(Date, RoomId)`.

```
Date                    date
RoomId                  uniqueidentifier
HostId                  uniqueidentifier
Category                int
SelectionMode           int
StageCapacity           int
DistinctJoiners         int      non-host
JoinEvents              int      raw, reconnect-inflated
DistinctSpeakers        int      non-host mic_activated
HandRaises              int      new event
StagePromotions         int      new event
SpeakingSeconds         float    non-host mic_deactivated segments
TimeExhaustedEvents     int
ExtraTimeGrants         int
KickEvents              int
ReportsAboutRoom        int
WentLiveAtUtc           datetime2?
EndedAtUtc              datetime2?
ActualDurationSeconds   int?
ComputedAtUtc           datetime2
SchemaVersion           tinyint

UNIQUE (Date, RoomId)
INDEX (Date, HostId)
INDEX (Date, Category)
```

**Supports**: M-203, M-400, M-401, M-402, M-403, M-501, and every per-room drill-down.

**Room dimensions are denormalised onto the row** (`HostId`, `Category`, `SelectionMode`, `StageCapacity`). **INFERENCE** — this is the single most important design choice in this table. It makes M-403's segmentation a `GROUP BY` on the rollup rather than a join back to `Rooms`, and it freezes the configuration *as it was on that date*, so a later edit to a room cannot retroactively change historical segmentation. That is the same class of defect as TRUST-02 (status backdating), avoided by construction.

**Rooms spanning midnight** — a 2–3 hour room can cross a UTC date boundary. **RECOMMENDATION** — attribute each event to the date of its own `OccurredAtUtc`, producing two rows for such a room. Room-level totals sum across dates. **INFERENCE** — the alternative (attributing everything to the room's start date) would make daily sums wrong, and daily sums are what the platform trend uses.

---

## RM-3 — `DailyHostMetrics`

**Grain**: one row per `(Date, HostId)`.

```
Date                     date
HostId                   uniqueidentifier
RoomsCreated             int
RoomsWentLive            int
TotalDistinctJoiners     int    across that host's rooms that day
TotalDistinctSpeakers    int
AvgSpeakersPerRoom       float
ReportsAboutHostRooms    int
ComputedAtUtc            datetime2

UNIQUE (Date, HostId)
```

**Supports**: M-200, M-201, M-202, M-204.

**INFERENCE — why the supply side gets its own table.** `05-analytics-gap-analysis.md` GAP-07 identifies host-side analytics as the highest value-to-effort item in the programme, and the fact that no existing endpoint computes it. A dedicated grain makes host retention and concentration cheap to query and, critically, **survives the raw purge** — so Cocorra gains a multi-year view of its most fragile dependency.

**M-201 and M-202 are computed from this table, not stored in it.** Both are set operations across periods (intersection for retention, top-N share for concentration), which are not daily-additive.

---

## RM-4 — `DailyFunnelMetrics`

**Grain**: one row per `(CohortDate, FunnelName, StepIndex)`.

```
CohortDate      date
FunnelName      varchar(64)   'onboarding' | 'stage'
StepIndex       tinyint
StepName        varchar(64)
UsersReached    int           sequential — prior steps completed first
MedianSecondsFromPrevious int
ComputedAtUtc   datetime2

UNIQUE (CohortDate, FunnelName, StepIndex)
```

**Supports**: M-300, M-400.

**INFERENCE — this table encodes the TRUST-06 fix structurally.** `UsersReached` is defined as *sequential*: a user counts at step N only if every prior step's `OccurredAtUtc` precedes it. Because the rows are ordered by `StepIndex`, monotonicity (`UsersReached[N] <= UsersReached[N-1]`) becomes a checkable invariant on the stored data, not merely a property of a query. That makes the funnel's correctness continuously assertable rather than testable only at implementation time.

**Late-arriving completions** — onboarding steps complete over days (admin review latency). **RECOMMENDATION** — recompute cohorts for a trailing window (e.g. the last 45 days) on each run, not just the current day. **INFERENCE** — a cohort aggregated once on its cohort date would permanently record an `activation_completed` count of near-zero, because approval typically arrives later. This is the specific failure mode that would make the onboarding funnel look catastrophically broken while being merely premature.

---

## RM-5 — `DailyStateSnapshots`

**Grain**: one row per `(Date, MetricKey)`.

```
Date          date
MetricKey     varchar(64)   'pending_verification_queue' | 'active_users_total' |
                            'fcm_token_coverage' | 'open_reports' | 'rerecord_queue'
Value         float
ComputedAtUtc datetime2

UNIQUE (Date, MetricKey)
```

**Supports**: M-303, FCM token coverage, and every count that is state rather than an event.

**FACT** — these quantities have no event trail. `AdminService.GetDashboardStatsAsync` is a bare `GroupBy(Status)` with no date filter; yesterday's pending count is unrecoverable.

**INFERENCE — this table is P0 despite being the smallest thing in the programme.** Every other read model can be backfilled from raw events. This one cannot: history not captured today is unrecoverable tomorrow, and every day of delay is a permanent hole. It is also the cheapest item to build — a scheduled job running five `COUNT` queries against existing tables.

**Key-value shape, deliberately.** **INFERENCE** — new state metrics arrive as new `MetricKey` values with no schema change. A wide-column table would require a migration per metric, which in practice means the metric does not get added.

---

# Aggregation Strategy

```
dbo.UserEvents  (raw, 180-day retention)
        │
        │  incremental read: WHERE Id > lastProcessedEventId
        ▼
AnalyticsAggregationService : BackgroundService     [hourly]
        │  ├─ recompute affected daily grains
        │  ├─ idempotent UPSERT on the natural key
        │  └─ advance watermark only after a successful commit
        ▼
RM-1 … RM-4  (indefinite retention)

dbo.AspNetUsers, dbo.Reports, dbo.Rooms   (current state)
        │
        ▼
StateSnapshotService : BackgroundService            [daily, 00:15 UTC]
        │  └─ idempotent UPSERT on (Date, MetricKey)
        ▼
RM-5  (indefinite retention)
```

## Frequency

**RECOMMENDATION — hourly for event rollups; daily for snapshots.**

**INFERENCE — why hourly rather than the more obvious daily.** Three reasons specific to Cocorra:

1. **Rooms are 2–3 hours long** (`AllowedDurations`). A daily job means a coach who ran a room this morning sees nothing about it until tomorrow, which undermines the dashboard's usefulness for the people closest to the rooms.
2. **Incremental work stays small.** An hourly job processes a fraction of the day's events, so a failure costs one hour of lag rather than a full day, and a retry is cheap.
3. **It matches the existing cache TTL.** `AnalyticsService` caches for 10 minutes, so hourly freshness is already the effective ceiling on how fresh a dashboard read can be.

**Why not real-time (RECOMMENDATION against)** — streaming aggregation would add complexity and a new failure mode for freshness nobody has asked for. **INFERENCE** — no decision in `07-decision-framework.md` is time-sensitive at sub-hourly resolution. The Decision Center in `09-` detects week-over-week change, not minute-over-minute.

## Watermark checkpointing

```
AggregationCheckpoint
    JobName             varchar(64) PK
    LastProcessedEventId bigint
    LastRunAtUtc        datetime2
    LastSuccessAtUtc    datetime2
    ConsecutiveFailures int
```

**INFERENCE — why `UserEvent.Id` is the right watermark.** It is a `bigint` identity: monotonic, gap-tolerant, and assigned at insert. `OccurredAtUtc` would be the wrong choice, because the flush service persists in batches — an event that *occurred* at 10:59 may be *inserted* at 11:01, after a timestamp-based watermark has already advanced past 11:00. That event would be silently skipped forever. The identity column has no such hazard.

**Caveat, and why it is acceptable (INFERENCE)** — identity values are allocated at insert, so rows can commit slightly out of id order under concurrency. A watermark could theoretically skip a row committed just after a higher id. With a **single** flush service instance writing sequential batches, this window is negligible. **RECOMMENDATION** — apply a small safety lag (re-read from `lastProcessedEventId − N`) and rely on idempotent upserts to absorb the overlap. This costs nothing and removes the edge case entirely.

## Idempotency (INV-4)

**RECOMMENDATION** — every aggregation write is an UPSERT on the natural key, computing the **full** value for that grain rather than incrementing.

```
For each affected (Date, RoomId):
    recompute all columns from raw events for that date and room
    UPSERT into DailyRoomMetrics
```

**INFERENCE — why full recomputation rather than incremental addition.** Incrementing is faster but not idempotent: a retried batch would double-count. Full recomputation per affected grain means running the job twice produces the identical result, which is what makes retry safe, backfill safe, and formula correction safe. The cost is bounded because only *affected* grains are recomputed, identified from the events read since the watermark.

---

# Backfill Strategy

**RECOMMENDATION** — one rule governs everything here: **never fabricate historical events.** A read model may only contain what raw events actually support. Where they do not, the metric starts on its instrumentation date with an explicit marker.

| Read model / metric | Backfill | Window | Justification |
|---|:--:|---|---|
| RM-1 `DailyPlatformMetrics` | **FULL BACKFILL** | 180 days | All source events exist today: `room_joined`, `mic_activated`, `user_registered`, `activation_completed` |
| RM-1 `RoomsWentLive` column | **NO BACKFILL** | — | `room_went_live` does not exist yet (Finding C). Column is NULL before the deployment date. |
| RM-2 — joiners, speakers, reports | **FULL BACKFILL** | 180 days | Source events exist |
| RM-2 — hand raises, promotions, speaking seconds, exhaustions, grants, kicks | **NO BACKFILL** | — | Events never captured (TRUST-04). Columns NULL before deployment. |
| RM-2 — `ActualDurationSeconds` | **PARTIAL BACKFILL** | 180 days | **INFERENCE** — approximable as `MIN(RoomParticipant.JoinedAt)` to `room_ended.OccurredAtUtc`, because the host is inserted as a participant at start. Must be flagged `IsEstimated`. |
| RM-3 `DailyHostMetrics` | **FULL BACKFILL** | Full history | **FACT** — sourced from `Rooms.HostId` and `Rooms.CreatedAt`, which are **relational and never purged**. This is the one read model that can be backfilled beyond 180 days. |
| RM-4 onboarding funnel | **FULL BACKFILL** | 180 days | All six onboarding events exist |
| RM-4 stage funnel | **NO BACKFILL** | — | Steps 2 and 3 never captured |
| RM-5 `DailyStateSnapshots` | **NO BACKFILL** | — | **Pure state with no history. Structurally unrecoverable.** |

**INFERENCE — the most valuable line in this table is RM-3.** Because host metrics derive entirely from the `Rooms` table, Cocorra can reconstruct its complete supply history from day one — not just 180 days. That gives the platform's leading indicator a longer baseline than any other metric, immediately, at the cost of one backfill run.

## Marking non-backfillable metrics

**RECOMMENDATION** — a `MetricAvailability` registry recording, per metric, the first date on which its data is trustworthy. Queries spanning that boundary return an explicit marker.

**INFERENCE — why this is a correctness requirement, not a nicety.** M-400 has no data before `hand_raised` ships. If the API returns 0 for that period, the chart reads as *"nobody raised their hand"* — a confident, false, and entirely plausible conclusion. Returning "not measured" is the difference between an honest gap and a fabricated finding. This is the same class of failure as TRUST-02, and the registry is what prevents the new system from reproducing it.

## Backfill execution

**RECOMMENDATION** — a one-shot, resumable, batched job, run manually rather than on a schedule:
1. Process oldest date first, one day at a time.
2. Use the same idempotent UPSERT path as the live aggregator — **INFERENCE**: sharing the code path is what guarantees backfilled and live-aggregated rows are identical, which is otherwise a common source of subtle divergence.
3. Checkpoint per completed date so an interruption resumes rather than restarts.
4. Throttle between batches to avoid competing with production traffic.
5. Run against a restored copy first and reconcile against live queries before running in production.

---

# Validation

| # | Test | Asserts |
|:--:|---|---|
| **1** | Reconciliation | Rollup values equal a direct live query over the same window, for every metric, within a documented tolerance |
| **2** | Idempotency | Running the aggregator twice over the same range produces byte-identical rows |
| **3** | Watermark advance | Only advances after a successful commit; a forced failure leaves it unchanged |
| **4** | Gap detection | Missing dates are flagged, never interpolated |
| **5** | Additivity | Summing daily counts equals the period count **for additive columns only**; distinct-user columns are asserted *non*-additive and sourced from the weekly grain |
| **6** | Midnight-spanning rooms | A room crossing UTC midnight produces two `DailyRoomMetrics` rows whose sums equal the room total |
| **7** | Late-arriving cohort | An `activation_completed` arriving 3 days after the cohort date is picked up by the trailing-window recompute |
| **8** | Backfill parity | Backfilled rows are identical to rows produced by the live aggregator over the same range |
| **9** | Availability marker | A query spanning a metric's instrumentation date returns "not measured" for the earlier portion, never 0 |
| **10** | Rate correctness | A weekly rate computed from summed numerators/denominators differs from the average of daily rates — asserting rates are not stored |

**INFERENCE — tests 5, 9, and 10 are the ones that catch the failures this document is designed to prevent.** Test 5 catches the distinct-count summing error; test 9 catches uninstrumented periods rendering as zero; test 10 catches stored percentages. Each corresponds to a specific way a well-built aggregation layer still produces wrong numbers.

---

# Summary

| Read Model | Metrics Supported | Source Data | Aggregation Frequency | Historical Granularity |
|---|---|---|---|---|
| **RM-1** `DailyPlatformMetrics` (+ weekly grain) | M-100, M-101, M-200, M-500 | `UserEvents`, `Rooms`, `AspNetUsers` | Hourly | Daily + ISO week, indefinite |
| **RM-2** `DailyRoomMetrics` | M-203, M-400, M-401, M-402, M-403, M-501 | `UserEvents`, `Rooms` | Hourly | Per room-day, indefinite |
| **RM-3** `DailyHostMetrics` | M-200, M-201, M-202, M-204 | `Rooms`, `UserEvents` | Hourly | Per host-day, indefinite |
| **RM-4** `DailyFunnelMetrics` | M-300, M-400 | `UserEvents` | Hourly, 45-day trailing recompute | Per cohort-day, indefinite |
| **RM-5** `DailyStateSnapshots` | M-303, token coverage, open reports | `AspNetUsers`, `Reports` | Daily 00:15 UTC | Daily, indefinite |

**Three conclusions (INFERENCE).**

**Aggregation is what gives Cocorra history.** Raw events die at 180 days; rollups do not. This layer, not a retention extension, is the answer to the platform's inability to compare anything year over year.

**RM-5 is the most urgent and the smallest.** It is five `COUNT` queries on a schedule, and it is the only read model whose data cannot be backfilled. Every day it is not running is a permanent gap in a series the operations team will eventually need.

**RM-3 offers the deepest history for the least work.** Because it derives from relational `Rooms` data rather than purgeable events, it can be backfilled to the platform's first day — giving the supply-side leading indicator a longer baseline than any other metric in the system.
