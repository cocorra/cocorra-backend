# 29 — Final Metric Trust Register

> **Generated**: 2026-09-02 | **Source of truth**: `Cocorra.BLL/Services/Analytics/MetricRegistry.cs`
> **Live equivalent**: `GET /Analytics/Metrics/Registry`
> **Regenerate this document whenever the registry changes.** If they disagree, the registry is right.

---

## How to read a trust level

| Level | Meaning | Dashboard treatment |
|---|---|---|
| **VERIFIED** | Formula, population and exclusions proven; no known caveat | Normal presentation, small unobtrusive badge |
| **CONDITIONALLY RELIABLE** | Usable, but only alongside a stated condition | Badge **plus the condition rendered inline** — one line, adjacent to the number, **not a tooltip** |
| **EXPERIMENTAL** | Computed correctly, not yet validated against enough real emission | Badge plus visual de-emphasis, explicit "not yet validated" note |
| **UNRELIABLE** | Known to mislead | **Must not be displayed.** Served only where a legacy route awaits cutover |
| **DEPRECATED** | Removed from every response | Cannot be rendered — the field does not exist |

**Why CONDITIONALLY RELIABLE must be inline and not hover**: a tooltip is not read by someone scanning a dashboard, and it does not survive a screenshot. The condition is what makes the number usable, so hiding it behind an interaction defeats the purpose.

**Why DEPRECATED is not a code enum value**: deprecation is enforced by *removal*, not by a label. A warned-but-visible wrong number still ends up in a deck without its warning. Server-side removal is the only enforcement that survives contact with real users.

---

# Summary

| Trust level | Count | Metric keys |
|---|:--:|---|
| **VERIFIED** | **14** | M-100, M-101, M-102, M-200, M-201, M-202, M-205, M-300, M-301, M-303, M-501, M-505, M-507, M-508 |
| **CONDITIONALLY RELIABLE** | **10** | M-103, M-302, M-500, M-502, M-503, M-504, M-506, M-601, M-701, M-702 |
| **EXPERIMENTAL** | **1** | M-400 |
| **UNRELIABLE** | **2** | M-102-LEGACY, M-507-LEGACY |
| **Total contracted** | **27** | |
| **DEPRECATED (removed from all responses)** | 4 | Top Speakers, Hand-Raise Count, Avg Duration Hours, Projected Status Breakdown |
| **RESERVED (planned, unimplemented)** | 8 | M-203, M-204, M-401, M-402, M-403, M-600, M-602, and the plan's M-302 |

*The plan's M-302 (Activation → First Room Join) is unimplemented while the code's M-302 (Review Latency) is served — the ID is taken and a new one is needed. See `26-metric-registry-reconciliation.md`.*

**No metric in this register is ambiguous.** Every one of the 27 has a formula, a population, a stated set of exclusions, an explicit historical-reliability classification, and a validation method, all executable from code.

---

# VERIFIED

---

## M-100 — Weekly Participating Users (WPU) · **NORTH STAR**

| | |
|---|---|
| **Definition** | Distinct non-host users who joined at least one live room in the window |
| **Population** | Users with a `room_joined` event in the window |
| **Formula** | `COUNT(DISTINCT UserId) WHERE EventType='room_joined' AND UserId != Room.HostId` |
| **Data source** | RM-1 `DailyPlatformMetrics.DistinctJoiningUsers` |
| **Endpoints** | `GET /Analytics/Platform/Health`, `GET /Analytics/Decisions` |
| **Historical reliability** | HISTORICALLY ACCURATE within raw retention; indefinite once read models accumulate |
| **Exclusions** | Room host joining own room; anonymous joins |
| **Limitations** | Bounded by 180-day raw retention unless aggregated. Counts attendance, not conversation — read beside M-101 |
| **Trust** | **VERIFIED** |
| **Decision supported** | Is Cocorra delivering more value than last week, and which input constrained it? |

**Note on availability**: until this phase, M-100 was reachable **only** through `/Analytics/Decisions`, which must not be relied on before 4–6 weeks of baseline exist. The declared north star was therefore unreadable on any page anyone could trust. `A-1` fixes that.

---

## M-101 — Speaking Conversion Rate

| | |
|---|---|
| **Definition** | Share of non-host joiners who activated a microphone |
| **Population** | Distinct non-host joiners in the window |
| **Formula** | `COUNT(DISTINCT NonHostMicActivators) / COUNT(DISTINCT NonHostJoiners) * 100` |
| **Data source** | `UserEvents` (`mic_activated`), RM-1 |
| **Endpoints** | `/Analytics/Participation`, `/Analytics/Participation/ActiveVsPassive`, `/Analytics/Platform/Health` |
| **Historical reliability** | HISTORICALLY ACCURATE |
| **Exclusions** | Room host, from numerator **and** denominator |
| **Limitations** | Derived from `mic_activated`, which fires on an unmute transition, so a participant who never mutes is counted once |
| **Trust** | **VERIFIED** |
| **Decision supported** | Is attendance becoming participation? |

**On the multi-day window**: the rate is recomputed from summed numerator and denominator, never averaged across daily percentages. Averaging would weight a quiet Tuesday equally with a busy Saturday and produce a figure matching no real period.

---

## M-102 — Weekly Return Rate

| | |
|---|---|
| **Definition** | Of non-host users who joined in week N, the share who joined again in any later week |
| **Population** | Non-host joiners, grouped by ISO week (Monday 00:00 UTC) |
| **Formula** | `COUNT(DISTINCT ReturningJoiners) / COUNT(DISTINCT CohortJoiners) * 100` |
| **Data source** | `UserEvents` (`room_joined`) |
| **Endpoint** | `GET /Analytics/Return/Weekly` |
| **Historical reliability** | HISTORICALLY ACCURATE within raw retention |
| **Exclusions** | Room hosts. **`session_started` is deliberately not consulted** |
| **Limitations** | Hard deletes remove non-returners from the denominator, biasing the rate **upward** until AN-013 lands. The final week in any window has no later week yet, so `isComplete=false` and it must not be charted |
| **Trust** | **VERIFIED** |
| **Decision supported** | Was the experience worth repeating? |

**Why this replaced rather than repaired the old metric**: changing the legacy `== day` comparison to `>= day` while leaving `session_started` as the signal would have produced a plausible number resting on a cookie-derived input never validated on the Flutter client. `room_joined` is server-emitted, indexed, cookie-independent, and untouched by any of D-1…D-5.

---

## M-200 — Distinct Active Hosts

| | |
|---|---|
| **Definition** | Distinct hosts who created at least one room in each period |
| **Population** | `Rooms.HostId` |
| **Formula** | `COUNT(DISTINCT Rooms.HostId) GROUP BY period(Rooms.CreatedAt)` |
| **Data source** | `Rooms`, RM-3 `DailyHostMetrics` |
| **Endpoints** | `GET /Analytics/Supply/Health`, `GET /Analytics/Platform/Health` |
| **Historical reliability** | **HISTORICALLY ACCURATE TO DAY ONE** — `Rooms` is relational and never purged |
| **Exclusions** | Hosts with no room in the period — absence is the signal |
| **Limitations** | Counts hosts who *created* a room, not who ran a good one. On `/Analytics/Platform/Health` the multi-day figure is the **MAX of daily distincts**, a lower bound: summing would double-count a host active on two days |
| **Trust** | **VERIFIED** |
| **Decision supported** | Recruit more coaches, or help existing coaches run better rooms? |

**This ID was corrected in this phase** — it previously carried the "Rooms Gone Live" definition while attached to a host-count payload. See `26-metric-registry-reconciliation.md` §3.

---

## M-201 — Host Second-Room Rate

| | |
|---|---|
| **Definition** | Of hosts whose **first** room falls in the window, the share who created a second room at any later point |
| **Population** | First-time hosts in the window |
| **Formula** | `COUNT(hosts with >=2 rooms and first room in window) / COUNT(hosts with first room in window) * 100` |
| **Data source** | `Rooms`, RM-3 |
| **Endpoint** | `GET /Analytics/Supply/Health` |
| **Historical reliability** | HISTORICALLY ACCURATE TO DAY ONE |
| **Exclusions** | Hosts whose first room predates the window — they are not new hosts |
| **Limitations** | A host whose second room falls after the query window still counts, so the figure **rises as later data arrives**. A reading taken today for last month is not final |
| **Trust** | **VERIFIED** |
| **Decision supported** | Is host recruitment worth the spend, or do recruits run one room and stop? |

---

## M-202 — Host Concentration

| | |
|---|---|
| **Definition** | Share of rooms run by the top host and top 3 hosts, plus how many hosts cover half of all rooms |
| **Population** | All hosts with a room in the window |
| **Formula** | `top-N room count / total rooms`; smallest `k` where `sum(top k) >= total/2` |
| **Data source** | `Rooms`, RM-3 |
| **Endpoint** | `GET /Analytics/Supply/Health` |
| **Historical reliability** | HISTORICALLY ACCURATE TO DAY ONE |
| **Exclusions** | None |
| **Limitations** | Counts **rooms, not audience**: a host running many small rooms outranks one running a few large ones |
| **Trust** | **VERIFIED** |
| **Decision supported** | How much key-person risk sits on the supply side? |

**Display requirement**: show the histogram, not the mean. With a small coach pool one prolific host makes the average meaningless — the shape is the finding.

---

## M-205 — Rooms Gone Live

| | |
|---|---|
| **Definition** | Rooms created in the window whose status is anything other than `Scheduled` |
| **Population** | `Rooms` |
| **Formula** | `COUNT(Rooms) WHERE Status != Scheduled AND CreatedAt IN window` |
| **Data source** | `Rooms`, RM-1 `RoomsGoneLive` |
| **Endpoints** | `GET /Analytics/Rooms`, `GET /Analytics/Platform/Health` |
| **Historical reliability** | HISTORICALLY ACCURATE TO DAY ONE |
| **Exclusions** | Scheduled rooms cancelled before starting |
| **Limitations** | **Inferred from terminal status, not observed at go-live.** `room_went_live` (AN-017) is the direct signal and is behind `Analytics:EnableNewEventEmission` |
| **Trust** | **VERIFIED** |
| **Decision supported** | Is voice supply actually being delivered, as opposed to scheduled? |

---

## M-300 — Report Insights

| | |
|---|---|
| **Definition** | Counts of `Report` rows by status and reason in the window, plus most-reported users |
| **Population** | All reports in the window |
| **Formula** | `COUNT(Reports) GROUP BY Status, Reason` |
| **Data source** | `Reports` |
| **Endpoints** | `/Analytics/Reports`, `/Analytics/Summary`, `/Analytics/Safety/ReportRate` |
| **Historical reliability** | HISTORICALLY ACCURATE — relational, never purged |
| **Exclusions** | None |
| **Limitations** | `Report.Status` is a free-form string; only `Open`, `Resolved` and `Rejected` are written today. AN-033 added a typed `StatusCode` alongside it |
| **Trust** | **VERIFIED** — the only metric graded VERIFIED by the original audit |
| **Decision supported** | Where should moderation attention go? |

---

## M-301 — Report Rate by Room Category

| | |
|---|---|
| **Definition** | Reports naming a room in each category, per 1,000 distinct non-host joins of rooms in that category |
| **Population** | Reports carrying room context |
| **Formula** | `reports_in_category / non_host_joins_in_category * 1000` |
| **Data source** | `Reports`, `Rooms`, `UserEvents` |
| **Endpoint** | `GET /Analytics/Safety/ReportRate` — **Admin only** |
| **Historical reliability** | HISTORICALLY ACCURATE |
| **Exclusions** | Reports with no room context are **excluded, not bucketed into Others**. Host joins excluded from the exposure denominator |
| **Limitations** | Measures **reports filed, not incidents occurred** — under-reporting in a sensitive category would read as safety. A category with no joins returns `null`, not a zero rate |
| **Trust** | **VERIFIED** |
| **Decision supported** | Do `MentalHealth` and `Relationships` rooms need category-specific safeguards? |

**The highest-stakes analysis in the register.** Two of three categories carry duty-of-care obligations a general social product does not. Both inputs were already verified; the segmentation had simply never been run.

**Display requirements**: normalise per 1,000 joins, never raw counts — raw counts rise with growth. Show absolute counts beside every rate; with three categories, cells can be small enough that a percentage alone misleads.

---

## M-303 — Pending Verification Queue Depth

| | |
|---|---|
| **Definition** | Daily snapshot count of users with `Status = Pending` |
| **Population** | `AspNetUsers` |
| **Formula** | `COUNT(AspNetUsers) WHERE Status = 'Pending'` |
| **Data source** | RM-5 `DailyStateSnapshots` |
| **Endpoint** | `GET /Analytics/Review/Latency` |
| **Historical reliability** | **CURRENT SNAPSHOT ONLY before the snapshot job ran; HISTORICALLY ACCURATE from its first run** |
| **Exclusions** | None |
| **Limitations** | **Cannot be backfilled.** Missing dates render as gaps, never interpolations — `snapshotGapDates` on `/Analytics/System/Health` lists them |
| **Trust** | **VERIFIED** (forward-looking) |
| **Decision supported** | Is the manual review backlog growing? |

**Why this was P0 despite being small**: it is the only read model that cannot be reconstructed. Every day the job did not run is a permanent hole.

---

## M-501 — User Registrations

| | |
|---|---|
| **Definition** | Count of `ApplicationUser` rows bucketed by `CreatedAt` |
| **Population** | All users |
| **Formula** | `COUNT(*) GROUP BY bucket(CreatedAt)` |
| **Data source** | `AspNetUsers`, RM-1 |
| **Endpoints** | `/Analytics/Users/Growth`, `/Analytics/Summary`, `/Analytics/Platform/Health` |
| **Historical reliability** | HISTORICALLY ACCURATE, with the deletion caveat below |
| **Exclusions** | None |
| **Limitations** | **Hard deletes remove users retroactively, so historical buckets can shrink** until AN-013 lands |
| **Trust** | **VERIFIED** |
| **Decision supported** | Are new users arriving? |

**Validation**: cumulative registrations must never decrease between runs (test 61). This catches the hard-delete distortion even before soft delete ships.

---

## M-505 — Most Active Rooms

| | |
|---|---|
| **Definition** | Rooms ranked by count of `room_joined` events in the window |
| **Population** | Rooms with at least one join |
| **Formula** | `COUNT(room_joined) GROUP BY RoomId ORDER BY count DESC` |
| **Data source** | `UserEvents` |
| **Endpoint** | `GET /Analytics/Rooms/Active` |
| **Historical reliability** | HISTORICALLY ACCURATE within raw retention |
| **Exclusions** | None |
| **Limitations** | Counts **joins, not concurrent presence**: a room with churn can outrank a room with a stable audience |
| **Trust** | **VERIFIED** |
| **Decision supported** | Which rooms and categories actually draw an audience? |

---

## M-507 — Sequential Activation Funnel

| | |
|---|---|
| **Definition** | Sequential funnel: a user counts at step N only if every earlier step has an earlier-or-equal first occurrence |
| **Population** | Users with at least one step event in the window |
| **Formula** | `\|{u : first(step_1) <= … <= first(step_N)}\|` |
| **Data source** | `UserEvents`, RM-4 |
| **Endpoints** | `GET /Analytics/Activation/Funnel`, `GET /Analytics/Funnel` (legacy shape) |
| **Historical reliability** | HISTORICALLY ACCURATE within raw retention |
| **Exclusions** | Events with no `UserId` |
| **Limitations** | Only measures instrumented steps; an uninstrumented step is **absent from the response**, not reported as zero |
| **Trust** | **VERIFIED** |
| **Decision supported** | Where do new users stall on the way to their first room? |

**Fixes D-5.** Monotonicity is structural — the qualified set only shrinks — with a defensive assertion that throws rather than returning a funnel that widens downward. Each step also carries median and p90 elapsed time from the previous step.

---

## M-508 — Voice Verification Drop-off

| | |
|---|---|
| **Definition** | Counts at each voice-verification stage from submission to result |
| **Population** | Users who submitted a voice verification |
| **Formula** | `COUNT(DISTINCT UserId)` per verification stage |
| **Data source** | `UserEvents`, RM-1 `VoiceVerificationsApproved` |
| **Endpoints** | `GET /Analytics/VoiceVerification/DropOff`, `GET /Analytics/Platform/Health` (as "New Activations") |
| **Historical reliability** | HISTORICALLY ACCURATE within raw retention |
| **Exclusions** | None |
| **Limitations** | **Conversion only** — how many cleared each stage, not how long they waited. Read beside M-302 |
| **Trust** | **VERIFIED** |
| **Decision supported** | Is the manual review gate rejecting or delaying? |

---

# CONDITIONALLY RELIABLE

> **Every metric below must render its condition inline, adjacent to the number.**

---

## M-103 — Weekly Cohort Retention Grid

| | |
|---|---|
| **Definition** | Users grouped by the week of their **first** room join; each later cell is the share of that cohort joining in that week |
| **Population** | Non-host first-time joiners |
| **Formula** | `active_in_week_N / cohort_size * 100` |
| **Data source** | `UserEvents`, RM-1 |
| **Endpoint** | `GET /Analytics/Return/CohortGrid` |
| **Historical reliability** | PARTIALLY RECONSTRUCTABLE — bounded by raw retention |
| **Exclusions** | Room hosts |
| **Limitations** | Needs **~8 weeks** to read as a trend; `hasSufficientHistory` reports whether the bar is met. Recent cohorts have shorter rows **by construction — a short row is missing data, not a collapse in retention**. Hard deletes bias every row upward |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | Is retention improving for newer cohorts? |

**Display requirement**: hide the grid until 8 weeks exist. A sparse grid of mostly-empty cells invites over-interpretation of tiny samples; hidden beats sparse.

---

## M-302 — Voice Verification Review Latency

| | |
|---|---|
| **Definition** | Hours between a user's first `voice_verification_submitted` and their first `voice_verification_result` |
| **Population** | Users who received a result |
| **Formula** | `percentile(result_time − submit_time)` over reviewed users |
| **Data source** | `UserEvents` |
| **Endpoint** | `GET /Analytics/Review/Latency` |
| **Historical reliability** | PARTIALLY RECONSTRUCTABLE — bounded by 180-day raw retention |
| **Exclusions** | Submissions still awaiting a result — they have no latency yet, and counting them as zero would understate the wait |
| **Limitations** | **Percentiles only, by contract: NO MEAN is returned.** Pending submissions are excluded, so a growing backlog does not move this figure — **read it beside M-303** |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | Invest in review capacity, or leave the gate alone? |

**Excluding the mean is a contract requirement, not a presentation preference.** If most reviews take 20 minutes and 15% take three days, the mean describes nobody and hides the users being harmed. `ValidationMethod` asserts the response contains no mean field.

---

## M-500 — Platform Summary

| | |
|---|---|
| **Definition** | Composite of the registration, room, participation and report metrics |
| **Population** | Inherited per component |
| **Formula** | Composite — see component contracts |
| **Data source** | Inherited |
| **Endpoint** | `GET /Analytics/Summary` |
| **Historical reliability** | Inherited (weakest) |
| **Exclusions** | Inherited |
| **Limitations** | **Inherits the weakest trust level of its components.** Read the component contracts before acting on it |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | None on its own — an orientation view |

**Superseded by `GET /Analytics/Platform/Health` (A-1)**, which returns per-metric trust instead of one aggregate verdict. This route stays live until the dashboard cuts over, then becomes `410 Gone`.

---

## M-502 — User Status At Time

| | |
|---|---|
| **Definition** | Per user, the most recent `voice_verification_result` at or before each bucket boundary; no event means Pending |
| **Population** | All users |
| **Formula** | `status(user, t) = last(voice_verification_result WHERE OccurredAtUtc <= t) ?? 'Pending'` |
| **Data source** | `UserEvents` |
| **Endpoint** | `GET /Analytics/Users/Growth` |
| **Historical reliability** | PARTIALLY RECONSTRUCTABLE — reaches back only as far as raw retention |
| **Exclusions** | Users with no status event are reported as Pending, not omitted |
| **Limitations** | Reconstructed from events, so bounded by the 180-day window. Users whose status changed before event tracking existed appear as Pending. `statusHistoryAvailableFromUtc` marks the boundary |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | What did the verification funnel look like historically? |

**Fixes D-3.** The old metric bucketed users by `CreatedAt` and counted them by *current* status, so a user who registered in month 1 and was banned in month 6 appeared as banned in month 1. The distortion grew with bucket age.

---

## M-503 — Room Participation

| | |
|---|---|
| **Definition** | `RoomParticipant` rows in the window, excluding each room's own host |
| **Population** | Non-host participants |
| **Formula** | `COUNT(*) WHERE JoinedAt IN window AND UserId != Room.HostId` |
| **Data source** | `RoomParticipants` |
| **Endpoints** | `/Analytics/Participation`, `/Analytics/Summary`, `/Analytics/Participation/ActiveVsPassive` |
| **Historical reliability** | HISTORICALLY ACCURATE |
| **Exclusions** | Room host in their own room |
| **Limitations** | **`TotalSpokenSeconds` includes idle open-mic time for anyone on stage**, so spoken-time averages overstate speech. Top speakers and hand-raise counts are not returned — neither is measurable from current data |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | How deep is audience participation? |

**Fixes D-1.** Host exclusion is the correction: a silent host accrued the room's full 2–3 hours as `TotalSpokenSeconds` while emitting no `mic_activated`, so the same person ranked as top speaker *and* passive listener in two panels of one dashboard. Test 4 asserts no `UserId` appears in both sets.

---

## M-504 — Room Analytics

| | |
|---|---|
| **Definition** | Rooms bucketed by `CreatedAt` with status and category breakdowns |
| **Population** | All rooms |
| **Formula** | `COUNT(*) GROUP BY bucket(CreatedAt), Status, Category` |
| **Data source** | `Rooms` |
| **Endpoints** | `/Analytics/Rooms`, `/Analytics/Summary` |
| **Historical reliability** | PARTIALLY RECONSTRUCTABLE — see the status caveat |
| **Exclusions** | None |
| **Limitations** | **Status counts are CURRENT, not as-at-period**: a room created Live and later Ended counts as Ended in every historical bucket, so a past period's Live/Ended split changes over time. **`AvgDurationHours` was removed** (TRUST-09) |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | How much supply exists, and of what kind? |

---

## M-506 — Peak Active Hours

| | |
|---|---|
| **Definition** | `room_joined` events grouped by UTC hour of day |
| **Population** | All joins in the window |
| **Formula** | `COUNT(room_joined) GROUP BY HOUR(OccurredAtUtc)` |
| **Data source** | `UserEvents` |
| **Endpoint** | `GET /Analytics/PeakHours` |
| **Historical reliability** | HISTORICALLY ACCURATE within raw retention |
| **Exclusions** | None |
| **Limitations** | **Reported in UTC while the user base is predominantly UTC+2/+3**, so displayed peaks are shifted from local time |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | When should rooms be scheduled and admin review staffed? |

**Mandatory display requirement (AN-036)**: apply `Meta.display.suggestedDisplayOffsetMinutes` (default 180). An unconverted chart sends a coach to a slot 2–3 hours off the real peak — a wrong answer delivered confidently, which is worse than no answer.

---

## M-601 — Support Volume and Response Time

| | |
|---|---|
| **Definition** | Support tickets by type per 1,000 active users; first-admin-reply and resolution times |
| **Population** | All support tickets and chats in the window |
| **Formula** | `tickets_of_type / active_users * 1000`; `percentile(first_admin_message − chat_created)` |
| **Data source** | `SupportTickets`, `SupportChats`, `SupportMessages` |
| **Endpoint** | `GET /Analytics/Support` |
| **Historical reliability** | HISTORICALLY ACCURATE — relational |
| **Exclusions** | None. **Anonymous tickets are included** — a user who cannot log in is the signal, not noise |
| **Limitations** | **PROXY MEASURE — no error tracking, structured logging sink or APM exists**, so this counts problems users bothered to report, not failures that occurred. `SupportTicket.Status` is free-form |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | Prioritise stability work? |

**The proxy label must be displayed with the number.** This is a lagging signal filtered by users' willingness to complain and biased toward loud failures. A silent audio failure that drives users away produces no signal here, so **a quiet week must not read as a healthy one.**

---

## M-701 — Social Graph Health

| | |
|---|---|
| **Definition** | Friend requests sent and accepted, acceptance rate, median time to accept, message volume |
| **Population** | All friend requests and messages in the window |
| **Formula** | `accepted / sent * 100`; `percentile(accepted_at − sent_at)` |
| **Data source** | `FriendRequest`, `Messages` |
| **Endpoint** | `GET /Analytics/Social` |
| **Historical reliability** | HISTORICALLY ACCURATE — relational |
| **Exclusions** | None |
| **Limitations** | **Reciprocity, not volume**: sends alone would let a spam wave read as engagement. `conversationsStarted` is `null` before AN-030 instrumentation rather than 0. Request origin defaults to `friend_list`; room-originated requests are not yet distinguishable |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | Invest in messaging and the friend graph, or treat them as utilities? |

**Mandatory display requirement**: **reciprocity before volume.** A high volume of one-directional messages is a warning sign — plausibly unwanted contact — not engagement growth. Ordering shapes the first reading.

---

## M-702 — MBTI Dichotomy vs Speaking

| | |
|---|---|
| **Definition** | For each of the four MBTI dichotomies, the share of non-host joiners with that trait who emitted `mic_activated` |
| **Population** | Non-host joiners with a recorded MBTI |
| **Formula** | `speakers_with_trait / joiners_with_trait * 100`, per dichotomy |
| **Data source** | `AspNetUsers`, `UserEvents` |
| **Endpoint** | `GET /Analytics/Mbti/Dichotomies` |
| **Historical reliability** | HISTORICALLY ACCURATE within raw retention |
| **Exclusions** | Users with no MBTI; users who never joined; room hosts |
| **Limitations** | **OBSERVATIONAL AND SELF-SELECTED**: users choose their own MBTI *and* choose whether to speak. **A gap is an association, never evidence of cause.** Four dichotomies rather than sixteen types, deliberately — sixteen cells are too small at Cocorra's volume, and testing sixteen hypotheses invites a chance finding |
| **Trust** | **CONDITIONALLY RELIABLE** |
| **Decision supported** | Room format design — **never user targeting** |

---

# EXPERIMENTAL

---

## M-400 — Stage Participation Funnel

| | |
|---|---|
| **Definition** | Sequential per-(room, participant) progression across `room_joined` → `hand_raised` → `stage_promoted` → `mic_activated` |
| **Population** | Non-host participants of rooms in the window |
| **Formula** | Per `(RoomId, UserId)`: `first(step_1) <= … <= first(step_N)`; count of pairs satisfying the prefix through step N |
| **Data source** | `UserEvents`, `Rooms` |
| **Endpoint** | `GET /Analytics/Participation/StageFunnel` |
| **Historical reliability** | **NOT HISTORICALLY RELIABLE.** Steps 2 and 3 do not exist before AN-017/AN-018 deployment |
| **Exclusions** | Room hosts; events with no `RoomId`; repeat events within a step |
| **Limitations** | **CANNOT BE BACKFILLED** — `hand_raised` and `stage_promoted` were never captured. Steps 2 and 3 are behind flags defaulting to **off**; while either is off the step returns `null` with `isMeasured=false` and **MUST render as a visible gap, never 0**. Direct host promotions without a hand raise drop out at step 2 by construction and are reported separately as `directPromotionsWithoutHandRaise`. Not segmented by `SelectionMode`, so automatic and manual stage rooms are pooled |
| **Trust** | **EXPERIMENTAL** → VERIFIED after 4 weeks of stable emission |
| **Decision supported** | Which control point in the stage flow should be redesigned? |

**The most important display rule in the register.** Rendering `0` for an uninstrumented step is not a display shortcut; it is a fabricated finding, and a plausible one. `observedParticipations` carries whatever each step genuinely saw, so a partially instrumented funnel still shows real numbers where it has them:

```
Stage Funnel — this week
  Joined room            1,247  ████████████████████
  Raised hand               ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Promoted to stage         ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Activated microphone     312  █████                 observed only
```

---

# UNRELIABLE

> **Neither may be displayed.** Both are served only so existing dashboard panels keep rendering until cutover, after which their routes become `410 Gone`.

---

## M-102-LEGACY — Legacy Retention Cohort

| | |
|---|---|
| **Definition** | Share of a cohort with an activity event on **exactly** day N after their cohort date |
| **Population** | Users with a cohort event |
| **Formula** | `\|{u : exists activity where days(activity − cohort) = N}\| / \|cohort\|` |
| **Data source** | `UserEvents` |
| **Endpoint** | `GET /Analytics/Retention` — **retained only until cutover** |
| **Historical reliability** | NOT HISTORICALLY RELIABLE |
| **Exclusions** | None |
| **Limitations** | **Exact-day matching: a user active on days 2 and 5 counts for neither D1 nor D7.** The default activity signal is `session_started`, cookie-derived and unvalidated on the Flutter client. **Superseded by M-102. Do not use for decisions** |
| **Trust** | **UNRELIABLE** |
| **Decision supported** | **None.** |

**Validation method: none.** Graded UNRELIABLE and retained only so existing dashboard panels keep rendering until the M-102 cutover. Validate against M-102 instead. **Must not be displayed on the new dashboard.**

---

## M-507-LEGACY — Legacy Independent-Step Funnel

| | |
|---|---|
| **Definition** | Count of distinct users per step, each step counted **independently** with no ordering constraint |
| **Population** | Users with any step event in the window |
| **Formula** | per step: `COUNT(DISTINCT UserId) WHERE EventType = step` |
| **Data source** | `UserEvents` |
| **Endpoint** | `GET /Analytics/Funnel` — **retained only until cutover** |
| **Historical reliability** | NOT HISTORICALLY RELIABLE |
| **Exclusions** | Events with no `UserId` |
| **Limitations** | **NOT A FUNNEL: steps are counted independently, so the result can WIDEN downward** — a later step may report more users than an earlier one (defect **D-5**). No ordering is enforced, so a user who performed step 3 before step 1 counts at both. **Superseded by M-507. Do not use for decisions** |
| **Trust** | **UNRELIABLE** |
| **Decision supported** | **None.** |

**Added during the final audit.** This route previously declared **M-507** — the *corrected*, sequential contract — while computing the defective independent-step version. The trust envelope was therefore certifying D-5 as fixed on the one route that still has it. Same defect class as the M-200 mis-attachment: a complete, well-formed contract describing something the endpoint does not do.

---

# DEPRECATED — removed from every response

These have **no contract and no field**. They cannot be rendered, by construction.

| Metric | Was served on | Why removed | Replacement |
|---|---|---|---|
| **Top Speakers** | `/Analytics/Participation` | Ranked hosts by room length — a host's mic opens with the room, so `TotalSpokenSeconds` accrued the full 2–3 hours of idle open-mic time (D-1/TRUST-01) | M-401 *(RESERVED)* — non-host speaking minutes from `mic_deactivated.segmentSeconds` |
| **Users Who Raised Hand** | `/Analytics/Participation` | Read `RoomParticipant.IsHandRaised`, a **transient live-state flag reset on approval**, so it counted only hands still up at query time | `hand_raised` events (AN-018) |
| **Avg Duration Hours** | `/Analytics/Rooms` | Averaged `Room.DurationHours` — a host-typed **scheduling** field defaulting to 2, not an observed duration (TRUST-09). Removed in this phase | `room_ended.actualDurationSeconds` (AN-019), measured against `room_went_live` |
| **Projected Status Breakdown** | `/Analytics/Users/Growth` | Bucketed users by `CreatedAt` and counted by **current** status, so history was rewritten as users changed state (D-3/TRUST-02) | M-502, reconstructed from events |

**Why removal rather than a warning label**: a warned-but-visible wrong number still gets screenshotted into a deck without its warning. Server-side removal is the only enforcement that survives contact with real users. `Meta.limitations` on the parent metric records that the field was removed and why, so the absence is explained rather than mysterious.

---

# RESERVED — planned, not implemented

Nothing may claim these IDs. Listed so a future implementer does not reuse one for a different metric.

| ID | Planned metric | Blocked on |
|---|---|---|
| **M-203** | Distinct Non-Host Speakers per Room | Nothing — computable today |
| **M-204** | Audience Return per Host | Nothing — computable today |
| **M-401** | Non-Host Speaking Minutes | `mic_deactivated` history (`EnableHighFrequencyEvents`) |
| **M-402** | Hand-Raise → Stage Promotion Rate | Available now as M-400's step-2→step-3 conversion; no separate contract |
| **M-403** | Speaking Conversion by Room Configuration | Nothing — computable today |
| **M-600** | Message Reciprocity Rate | Not computed. M-701 covers **friend-request** reciprocity, not message reply rate |
| **M-602** | Push Send Success Rate | `push_send_attempted` / `push_send_result` history (`EnableNewEventEmission`) |
| *(plan's)* **M-302** | Activation → First Room Join | Nothing — computable today. The ID is taken; a new one is needed |

**INFERENCE** — four of these (M-203, M-204, M-403, and activation→first-join) need **no new events and no schema change**. They are the cheapest remaining analytics work in the repository and are listed in the final report's next-steps as such.

---

# Cross-cutting limitations

These apply to **every** metric above and are stated once rather than repeated 26 times.

| # | Limitation | Direction of bias |
|:--:|---|---|
| **U-1** | Raw events purged after 180 days (`RawEventRetentionDays`). No event-derived metric has history beyond that until read models accumulate | Truncation, not bias |
| **U-2** | Events can be lost on channel overflow. Absolute counts are **lower bounds**. Observable via `eventsDroppedOnEnqueue` on `/Analytics/System/Health` | Downward |
| **U-3** | **Hard deletes** remove users from all longitudinal analysis (AN-013 blocked on a data-protection decision) | **Upward** on every user rate |
| **U-4** | All computation is UTC. The user base is UTC+2/+3, so daily and hourly buckets do not align with local days. `Meta.display.suggestedDisplayOffsetMinutes` provides the offset | Phase shift |
| **U-5** | Read models are populated by an hourly job. A stale pipeline makes every trust badge misleading — **always read `pipelineHealthy` from `/Analytics/System/Health` alongside** | Staleness |

**U-5 is the one that voids the others.** A trust badge on a metric whose pipeline stopped three days ago is worse than no badge, because it certifies stale data as verified. Trust and freshness must be displayed together or neither means anything.
