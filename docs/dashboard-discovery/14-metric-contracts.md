# 14 — Metric Contracts

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 3
> **Depends on**: `08a-metric-trust-framework.md` (structure), `13-data-trust-correction-plan.md` (corrections), `11-current-state-validation.md` (verified sources)
> **Scope**: Documentation only.

---

## Mandatory Rule

**No metric may enter the dashboard unless it defines all four of:**

```
Business Purpose  +  Technical Definition  +  Formula  +  Validation Method
```

A metric missing any of these is **EXPERIMENTAL** by default and may not be the sole basis for a decision.

## Where these contracts live in code

**RECOMMENDATION** — these contracts must be executable, not documentary. `12-target-analytics-architecture.md` specifies `IMetricRegistry` / `MetricRegistry` in `Cocorra.BLL/Services/Analytics/`, holding one entry per metric. The registry is read by two consumers:

1. The computation layer, for `Exclusions` (host exclusion in particular — INV-7).
2. The API layer, which serialises trust metadata into the already-present, currently-unused `Response<T>.Meta` field (`Cocorra.BLL/Base/Response.cs`).

**INFERENCE** — if a contract lives only in markdown it will drift from the code within one release. Binding the trust metadata to the computation is what prevents the drift, and it is the reason `Meta` matters: it is a transport that already exists on every analytics response and costs nothing to populate.

## Contract field template

```yaml
metric_id:                # stable identifier, never reused
metric_name:              # exactly as displayed in the UI
business_purpose:         # why this exists at all
decision_supported:       # the decision from 07-decision-framework.md
business_definition:      # one sentence, no formula, actionable by a non-engineer
technical_definition:     # precise computation naming tables/columns/event types
formula:                  # literal expression or SQL equivalent
population:               # entity set in scope before filtering
inclusions:               # edge cases deliberately counted
exclusions:               # edge cases deliberately removed — host exclusion stated explicitly
time_window:              # fixed | rolling | all-time; the column filtered on
timezone:                 # UTC | local
data_sources:             # tables
event_sources:            # event types; server- or client-emitted
aggregation_method:       # LIVE QUERY | READ MODEL | HYBRID
historical_reliability:   # HISTORICALLY ACCURATE | CURRENT SNAPSHOT ONLY |
                          # PARTIALLY RECONSTRUCTABLE | NOT HISTORICALLY RELIABLE
known_limitations:        # every known bias, with direction where known
trust_level:              # VERIFIED | CONDITIONALLY RELIABLE | EXPERIMENTAL | UNRELIABLE
owner:                    # role accountable for the definition
validation_method:        # the test that proves it correct
```

## Universal limitations

**FACT** — these apply to every event-derived metric in this document and are recorded once here rather than repeated in 30 contracts:

- **U-1** — Raw events are purged after 180 days (`EventCleanupService.cs`, hardcoded `AddDays(-180)`). No metric has history beyond that until read models accumulate.
- **U-2** — Events can be lost on channel overflow (`DropWrite`) and on flush-batch failure (`batch.Clear()` in `finally`). Absolute counts are lower bounds until TRUST-07 is closed.
- **U-3** — Hard deletes remove users from all longitudinal analysis, biasing every rate involving users **upward** (TRUST-05).
- **U-4** — All computation is UTC. The user base is MENA (UTC+2/+3), so daily and hourly buckets do not align with local days.

Each contract's `known_limitations` field lists only what is **additional** to these.

---

# Tier 1 — North Star and Platform Health

---

## M-100 — Weekly Participating Users (WPU)

| Field | Value |
|---|---|
| **metric_id** | `M-100` |
| **metric_name** | Weekly Participating Users |
| **business_purpose** | The North Star. Counts verified users who received the product's core value: taking part in a live room. |
| **decision_supported** | Is Cocorra delivering more value this week than last, and which input constrained it? |
| **business_definition** | The number of distinct users, excluding each room's own host, who joined at least one live room during a rolling 7-day window. |
| **technical_definition** | `COUNT(DISTINCT UserEvents.UserId)` where `EventType = 'room_joined'` and `OccurredAtUtc` falls in the window, excluding rows where `UserId = Room.HostId` for the room identified by the promoted `RoomId` column. |
| **formula** | `COUNT(DISTINCT ue.UserId) FROM UserEvents ue JOIN Rooms r ON r.Id = ue.RoomId WHERE ue.EventType='room_joined' AND ue.OccurredAtUtc >= @from AND ue.OccurredAtUtc < @to AND ue.UserId <> r.HostId AND ue.UserId IS NOT NULL` |
| **population** | Users with at least one `room_joined` event in the window |
| **inclusions** | All room categories; public and private rooms; every join regardless of stage participation |
| **exclusions** | **Room hosts, for their own rooms** (INV-7, TRUST-01). Events with `UserId IS NULL` (deleted users). |
| **time_window** | Rolling 7 days on `OccurredAtUtc` |
| **timezone** | UTC |
| **data_sources** | `UserEvents`, `Rooms` |
| **event_sources** | `room_joined` — **server-emitted**, `RoomHub.cs:270` |
| **aggregation_method** | READ MODEL (`DailyPlatformMetrics`) with live-query fallback |
| **historical_reliability** | **HISTORICALLY ACCURATE** within the raw retention window; indefinitely once read models accumulate |
| **known_limitations** | Counts attendance, not conversation — must always be displayed beside M-101. Bounded by room supply (M-200): a flat WPU in a low-supply week is not a demand signal. `room_joined` fires per SignalR reconnect, which distinct-user counting neutralises. |
| **trust_level** | **VERIFIED** |
| **owner** | Product Owner |
| **validation_method** | (1) Distinct-count test: a fixture user joining the same room 5 times counts once. (2) Host-exclusion test: a host joining their own room is absent; the same user joining *another* room is present. (3) Reconciliation against `RoomParticipant` distinct users, tolerance documented. |

---

## M-101 — Speaking Conversion Rate

| Field | Value |
|---|---|
| **metric_id** | `M-101` |
| **metric_name** | Speaking Conversion Rate |
| **business_purpose** | Guards M-100 against attendance without participation. Cocorra's entire stage design exists to produce this conversion. |
| **decision_supported** | Do listeners become speakers? Should the stage flow be redesigned? |
| **business_definition** | The share of participating users who activated their microphone at least once. |
| **technical_definition** | Distinct non-host users with `mic_activated` in the window, divided by M-100 over the same window. |
| **formula** | `COUNT(DISTINCT speakers) / NULLIF(COUNT(DISTINCT joiners), 0) * 100`, both host-excluded |
| **population** | The M-100 population |
| **inclusions** | Any mic activation, of any duration |
| **exclusions** | **Room hosts** — critical here in *both* directions: hosts never emit `mic_activated` (Finding A), so including them deflates the numerator while inflating the denominator |
| **time_window** | Rolling 7 days |
| **timezone** | UTC |
| **data_sources** | `UserEvents`, `Rooms` |
| **event_sources** | `room_joined`, `mic_activated` — both server-emitted (`RoomHub.cs:270, 521`) |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | An activated mic is not audible speech — no LiveKit telemetry exists (Finding B). Measures *whether* someone spoke, never *how much* or *whether anyone heard*. |
| **trust_level** | **VERIFIED** (host-excluded). **CONDITIONALLY RELIABLE** if hosts are included. |
| **owner** | Product Owner |
| **validation_method** | (1) A room with 1 host + 3 joiners of whom 2 unmute yields 66.7%, not 50% or 75%. (2) Cross-metric consistency: no `UserId` may appear in both the speaker set and the passive set for the same window — the executable statement of the TRUST-01 contradiction. |

---

## M-102 — Weekly Return Rate

| Field | Value |
|---|---|
| **metric_id** | `M-102` |
| **metric_name** | Weekly Return Rate |
| **business_purpose** | Whether participation was worth repeating. Replaces the UNRELIABLE retention metric (TRUST-03). |
| **decision_supported** | Should Cocorra prioritise retention work? |
| **business_definition** | Of users who participated in a room during a given week, the share who participated again in any later week. |
| **technical_definition** | Cohort = distinct non-host `room_joined` users in week *N*. Returned = those with a `room_joined` event with `OccurredAtUtc >= start of week N+1`. **"Any later week," never "exactly day N."** |
| **formula** | `COUNT(DISTINCT returned) / NULLIF(COUNT(DISTINCT cohort), 0) * 100` |
| **population** | Non-host participants in the cohort week |
| **inclusions** | Return via any room, any category, any host |
| **exclusions** | Room hosts; `UserId IS NULL` |
| **time_window** | Weekly cohort; forward-looking observation window, explicitly bounded |
| **timezone** | UTC (week boundaries do not match local weeks — U-4) |
| **data_sources** | `UserEvents` |
| **event_sources** | `room_joined` only — **deliberately not `session_started`** |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **PARTIALLY RECONSTRUCTABLE** — bounded by U-1 |
| **known_limitations** | **Biased upward by hard deletes (U-3)**; magnitude unknown. Depends on room supply existing in the following week — a low-supply week depresses the prior week's measured return for reasons unrelated to satisfaction. |
| **trust_level** | **CONDITIONALLY RELIABLE** — valid if the upward bias and supply dependency are stated with the number |
| **owner** | Product Owner |
| **validation_method** | (1) A user active on days 2 and 5 counts as returned — the direct regression test for the exact-day bug. (2) The new value must be ≥ the deprecated retention metric over the same cohort. (3) Assert the query reads zero `session_started` rows. |

---

# Tier 2 — Supply Health

**INFERENCE** — every metric in this tier is computable today from verified relational data, and none is computed by any existing endpoint (`11-`, §2.3). This is the highest value-to-effort tier in the programme.

---

## M-200 — Distinct Active Hosts

| Field | Value |
|---|---|
| **metric_id** | `M-200` |
| **metric_name** | Distinct Active Hosts |
| **business_purpose** | Cocorra's leading indicator. Supply loss precedes and causes demand loss by weeks. |
| **decision_supported** | Recruit more coaches, or help existing ones? |
| **business_definition** | The number of distinct users who hosted at least one room during the period. |
| **technical_definition** | `COUNT(DISTINCT Rooms.HostId)` where `Rooms.CreatedAt` falls in the window. |
| **formula** | `SELECT COUNT(DISTINCT HostId) FROM Rooms WHERE CreatedAt >= @from AND CreatedAt < @to` |
| **population** | All rooms created in the window |
| **inclusions** | Every room regardless of status — including `Cancelled` and never-started |
| **exclusions** | None |
| **time_window** | Rolling 7 days on `CreatedAt` |
| **timezone** | UTC |
| **data_sources** | `Rooms` |
| **event_sources** | None — pure relational |
| **aggregation_method** | READ MODEL (`DailyHostMetrics`) |
| **historical_reliability** | **HISTORICALLY ACCURATE** — `CreatedAt` is immutable; unaffected by U-1 and U-2 |
| **known_limitations** | Counts room *creation*, not room *going live* (Finding C). A host who scheduled a room that never started is counted. |
| **trust_level** | **VERIFIED** |
| **owner** | Product Owner |
| **validation_method** | Direct SQL reconciliation against `Rooms`. **INFERENCE** — no event pipeline is involved, so this metric is immune to U-1 and U-2 and can serve as a control when validating event-derived metrics. |

---

## M-201 — Host Retention

| Field | Value |
|---|---|
| **metric_id** | `M-201` |
| **metric_name** | Host Retention |
| **business_purpose** | Whether the supply side is stable or eroding. |
| **decision_supported** | Is coach churn a threat requiring intervention? |
| **business_definition** | Of users who hosted a room last month, the share who hosted again this month. |
| **technical_definition** | `|hosts(M-1) ∩ hosts(M)| / |hosts(M-1)|` over `Rooms.HostId` grouped by `CreatedAt` month. |
| **formula** | `COUNT(DISTINCT prior ∩ current) / NULLIF(COUNT(DISTINCT prior), 0) * 100` |
| **population** | Distinct hosts in the prior period |
| **inclusions** | All rooms |
| **exclusions** | None |
| **time_window** | Month over month on `CreatedAt` |
| **timezone** | UTC |
| **data_sources** | `Rooms` |
| **event_sources** | None |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | A host who deleted their account disappears from both periods (U-3). Small denominators make the percentage volatile — **RECOMMENDATION**: display the absolute counts alongside the rate, never the rate alone. |
| **trust_level** | **VERIFIED** |
| **owner** | Product Owner |
| **validation_method** | Fixture with 3 hosts in M-1, 2 of whom host in M, yields 66.7%. Assert absolute counts accompany the rate. |

---

## M-202 — Supply Concentration

| Field | Value |
|---|---|
| **metric_id** | `M-202` |
| **metric_name** | Supply Concentration (Top-3 Host Share) |
| **business_purpose** | Detects the failure mode where the headline room count looks healthy while dependency narrows. |
| **decision_supported** | Is the platform dangerously dependent on a few coaches? |
| **business_definition** | The share of all rooms in the period created by the three most active hosts. |
| **technical_definition** | Sum of room counts for the top 3 `HostId` by room count, divided by total rooms in the window. |
| **formula** | `SUM(top 3 host room counts) / NULLIF(COUNT(*), 0) * 100` |
| **population** | All rooms in the window |
| **inclusions** | All statuses |
| **exclusions** | None |
| **time_window** | Rolling 7 or 30 days |
| **timezone** | UTC |
| **data_sources** | `Rooms` |
| **event_sources** | None |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | "Top 3" is arbitrary at small host counts. **RECOMMENDATION** — report the full host-count distribution beside it; the shape carries the information, the single number is a summary. |
| **trust_level** | **CONDITIONALLY RELIABLE** — meaningful only with the total host count displayed |
| **owner** | Product Owner |
| **validation_method** | Fixture with a known distribution; assert exact share. Assert the total host count is present in the response. |

---

## M-203 — Distinct Non-Host Speakers per Room

| Field | Value |
|---|---|
| **metric_id** | `M-203` |
| **metric_name** | Distinct Non-Host Speakers per Room |
| **business_purpose** | The best available hosting-quality proxy, and the measure that distinguishes a conversation from a broadcast. |
| **decision_supported** | Which coaches run rooms that get people talking? Is Cocorra a conversation platform or a broadcast platform? |
| **business_definition** | For each room, how many different non-host participants activated a microphone. |
| **technical_definition** | `COUNT(DISTINCT UserEvents.UserId)` where `EventType='mic_activated'`, grouped by the promoted `RoomId`, excluding `Room.HostId`. |
| **formula** | `SELECT ue.RoomId, COUNT(DISTINCT ue.UserId) FROM UserEvents ue JOIN Rooms r ON r.Id=ue.RoomId WHERE ue.EventType='mic_activated' AND ue.UserId <> r.HostId GROUP BY ue.RoomId` |
| **population** | Rooms with at least one `mic_activated` event |
| **inclusions** | Any activation, any duration |
| **exclusions** | **Room host** |
| **time_window** | Per room, bounded by the room's lifetime |
| **timezone** | UTC |
| **data_sources** | `UserEvents`, `Rooms` |
| **event_sources** | `mic_activated` — server-emitted |
| **aggregation_method** | READ MODEL (`DailyRoomMetrics`) |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | Rooms with zero speakers produce no events and are absent unless left-joined from `Rooms` — **RECOMMENDATION**: left-join, because a zero-speaker room is the most informative case. |
| **trust_level** | **VERIFIED** |
| **owner** | Product Owner |
| **validation_method** | (1) Host-only room reports 0, not 1. (2) Zero-speaker rooms appear with 0 via the left join. (3) Distribution test: assert the shape is reported, not only the mean. |

---

## M-204 — Audience Return per Host

| Field | Value |
|---|---|
| **metric_id** | `M-204` |
| **metric_name** | Audience Return per Host |
| **business_purpose** | The closest available measure of coach quality. |
| **decision_supported** | Which coaches should be supported, featured, or coached? |
| **business_definition** | For a host, the share of their room participants who attend a later room by the same host. |
| **technical_definition** | Per host: distinct participants in room *R* who also appear in any room by the same host with a later `CreatedAt`, divided by distinct participants in *R*. |
| **formula** | `COUNT(DISTINCT returning participants) / NULLIF(COUNT(DISTINCT participants), 0) * 100` |
| **population** | Non-host participants of the host's rooms |
| **inclusions** | Return to any later room by the same host |
| **exclusions** | The host; `UserId IS NULL` |
| **time_window** | Rolling 30 days, forward-looking |
| **timezone** | UTC |
| **data_sources** | `UserEvents`, `Rooms` |
| **event_sources** | `room_joined` |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **PARTIALLY RECONSTRUCTABLE** (U-1) |
| **known_limitations** | Requires the host to have hosted at least twice in the window — a new host is structurally unmeasurable. Confounded by scheduling slot and category. **Correlational**: a host with a better slot will score better without being a better host. |
| **trust_level** | **CONDITIONALLY RELIABLE** |
| **owner** | Product Owner |
| **validation_method** | Fixture with 2 rooms by one host and known overlap; assert exact share. Assert hosts with fewer than 2 rooms are excluded rather than reported as 0. |

---

# Tier 3 — Activation Pipeline

---

## M-300 — Sequential Onboarding Funnel

| Field | Value |
|---|---|
| **metric_id** | `M-300` |
| **metric_name** | Sequential Onboarding Funnel |
| **business_purpose** | Where prospective users abandon the five-step verification gate. Replaces the non-sequential funnel (TRUST-06). |
| **decision_supported** | Should onboarding be restructured? |
| **business_definition** | The number of users reaching each onboarding step, counting a user at step N only if they completed every prior step first. |
| **technical_definition** | Per user, ordered progression across `user_registered` → `email_confirmed` → `voice_verification_submitted` → `mbti_submitted` → `activation_completed`, requiring step N's `OccurredAtUtc` to precede step N+1's. |
| **formula** | Per user: `MIN(OccurredAtUtc)` per step; count at step N only where `t(1) <= t(2) <= … <= t(N)`. |
| **population** | Users with `user_registered` in the cohort window |
| **inclusions** | Re-record resubmissions collapse to the first submission |
| **exclusions** | Events with `UserId IS NULL` |
| **time_window** | Fixed cohort window on the first step; forward-looking for later steps |
| **timezone** | UTC |
| **data_sources** | `UserEvents` |
| **event_sources** | Five events, all **server-emitted** |
| **aggregation_method** | LIVE QUERY initially; READ MODEL (`DailyFunnelMetrics`) once the aggregation layer exists |
| **historical_reliability** | **PARTIALLY RECONSTRUCTABLE** (U-1) |
| **known_limitations** | Cannot see pre-submission abandonment (no `registration_started`). The `activation_completed` step lags by admin review latency (M-301), so a cohort window shorter than that latency understates the final step. |
| **trust_level** | **VERIFIED** |
| **owner** | Product Owner |
| **validation_method** | **(1) Monotonicity — each step's count must be ≤ the previous step's.** This is impossible to satisfy under the current implementation, making it a precise acceptance criterion. (2) A user with `activation_completed` before `email_confirmed` must not count at the later step. (3) Sequential counts ≤ current independent counts at every step. |

---

## M-301 — Admin Review Latency

| Field | Value |
|---|---|
| **metric_id** | `M-301` |
| **metric_name** | Admin Review Latency |
| **business_purpose** | The verification queue is a hard serialisation point on the entire growth funnel: no acquisition effort can produce more active users than it approves. |
| **decision_supported** | Is manual review a throughput bottleneck? |
| **business_definition** | How long users wait between submitting their voice recording and receiving a decision. |
| **technical_definition** | Per user, `OccurredAtUtc` of the first `voice_verification_result` minus `OccurredAtUtc` of the most recent preceding `voice_verification_submitted`. Reported as median, p90, p99. |
| **formula** | `PERCENTILE_CONT(0.5 | 0.9 | 0.99) over (t_result − t_submitted)` in hours |
| **population** | Users with both events in the window |
| **inclusions** | Re-record cycles counted as separate review instances |
| **exclusions** | Users still awaiting a decision — **INFERENCE**: this is a survivorship bias in the wrong direction. The longest-waiting users are excluded precisely because they are still waiting, so the metric understates the problem exactly when it is worst. Pending queue depth (M-303) must be shown beside it. |
| **time_window** | Rolling 30 days on the result event |
| **timezone** | UTC — but **RECOMMENDATION**: display the day-of-week/hour breakdown in local time, since it drives staffing decisions |
| **data_sources** | `UserEvents` |
| **event_sources** | `voice_verification_submitted`, `voice_verification_result` — both server-emitted |
| **aggregation_method** | LIVE QUERY |
| **historical_reliability** | **PARTIALLY RECONSTRUCTABLE** (U-1) |
| **known_limitations** | Cannot attribute to a reviewer until `user_status_changed` carries `changedByAdminId`. **Never report the mean** — see validation. |
| **trust_level** | **VERIFIED** |
| **owner** | Operations |
| **validation_method** | (1) Fixture with known gaps yields exact percentiles. (2) **Assert the response contains no mean.** **INFERENCE** — if most reviews take 20 minutes and 15% take 3 days, the mean describes nobody and hides the users being harmed; excluding it is a contract requirement, not a presentation preference. (3) Assert pending-count context is present. |

---

## M-302 — Activation to First Room Join

| Field | Value |
|---|---|
| **metric_id** | `M-302` |
| **metric_name** | Activation → First Room Join |
| **business_purpose** | Approving a user is not the goal; a user who joins a room is. |
| **decision_supported** | Is the onboarding problem above the gate or below it? |
| **business_definition** | The share of newly activated users who join their first room within 7 days of activation. |
| **technical_definition** | Users with `activation_completed` in the cohort window who have a subsequent `room_joined` with `OccurredAtUtc` within 7 days. |
| **formula** | `COUNT(DISTINCT activated ∧ joined within 7d) / NULLIF(COUNT(DISTINCT activated), 0) * 100` |
| **population** | Users activated in the cohort window |
| **inclusions** | Any room, any category |
| **exclusions** | Users activated fewer than 7 days ago — the window must be complete or the rate is understated |
| **time_window** | Fixed cohort; 7-day forward observation |
| **timezone** | UTC |
| **data_sources** | `UserEvents` |
| **event_sources** | `activation_completed`, `room_joined` |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **PARTIALLY RECONSTRUCTABLE** (U-1) |
| **known_limitations** | Bounded by room supply: a user activated in a week with no live rooms cannot join one. **RECOMMENDATION** — display beside M-200. |
| **trust_level** | **VERIFIED** |
| **owner** | Product Owner |
| **validation_method** | (1) A user joining on day 8 does not count. (2) Incomplete cohorts are excluded, not counted as failures. (3) Cross-check the denominator against M-300's final step. |

---

## M-303 — Pending Verification Queue Depth

| Field | Value |
|---|---|
| **metric_id** | `M-303` |
| **metric_name** | Pending Verification Queue Depth |
| **business_purpose** | Whether the review backlog is growing. |
| **decision_supported** | Add review capacity? |
| **business_definition** | The number of users awaiting a verification decision, captured daily. |
| **technical_definition** | Daily snapshot of `COUNT(*) FROM AspNetUsers WHERE Status = 0 (Pending)`, written to `DailyStateSnapshots`. |
| **formula** | `SELECT COUNT(*) FROM AspNetUsers WHERE Status = 0` |
| **population** | All users |
| **inclusions** | `Pending` only |
| **exclusions** | `ReRecord` — tracked as a separate series (a different queue with different handling) |
| **time_window** | Daily snapshot, one row per day |
| **timezone** | UTC snapshot boundary |
| **data_sources** | `AspNetUsers` → `DailyStateSnapshots` |
| **event_sources** | None |
| **aggregation_method** | READ MODEL (snapshot) |
| **historical_reliability** | **CURRENT SNAPSHOT ONLY** before the snapshot job exists; **HISTORICALLY ACCURATE** from the day it starts running |
| **known_limitations** | **No history exists before the snapshot job's first run.** **INFERENCE** — this is why the job is P0 despite being small: history not captured today is unrecoverable tomorrow, and every day of delay is a permanent hole in the series. |
| **trust_level** | **VERIFIED** (forward-looking) |
| **owner** | Operations |
| **validation_method** | (1) Snapshot equals a direct count at capture time. (2) Idempotency: two runs on the same date produce one row, not two. (3) A gap-detection query flags missing dates rather than interpolating them. |

---

# Tier 4 — Room Participation

---

## M-400 — Stage Funnel

| Field | Value |
|---|---|
| **metric_id** | `M-400` |
| **metric_name** | Stage Participation Funnel |
| **business_purpose** | Locates *where* the listener→speaker journey breaks — the product's central unanswered question. |
| **decision_supported** | Which control point in the stage flow should be redesigned? |
| **business_definition** | How many room participants progress through joining, raising a hand, being promoted to stage, and speaking. |
| **technical_definition** | Sequential per-user progression across `room_joined` → `hand_raised` → `stage_promoted` → `mic_activated`, scoped to a single `RoomId`, with time ordering enforced. |
| **formula** | Per `(RoomId, UserId)`: ordered `MIN(OccurredAtUtc)` per step; count at step N only where all prior steps precede it. |
| **population** | Non-host participants of rooms in the window |
| **inclusions** | All rooms and categories |
| **exclusions** | **Room hosts** — a host is on stage by construction and would fill every step spuriously |
| **time_window** | Rolling 7 days |
| **timezone** | UTC |
| **data_sources** | `UserEvents`, `Rooms` |
| **event_sources** | `room_joined` (exists), `hand_raised` (**new**), `stage_promoted` (**new**), `mic_activated` (exists) |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **NOT HISTORICALLY RELIABLE before the new events ship.** Steps 2 and 3 do not exist prior to that date. |
| **known_limitations** | **Cannot be backfilled** — `hand_raised` and `stage_promoted` were never captured (TRUST-04). The series must start at the deployment date with an explicit marker. |
| **trust_level** | **EXPERIMENTAL** at launch → **VERIFIED** after 4 weeks of stable emission |
| **owner** | Product Owner |
| **validation_method** | (1) Monotonicity across all four steps. (2) Ordering: a `mic_activated` without a preceding `stage_promoted` in the same room does not count at step 4. (3) **Explicit start-date assertion**: querying before the deployment date returns a documented "not measured" marker, never a zero. **INFERENCE** — returning 0 for an uninstrumented period would read as "nobody raised their hand," which is the exact misreading this contract exists to prevent. |

---

## M-401 — Non-Host Speaking Minutes

| Field | Value |
|---|---|
| **metric_id** | `M-401` |
| **metric_name** | Non-Host Speaking Minutes |
| **business_purpose** | Replaces the deprecated Top Speakers metric (TRUST-01) with a defensible depth measure. |
| **decision_supported** | Who genuinely contributes most? Is airtime concentrated or distributed? |
| **business_definition** | Total minutes of open microphone time by participants other than the room host. |
| **technical_definition** | `SUM(segmentSeconds)` from `mic_deactivated` events where `isHost = false`, converted to minutes. |
| **formula** | `SUM(JSON_VALUE(PropertiesJson,'$.segmentSeconds')) / 60.0` where `EventType='mic_deactivated'` and `isHost=false` |
| **population** | Non-host participants with at least one completed mic segment |
| **inclusions** | Every closed segment |
| **exclusions** | **Hosts**. Open segments at query time (not yet closed). |
| **time_window** | Rolling 7 days on the deactivation event |
| **timezone** | UTC |
| **data_sources** | `UserEvents` |
| **event_sources** | `mic_deactivated` (**new**) |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **NOT HISTORICALLY RELIABLE before the event ships** |
| **known_limitations** | **Measures unmuted time, not audio** (Finding B). No LiveKit telemetry exists, so a speaker who unmutes and stays silent books the time. **This limitation must be displayed with the metric**, not footnoted — it is the difference between "spoke for 20 minutes" and "had an open mic for 20 minutes." Segments open at process crash are never closed. |
| **trust_level** | **CONDITIONALLY RELIABLE** — never VERIFIED without media-layer telemetry |
| **owner** | Product Owner |
| **validation_method** | (1) A host's segments are excluded. (2) Unmute→mute over 90s yields 1.5 minutes. (3) **Assert the "unmuted time, not audio" caveat is present in the response `Meta`** — a contract-level test, because the caveat is what makes the number honest. |

---

## M-402 — Hand-Raise to Stage Promotion Rate

| Field | Value |
|---|---|
| **metric_id** | `M-402` |
| **metric_name** | Hand-Raise → Stage Promotion Rate |
| **business_purpose** | Whether host responsiveness or stage capacity is the constraint on participation. |
| **decision_supported** | Change the default `SelectionMode`? Raise `StageCapacity`? |
| **business_definition** | Of participants who raise a hand, the share promoted to the stage, and how long they wait. |
| **technical_definition** | Distinct `(RoomId, UserId)` with `stage_promoted` divided by distinct `(RoomId, UserId)` with `hand_raised`; median `secondsWaiting` from the promotion event. |
| **formula** | `COUNT(DISTINCT promoted) / NULLIF(COUNT(DISTINCT raised), 0) * 100` |
| **population** | Non-host participants who raised a hand |
| **inclusions** | All rooms |
| **exclusions** | Hosts; participants promoted without raising (direct host invitation) are excluded from the numerator to keep it a true subset |
| **time_window** | Rolling 7 days |
| **timezone** | UTC |
| **data_sources** | `UserEvents`, `Rooms` |
| **event_sources** | `hand_raised`, `stage_promoted` (**both new**) |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **NOT HISTORICALLY RELIABLE before the events ship** |
| **known_limitations** | A hand lowered before promotion is indistinguishable from abandonment unless `hand_lowered.wasApproved` is populated. Segment by `SelectionMode` — pooling automatic and manual rooms averages two different mechanisms into a meaningless middle. |
| **trust_level** | **EXPERIMENTAL** at launch → **VERIFIED** after 4 weeks |
| **owner** | Product Owner |
| **validation_method** | (1) Rate ≤ 100% always. (2) Segmented by `SelectionMode` in the response. (3) `secondsWaiting` reconciles against the two events' timestamp difference. |

---

## M-403 — Speaking Conversion by Room Configuration

| Field | Value |
|---|---|
| **metric_id** | `M-403` |
| **metric_name** | Speaking Conversion by Room Configuration |
| **business_purpose** | Whether host-configurable settings actually change participation outcomes. |
| **decision_supported** | Change room defaults (`SelectionMode`, `StageCapacity`, `DefaultSpeakerDurationMinutes`)? |
| **business_definition** | M-101 segmented by the room's selection mode, category, and stage capacity. |
| **technical_definition** | M-101 grouped by `Rooms.SelectionMode`, `Rooms.Category`, and `Rooms.StageCapacity` bands. |
| **formula** | M-101 formula with `GROUP BY` on the room dimension |
| **population** | The M-101 population |
| **inclusions / exclusions** | As M-101, including host exclusion |
| **time_window** | Rolling 30 days — **INFERENCE**: a longer window than M-101 because segmentation splits an already-small sample |
| **timezone** | UTC |
| **data_sources** | `UserEvents`, `Rooms` |
| **event_sources** | `room_joined`, `mic_activated` |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | **Strictly correlational.** Hosts *choose* the selection mode, so the comparison confounds the mode with the kind of host who selects it. Only 3 categories and 2 selection modes exist, so cells are few but may still be thin. **RECOMMENDATION** — display cell sizes with every rate; suppress cells below a documented minimum rather than showing a noisy percentage. |
| **trust_level** | **CONDITIONALLY RELIABLE** — correlational, must be labelled as such on the chart itself |
| **owner** | Product Owner |
| **validation_method** | (1) Segment counts sum to the M-101 total. (2) Assert cell sizes are present. (3) Assert the correlational label is in `Meta`. |

---

# Tier 5 — Safety & Trust

---

## M-500 — Report Rate per 1,000 Room Joins

| Field | Value |
|---|---|
| **metric_id** | `M-500` |
| **metric_name** | Report Rate per 1,000 Room Joins |
| **business_purpose** | Whether safety is deteriorating, normalised so growth does not masquerade as a safety problem. |
| **decision_supported** | Invest in proactive moderation? |
| **business_definition** | Reports filed per 1,000 room joins in the period. |
| **technical_definition** | `COUNT(Reports) WHERE CreatedAt in window`, divided by M-100's underlying join count, times 1,000. |
| **formula** | `COUNT(reports) / NULLIF(COUNT(DISTINCT joins), 0) * 1000` |
| **population** | All reports in the window |
| **inclusions** | All `ReportCategory` values; user-targeted and room-targeted |
| **exclusions** | None |
| **time_window** | Rolling 7 days on `Report.CreatedAt` (indexed) |
| **timezone** | UTC |
| **data_sources** | `Reports`, `UserEvents` |
| **event_sources** | `user_reported`, `room_joined` |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **HISTORICALLY ACCURATE** — `Reports` is relational and unaffected by U-1/U-2; only the denominator is event-derived |
| **known_limitations** | Measures *reported* harm, not harm. Under-reporting is unmeasurable. Denominator inherits U-2. |
| **trust_level** | **VERIFIED** |
| **owner** | Trust & Safety |
| **validation_method** | Numerator reconciles directly against `SELECT COUNT(*) FROM Reports`. Denominator reconciles against M-100. |

---

## M-501 — Report Rate by Room Category

| Field | Value |
|---|---|
| **metric_id** | `M-501` |
| **metric_name** | Report Rate by Room Category |
| **business_purpose** | **The highest-stakes available analysis in the product.** Two of three categories are `Relationships` and `MentalHealth`, which carry duty-of-care obligations a general social product does not. |
| **decision_supported** | Do `MentalHealth` rooms need category-specific safeguards? |
| **business_definition** | Reports per 1,000 room joins, split by the category of the room the report concerns. |
| **technical_definition** | `user_reported` events joined on `reportedRoomId` → `Rooms.Category`; denominator is joins per category. |
| **formula** | Per category: `COUNT(reports) / NULLIF(COUNT(DISTINCT joins), 0) * 1000` |
| **population** | Reports carrying a non-null `reportedRoomId` |
| **inclusions** | All three categories |
| **exclusions** | Reports with no room context — **INFERENCE**: these are user-to-user reports outside a room and belong in M-500, not here; silently folding them into a category would misattribute them |
| **time_window** | Rolling 30 days — a longer window than M-500 because splitting three ways thins the sample |
| **timezone** | UTC |
| **data_sources** | `Reports`, `Rooms`, `UserEvents` |
| **event_sources** | `user_reported` (carries `reportedRoomId`), `room_joined` |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | Only 3 categories, so cells may be small — report absolute counts alongside rates. `Report.ReportedUserId` is `SetNull` on user deletion, though `ReportedRoomId` is unaffected. |
| **trust_level** | **VERIFIED** |
| **owner** | Trust & Safety |
| **validation_method** | (1) Per-category report counts sum to the total with a room context. (2) Reports without a room context are excluded, not bucketed into `Others`. (3) Absolute counts present. |

---

## M-502 — Repeat-Reported Users

| Field | Value |
|---|---|
| **metric_id** | `M-502` |
| **metric_name** | Repeat-Reported Users |
| **business_purpose** | Whether the enforcement ladder stops repeat offenders. |
| **decision_supported** | Tune the enforcement ladder? |
| **business_definition** | Users reported more than once in the period, ranked by report count. |
| **technical_definition** | `GROUP BY Reports.ReportedUserId HAVING COUNT(*) > 1`, ordered descending. |
| **formula** | `SELECT ReportedUserId, COUNT(*) FROM Reports WHERE CreatedAt in window GROUP BY ReportedUserId HAVING COUNT(*) > 1` |
| **population** | Reports with a non-null `ReportedUserId` |
| **inclusions** | All categories and statuses |
| **exclusions** | Reports whose target deleted their account (`SetNull`) |
| **time_window** | Rolling 30 days |
| **timezone** | UTC |
| **data_sources** | `Reports` |
| **event_sources** | None |
| **aggregation_method** | LIVE QUERY |
| **historical_reliability** | **HISTORICALLY ACCURATE** for surviving users |
| **known_limitations** | **INFERENCE — a moderation-integrity issue, not only an analytics one**: because `ReportedUserId` is `SetNull` on delete, a user with an adverse history can erase it by deleting their account. Soft delete (TRUST-05) would close this. |
| **trust_level** | **CONDITIONALLY RELIABLE** |
| **owner** | Trust & Safety |
| **validation_method** | (1) Users with exactly 1 report are excluded. (2) After a user deletion, assert whether their reports remain attributed — the direct test for the TRUST-05 dependency. |

---

# Tier 6 — Engagement Surfaces

---

## M-600 — Message Reciprocity Rate

| Field | Value |
|---|---|
| **metric_id** | `M-600` |
| **metric_name** | Message Reciprocity Rate |
| **business_purpose** | **INFERENCE** — raw message volume can rise because of unwanted contact. Reciprocity is what distinguishes a conversation from harassment, and it is the only version of this metric safe to treat as an engagement signal. |
| **decision_supported** | Invest in messaging, or treat it as a utility? |
| **business_definition** | The share of message conversations in which both participants sent at least one message. |
| **technical_definition** | Over `Messages` in the window, group by the unordered pair `(LEAST(SenderId,ReceiverId), GREATEST(...))`; a pair is reciprocal if both directions appear. |
| **formula** | `COUNT(reciprocal pairs) / NULLIF(COUNT(distinct pairs), 0) * 100` |
| **population** | Distinct sender/receiver pairs with ≥1 message in the window |
| **inclusions** | Both `ChatHub` and in-room private messages — **FACT**: both route through `ChatService.SaveMessageAsync` and are currently indistinguishable |
| **exclusions** | None |
| **time_window** | Rolling 30 days on `Messages.CreatedAt` (indexed) |
| **timezone** | UTC |
| **data_sources** | `Messages` |
| **event_sources** | None — relational, so unaffected by U-1/U-2 |
| **aggregation_method** | LIVE QUERY |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | Window-edge effects: a reply just outside the window reads as non-reciprocal. Cannot distinguish origin surface until `message_sent.originSurface` exists (GAP-16). |
| **trust_level** | **VERIFIED** |
| **owner** | Product Owner |
| **validation_method** | (1) A one-directional pair is non-reciprocal. (2) Pair keying is order-independent: (A→B) and (B→A) are one pair, not two. |

---

## M-601 — Technical Problem Ticket Rate

| Field | Value |
|---|---|
| **metric_id** | `M-601` |
| **metric_name** | Technical Problem Ticket Rate |
| **business_purpose** | **INFERENCE** — with no error tracking anywhere in the stack (errors reach `ILogger` → Docker stdout, unpersisted), this is Cocorra's only systematic reliability signal. |
| **decision_supported** | Prioritise stability work? Escalate the need for real error tracking? |
| **business_definition** | Support tickets of type `TechnicalProblem` per 1,000 active users. |
| **technical_definition** | `COUNT(SupportTickets WHERE Type = TechnicalProblem)` in the window, normalised by distinct active users. |
| **formula** | `COUNT(tickets) / NULLIF(COUNT(DISTINCT active users), 0) * 1000` |
| **population** | Tickets in the window |
| **inclusions** | Anonymous tickets (`SupportTicket.UserId` is nullable) |
| **exclusions** | Other ticket types — reported as separate series |
| **time_window** | Rolling 7 days on `CreatedAt` |
| **timezone** | UTC |
| **data_sources** | `SupportTickets`, `UserEvents` |
| **event_sources** | `room_joined` (denominator) |
| **aggregation_method** | LIVE QUERY |
| **historical_reliability** | **HISTORICALLY ACCURATE** |
| **known_limitations** | **A lagging proxy, not a reliability measurement.** Filtered by users' willingness to complain and biased toward loud failure modes: a silent audio failure that drives users away produces no signal at all. **Must be labelled "proxy — no error tracking exists" in the UI.** |
| **trust_level** | **CONDITIONALLY RELIABLE** — usable for direction, never for absolute reliability claims |
| **owner** | Engineering |
| **validation_method** | (1) Type filtering is exact. (2) Anonymous tickets are included. (3) **Assert the proxy label is present in `Meta`** — the label is what keeps the metric honest. |

---

## M-602 — Push Send Success Rate

| Field | Value |
|---|---|
| **metric_id** | `M-602` |
| **metric_name** | Push Send Success Rate |
| **business_purpose** | **INFERENCE** — commit `dc1c933` fixed *reversed FCM delivery*. An identical regression today would be invisible to the dashboard and would surface only through user complaints, as it did the first time. This is a regression guard for a defect class that has already occurred once in this codebase. |
| **decision_supported** | Is push infrastructure healthy? Is notification investment worthwhile? |
| **business_definition** | The share of attempted push notifications that Firebase accepted. |
| **technical_definition** | `push_send_result` events with `success = true` divided by all `push_send_result` events in the window. |
| **formula** | `COUNT(success=true) / NULLIF(COUNT(*), 0) * 100` |
| **population** | Push attempts in the window |
| **inclusions** | All notification types |
| **exclusions** | Users with no FCM token (no attempt is made) — tracked separately as token coverage |
| **time_window** | Rolling 7 days |
| **timezone** | UTC |
| **data_sources** | `UserEvents` |
| **event_sources** | `push_send_attempted`, `push_send_result` (**both new**) |
| **aggregation_method** | READ MODEL |
| **historical_reliability** | **NOT HISTORICALLY RELIABLE before the events ship** — the FCM response is currently discarded |
| **known_limitations** | FCM acceptance is not device delivery. A push accepted by Firebase may still never reach the device. This metric detects *send-path* failures only. |
| **trust_level** | **CONDITIONALLY RELIABLE** |
| **owner** | Engineering |
| **validation_method** | (1) A mocked FCM failure produces `success=false` with an `errorCode`. (2) Attempt and result counts reconcile. (3) Token coverage is reported alongside. |

---

# Deprecated Metrics

**RECOMMENDATION** — these must be removed from the API response, not merely hidden in the UI. **INFERENCE** — a consumer relying on a hidden-but-present field would silently read a wrong value; removing the field makes the dependency fail loudly, which is the safer failure mode.

| Deprecated metric | Reason | Replacement | Reference |
|---|---|---|---|
| Top Speakers | Ranks hosts by room duration | M-401 | TRUST-01 |
| Users Who Raised Hand | Live boolean; historically ~0 | M-402 | TRUST-04 |
| Retention D1/D7/D30 | Exact-day matching + cookie signal | M-102 | TRUST-03 |
| User Growth — status breakdown | Current status backdated into history | Reconstructed series with explicit start date | TRUST-02 |
| Avg Room Duration | Averages a configured constant (2 or 3) | Actual duration from `room_went_live` + `room_ended` | TRUST-09 |
| Funnel (independent counts) | Not a funnel | M-300 | TRUST-06 |
| Users Who Spoke (`TotalSpokenSeconds > 0`) | Inflated by one host per room | Redefined via `mic_activated`, host-excluded | TRUST-01 |

**KEEP unchanged**: registration counts by period, report counts and category mix, most-active-rooms by `UniqueJoiners`, peak hours (with a local-time display fix).

---

# Contract Summary

| ID | Metric | Trust Level | Historical Reliability | Aggregation | New events needed? |
|---|---|:--:|:--:|:--:|:--:|
| M-100 | Weekly Participating Users | **VERIFIED** | HISTORICALLY ACCURATE | READ MODEL | No |
| M-101 | Speaking Conversion Rate | **VERIFIED** | HISTORICALLY ACCURATE | READ MODEL | No |
| M-102 | Weekly Return Rate | CONDITIONALLY RELIABLE | PARTIALLY RECONSTRUCTABLE | READ MODEL | No |
| M-200 | Distinct Active Hosts | **VERIFIED** | HISTORICALLY ACCURATE | READ MODEL | No |
| M-201 | Host Retention | **VERIFIED** | HISTORICALLY ACCURATE | READ MODEL | No |
| M-202 | Supply Concentration | CONDITIONALLY RELIABLE | HISTORICALLY ACCURATE | READ MODEL | No |
| M-203 | Non-Host Speakers per Room | **VERIFIED** | HISTORICALLY ACCURATE | READ MODEL | No |
| M-204 | Audience Return per Host | CONDITIONALLY RELIABLE | PARTIALLY RECONSTRUCTABLE | READ MODEL | No |
| M-300 | Sequential Onboarding Funnel | **VERIFIED** | PARTIALLY RECONSTRUCTABLE | LIVE → READ MODEL | No |
| M-301 | Admin Review Latency | **VERIFIED** | PARTIALLY RECONSTRUCTABLE | LIVE QUERY | No |
| M-302 | Activation → First Room Join | **VERIFIED** | PARTIALLY RECONSTRUCTABLE | READ MODEL | No |
| M-303 | Pending Queue Depth | **VERIFIED** (forward) | CURRENT SNAPSHOT → ACCURATE | READ MODEL | No (snapshot job) |
| M-400 | Stage Funnel | EXPERIMENTAL → VERIFIED | NOT HISTORICALLY RELIABLE | READ MODEL | **Yes** |
| M-401 | Non-Host Speaking Minutes | CONDITIONALLY RELIABLE | NOT HISTORICALLY RELIABLE | READ MODEL | **Yes** |
| M-402 | Hand-Raise → Promotion Rate | EXPERIMENTAL → VERIFIED | NOT HISTORICALLY RELIABLE | READ MODEL | **Yes** |
| M-403 | Conversion by Room Config | CONDITIONALLY RELIABLE | HISTORICALLY ACCURATE | READ MODEL | No |
| M-500 | Report Rate per 1,000 Joins | **VERIFIED** | HISTORICALLY ACCURATE | READ MODEL | No |
| M-501 | Report Rate by Category | **VERIFIED** | HISTORICALLY ACCURATE | READ MODEL | No |
| M-502 | Repeat-Reported Users | CONDITIONALLY RELIABLE | HISTORICALLY ACCURATE | LIVE QUERY | No |
| M-600 | Message Reciprocity | **VERIFIED** | HISTORICALLY ACCURATE | LIVE QUERY | No |
| M-601 | Technical Ticket Rate | CONDITIONALLY RELIABLE | HISTORICALLY ACCURATE | LIVE QUERY | No |
| M-602 | Push Send Success Rate | CONDITIONALLY RELIABLE | NOT HISTORICALLY RELIABLE | READ MODEL | **Yes** |

**Distribution**: 12 VERIFIED · 8 CONDITIONALLY RELIABLE · 2 EXPERIMENTAL (maturing) · 0 UNRELIABLE.

**INFERENCE — two observations that shape sequencing.**

**Sixteen of twenty-two metrics need no new events.** They are computable today from data the audit verified as correct. The blocker for most of the target dashboard is **queries and read models**, not instrumentation — which means a large share of the value can ship before the first new event is emitted.

**No metric in the target set is UNRELIABLE.** That is by construction: a metric that cannot reach at least CONDITIONALLY RELIABLE does not get a contract, and without a contract it does not enter the dashboard. This is the mechanism that prevents a repeat of the current state, where three UNRELIABLE metrics render with the same authority as the sound ones.
