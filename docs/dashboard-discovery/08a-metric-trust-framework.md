# 08a — Metric Trust Framework

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Depends on**: `07-metric-verification.md`, `07-decision-framework.md` (Findings A–E), `05-analytics-gap-analysis.md`
> **Scope**: Documentation only.

---

## Why This Exists

**FACT** — `07-metric-verification.md` established that of the twelve metrics in the current dashboard, three are misleading or incorrect (User Growth, Retention Cohort, and the participation hand-raise count), and several more carry material caveats.

**INFERENCE — the problem this creates.** Those three metrics look exactly as credible as the nine that are sound. They render in the same cards, with the same precision, next to the same date pickers. A product owner reading the dashboard has no way to tell that the retention number is systematically too low or that the growth chart rewrites its own history. The failure is not that Cocorra computes some metrics incorrectly — every analytics system does. The failure is that **the dashboard does not carry the information needed to distinguish a trustworthy number from an untrustworthy one**.

This framework fixes that by requiring every metric to carry its own provenance and its own limitations, displayed at the point of use.

---

## The Required Metadata Structure

Every metric on the Cocorra dashboard must define all thirteen fields. A metric missing any field is **EXPERIMENTAL** by default until completed.

```
Metric Name
Business Definition
Technical Definition
Formula
Population
Inclusions
Exclusions
Time Window
Timezone
Data Source
Historical Reliability
Known Limitations
Trust Level
```

### Field-by-field

| Field | What it must contain | Why it matters for Cocorra specifically |
|---|---|---|
| **Metric Name** | The exact name shown in the UI. | Must match the label, or the register cannot be found from the number. |
| **Business Definition** | One sentence a non-engineer can act on. No formulas. | **INFERENCE** — Several current metric names promise more than they deliver. "Top Speakers" implies speaking; it measures unmuted time. The business definition is where that gap becomes visible. |
| **Technical Definition** | The precise computational statement, naming tables, columns, and event types. | Removes ambiguity between "joins" (event rows) and "joiners" (distinct users) — a distinction that matters, since `room_joined` fires per SignalR reconnect. |
| **Formula** | The literal expression or SQL-equivalent. | `07-metric-verification.md` demonstrated the value: publishing the formula is what exposed the exact-day retention bug. |
| **Population** | Which entities are in scope before filtering. | Cocorra's default authorization requires `VerificationStatus=Active`. "All users" and "users who can actually use the product" are materially different populations. |
| **Inclusions** | Edge cases deliberately counted. | e.g. Do re-record submissions count as new voice submissions? |
| **Exclusions** | Edge cases deliberately removed. | **FACT, Finding A** — host exclusion is mandatory for every room-participation metric. This field is where that becomes auditable rather than assumed. |
| **Time Window** | The period, and whether it is fixed, rolling, or all-time. | Distinguishes a snapshot (`/Admin/Dashboard/Stats`, no date filter) from a windowed metric. |
| **Timezone** | The timezone of bucketing. | **FACT** — all Cocorra analytics are UTC; the user base is MENA (UTC+2/+3). A "daily" metric bucketed in UTC splits the local evening across two days. |
| **Data Source** | Table(s) or event type(s), and whether server- or client-emitted. | **FACT** — `notification_opened`, `feature_viewed`, and `room_create_started` are client-emitted and therefore untrusted; everything else is server-authoritative. Consumers must be able to tell which. |
| **Historical Reliability** | One of the four classifications below. | The most important field. See the next section. |
| **Known Limitations** | Every known bias, with its **direction** where known. | **INFERENCE** — direction is what makes a biased metric usable. "Return rate is biased upward by hard deletes" is actionable; "return rate may be inaccurate" is not. |
| **Trust Level** | One of the four levels below. | The badge shown next to the number. |

---

## Historical Reliability Classification

This classification exists because the previous audit's central finding was a **snapshot-versus-history** problem: the schema stores current state where analytics needs transition history (`05-analytics-gap-analysis.md`, GAP-05).

| Classification | Meaning | Cocorra examples |
|---|---|---|
| **HISTORICALLY ACCURATE** | The value for a past period is the same today as it was then. Safe for trend analysis. | Registrations by month (`ApplicationUser.CreatedAt`); reports by month (`Report.CreatedAt`); room joins by week (`room_joined` events, within 180 days). |
| **CURRENT SNAPSHOT ONLY** | The metric describes now and has no past. Comparing it to a historical figure is meaningless, because no historical figure exists. | `/Admin/Dashboard/Stats` status counts; FCM token coverage; pending queue depth; `IsHandRaised` counts; block prevalence. |
| **PARTIALLY RECONSTRUCTABLE** | Past values can be rebuilt from a secondary source, usually events, within their retention window. | Historical user status (from `voice_verification_result`, ≤180 days); admin review latency (from the event pair, ≤180 days); room go-live time (approximated by `MIN(RoomParticipant.JoinedAt)`). |
| **NOT HISTORICALLY RELIABLE** | The value for a past period **changes** when queried later. Trends built on it are artefacts. | User Growth status breakdown (**FACT** — current status backdated into historical buckets); any all-time count affected by hard deletes; `TotalSpokenSeconds` aggregates for hosts. |

### The classification that causes the most damage

**NOT HISTORICALLY RELIABLE** is the dangerous one, and it deserves its own warning.

**FACT** — `AnalyticsRepository.GetUserGrowthAsync` (`AnalyticsRepository.cs:21-93`) buckets users by `CreatedAt` and counts them by **current** `Status`.

**INFERENCE — why this is worse than a simple error.** The distortion is *time-dependent*. Recent months look accurate, because few of their users have changed status yet. Older months are heavily rewritten, because their users have had months to be banned, rejected, or deleted. The chart therefore displays a systematic downward slope in "Active" for older cohorts that is a pure artefact of elapsed time.

The natural reading of that chart — *"our early users were lower quality"* — is false, and it is the reading almost anyone would arrive at. A metric that reliably produces a specific wrong conclusion is more harmful than a metric that produces no conclusion.

**RECOMMENDATION** — any metric classified NOT HISTORICALLY RELIABLE should either be removed from trend visualisations entirely, or rendered with an explicit warning stating that historical values change. It should never appear as a plain line chart.

---

## Trust Level Definitions

| Level | Definition | What it licenses |
|---|---|---|
| **VERIFIED** | Formula independently checked; data source server-authoritative; no known bias affecting the conclusion; historically accurate within its stated window. | Safe as the sole basis for a decision. |
| **CONDITIONALLY RELIABLE** | Correct **given a stated condition** — a required exclusion, a known-direction bias, or a bounded window. Usable if the condition is respected. | Safe for a decision **when the condition is stated alongside the number**. |
| **EXPERIMENTAL** | Newly defined, unvalidated against a second source, or dependent on a client-emitted event. | May inform investigation. Must not be the sole basis for a decision. |
| **UNRELIABLE** | Known to be wrong, or resting on a source with an unquantified bias large enough to reverse a conclusion. | Must not appear on the dashboard without a visible warning, and must never drive a decision. |

**RECOMMENDATION — display rules.** The trust badge travels with the number, at the point of use, not in a separate document. A wiki page about metric definitions is not read; a badge beside a KPI is. Specifically:
- **VERIFIED** — badge only.
- **CONDITIONALLY RELIABLE** — badge plus the condition, in one line, adjacent to the number.
- **EXPERIMENTAL** — badge plus a visual de-emphasis.
- **UNRELIABLE** — either removed, or shown with an explicit inline warning. **INFERENCE** — removal is usually the right choice. A warned-but-visible wrong number still gets screenshotted into a deck without its warning.

---

# Metric Register — Current Dashboard Metrics

Every metric currently exposed by `AdminController` and `AnalyticsController`, with complete trust metadata. Trust levels here are consistent with `07-metric-verification.md`, adjusted where Findings A–E change the assessment.

---

## M-01 — Total / Active / Pending / Banned / Rejected / ReRecord Users

| Field | Value |
|---|---|
| **Metric Name** | Dashboard Stats (user status counts) |
| **Business Definition** | How many users exist right now in each verification state. |
| **Technical Definition** | Count of `AspNetUsers` rows grouped by the `Status` integer column, with no date filter. |
| **Formula** | `SELECT Status, COUNT(*) FROM AspNetUsers GROUP BY Status` |
| **Population** | All user rows currently in the database. |
| **Inclusions** | Every status value (0 Pending, 1 Active, 2 Rejected, 3 Banned, 4 ReRecord). |
| **Exclusions** | Deleted accounts — **FACT**, `DeleteAccountAsync` hard-deletes the row. |
| **Time Window** | None. All-time, point-in-time. |
| **Timezone** | N/A (no date dimension). |
| **Data Source** | `AspNetUsers` table via `AdminService.GetDashboardStatsAsync` (`AdminService.cs:383-401`). Server-authoritative. |
| **Historical Reliability** | **CURRENT SNAPSHOT ONLY** |
| **Known Limitations** | (1) No time dimension — yesterday's value is unrecoverable. (2) Hard deletes mean totals can *decrease*, so this is not a cumulative registration count. (3) Cannot answer "is the backlog growing," which is the question it is most often used for. |
| **Trust Level** | **CONDITIONALLY RELIABLE** — accurate as a snapshot; must never be compared to a remembered earlier value. |

---

## M-02 — User Growth (registration trend + status breakdown)

| Field | Value |
|---|---|
| **Metric Name** | User Growth |
| **Business Definition** | Claims to show how many users registered in each period and their status. |
| **Technical Definition** | Users bucketed by `CreatedAt`, counted by **current** `Status`. |
| **Formula** | `SELECT CreatedAt, Status, MBTI, Age FROM AspNetUsers WHERE CreatedAt BETWEEN @from AND @to` → in-memory grouping by month/day, counting current `Status` per bucket. |
| **Population** | Users with `CreatedAt` in the window, still present in the database. |
| **Inclusions** | All statuses. |
| **Exclusions** | Deleted users. |
| **Time Window** | Caller-supplied `from`/`to`; monthly or daily buckets. |
| **Timezone** | UTC. |
| **Data Source** | `AspNetUsers` via `AnalyticsRepository.cs:21-93`. |
| **Historical Reliability** | **NOT HISTORICALLY RELIABLE** |
| **Known Limitations** | (1) **FACT** — status is backdated: a January registrant banned in June is counted as Banned in January. (2) Distortion grows with age of the bucket, producing a false quality gradient across cohorts. (3) Hard deletes shrink historical counts over time. (4) MBTI distribution is window-scoped, not all-user. (5) All users in the window are materialised into memory. |
| **Trust Level** | **UNRELIABLE** |
| **RECOMMENDATION** | The **registration count** per bucket is sound and should be kept as a separate VERIFIED metric. The **status breakdown** should be removed or reconstructed from `voice_verification_result` events within the 180-day window (GAP-02). |

---

## M-03 — Platform Summary

| Field | Value |
|---|---|
| **Metric Name** | Platform Summary |
| **Business Definition** | Combined snapshot of users, rooms, participation, and reports. |
| **Technical Definition** | Parallel execution of `GetUserGrowthAsync`, `GetRoomAnalyticsAsync`, `GetParticipationStatsAsync`, `GetReportInsightsAsync`, bundled with a `GeneratedAt` timestamp. |
| **Formula** | Composition of M-02, M-04, M-05, M-06. |
| **Population** | Union of the four sub-metric populations. |
| **Inclusions / Exclusions** | Inherited from each component. |
| **Time Window** | Caller-supplied; cached 10 minutes with `SemaphoreSlim` stampede protection. |
| **Timezone** | UTC. |
| **Data Source** | `AnalyticsService.GetPlatformSummaryAsync`. |
| **Historical Reliability** | **NOT HISTORICALLY RELIABLE** — inherits M-02. |
| **Known Limitations** | **INFERENCE** — a composite is only as trustworthy as its weakest component. Bundling M-02 (UNRELIABLE) with M-06 (VERIFIED) into one response means a consumer cannot tell which parts to trust. The composite structure actively obscures the trust distinction. |
| **Trust Level** | **CONDITIONALLY RELIABLE** — usable only if each component is evaluated separately. |
| **RECOMMENDATION** | Do not present composites as single trust units. Either split the response or attach per-section trust levels within it. |

---

## M-04 — Room Analytics

| Field | Value |
|---|---|
| **Metric Name** | Room Analytics |
| **Business Definition** | How many rooms existed, in which categories, with how many participants and what duration. |
| **Technical Definition** | Rooms filtered by `StartDate` in window; per-room participant count; grouped by category; ordered by participant count. |
| **Formula** | See `07-metric-verification.md` M-3. `AvgDurationHours = AVG(Room.DurationHours)`. |
| **Population** | Rooms with `StartDate` in the window. |
| **Inclusions** | All room statuses; all participant statuses in the count. |
| **Exclusions** | None — **and this is the defect**. |
| **Time Window** | Caller-supplied, filtered on `StartDate`. |
| **Timezone** | UTC. |
| **Data Source** | `Rooms` + `RoomParticipants` via `AnalyticsRepository.cs:98-164`. |
| **Historical Reliability** | **HISTORICALLY ACCURATE** for counts and categories; **NOT HISTORICALLY RELIABLE** for duration. |
| **Known Limitations** | (1) **FACT** — `ParticipantCount` includes `Left`, `Kicked`, `Rejected`, `PendingApproval`, inflating "top rooms." (2) **FACT** — `AvgDurationHours` is the *configured* duration (only ever 2 or 3), not actual runtime; it is a constant-ish value dressed as a measurement. (3) **FACT, Finding C** — actual go-live time is not recorded, so real duration is unobtainable. (4) `StartDate` is the *scheduled* date for scheduled rooms, so window filtering can misplace a room that started late. |
| **Trust Level** | **CONDITIONALLY RELIABLE** — room and category counts are sound; participant counts require status filtering; **duration must not be used at all**. |

---

## M-05 — Participation Stats

| Field | Value |
|---|---|
| **Metric Name** | Participation Stats (incl. Top Speakers, Users Who Raised Hand) |
| **Business Definition** | Claims to show who participated, who spoke most, and when activity peaks. |
| **Technical Definition** | `RoomParticipants` filtered by `JoinedAt` in window; sum `TotalSpokenSeconds` per user; count `IsHandRaised`; group by `JoinedAt.Hour`. |
| **Formula** | See `07-metric-verification.md` M-4. |
| **Population** | Participant rows with `JoinedAt` in the window. |
| **Inclusions** | All participants **including hosts** — **and this is the defect**. |
| **Exclusions** | None. |
| **Time Window** | Caller-supplied, on `JoinedAt`. |
| **Timezone** | UTC. |
| **Data Source** | `RoomParticipants` via `AnalyticsRepository.cs:166-231`. |
| **Historical Reliability** | **NOT HISTORICALLY RELIABLE** |
| **Known Limitations** | (1) **FACT, Finding A** — hosts are inserted with `IsMuted=false` and `LastUnmutedAt=UtcNow` at room start, so a silent host accrues the room's full 2–3 hour duration as "spoken time." Top Speakers is effectively a list of coaches ranked by room length. (2) **FACT** — `IsHandRaised` is a live boolean reset by `LowerHand`; the historical count is near-permanently ~0 and the metric is meaningless for any past window. (3) **FACT, Finding B** — `TotalSpokenSeconds` measures *unmuted time*, not audio; no LiveKit telemetry exists. (4) **FACT** — `JoinedAt` is overwritten on rejoin (`RoomHub.cs:245-253`), so a user can appear in multiple windows. (5) **FACT, Correction 1** — finalisation depends on `RoomHub`'s static in-memory `_connections`; an API restart during live rooms leaves segments unfinalised. (6) Peak hours are by *join* time, not activity time. |
| **Trust Level** | **UNRELIABLE** |
| **RECOMMENDATION** | Remove Top Speakers and Users-Who-Raised-Hand from the dashboard until `mic_deactivated` and `hand_raised` exist (GAP-01, GAP-06). **INFERENCE** — the concern is not that these numbers are imprecise; it is that Top Speakers looks entirely plausible while being systematically wrong, and it contradicts M-12 about the same people at the same time. |

---

## M-06 — Report Insights

| Field | Value |
|---|---|
| **Metric Name** | Report Insights |
| **Business Definition** | How many reports were filed, in which categories, at what status, against whom. |
| **Technical Definition** | `Reports` filtered by `CreatedAt`; grouped by `Category`, `Status` (string compare), and `ReportedUserId`. |
| **Formula** | See `07-metric-verification.md` M-6 equivalent. |
| **Population** | Report rows with `CreatedAt` in the window. |
| **Inclusions** | All reports. |
| **Exclusions** | None explicit. |
| **Time Window** | Caller-supplied, on `CreatedAt` (indexed). |
| **Timezone** | UTC. |
| **Data Source** | `Reports` via `AnalyticsRepository.cs:233-298`. Server-authoritative. |
| **Historical Reliability** | **HISTORICALLY ACCURATE** for volume and category; **PARTIALLY RECONSTRUCTABLE** for status. |
| **Known Limitations** | (1) **FACT** — `Status` is a free-form string; only "Open", "Resolved", "InProgress" are recognised, and any other value is silently dropped from status counts. (2) **FACT** — `ReportedUserId` is `SetNull` on user delete, so a reported user who deletes their account disappears from "most reported." (3) Raw counts are not normalised by platform activity, so they rise with growth. |
| **Trust Level** | **VERIFIED** for volume and category mix. **CONDITIONALLY RELIABLE** for status counts. |
| **RECOMMENDATION** | Normalise to reports per 1,000 room joins, and add the by-category cut (GAP-12), which is the highest-value uncomputed analysis in the system. |

---

## M-07 — Funnel Analysis

| Field | Value |
|---|---|
| **Metric Name** | Funnel |
| **Business Definition** | Claims to show how many users completed each onboarding step. |
| **Technical Definition** | For each requested event type, `COUNT(DISTINCT UserId)` in the window — **independently per step, with no ordering constraint**. |
| **Formula** | `SELECT EventType, COUNT(DISTINCT UserId) FROM UserEvents WHERE EventType IN (@steps) AND OccurredAtUtc BETWEEN @from AND @to AND UserId IS NOT NULL GROUP BY EventType` |
| **Population** | Users with any of the named events in the window. |
| **Inclusions** | All matching events. |
| **Exclusions** | Events with NULL `UserId` (which includes events from deleted users, since `UserEvent.UserId` is `SetNull`). |
| **Time Window** | Caller-supplied. |
| **Timezone** | UTC. |
| **Data Source** | `UserEvents` via `AnalyticsRepository.cs:300-322`. Server-emitted. |
| **Historical Reliability** | **PARTIALLY RECONSTRUCTABLE** — bounded by the 180-day purge. |
| **Known Limitations** | (1) **FACT** — not sequential. Steps are counted independently, so the "funnel" can *widen* downward, which is impossible in a real funnel and is a visible symptom of the defect. (2) **FACT** — `EventCleanupService` purges beyond 180 days. (3) **FACT** — events can be dropped under load: `EventTracker` uses a bounded channel (10K) with `DropWrite`, so a sustained burst silently loses events. |
| **Trust Level** | **EXPERIMENTAL** — usable for relative comparison of step magnitudes; **not** a funnel. |
| **RECOMMENDATION** | Recompute sequentially with a per-user time-ordering constraint (GAP-13). The underlying data fully supports this; only the query is wrong. |

---

## M-08 — Retention Cohort

| Field | Value |
|---|---|
| **Metric Name** | Retention (D1 / D7 / D30) |
| **Business Definition** | Claims to show what share of a registration cohort came back. |
| **Technical Definition** | Cohort = users with `cohortEvent` in window, cohort date = `MIN(OccurredAtUtc)`. Retention at day N = users with `activeEvent` **exactly** N days after their cohort date. |
| **Formula** | `COUNT(DISTINCT UserId WHERE DATEDIFF(DAY, CohortDate, OccurredAtUtc) = @day) / CohortSize` |
| **Population** | Users with `user_registered` in the window (default). |
| **Inclusions** | Default `activeEvent` is `session_started`. |
| **Exclusions** | Deleted users (rows gone; events anonymised by `SetNull`). |
| **Time Window** | Caller-supplied for the cohort; **unbounded** for the activity lookup. |
| **Timezone** | UTC. |
| **Data Source** | `UserEvents` via `AnalyticsRepository.cs:324-392`. |
| **Historical Reliability** | **NOT HISTORICALLY RELIABLE** |
| **Known Limitations** | (1) **FACT** — exact-day matching (`== day`): a user active on days 2, 3, and 5 contributes zero to D1. Systematically undercounts, severely. (2) **FACT** — `session_started` is cookie-based (`SessionTrackingMiddleware:53`) on a Flutter client, and deduplication uses in-process `IMemoryCache` lost on restart. (3) **FACT** — 180-day purge caps cohort depth. (4) **FACT** — hard deletes bias every rate upward. (5) The activity query has no time bound and loads all matching events for the cohort. |
| **Trust Level** | **UNRELIABLE** |
| **RECOMMENDATION** | Replace with room-join-based weekly return (GAP-04). **INFERENCE** — this is not a repair of the existing metric but a different and better one: `room_joined` is server-authoritative, cookie-independent, and measures return to the product's actual value event rather than return to a cookie. |

---

## M-09 — Most Active Rooms

| Field | Value |
|---|---|
| **Metric Name** | Most Active Rooms |
| **Business Definition** | Which rooms drew the most join activity. |
| **Technical Definition** | `room_joined` events grouped by the promoted `RoomId` column; join event count and distinct joiner count; enriched with room title and category. |
| **Formula** | See `07-metric-verification.md` M-9 equivalent. |
| **Population** | `room_joined` events with non-null `RoomId` in the window. |
| **Inclusions** | All joins, including repeat joins by the same user. |
| **Exclusions** | None. **Hosts are included** — see limitations. |
| **Time Window** | Caller-supplied, on `OccurredAtUtc`. |
| **Timezone** | UTC. |
| **Data Source** | `UserEvents` via `AnalyticsRepository.cs:399-444`. Server-emitted; `RoomId` is a promoted, indexed column. |
| **Historical Reliability** | **HISTORICALLY ACCURATE** within 180 days. |
| **Known Limitations** | (1) **FACT** — `room_joined` fires on every SignalR reconnect, so `JoinEvents` is inflated by reconnection churn; `UniqueJoiners` is the sound figure. (2) **INFERENCE** — a room with poor network conditions can rank highly on `JoinEvents` purely through reconnects, which inverts the metric's meaning for exactly the rooms that went worst. (3) Hosts are counted as joiners. |
| **Trust Level** | **CONDITIONALLY RELIABLE** — use `UniqueJoiners`; treat `JoinEvents` as a reconnection-contaminated figure rather than an engagement measure. |

---

## M-10 — Peak Active Hours

| Field | Value |
|---|---|
| **Metric Name** | Peak Hours |
| **Business Definition** | Which hours of the day see the most activity. |
| **Technical Definition** | All `UserEvents` in window grouped by `OccurredAtUtc.Hour`; event count and distinct active users; all 24 hours zero-filled. |
| **Formula** | `SELECT DATEPART(HOUR, OccurredAtUtc), COUNT(*), COUNT(DISTINCT UserId) FROM UserEvents WHERE OccurredAtUtc BETWEEN @from AND @to GROUP BY DATEPART(HOUR, OccurredAtUtc)` |
| **Population** | All events of every type in the window. |
| **Inclusions** | Every event type, weighted equally. |
| **Exclusions** | None. |
| **Time Window** | Caller-supplied. |
| **Timezone** | **UTC** — and this is the defect. |
| **Data Source** | `UserEvents` via `AnalyticsRepository.cs:447-469`. |
| **Historical Reliability** | **HISTORICALLY ACCURATE** within 180 days. |
| **Known Limitations** | (1) **FACT** — UTC only, while the user base is MENA (UTC+2/+3). A coach scheduling against this chart would target a slot 2–3 hours off the real local peak. (2) **INFERENCE** — all event types are weighted equally, so a burst of `session_started` counts the same as room participation; the chart measures API traffic, which is not the same thing as user activity. |
| **Trust Level** | **CONDITIONALLY RELIABLE** — sound as UTC event-volume; **must** be converted to local time before anyone schedules against it. |

---

## M-11 — Voice Verification Drop-Off

| Field | Value |
|---|---|
| **Metric Name** | Voice Verification Drop-Off |
| **Business Definition** | What share of users who submitted a voice recording were activated. |
| **Technical Definition** | Distinct users with `voice_verification_submitted` vs distinct users with `activation_completed`, both in the window. |
| **Formula** | `CompletionRate = Completed / Started * 100` |
| **Population** | Users with either event in the window. |
| **Inclusions** | Re-record submissions (deduplicated by DISTINCT). |
| **Exclusions** | Events with NULL `UserId`. |
| **Time Window** | Caller-supplied. |
| **Timezone** | UTC. |
| **Data Source** | `UserEvents` via `AnalyticsRepository.cs:472-498`. Server-emitted; `activation_completed` is deduplicated at emit time (`AdminService` checks `AnyAsync` first). |
| **Historical Reliability** | **PARTIALLY RECONSTRUCTABLE** — bounded by the 180-day purge. |
| **Known Limitations** | (1) **INFERENCE** — the two events are counted within the *same* window but are separated by admin review latency. A user who submits on the last day of the window is counted in the denominator and cannot yet be in the numerator, so the completion rate is depressed at the window's trailing edge. The bias grows as review latency grows — precisely when the metric is most likely to be examined. (2) 180-day cap. (3) Does not distinguish `Rejected` from `ReRecord` from still-pending. |
| **Trust Level** | **CONDITIONALLY RELIABLE** — use windows materially longer than the review latency, and read the outcome mix from `voice_verification_result` alongside it. |

---

## M-12 — Active vs Passive Participation

| Field | Value |
|---|---|
| **Metric Name** | Active vs Passive Participation |
| **Business Definition** | What share of room participants actually spoke. |
| **Technical Definition** | Distinct users with `room_joined` in window; of those, distinct users with `mic_activated`; passive = the difference. |
| **Formula** | `ActiveRate = Speakers / Joiners * 100` |
| **Population** | Users with `room_joined` in the window. |
| **Inclusions** | **Hosts are included in the denominator** — and this is the defect. |
| **Exclusions** | None. |
| **Time Window** | Caller-supplied. |
| **Timezone** | UTC. |
| **Data Source** | `UserEvents` via `AnalyticsRepository.cs:501-540`. Both events server-emitted. |
| **Historical Reliability** | **HISTORICALLY ACCURATE** within 180 days. |
| **Known Limitations** | (1) **FACT, Finding A** — hosts never emit `mic_activated` for their initial open mic, so every room contributes at least one artificial "passive listener." In small rooms this materially depresses the reported active rate. (2) **INFERENCE** — this metric and M-05 classify the same host oppositely at the same moment: top speaker there, passive listener here. (3) **FACT** — the joiner list is materialised into memory and used in a `.Contains()` LINQ query, producing a large `IN (...)` clause that will degrade at scale. (4) **FACT, Finding B** — an activated mic is not audible speech. |
| **Trust Level** | **CONDITIONALLY RELIABLE** — sound **only** with hosts excluded from the denominator. As shipped, it is biased downward by exactly one user per room. |

---

# Trust Summary — Current Dashboard

| # | Metric | Historical Reliability | Trust Level |
|:--:|---|---|:--:|
| M-01 | Dashboard Stats | CURRENT SNAPSHOT ONLY | **CONDITIONALLY RELIABLE** |
| M-02 | User Growth | **NOT HISTORICALLY RELIABLE** | **UNRELIABLE** |
| M-03 | Platform Summary | NOT HISTORICALLY RELIABLE (inherited) | **CONDITIONALLY RELIABLE** |
| M-04 | Room Analytics | HISTORICALLY ACCURATE (counts) / NOT RELIABLE (duration) | **CONDITIONALLY RELIABLE** |
| M-05 | Participation Stats | **NOT HISTORICALLY RELIABLE** | **UNRELIABLE** |
| M-06 | Report Insights | HISTORICALLY ACCURATE | **VERIFIED** |
| M-07 | Funnel | PARTIALLY RECONSTRUCTABLE | **EXPERIMENTAL** |
| M-08 | Retention Cohort | **NOT HISTORICALLY RELIABLE** | **UNRELIABLE** |
| M-09 | Most Active Rooms | HISTORICALLY ACCURATE | **CONDITIONALLY RELIABLE** |
| M-10 | Peak Hours | HISTORICALLY ACCURATE | **CONDITIONALLY RELIABLE** |
| M-11 | Voice Drop-Off | PARTIALLY RECONSTRUCTABLE | **CONDITIONALLY RELIABLE** |
| M-12 | Active vs Passive | HISTORICALLY ACCURATE | **CONDITIONALLY RELIABLE** |

**Distribution:** 1 VERIFIED, 7 CONDITIONALLY RELIABLE, 1 EXPERIMENTAL, 3 UNRELIABLE.

**INFERENCE — what this distribution means.** Exactly one metric out of twelve can be used as the sole basis for a decision without a stated condition. Three are wrong. The remaining seven are usable but only if the reader knows the condition — and today, nothing in the dashboard tells them what the condition is. That is the precise gap this framework exists to close, and it explains the trust verdict in `EXECUTIVE-SUMMARY.md`.

---

# Metric Register Template

**RECOMMENDATION** — every new metric must complete this template before it appears on the dashboard.

```yaml
metric_name:            # exactly as displayed in the UI
business_definition:    # one sentence, no formulas, actionable by a non-engineer
technical_definition:   # precise computation naming tables/columns/event types
formula:                # literal expression or SQL equivalent
population:             # entity set in scope before filtering
inclusions:             # edge cases deliberately counted
exclusions:             # edge cases deliberately removed (state host exclusion explicitly)
time_window:            # fixed | rolling | all-time; the column filtered on
timezone:               # UTC | local; state which, always
data_source:            # table(s) / event type(s); server-emitted or client-emitted
historical_reliability: # HISTORICALLY ACCURATE | CURRENT SNAPSHOT ONLY |
                        # PARTIALLY RECONSTRUCTABLE | NOT HISTORICALLY RELIABLE
known_limitations:      # every known bias, with direction where known
trust_level:            # VERIFIED | CONDITIONALLY RELIABLE | EXPERIMENTAL | UNRELIABLE
owner:                  # who maintains this definition
last_verified:          # date the formula was last independently checked
```

## Mandatory checks before assigning VERIFIED

**RECOMMENDATION** — a metric may be marked VERIFIED only after all six pass:

1. **Formula independently reproduced** against the database by someone other than its author. **INFERENCE** — this is how the exact-day retention bug and the status-backdating bug would have been caught before shipping.
2. **Host exclusion confirmed** for any room-participation metric (Finding A).
3. **Distinct-vs-raw counting confirmed** for any event-derived metric (`room_joined` fires per reconnect).
4. **Historical reliability classified**, not assumed. The default assumption should be CURRENT SNAPSHOT ONLY until proven otherwise, because Cocorra's schema stores state rather than transitions.
5. **Timezone stated.** UTC is the default and is wrong for anything a MENA-based human will schedule against.
6. **Event-drop exposure noted.** **FACT** — `EventTracker` uses a bounded channel (10K) with `DropWrite`; any event-derived metric can silently undercount under sustained load. This is a shared limitation of every event-based metric and should be recorded once and referenced, not rediscovered per metric.

## Re-verification triggers

**RECOMMENDATION** — a metric returns to EXPERIMENTAL and must be re-verified when any of these occur:

- Its underlying event's emit site changes.
- A new emit site for the same event type is added.
- The schema of any source table changes.
- The retention window (currently 180 days) changes.
- A related bug is fixed — **INFERENCE** — a fix changes the data-generating process, so pre-fix and post-fix periods are not comparable. Commit `dc1c933` (FCM delivery) is a concrete instance: any notification metric spanning that deployment compares two different systems.
