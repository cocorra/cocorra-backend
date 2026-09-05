# 19 — Analytics API Blueprint

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 8
> **Depends on**: `14-metric-contracts.md`, `17-read-models-and-aggregation.md`, `11-current-state-validation.md`
> **Scope**: Documentation only. **No endpoints were created.**

---

# Design Position

**RECOMMENDATION — extend the existing controller and conventions. Do not build a parallel API.**

**FACT — the conventions already in place**, all verified at HEAD:

| Convention | Evidence |
|---|---|
| Route constants centralised | `Cocorra.DAL/AppMetaData/Router.cs:91-105` — `AnalyticsRouting` |
| Authorization | `[Authorize(Roles = "Admin,Coach")]` at class level (`AnalyticsController.cs:19`), plus the default policy requiring `VerificationStatus=Active` (`Program.cs:330-334`) |
| Response envelope | `Response<T>` with `StatusCode`, `Meta`, `Succeeded`, `Message`, `Errors`, `Data` |
| Date defaults | `ResolveFrom` → `UtcNow.AddDays(-30).Date`; `ResolveTo` → `UtcNow` (`AnalyticsService.cs:42-47`) |
| Caching | `IMemoryCache`, 10-minute TTL, per-metric `SemaphoreSlim` stampede guards |
| DTO location | `Cocorra.DAL/DTOS/AnalyticsDto/` |

**INFERENCE — the single most useful discovery for this phase.** `Response<T>.Meta` is `object?`, is present on every response, is accepted by `ResponseHandler.Success<T>(entity, meta)`, and is **currently always null**. The trust-metadata requirement from `08a-metric-trust-framework.md` therefore needs no envelope redesign, no versioned response type, and no breaking change. Populating `Meta` is purely additive and cannot break a client that ignores it.

---

# Trust Metadata Contract

Every analytics endpoint returns `Meta` shaped as follows. This is the mechanism that prevents the current failure — wrong and right metrics rendering with identical authority.

```json
{
  "metrics": [
    {
      "metricId": "M-100",
      "name": "Weekly Participating Users",
      "trustLevel": "VERIFIED",
      "historicalReliability": "HISTORICALLY_ACCURATE",
      "exclusions": ["room host (own room)", "deleted users"],
      "limitations": [
        "Counts attendance, not conversation — read with M-101",
        "Bounded by room supply — read with M-200"
      ],
      "dataAvailableFromUtc": "2026-03-05T00:00:00Z",
      "computedAtUtc": "2026-09-01T09:05:00Z",
      "aggregationMethod": "READ_MODEL"
    }
  ],
  "window": {
    "fromUtc": "2026-08-25T00:00:00Z",
    "toUtc": "2026-09-01T00:00:00Z",
    "timezone": "UTC",
    "suggestedDisplayOffsetMinutes": 180,
    "isPartialPeriod": false
  },
  "freshness": {
    "lastAggregationUtc": "2026-09-01T09:05:00Z",
    "lagMinutes": 12,
    "pipelineHealthy": true
  }
}
```

**Field rationale (INFERENCE)**

- **`dataAvailableFromUtc`** — the most important field, and the one most likely to be omitted. M-400 has no data before `hand_raised` ships. Returning `0` for that period would read as *"nobody raised their hand"* — a confident, plausible, and false conclusion. This field lets the client render "not measured" instead of a zero.
- **`suggestedDisplayOffsetMinutes`** — the server computes in UTC (INV-9) and never converts, but the user base is MENA (UTC+2/+3). Returning a suggested offset lets the client display local time without the server storing or bucketing in a local zone.
- **`isPartialPeriod`** — a week queried mid-week is not comparable to a complete one. Without this flag, the current week always appears to be a decline.
- **`freshness`** — sourced from `AggregationCheckpoint` (`18-`). **INFERENCE** — a metric's trust level is meaningless if the pipeline feeding it stopped three days ago. Trust and freshness must travel together.

---

# API Rules

**RECOMMENDATION** — these are binding on every endpoint below.

| # | Rule | Rationale |
|:--:|---|---|
| **R-1** | Explicit `from`/`to` supported; documented defaults when omitted | Existing behaviour (`ResolveFrom`/`ResolveTo`); keep it |
| **R-2** | UTC internally, always | INV-9. Converting server-side would corrupt stored buckets |
| **R-3** | Return timezone context in `Meta` | The user base is not in UTC; the client needs the offset to display honestly |
| **R-4** | Support a comparison period where a trend decision depends on it | A number without a prior-period reference supports no decision |
| **R-5** | Never silently mix incompatible populations | See below — the rule that matters most here |
| **R-6** | Expose limitations in `Meta`, never only in documentation | A wiki page is not read; a field adjacent to the number is |
| **R-7** | Never return `0` for an uninstrumented period | Return `null` plus `dataAvailableFromUtc`. `0` is a false finding |
| **R-8** | Deprecated metrics are **removed** from the response, not hidden | A hidden-but-present field lets a consumer silently read a wrong value; removal fails loudly |

**On R-5 — the concrete instances in Cocorra:**

- **Host vs non-host.** Every room metric excludes hosts (INV-7). An endpoint must never return a mixed figure, because that is exactly how TRUST-01 arose.
- **Rooms created vs rooms gone live.** Different denominators. `RoomsWentLive` is NULL before `room_went_live` ships and must be reported as NULL, not folded into `RoomsCreated`.
- **Reports with and without room context.** M-501 segments by room category; reports with no `reportedRoomId` belong in M-500 only. Bucketing them into `Others` would misattribute them.
- **Pre- and post-instrumentation periods.** Governed by R-7.

---

# Endpoint Catalogue

Routes follow the existing `Router.AnalyticsRouting` prefix convention (`Rule + "Analytics"`).

---

## Group A — Platform Health

### A-1 `GET /Api/V1/Analytics/Platform/Health`

| Field | Value |
|---|---|
| **Purpose** | The North Star and its four inputs, in one call. The dashboard's landing view. |
| **Decision supported** | Is Cocorra delivering more value this week, and which input constrained it? |
| **Metrics returned** | M-100, M-101, M-102, M-200, plus rooms-gone-live and new-activations |
| **Filters** | none |
| **Date range** | `from`, `to`; default rolling 7 days |
| **Comparison period** | `compareTo=previous_period` — **required in practice**; the headline number is meaningless without a prior week |
| **Segmentation** | none — this view is deliberately unsegmented |
| **Drill-down** | Each metric returns a `drillDownEndpoint` pointing at its detail route |
| **Data freshness** | Read models, hourly |
| **Trust metadata** | Per-metric entries; `RoomsWentLive` returns `null` with `dataAvailableFromUtc` until E-09 ships |

**INFERENCE** — this endpoint replaces the current `/Analytics/Summary`, which bundles four sub-metrics of differing trust into a single response. `08a` M-03 flags exactly that: a composite is only as trustworthy as its weakest component, and bundling obscures which parts to trust. The replacement returns per-metric trust rather than one aggregate verdict.

---

## Group B — Supply Health

**INFERENCE** — this entire group is new, is computable today from verified relational data, and has **no counterpart in the existing eleven endpoints**. `05-analytics-gap-analysis.md` GAP-07 identifies it as the highest value-to-effort item in the programme.

### B-1 `GET /Api/V1/Analytics/Supply/Overview`

| Field | Value |
|---|---|
| **Purpose** | Whether room supply is healthy and how concentrated it is |
| **Decision supported** | Recruit more coaches, or enable existing ones? |
| **Metrics** | M-200, M-201, M-202, rooms created, new hosts |
| **Filters** | `category` |
| **Date range** | default rolling 7 days; monthly grain for M-201 |
| **Comparison** | `compareTo=previous_period` |
| **Segmentation** | by `category` |
| **Drill-down** | → B-2 |
| **Freshness** | Read model `DailyHostMetrics`, hourly |
| **Trust** | M-200/M-201 **VERIFIED**; M-202 **CONDITIONALLY RELIABLE** — must return the total host count alongside the share, or the percentage is unreadable |

### B-2 `GET /Api/V1/Analytics/Supply/Hosts`

| Field | Value |
|---|---|
| **Purpose** | Per-host performance leaderboard |
| **Decision supported** | Which coaches to support, feature, or coach? |
| **Metrics** | Rooms hosted, median participants, M-203, M-204 |
| **Filters** | `minRooms` (default 1), `category` |
| **Segmentation** | by host |
| **Drill-down** | → B-3 (a specific host's rooms) |
| **Trust** | M-204 **CONDITIONALLY RELIABLE** — hosts with fewer than 2 rooms in the window are **excluded**, not reported as 0 |

**INFERENCE on the exclusion** — reporting a new host's audience return as 0% would rank them below a genuinely poor host, which inverts the metric's meaning for exactly the people most likely to be reviewed.

### B-3 `GET /Api/V1/Analytics/Supply/Schedule`

| Field | Value |
|---|---|
| **Purpose** | Room coverage by day-of-week × hour |
| **Decision supported** | Where are the scheduling gaps? |
| **Metrics** | Rooms live per (day, hour); participants per (day, hour) |
| **Date range** | default rolling 30 days |
| **Trust** | **CONDITIONALLY RELIABLE** — computed in UTC; `Meta.window.suggestedDisplayOffsetMinutes` is **mandatory** here. A coach acting on an unconverted UTC heatmap would schedule 2–3 hours off the real local peak |

---

## Group C — Activation Pipeline

### C-1 `GET /Api/V1/Analytics/Activation/Funnel`

| Field | Value |
|---|---|
| **Purpose** | Sequential onboarding funnel. **Replaces** the current `/Analytics/Funnel` |
| **Decision supported** | Where do prospects abandon the five-step gate? |
| **Metrics** | M-300 — per-step users **and** median elapsed time |
| **Filters** | `cohortFrom`, `cohortTo` |
| **Comparison** | `compareTo=previous_cohort` |
| **Trust** | **VERIFIED**. Monotonicity is a response invariant: each step ≤ the previous |

**FACT** — the current endpoint (`AnalyticsRepository.cs:300-322`) counts steps independently and can return a *widening* funnel. **RECOMMENDATION** — the new endpoint must assert monotonicity before returning; a violation indicates a computation bug and should surface as an error, not as a chart.

**RECOMMENDATION** — median elapsed time per step is not optional. **INFERENCE** — one of Cocorra's steps is a human review queue; a conversion-only funnel would render an 18-hour wait and an instant drop-off identically.

### C-2 `GET /Api/V1/Analytics/Activation/ReviewLatency`

| Field | Value |
|---|---|
| **Purpose** | How long users wait for a verification decision |
| **Decision supported** | Is the manual queue a throughput bottleneck? |
| **Metrics** | M-301 (median, p90, p99), M-303 queue depth, outcome mix |
| **Segmentation** | by day-of-week, by hour |
| **Freshness** | Live query for latency; snapshot read model for queue depth |
| **Trust** | **VERIFIED** |

**RECOMMENDATION — the response must not contain a mean.** **INFERENCE** — if most reviews take 20 minutes and 15% take 3 days, the mean describes nobody and hides the users being harmed. Excluding it is a contract requirement (M-301 validation), not a presentation preference.

**RECOMMENDATION** — return queue depth alongside latency. Latency excludes users still waiting, so it understates the problem exactly when the backlog is worst.

### C-3 `GET /Api/V1/Analytics/Activation/FirstRoomJoin`

| Field | Value |
|---|---|
| **Purpose** | Whether activated users reach the product |
| **Decision supported** | Is the onboarding problem above or below the gate? |
| **Metrics** | M-302, plus median hours from activation to first join |
| **Trust** | **VERIFIED** — incomplete cohorts are excluded, never counted as failures |

**INFERENCE** — the most important activation metric and the one most likely to be overlooked. If a large share of approved users never join a room, the gate is working and the product is failing downstream of it — and no funnel optimisation above the gate would help.

---

## Group D — Room Participation

### D-1 `GET /Api/V1/Analytics/Rooms/StageFunnel`

| Field | Value |
|---|---|
| **Purpose** | Where the listener→speaker journey breaks |
| **Decision supported** | Which stage control point should be redesigned? |
| **Metrics** | M-400 (4 steps), M-402 |
| **Segmentation** | `selectionMode`, `category`, `stageCapacity` band |
| **Trust** | **EXPERIMENTAL** at launch → **VERIFIED** after 4 weeks |

**RECOMMENDATION — R-7 applies most sharply here.** Steps 2 and 3 do not exist before E-01/E-03 ship. The response must return `null` with `dataAvailableFromUtc`, and the client must render a visible gap labelled "not measured." **INFERENCE** — hiding the uninstrumented steps would make a two-step funnel look complete and would misrepresent how much Cocorra knows about its own core loop.

### D-2 `GET /Api/V1/Analytics/Rooms/Participation`

| Field | Value |
|---|---|
| **Purpose** | Conversion and speaking depth. **Replaces** the current `/Analytics/Participation` |
| **Metrics** | M-101, M-203 (distribution), M-401 |
| **Segmentation** | `category`, `selectionMode`, host |
| **Trust** | M-101 **VERIFIED**; M-401 **CONDITIONALLY RELIABLE** |

**RECOMMENDATION** — `Meta` for M-401 must carry the limitation *"measures unmuted microphone time, not audio"* as a first-class field. **INFERENCE** — that caveat is the difference between "spoke for 20 minutes" and "had an open mic for 20 minutes," and it cannot be resolved without LiveKit telemetry.

**FACT** — Top Speakers and Users-Who-Raised-Hand are **removed** from this response, not hidden (R-8).

### D-3 `GET /Api/V1/Analytics/Rooms/Detail/{roomId}`

| Field | Value |
|---|---|
| **Purpose** | Deepest drill-down: a single room's full picture |
| **Metrics** | Joiners, speakers, hand raises, promotions, speaking seconds, reports, duration |
| **Freshness** | Live query — a single room is cheap and may be inspected while still live |
| **Trust** | Mixed; per-field trust entries |

**INFERENCE** — this is the terminal node of every drill-down path in `09-recommended-dashboard.md` and the point at which an investigator stops asking "which segment" and starts looking at a specific event sequence.

---

## Group E — Safety

### E-1 `GET /Api/V1/Analytics/Safety/Overview`

| Field | Value |
|---|---|
| **Purpose** | Normalised safety trend. **Replaces** the raw-count `/Analytics/Reports` |
| **Metrics** | M-500, M-501, category mix, M-502 |
| **Segmentation** | **by room category — the primary cut** |
| **Trust** | **VERIFIED** |

**INFERENCE — the highest-stakes endpoint in the catalogue.** Two of three room categories are `Relationships` and `MentalHealth`. Both inputs already exist and are verified; the segmentation costs one `GROUP BY` and has never been run. **RECOMMENDATION** — return absolute counts beside every rate; with three categories the cells can be small enough that a percentage alone misleads.

### E-2 `GET /Api/V1/Analytics/Safety/Moderation`

| Field | Value |
|---|---|
| **Purpose** | Enforcement effectiveness |
| **Metrics** | Action mix, hours-to-action, recidivism |
| **Trust** | **NOT AVAILABLE** until E-17 `moderation_action_taken` ships |

**RECOMMENDATION** — do not build this endpoint before the event exists. **INFERENCE** — an endpoint returning empty results is indistinguishable from one reporting that moderation is inactive, which is the R-7 failure in a different form.

---

## Group F — Engagement & Reliability

### F-1 `GET /Api/V1/Analytics/Engagement/Social`

| Field | Value |
|---|---|
| **Purpose** | Whether social surfaces are used and reciprocated |
| **Metrics** | M-600, friend requests sent/accepted, friendship utilisation |
| **Freshness** | Live query — relational tables, unaffected by the raw purge |
| **Trust** | M-600 **VERIFIED** |

**RECOMMENDATION** — return reciprocity **before** volume in the payload ordering. **INFERENCE** — a high volume of one-directional messages is a warning sign, plausibly unwanted contact, not an engagement win. Ordering the response so reciprocity is read first is a small design choice that reduces the chance of the wrong reading.

### F-2 `GET /Api/V1/Analytics/System/Reliability`

| Field | Value |
|---|---|
| **Purpose** | The only systematic reliability signal Cocorra has |
| **Metrics** | M-601, support first-response and resolution time, M-602, FCM token coverage |
| **Trust** | M-601 **CONDITIONALLY RELIABLE** — `Meta` must carry *"proxy — no error tracking exists"* |

**FACT** — no analytics endpoint currently covers support at all; the data exists and is unexposed (GAP-10).

### F-3 `GET /Api/V1/Analytics/System/Health`

| Field | Value |
|---|---|
| **Purpose** | Pipeline health and data freshness |
| **Decision supported** | Can I trust what I am looking at right now? |
| **Metrics** | Aggregation lag, consecutive failures, dead-lettered count, dropped-on-enqueue count, snapshot gaps |
| **Source** | `AggregationCheckpoint`, dead-letter counters (`18-`) |
| **Trust** | **VERIFIED** |

**INFERENCE — this endpoint closes the loop that `08a` opens.** A metric's trust level is meaningless if the pipeline feeding it stopped three days ago. **FACT** — with no structured logging sink, no APM, and no metrics export, the dead-letter table could fill silently and nobody would know. This endpoint is the only proposed mechanism that makes the durability work observable.

---

## Group G — Metric Registry

### G-1 `GET /Api/V1/Analytics/Metrics/Registry`

| Field | Value |
|---|---|
| **Purpose** | The full contract for every metric, served from `IMetricRegistry` |
| **Decision supported** | Can I rely on this number, and what is it not telling me? |
| **Returns** | All fields from `14-metric-contracts.md` |
| **Trust** | N/A — this *is* the trust surface |

**INFERENCE** — serving the registry from the same in-code source that the computation layer reads is what prevents documentation drift. A markdown-only contract diverges from the code within one release; an endpoint backed by `IMetricRegistry` cannot.

---

# Endpoint Disposition

| Existing endpoint | Disposition | Successor |
|---|:--:|---|
| `/Analytics/Summary` | **REPLACE** | A-1 (per-metric trust instead of a composite) |
| `/Analytics/Users/Growth` | **MODIFY** | Registration counts kept; status breakdown replaced (TRUST-02) |
| `/Analytics/Rooms` | **MODIFY** | Keep counts and category mix; **remove** `AvgDurationHours` (TRUST-09) |
| `/Analytics/Participation` | **REPLACE** | D-2. **Remove** Top Speakers and hand-raise count |
| `/Analytics/Reports` | **MODIFY** | E-1 — normalised and segmented by category |
| `/Analytics/Funnel` | **REPLACE** | C-1 — sequential |
| `/Analytics/Retention` | **REPLACE** | M-102 via A-1 |
| `/Analytics/Rooms/Active` | **KEEP** | Sound; use `UniqueJoiners`, not `JoinEvents` |
| `/Analytics/PeakHours` | **MODIFY** | Add timezone context to `Meta` |
| `/Analytics/VoiceVerification/DropOff` | **MODIFY** | Folded into C-1/C-2 |
| `/Analytics/Participation/ActiveVsPassive` | **MODIFY** | Host exclusion applied (TRUST-01) |
| `/Admin/Dashboard/Stats` | **KEEP** | Sound as a snapshot; add a history series from RM-5 |

**New**: A-1, B-1, B-2, B-3, C-1, C-2, C-3, D-1, D-2, D-3, E-1, E-2, F-1, F-2, F-3, G-1.

---

# Cross-Cutting Design

## Caching

**RECOMMENDATION — keep `AnalyticsService` unchanged.** **FACT** — `IMemoryCache` with a 10-minute TTL and per-metric `SemaphoreSlim` double-checked locking is already correct for a single-instance deployment.

**INFERENCE** — read models reduce what the cache protects against, but it costs nothing to keep and still absorbs repeated identical requests. Cache TTL should not exceed the aggregation interval; with hourly rollups, 10 minutes remains appropriate.

## Authorization

**RECOMMENDATION** — keep `[Authorize(Roles = "Admin,Coach")]` for Groups A–D and G.

**RECOMMENDATION — restrict Groups E and F to `Admin`.** **INFERENCE** — safety detail (E-1, E-2) exposes reported-user identities and per-host report concentration; a coach seeing their own report statistics is reasonable, seeing another host's is not. F-3 exposes internal pipeline state. Neither belongs in the Coach role.

**RECOMMENDATION** — B-2's host leaderboard should scope a Coach to their own rows. **INFERENCE** — a cross-host comparison visible to every coach changes the social dynamics of the platform in ways that are a product decision, not an API default.

## Pagination

**RECOMMENDATION** — every list-returning endpoint (B-2, D-3 event lists, E-1 repeat-reported users) takes `page` and `pageSize` with a hard cap.

**FACT** — the existing `AnalyticsController` validates `limit` and returns `BadRequest` on invalid values (`AnalyticsControllerTests.cs` covers this). Follow the same pattern.

## Error semantics

| Condition | Response |
|---|---|
| Invalid date range (`from > to`) | `400` with a clear message |
| Range exceeding the cap | `400` naming the maximum |
| Metric not yet instrumented | `200` with `null` data and `dataAvailableFromUtc` — **never** `0` (R-7) |
| Aggregation stale beyond a threshold | `200` with data plus `Meta.freshness.pipelineHealthy = false` |
| Deprecated endpoint | `410 Gone` with a pointer to the successor |

**INFERENCE on the stale case** — returning an error would hide usable data; returning it silently would hide that it is old. Returning the data with an explicit unhealthy flag is the only option that lets the client decide, and it is consistent with the trust-metadata approach throughout.

---

# Validation

| # | Test | Asserts |
|:--:|---|---|
| **1** | Every endpoint populates `Meta.metrics` | No endpoint returns bare data without trust metadata (R-6) |
| **2** | Uninstrumented period | Returns `null` + `dataAvailableFromUtc`, never `0` (R-7) |
| **3** | Funnel monotonicity | C-1 and D-1 never return a widening funnel |
| **4** | Deprecated fields absent | Top Speakers, hand-raise count, and `AvgDurationHours` are gone from responses (R-8) |
| **5** | Host exclusion | Every room-participation endpoint excludes hosts (INV-7) |
| **6** | Timezone context | Every date-bucketed endpoint returns `suggestedDisplayOffsetMinutes` |
| **7** | Comparison period | `compareTo` returns a comparable window and flags `isPartialPeriod` |
| **8** | Authorization | Coach receives `403` on Admin-only routes |
| **9** | Freshness | `Meta.freshness` reflects the actual `AggregationCheckpoint` state |
| **10** | Registry consistency | G-1 output matches the metadata embedded in each metric's own response |

**INFERENCE — test 10 is the one that keeps the system honest over time.** If the registry and the per-response metadata are served from the same `IMetricRegistry` instance, they cannot diverge. Asserting it continuously catches the case where someone adds a metric to a response without a contract — which is exactly how the current dashboard accumulated three UNRELIABLE metrics with no warning label.

---

# Summary

| Group | Endpoints | New? | Primary decision |
|---|:--:|:--:|---|
| **A** Platform Health | 1 | Replaces Summary | Is value delivery growing? |
| **B** Supply Health | 3 | **All new** | Recruit or enable coaches? |
| **C** Activation | 3 | 2 new, 1 replaces Funnel | Where does onboarding leak? |
| **D** Room Participation | 3 | 2 new, 1 replaces Participation | Where does the core loop break? |
| **E** Safety | 2 | 1 modified, 1 new | Are MentalHealth rooms riskier? |
| **F** Engagement & Reliability | 3 | **All new** | Is the product working? |
| **G** Registry | 1 | **New** | Can I trust this number? |

**Three conclusions (INFERENCE).**

**`Response<T>.Meta` makes the trust layer free.** The hardest requirement in `08a` — that trust information travel with the number — is satisfied by populating a field that already exists on every response and is currently always null. No breaking change, no versioning, no client coordination.

**Group B is the largest gap and the cheapest to close.** Three endpoints, no new events, no schema change, all reading verified relational data. It is entirely absent from the current API and answers the platform's most consequential unwatched question.

**R-7 is the rule that prevents the new system repeating the old one's failure.** Returning `0` for an uninstrumented period is not a display bug; it is a fabricated finding. Every metric that ships before its events do must return `null` with an availability date, and the client must render that as an honest gap.
