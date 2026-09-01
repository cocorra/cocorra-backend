# 05 — Analytics Gap Analysis (Decision-Driven)

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Note on numbering**: `05-event-tracking-audit.md` catalogued *what is tracked*. This document is organised by *what Cocorra needs to decide*. The two are complementary; this one supersedes nothing.
> **Depends on**: `07-decision-framework.md` (Findings A–E, Corrections 1–3), `07a-feature-investment-framework.md`, `07b-north-star-analysis.md`
> **Scope**: Documentation only.

---

## Method

Each gap is framed as a **decision Cocorra wants to make**, not as a missing metric. A gap only earns a place here if some real decision is blocked by it.

For each:

- **Required Evidence** — what would settle the question.
- **Current Evidence** — what the system provides today.
- **Missing Evidence** — what cannot be measured.
- **Risk of Acting Without Data** — CRITICAL / HIGH / MEDIUM / LOW.
- **Recommended Measurement** — event, properties, timestamp, entity relationship, historical requirement.
- **Priority** — P0 / P1 / P2 / P3.

**Risk classification** means the consequence of deciding *wrongly* because the data was absent or misleading:

| Level | Meaning |
|---|---|
| **CRITICAL** | Data actively misleads. A confident wrong decision is the likely outcome. |
| **HIGH** | Significant engineering investment would be allocated on no evidence. |
| **MEDIUM** | A suboptimal decision, correctable later at moderate cost. |
| **LOW** | Minor inefficiency. |

**Priority** is decision-value ÷ effort, ordered so that data-trust problems precede visibility problems:

| Level | Meaning |
|---|---|
| **P0** | Data trust. Must be resolved before the dashboard is used for major decisions. Includes actively wrong metrics. |
| **P1** | Blocks a high-value decision the team needs now. |
| **P2** | Enables deeper product intelligence. |
| **P3** | Advanced or speculative. |

---

# P0 — DATA TRUST GAPS

Everything in this section is a case where the system either produces a wrong number or destroys evidence irreversibly. Nothing downstream is safe until these are addressed.

---

## GAP-01 — "Who are our most engaged speakers?" is answered with a list of coaches ranked by room length

### Decision
Should Cocorra recognise, promote, or build features around its most engaged speakers?

### Required Evidence
Speaking time per user that reflects actual participation.

### Current Evidence
**FACT, Finding A** — When a room is created Live (`RoomService.cs:115-127`) or started (`RoomService.cs:439-449`), the host is inserted with `IsMuted = false` and `LastUnmutedAt = DateTime.UtcNow`. `TotalSpokenSeconds` accrues from that instant. A host who never touches their microphone accrues the room's entire wall-clock duration — 7,200 or 10,800 seconds, since `AllowedDurations` permits only 2 or 3 hours (`RoomService.cs:73`).

**FACT** — That initial open-mic state emits no `mic_activated` event, because `RoomHub.ToggleMic:518-521` fires only on a `muted → unmuted` transition.

### Missing Evidence
Any measure of genuine speaking.

### Why this is the single worst data-trust problem
**INFERENCE** — Two shipped metrics now contradict each other by construction:

| Shipped metric | How a passive host appears |
|---|---|
| Participation → **Top Speakers** | #1, with hours of "speaking" |
| Participation → **Active vs Passive** | a **passive listener** |

Both are on the dashboard. Both are wrong about the same person at the same time. This is worse than a missing metric: the Top Speakers leaderboard is confidently, plausibly, and consistently wrong, and it looks entirely reasonable to anyone reading it.

**INFERENCE — knock-on effect.** Because hosts are excluded from `mic_activated`, they are silently counted as passive listeners in the Active-vs-Passive denominator. Every room contributes at least one artificial "passive listener." In small rooms this materially depresses the reported active rate — the platform's headline participation-health number is biased downward by exactly one user per room.

### Risk of Acting Without Data
**CRITICAL**

### Recommended Measurement
**RECOMMENDATION**, in ascending cost:

1. **Analysis-layer fix, no code change** — exclude `UserId = Room.HostId` from Top Speakers, and add hosts to the Active-vs-Passive numerator (or report hosts as a separate cohort entirely). This removes the contradiction without touching the application.
2. **Event** — `mic_deactivated`
   - **Properties**: `roomId`, `segmentSeconds`, `isHost`
   - **Timestamp**: `OccurredAtUtc` at mute
   - **Relationship**: `UserId` → `ApplicationUser`; `RoomId` → `Room`
   - Paired with `mic_activated`, this yields a per-segment ledger instead of a mutable running total, and makes the host's initial state explicit.
3. **Historical data**: none reconstructable. Existing `TotalSpokenSeconds` values are permanently contaminated for hosts and cannot be repaired after the fact.

### Priority
**P0**

---

## GAP-02 — User growth history is retroactively rewritten by current status

### Decision
Is the platform growing, and what happened to each registration cohort?

### Required Evidence
For each cohort: how many registered, and what status they held *at the time*.

### Current Evidence
**FACT** — `AnalyticsRepository.GetUserGrowthAsync` (`AnalyticsRepository.cs:21-93`) buckets users by `CreatedAt` and then counts by **current** `Status`. A user who registered in January and was banned in June is reported as "Banned" in January's bucket. `07-metric-verification.md` marks this **MISLEADING** and **NOT SAFE FOR DECISIONS**; this analysis concurs.

### Missing Evidence
Historical status. **FACT** — `ApplicationUser` does not extend `BaseEntity` and has no `UpdatedAt`. No status-history table exists.

### Why this is P0 rather than merely wrong
**INFERENCE** — The distortion grows over time. A recent month looks accurate because few users have changed status yet; an older month is heavily rewritten. The chart therefore shows a systematic downward slope in "Active" for older cohorts that is a pure artefact of elapsed time. Anyone reading it will conclude that older cohorts were lower quality. They were not — they simply had longer to change status.

### Risk of Acting Without Data
**CRITICAL** — the chart's most natural reading ("our early users were worse") is a data artefact.

### Recommended Measurement
1. **Immediate, no code change (RECOMMENDATION)** — reconstruct historical status from `UserEvents` (`voice_verification_result` carries `{status}` with `OccurredAtUtc`) for the 180-day window. Within that window the answer is fully recoverable; the shipped endpoint simply queries the wrong source.
2. **Event** — `user_status_changed`
   - **Properties**: `fromStatus`, `toStatus`, `changedByAdminId`, `isBulkOperation`, `reason`
   - **Timestamp**: `OccurredAtUtc`
   - **Relationship**: `UserId` → the affected user; `changedByAdminId` → the acting admin
   - This single event closes GAP-02, GAP-03, and GAP-05.
3. **Historical data**: recoverable for 180 days from existing events; permanently lost before that.

### Priority
**P0**

---

## GAP-03 — Every user who churns hardest is deleted from the evidence

### Decision
Are we losing users, why, and which experiences precede loss?

### Required Evidence
A durable record of every user who ever existed, including those who left.

### Current Evidence
**FACT** — `AuthServices.DeleteAccountAsync` hard-deletes the `ApplicationUser` row. `UserEvent.UserId` is `SetNull` on delete, so the user's event history survives but is anonymised and can no longer be attributed or cohorted.

### Missing Evidence
The churned population itself.

### Why this contaminates everything downstream
**INFERENCE** — This is not one broken metric; it is a bias applied to every retention, cohort, and satisfaction analysis Cocorra will ever run:
- Retention is computed only over survivors, so every retention rate is **biased upward**.
- Registration history *decreases* retroactively as users delete, so past growth charts change every time someone leaves.
- The users most worth understanding — those who disliked the product enough to erase themselves — are precisely those guaranteed to be absent.
- **FACT** — `Report.ReportedUserId` is `SetNull` on delete, so a reported user who deletes their account vanishes from "most reported users." A bad actor can erase their own moderation history by deleting their account.

### Risk of Acting Without Data
**CRITICAL**

### Recommended Measurement
**RECOMMENDATION** — soft delete: `IsDeleted` flag plus `DeletedAt` timestamp, with personal data scrubbed in place rather than the row removed. This preserves analytical continuity while still honouring a deletion request in substance.
- **Required timestamp**: `DeletedAt`.
- **Required relationship**: preserve the `Id` so `UserEvent`, `Report`, and `RoomParticipant` foreign keys stay intact.
- **Historical data**: already-deleted users are unrecoverable. **INFERENCE** — every day this remains unaddressed permanently destroys more evidence, which is what makes it P0 rather than P1.

**Note (INFERENCE)** — this recommendation has data-protection implications. Whether a soft delete satisfies the applicable deletion obligation is a legal question, not an analytics one, and needs a decision from whoever owns that. The `account_deleted` event (`AuthServices.cs:565`, with `{reason}`) already survives deletion and provides a partial, anonymised record in the meantime.

### Priority
**P0**

---

## GAP-04 — Retention numbers are wrong in two independent ways

### Decision
Should Cocorra prioritise retention work, and is retention improving?

### Required Evidence
A reliable per-user recurring-activity signal, and a correct retention calculation over it.

### Current Evidence
Two independent defects, either of which alone would invalidate the metric:

**Defect 1 — the calculation (FACT)** — `AnalyticsRepository.cs:324-392` counts users active on **exactly** day N (`== day`). A user active on days 2, 3, and 5 but not day 1 contributes zero to D1 retention. `07-metric-verification.md` marks this **INCORRECT**.

**Defect 2 — the signal (FACT)** — the default `activeEvent` is `session_started`, emitted by `SessionTrackingMiddleware:53` and keyed on a `CocorraSessionId` **cookie**. The client is a Flutter mobile app. **INFERENCE** — cookie persistence across app launches on a mobile HTTP client is not guaranteed and depends entirely on the client's cookie-jar configuration. Compounding it, **FACT** — session deduplication uses in-process `IMemoryCache`, so every server restart re-counts all active sessions.

### Missing Evidence
A trustworthy activity signal — *for general activity*. See below.

### The important finding here
**INFERENCE, and this is the constructive part** — Cocorra does not need to fix `session_started` to measure retention. A **room-join-based** return metric is available today: `room_joined` is server-emitted, server-authoritative, indexed, cookie-independent, and marked VERIFIED. "Did this user join a room in a later week?" is both more reliable and more meaningful than "did a cookie survive," because it measures return to the *product's actual value event* rather than return to the app.

The gap is therefore **not** missing data. It is that the shipped endpoint uses the wrong source and the wrong formula, when a better source already exists.

### Risk of Acting Without Data
**CRITICAL** — the current numbers are believable-looking and wrong in a known direction (too low), which invites over-investment in retention work that may not be needed, or panic about a number that is an artefact.

### Recommended Measurement
1. **Immediate (RECOMMENDATION, analysis-layer only)** — compute weekly return from `room_joined` using "active in week N **or later**," not "exactly day N."
2. **For general (non-room) activity** — replace cookie-based sessions with an authenticated signal:
   - **Event** — `app_session_started`
   - **Properties**: `deviceId`, `appVersion`, `platform`
   - **Timestamp**: `OccurredAtUtc`
   - **Correlation**: a client-generated `sessionId` (UUID), persisted in app storage rather than a cookie
   - **Relationship**: `UserId` → `ApplicationUser`
   - **INFERENCE** — the correct primary key for mobile session identity is the authenticated user plus a device identifier, not an HTTP cookie. Cocorra already collects device metadata for `BlockedDevices`, so the concept exists in the system.
3. **Historical data**: 180 days of `room_joined` are available now and are sufficient to establish a baseline immediately.

### Priority
**P0**

---

## GAP-05 — Snapshot metrics have no history, so nothing can be compared to anything

### Decision
Is the verification backlog growing? Is the user base composition shifting?

### Required Evidence
Time series of counts that are currently only available as instantaneous snapshots.

### Current Evidence
**FACT** — `AdminService.GetDashboardStatsAsync` (`AdminService.cs:383-401`) is a bare `GroupBy(Status)` with no date filter. It reports the present and nothing else.

**FACT** — the same pattern recurs throughout the schema wherever state is stored without a transition log:

| Snapshot | Consequence |
|---|---|
| `ApplicationUser.Status` | Backlog depth over time unknown |
| `RoomParticipant.IsHandRaised` | Historical hand-raise count is effectively always ~0 |
| `RoomParticipant.IsOnStage` | Stage promotions invisible |
| `RoomReminder` rows | Deleted on un-toggle; intent history lost |
| `UserBlock` rows | Deleted on unblock; block prevalence unknowable historically |
| `Message.IsRead` | Read latency unknowable |

### Missing Evidence
Any historical dimension for state.

### Why this is one gap and not six
**INFERENCE** — This is a single architectural habit: the schema stores **current state** where analytics needs **transition history**. It explains the largest share of NOT POSSIBLE entries in the `07-decision-framework.md` matrix. Recognising it as one pattern matters, because it is fixable by one consistent rule rather than six unrelated patches.

### Risk of Acting Without Data
**HIGH** — the team cannot tell whether anything is getting better or worse, which makes every "did our change work?" question unanswerable.

### Recommended Measurement
**RECOMMENDATION — adopt one rule: every state transition emits an event.**

Concretely, and in priority order:

| Event | Properties | Closes |
|---|---|---|
| `user_status_changed` | `fromStatus`, `toStatus`, `changedByAdminId`, `isBulkOperation` | Backlog history, review latency, reviewer consistency, GAP-02 |
| `hand_raised` / `hand_lowered` | `roomId`, `secondsSinceJoin` | Historical hand-raise (GAP-06) |
| `stage_promoted` / `stage_demoted` | `roomId`, `targetUserId`, `byHostId` | Stage flow (GAP-06) |
| `reminder_set` / `reminder_removed` | `roomId`, `hoursUntilStart` | Reminder effectiveness (GAP-09) |
| `user_unblocked` | `blockedUserId` | Block prevalence over time |

Plus a **daily snapshot rollup** table for counts that are genuinely state rather than events (pending queue depth, total active users). **INFERENCE** — this is cheap, it is the only way to recover a time series for pure-state quantities, and it can start today without any application change, since it only reads existing tables.

**Historical data**: not reconstructable except for status, which is recoverable from `voice_verification_result` events within the 180-day window.

### Priority
**P0**

---

## GAP-06 — The middle of the core funnel is completely uninstrumented

### Decision
Where does the listener→speaker journey break, and which control point should be redesigned?

### Required Evidence
Per-step instrumentation of: joined → hand raised → approved to stage → mic activated → spoke → stayed.

### Current Evidence
**FACT** — only the first and fourth steps emit events:

| Step | Instrumented | Source |
|---|:--:|---|
| `room_joined` | ✅ | `RoomHub.cs:270` — VERIFIED |
| Hand raised | ❌ | `RoomHub.RaiseHand:381-400` writes a boolean, emits nothing |
| Approved to stage | ❌ | `ApproveToStage` emits nothing |
| `mic_activated` | ✅ | `RoomHub.cs:521` |
| Spoke meaningfully | ❌ | Findings A and B |
| Stayed | ❌ | No `LeftAt`; `JoinedAt` overwritten on rejoin (`RoomHub.cs:245-253`) |

### Missing Evidence
Four of six steps — every intermediate one.

### Why this is P0 rather than P1
**INFERENCE** — This is Cocorra's core value loop and its designated North Star input (`07b`, Input 3). The team can observe that Speaking Conversion moved and has **no instrumented path to why**. Every possible response — change the selection mode, raise stage capacity, extend speaker time, redesign the hand-raise affordance — is a guess. It sits in P0 not because a number is wrong, but because it makes the product's most important question structurally unanswerable, and because the missing data is not accumulating anywhere for later recovery.

### Risk of Acting Without Data
**CRITICAL**

### Recommended Measurement

| Event | Trigger | Properties | Purpose |
|---|---|---|---|
| `hand_raised` | `RoomHub.RaiseHand` | `roomId`, `secondsSinceJoin`, `currentStageOccupancy`, `stageCapacity` | Demand for the stage, and whether the stage was full |
| `hand_lowered` | `RoomHub.LowerHand` | `roomId`, `secondsRaised`, `wasApproved` | Gave-up-waiting signal |
| `stage_promoted` | `RoomHub.ApproveToStage` | `roomId`, `targetUserId`, `byHostId`, `secondsWaiting`, `selectionMode` | Approval latency and host behaviour |
| `stage_demoted` | `RoomHub.MoveToAudience` | `roomId`, `targetUserId`, `byHostId`, `stageSeconds` | Time on stage |
| `mic_deactivated` | `RoomHub.ToggleMic` | `roomId`, `segmentSeconds`, `isHost` | Real speaking segments (also closes GAP-01) |
| `speaker_time_exhausted` | `ToggleMic` throws "Your time is up!" | `roomId`, `allowedSeconds`, `extraGranted` | Whether the time budget binds |
| `extra_time_granted` | `RoomHub.GrantExtraTime` | `roomId`, `targetUserId`, `minutesGranted` | Host compensating for a too-tight budget |
| `room_left` *(extend existing)* | `LeaveRoom` / `OnDisconnected` | add `secondsInRoom`, `wasOnStage`, `didSpeak`, `leaveReason` | Time in room, drop-off shape |

**Required entity relationships** — every event carries `UserId` and the promoted, indexed `RoomId` column so it joins to `Room.Category`, `Room.SelectionMode`, `Room.HostId`, and `Room.StageCapacity`.

**Historical data** — none. **INFERENCE** — this data does not exist anywhere and is not accruing. Every week without it is a week that can never be analysed, which is the strongest argument for its priority.

### Priority
**P0**

---

# P1 — DECISION VISIBILITY GAPS

Data that exists but is not surfaced, or high-value decisions blocked by a small, well-defined gap.

---

## GAP-07 — Room supply health is fully measurable and entirely unwatched

### Decision
Should Cocorra recruit more coaches, or help existing coaches run better rooms?

### Required Evidence
Rooms per week, distinct active hosts per week, host retention, rooms per host, distinct non-host speakers per room, audience return per host.

### Current Evidence
**FACT** — every one of those is computable today from `Room.HostId`, `Room.CreatedAt`, and `mic_activated`/`room_joined` events. All are verified-reliable sources.

**FACT** — none of them is computed. All eleven `AnalyticsController` routes are user-, room-, participation-, report-, funnel-, retention-, active-room-, peak-hour-, voice-drop-off-, and active-vs-passive-oriented. There is no host-side or supply-side view anywhere.

### Missing Evidence
Only "rooms **gone live**" (Finding C). Everything else is present.

### Why this is the highest-value P1
**INFERENCE** — Cocorra is a two-sided marketplace whose supply side is very small. Losing two active coaches is a larger event than losing two hundred listeners, and it is visible weeks earlier. This is the platform's leading indicator, it requires no new instrumentation, and nobody is looking at it.

### Risk of Acting Without Data
**HIGH** — supply collapse would be diagnosed only after it had already depressed every user-side metric, by which point the causal direction would be ambiguous.

### Recommended Measurement
1. **Immediate (RECOMMENDATION)** — a supply analytics view built entirely on existing data. No events, no schema change.
2. **Event** — `room_went_live`
   - **Properties**: `roomId`, `wasScheduled`, `minutesLateVsSchedule`, `remindersSet`
   - **Timestamp**: `OccurredAtUtc` — this also supplies the actual go-live time that Finding C shows is missing everywhere
   - **Relationship**: `RoomId` → `Room`; `UserId` = host
3. **Historical data**: **INFERENCE** — go-live is approximable retroactively as `MIN(RoomParticipant.JoinedAt)` per room, since the host is inserted as a participant at start. Serviceable, undocumented, and fragile; acceptable as a stopgap.

### Priority
**P1**

---

## GAP-08 — Admin review latency is measurable and nothing measures it

### Decision
Is the manual voice-verification queue a throughput bottleneck?

### Required Evidence
Distribution of elapsed time from `voice_verification_submitted` to `voice_verification_result`, plus queue depth over time and per-reviewer consistency.

### Current Evidence
**FACT, and this corrects the earlier audit** — latency **is** derivable today. `06-blind-spots.md` §3 concluded it was impossible because `ApplicationUser` has no `UpdatedAt`. That is true of the *relational* data but not of the *event stream*: both events carry `UserId` and `OccurredAtUtc`, so the per-user gap is a straightforward query within the 180-day window.

**FACT** — no endpoint computes it.

### Missing Evidence
- Queue depth over time (GAP-05).
- Reviewer identity — **FACT**, `AdminService.cs:137` records only `{status}` against the reviewed user. Per-reviewer consistency is impossible.

### Why this matters disproportionately
**INFERENCE** — Every activated user passes through this queue. It is a hard serialisation point on the platform's entire growth funnel: no amount of acquisition spend can produce more active users than the queue approves. It is also, per `07a` FI-3 Stage 5, the best natural-experiment opportunity in the product, because approval latency is close to exogenous to the user.

### Risk of Acting Without Data
**HIGH**

### Recommended Measurement
1. **Immediate (RECOMMENDATION)** — latency distribution (median, p90, p99) by day-of-week and hour-of-day, computed from existing events.
2. **Event** — `user_status_changed` (as specified in GAP-02/GAP-05), whose `changedByAdminId` closes the reviewer-consistency gap.
3. **Historical data**: 180 days available now.

### Priority
**P1**

---

## GAP-09 — Room discovery is invisible end to end

### Decision
Should Cocorra invest in feed ranking, search, or the reminder loop?

### Required Evidence
Feed impressions per room; impression → join conversion; reminder set rate; reminder → attendance conversion; join entry-path attribution.

### Current Evidence
- **FACT** — `GET /Room/Feed` emits nothing. There is no record that a room was ever displayed to anyone.
- **FACT** — `ToggleReminder` emits nothing, and `RoomReminder` rows are deleted on un-toggle.
- **FACT** — `room_joined` carries only `{roomId}` — no source or referrer.
- **PARTIALLY AVAILABLE (INFERENCE)** — reminder → attendance is approximable today by joining surviving `RoomReminder` rows to `room_joined` for the same `(UserId, RoomId)`. Because un-toggles are hard-deleted, it can only see reminders still set at query time, so it reads **optimistically**. Direction of bias known; magnitude not.

### Missing Evidence
The entire top of the discovery funnel.

### Why this matters
**INFERENCE** — Rooms are ephemeral and scheduled. A room that nobody discovers in its 2–3 hour window is lost permanently — there is no catalogue, no recording, no second chance. Discovery is therefore more decisive for Cocorra than for a product with durable content. And with zero impression data, a low join count is indistinguishable between "nobody saw it" and "everybody saw it and passed," which are opposite problems requiring opposite fixes.

### Risk of Acting Without Data
**HIGH**

### Recommended Measurement

| Event | Trigger | Properties |
|---|---|---|
| `room_feed_viewed` | `GET /Room/Feed` returns | `roomIdsShown[]`, `feedPosition`, `filterApplied`, `resultCount` |
| `room_detail_viewed` | Room detail opened | `roomId`, `sourceSurface`, `feedPosition` |
| `reminder_set` / `reminder_removed` | `ToggleReminder` | `roomId`, `hoursUntilStart` |
| `room_joined` *(extend existing)* | unchanged | add `entrySource` ∈ {feed, reminder_push, deep_link, profile, direct} |

**Required correlation** — `entrySource` on `room_joined` is the highest-value single property in this document. **INFERENCE** — it converts every discovery question from unanswerable to trivial, and it is one string field on an event that already fires.

**Note (FACT)** — `room_feed_viewed` and `room_detail_viewed` are client-side and would need adding to the `ClientAllowedEvents` allowlist in `EventsController.cs:22`. Client-side events are untrusted; **RECOMMENDATION** — treat them as directional, never authoritative, and never mix them into a funnel with server-emitted events without labelling which steps are which.

### Priority
**P1**

---

## GAP-10 — Support data exists and no endpoint exposes it

### Decision
What are users struggling with, and is support volume a symptom of a fixable defect?

### Required Evidence
Ticket volume by `SupportTicketType` over time; chat volume; first-response time; resolution time; repeat contacters.

### Current Evidence
**FACT** — the data is largely present: `SupportTicket` with `Type` and `CreatedAt`; `SupportChat` with `CreatedAt` and `ClosedAt`; `SupportMessage` with `CreatedAt` and `IsFromAdmin` (making first-response time computable).

**FACT** — no analytics endpoint covers support at all.

### Missing Evidence
- Ticket resolution time (`SupportTicket.Status` is a free-form string with no `ResolvedAt`).
- Clean user attribution — **FACT**, `SupportChat.UserId` and `AdminId` are `string`, not `Guid`, unlike every other user reference in the schema. **INFERENCE** — this type mismatch makes joining support activity to behavioural data awkward and error-prone, and is plausibly part of why no endpoint was built.

### Why this matters more than it appears
**INFERENCE** — With no error tracking anywhere in the stack (`06-blind-spots.md` §9: errors reach `ILogger` → Docker stdout and are never persisted), `SupportTicketType.TechnicalProblem` volume is currently Cocorra's **only** systematic reliability signal. It is the closest thing the platform has to an outage alarm, and it is invisible on the dashboard.

### Risk of Acting Without Data
**MEDIUM** — the data survives and can be queried later; the cost is delay, not loss.

### Recommended Measurement
1. **Immediate (RECOMMENDATION)** — a support analytics endpoint over existing data.
2. **Schema** — `SupportTicket.ResolvedAt`; convert `Status` from string to enum.
3. **Historical data**: fully available — nothing is being lost while this waits.

### Priority
**P1**

---

## GAP-11 — Push notification delivery is unmeasured, in a codebase that has already shipped a delivery bug

### Decision
Should Cocorra invest in notification strategy, and is delivery currently working?

### Required Evidence
FCM send success/failure per notification; token validity across the active user base; open attribution back to the specific send.

### Current Evidence
**FACT** — `PushNotificationService.SendPushNotificationAsync` does not persist the Firebase response.
**FACT** — `notification_opened` is client-emitted with entirely client-defined properties, so there is no guaranteed `Notification.Id` to attribute an open to a send.
**AVAILABLE** — in-app read rate from `Notification.IsRead`.

### Missing Evidence
The entire delivery layer.

### Why the priority is higher than "we lack a nice-to-have metric"
**FACT** — commit `dc1c933` fixed *reversed FCM delivery* — notifications reaching the wrong user — by clearing stale tokens on logout and ban and enforcing device exclusivity.
**INFERENCE** — a defect of that severity has already occurred once in this codebase, and an identical regression today would be **invisible to the dashboard**. It would surface only through user complaints, exactly as it did the first time. For a known-recurring defect class, absence of monitoring is not a measurement gap so much as an unguarded regression.

### Risk of Acting Without Data
**HIGH**

### Recommended Measurement

| Event | Trigger | Properties |
|---|---|---|
| `push_send_attempted` | before the FCM call | `notificationId`, `notificationType`, `hasToken` |
| `push_send_result` | FCM response received | `notificationId`, `success`, `errorCode`, `tokenInvalidated` |
| `notification_opened` *(fix existing)* | client | **require** `notificationId` as a correlation id |

**Required correlation identifier** — `notificationId` linking send → delivery → open. **FACT** — `Notification.Id` already exists; it simply is not propagated into either the push payload or the client event.

**Complementary metric (RECOMMENDATION)** — share of `Active` users with a non-null `FcmToken`, tracked daily. Cheap, requires no new event, and would have made the original bug visible.

### Priority
**P1**

---

## GAP-12 — Safety measurement stops short of the one cut that matters most

### Decision
Do `MentalHealth` rooms need category-specific safeguards?

### Required Evidence
Report rate per 1,000 joins, **segmented by room category**.

### Current Evidence
**FACT** — fully available and not computed. `user_reported` carries `reportedRoomId` (`SupportService.cs:97`), which joins to `Room.Category`. The denominator (`room_joined` by room) is verified-reliable. `07-metric-verification.md` marks Report Insights **VERIFIED** — the highest-quality metric in the shipped dashboard.

### Missing Evidence
Only the segmentation. Both inputs exist.

### Why this is the highest-stakes available analysis
**INFERENCE** — Two of Cocorra's three room categories are `Relationships` and `MentalHealth`. Rooms discussing mental health carry a duty of care that a general social product does not. If reports concentrate in that category, it is a safety finding requiring a product response, not an analytics curiosity. The measurement costs one `GROUP BY` and nobody has run it.

### Risk of Acting Without Data
**HIGH** — a concentrated safety problem in the platform's most sensitive category would go unnoticed while every input needed to detect it sits in the database.

### Recommended Measurement
1. **Immediate (RECOMMENDATION)** — report rate per 1,000 room joins, by `Room.Category`, weekly. Existing data only.
2. **Event** — `moderation_action_taken`
   - **Properties**: `reportId`, `action` (`WarnUser`/`Mute24h`/`BanUser`/`RejectReport`), `targetUserId`, `byAdminId`, `hoursToAction`
   - **Purpose**: enforcement effectiveness and recidivism, currently unmeasurable.
3. **Schema** — `Report.Status` as an enum. **FACT** — it is a free-form string today and `AnalyticsRepository` recognises only "Open", "Resolved", "InProgress", silently dropping any other value from every status count.

### Priority
**P1**

---

## GAP-13 — The onboarding funnel endpoint cannot produce a funnel

### Decision
Where do prospective users abandon the five-step verification gate?

### Required Evidence
Sequential per-user progression with per-step elapsed time.

### Current Evidence
**FACT** — the *data* fully supports this: all six onboarding events are server-emitted with `UserId` and `OccurredAtUtc`.
**FACT** — the *endpoint* does not. `AnalyticsRepository.cs:300-322` counts `DISTINCT UserId` per event type independently. There is no ordering constraint, so it can report a later step with **more** users than an earlier one — impossible in a real funnel, and a visible symptom of the flaw.

### Missing Evidence
Only pre-submission abandonment (opened the registration form, never submitted) — which requires a client-side event.

### Risk of Acting Without Data
**MEDIUM** — the correct answer is computable from existing data today; the risk is that someone reads the current endpoint's output as a funnel and draws a conclusion from a non-monotonic chart.

### Recommended Measurement
1. **Immediate (RECOMMENDATION)** — a true sequential funnel: for each user, require step N's `OccurredAtUtc` to precede step N+1's. Existing data, corrected query.
2. **Client event** — `registration_started` (properties: `deviceId`, `platform`, `appVersion`), added to the `ClientAllowedEvents` allowlist.
3. **Historical data**: 180 days available now.

### Priority
**P1**

---

# P2 — PRODUCT INTELLIGENCE GAPS

Deeper analysis, valuable but not blocking a decision the team faces this quarter.

---

## GAP-14 — Time in room is unrecoverable

**Decision** — Should room length or format change? Do users leave early?

**Current Evidence** — **FACT** — no `LeftAt` on `RoomParticipant`; `RoomHub.JoinRoom:245-253` overwrites `JoinedAt` when re-activating a `Left` participant, destroying the original join time. `room_left` exists but carries only `{roomId}`.

**Missing Evidence** — All duration and drop-off-shape data.

**Why P2 and not P0 (INFERENCE)** — genuinely valuable, but the P0 items either produce actively wrong numbers or destroy evidence irreversibly. This one only prevents an analysis. It rises to P1 the moment room-format changes are on the roadmap, since it is the only way to evaluate them.

**Risk** — **MEDIUM**

**Recommended Measurement** — extend `room_left` with `secondsInRoom`, `wasOnStage`, `didSpeak`, `leaveReason` ∈ {explicit, disconnect, kicked, room_ended}. Add `RoomParticipant.LeftAt`, and stop overwriting `JoinedAt` on rejoin — **RECOMMENDATION**: model each attendance as its own row or session rather than mutating one.

**Priority** — **P2**

---

## GAP-15 — In-room group chat leaves no trace

**Decision** — Is text chat how the silent majority participates?

**Current Evidence** — **FACT** — `RoomHub.SendRoomGroupMessage:654-694` neither persists nor emits.

**Why this matters more than zero-data suggests (INFERENCE)** — Active-vs-Passive establishes that most participants never activate a mic, and labels them passive. If many are typing, the label is wrong and so is the conclusion drawn from it. Cocorra would be measuring participation on one channel while it happens on another.

**Risk** — **MEDIUM**

**Recommended Measurement** — **RECOMMENDATION**: before building persistence, run a cheap existence check — a single counter or SignalR volume inspection over one week — to establish whether the behaviour is material. If negligible, close the gap. If substantial, add `room_group_message_sent` (`roomId`, `messageLength`, `isOnStage`, `secondsSinceJoin`) and reinterpret Active-vs-Passive accordingly. Message *content* need not be persisted for the analytics question.

**Priority** — **P2**

---

## GAP-16 — DM origin surface is lost

**Decision** — Should Cocorra strengthen the room→DM bridge?

**Current Evidence** — **FACT** — `RoomHub.SendRoomPrivateMessage` and `ChatHub.SendMessage` both call `ChatService.SaveMessageAsync`, which emits an identical `message_sent` with only `{receiverId}` (`ChatService.cs:92`). The two surfaces are indistinguishable.

**Risk** — **MEDIUM**

**Recommended Measurement** — extend `message_sent` with `originSurface` ∈ {room, friends_list, profile} and optional `roomId`. **INFERENCE** — one property that separates "messaging is a room feature" from "messaging is a standalone feature," which is the entire decision.

**Priority** — **P2**

---

## GAP-17 — Friend-graph formation is unobservable

**Decision** — Should Cocorra build people discovery?

**Current Evidence** — **FACT** — `GET /api/Friends/search/{targetId}` requires the requester to already know the target's exact user ID. **INFERENCE** — friending must therefore be initiated from a room participant list or a profile view, neither of which emits an event. **FACT, Finding D** — re-sending after rejection mutates the existing row, overwriting `CreatedAt` and erasing the rejection.

**Risk** — **MEDIUM**

**Recommended Measurement** — extend `friend_request_sent` with `originSurface` and optional `sharedRoomId`; add `friend_request_rejected` and `friend_removed`; stop mutating rejected rows on re-send.

**Priority** — **P2**

---

## GAP-18 — No timezone dimension for a single-region user base

**Decision** — When should coaches schedule rooms?

**Current Evidence** — **FACT** — all analytics are UTC. Peak Hours groups by `OccurredAtUtc.Hour`. **INFERENCE** — the user base is Arabic-speaking MENA, so effectively UTC+2/+3, meaning the reported peak is offset by 2–3 hours from the local peak a coach would actually schedule against.

**Risk** — **MEDIUM** — a coach acting on a UTC peak-hours chart would schedule 2–3 hours off the real peak.

**Recommended Measurement** — **RECOMMENDATION**: display local time in the dashboard (a presentation-layer change, no data change). Longer term, capture `deviceTimezoneOffset` on session events so the assumption is verified rather than assumed.

**Priority** — **P2**

---

## GAP-19 — MBTI is collected from everyone and used for one pie chart

**Decision** — Does MBTI justify its onboarding friction?

**Current Evidence** — **FACT** — mandatory for every user; stored on `ApplicationUser.MBTI`; emitted as `mbti_submitted`; used solely as a distribution in `/Analytics/Users/Growth`.

**Missing Evidence** — Nothing structural. **INFERENCE** — the constraint is statistical: sixteen types across a small user base yields cells too thin to distinguish signal from noise.

**Risk** — **LOW** — the cost of not knowing is one unnecessary onboarding step.

**Recommended Measurement** — **RECOMMENDATION**: test the four dichotomies (E/I, S/N, T/F, J/P), not sixteen types, and test one hypothesis with a clear prior — do E-types activate the mic at a higher rate than I-types? Existing data; no instrumentation. If nothing shows at adequate sample size, MBTI is decoration and the step should be reconsidered on friction grounds.

**Priority** — **P2**

---

# P3 — ADVANCED INTELLIGENCE GAPS

---

## GAP-20 — Cocorra is blind to its own media layer

**Decision** — Should Cocorra invest in media infrastructure? Is audio quality driving people away?

**Current Evidence** — **FACT** — `ILiveKitService` exposes only `GenerateToken` and `UpdateStagePermissionAsync`. A repository-wide search for "webhook" returns zero matches in code or configuration. No participant events, no track events, no connection-quality data is ingested.

**Missing Evidence** — Everything about whether audio actually worked. **INFERENCE** — for a voice-first product, this is the largest single blind spot in the system. A room where audio failed for half the participants is indistinguishable from a room where half the participants chose not to speak — and those demand opposite responses.

**Risk** — **HIGH** in principle. **P3 in practice**, because the P0/P1 items are cheaper and address decisions the team faces sooner. **INFERENCE** — this escalates to P1 immediately if `TechnicalProblem` support tickets rise, which is the current de-facto detector.

**Recommended Measurement** — LiveKit webhook ingestion: `participant_joined`, `participant_left`, `track_published`, `track_unpublished`, plus connection-quality samples. Correlate on `roomId` and `participantIdentity`, which Cocorra already sets when generating tokens — so the correlation key already exists.

**Priority** — **P3**

---

## GAP-21 — No experimentation capability

**Decision** — Did any change we shipped actually work?

**Current Evidence** — **FACT** — no feature flags, no variant assignment, no experiment table, no bucketing logic anywhere in the solution.

**Missing Evidence** — Any basis for a causal claim (see `07a`, "The Causation Warning").

**Risk** — **MEDIUM** — every product decision is currently correlational.

**Recommended Measurement** — **RECOMMENDATION**, cheapest first: (1) exploit natural experiments already in the data, notably the approval-latency variation described in `07a` FI-3; (2) staged rollouts with a deterministic `UserId`-hash holdout, which needs no framework; (3) full A/B infrastructure only when volume justifies it. **INFERENCE** — with the manual approval gate throttling intake, Cocorra is unlikely to have the volume for well-powered A/B tests on secondary features in the near term. Treating A/B as the answer would be premature.

**Priority** — **P3**

---

## GAP-22 — No error or failure tracking

**Decision** — How reliable is the experience, and where does it break?

**Current Evidence** — **FACT** — errors reach `ILogger` → Docker stdout with 10MB/3-file rotation, and are never persisted or aggregated. No `registration_failed`, `login_failed`, `room_join_failed`, or `message_send_failed` events exist. No APM, no error-tracking service, no structured logging sink.

**Missing Evidence** — All failure data. **INFERENCE** — this is why support tickets currently function as the reliability signal (GAP-10). Cocorra learns about outages from users.

**Risk** — **HIGH** in principle; **P3** in sequencing because the P0/P1 items address decisions the team faces now.

**Recommended Measurement** — failure events on the paths that matter to users: `room_join_failed` (`roomId`, `reason`, `stage`), `registration_failed` (`step`, `reason`), `push_send_result` (already in GAP-11). **RECOMMENDATION** — a structured logging sink or error-tracking service would serve this better than analytics events, and is the more conventional solution.

**Priority** — **P3**

---

## GAP-23 — No acquisition attribution

**Decision** — Where should acquisition effort go?

**Current Evidence** — **FACT** — `ApplicationUser` has no source, referral, or campaign field. No invitation mechanism exists.

**Risk** — **LOW** at current scale. **INFERENCE** — this becomes important only once there is a deliberate acquisition budget to allocate; before that, there is nothing to attribute.

**Recommended Measurement** — an `AcquisitionSource` field captured at registration; `registration_started` carrying `referralCode`, `campaign`, and `platform`.

**Priority** — **P3**

---

# Gap Summary

| ID | Gap | Decision Blocked | Risk | Priority |
|---|---|---|:--:|:--:|
| **GAP-01** | Host mic open from room start; two metrics contradict | Who are our engaged speakers? | **CRITICAL** | **P0** |
| **GAP-02** | Growth history rewritten by current status | Is the platform growing? | **CRITICAL** | **P0** |
| **GAP-03** | Hard deletes erase the most-churned users | Are we losing users, and why? | **CRITICAL** | **P0** |
| **GAP-04** | Retention wrong twice over (exact-day + cookie) | Prioritise retention work? | **CRITICAL** | **P0** |
| **GAP-05** | Snapshot state with no transition history | Is anything getting better? | **HIGH** | **P0** |
| **GAP-06** | Core funnel middle uninstrumented | Where does listener→speaker break? | **CRITICAL** | **P0** |
| **GAP-07** | Supply-side health measurable, unwatched | Recruit coaches or enable them? | **HIGH** | **P1** |
| **GAP-08** | Review latency measurable, uncomputed | Is the queue a bottleneck? | **HIGH** | **P1** |
| **GAP-09** | Discovery invisible end to end | Invest in feed / reminders? | **HIGH** | **P1** |
| **GAP-10** | Support data exists, unexposed | What are users struggling with? | **MEDIUM** | **P1** |
| **GAP-11** | Push delivery unmeasured, after a known delivery bug | Invest in notifications? | **HIGH** | **P1** |
| **GAP-12** | Report rate by category not computed | Do MentalHealth rooms need safeguards? | **HIGH** | **P1** |
| **GAP-13** | Funnel endpoint cannot produce a funnel | Where do users abandon onboarding? | **MEDIUM** | **P1** |
| **GAP-14** | Time in room unrecoverable | Change room length or format? | **MEDIUM** | **P2** |
| **GAP-15** | Group chat leaves no trace | Is chat how silent users participate? | **MEDIUM** | **P2** |
| **GAP-16** | DM origin surface lost | Strengthen room→DM bridge? | **MEDIUM** | **P2** |
| **GAP-17** | Friend-graph formation unobservable | Build people discovery? | **MEDIUM** | **P2** |
| **GAP-18** | UTC-only for a UTC+2/+3 user base | When should coaches schedule? | **MEDIUM** | **P2** |
| **GAP-19** | MBTI collected, barely used | Does MBTI justify its friction? | **LOW** | **P2** |
| **GAP-20** | No media-layer telemetry | Invest in media infrastructure? | **HIGH** | **P3** |
| **GAP-21** | No experimentation capability | Did our change work? | **MEDIUM** | **P3** |
| **GAP-22** | No error or failure tracking | How reliable is the experience? | **HIGH** | **P3** |
| **GAP-23** | No acquisition attribution | Where should acquisition go? | **LOW** | **P3** |

---

## The Pattern Worth Noticing

**INFERENCE** — Sorting the gaps by *what kind of fix they need* is more useful than sorting by priority, because it changes who does the work:

| Fix type | Gaps | Character of the work |
|---|---|---|
| **Query/analysis only — no code change** | 02 (partial), 04 (partial), 07, 08, 12, 13, 19 | Seven gaps close by querying existing verified data correctly. No application change, no deployment risk. |
| **New events** | 01, 05, 06, 09, 11, 15, 16, 17 | The main instrumentation programme. |
| **Schema change** | 03, 10, 12 (status enum), 14 | Soft delete, resolution timestamps, enum conversion, `LeftAt`. |
| **New infrastructure** | 20, 21, 22 | Webhooks, experimentation, error tracking. |

**The headline (INFERENCE)** — a meaningful share of Cocorra's decision-making capability is blocked not by missing data but by **queries nobody has written against data that already exists and has already been verified as correct**. Supply health, review latency, report-rate-by-category, and a true sequential funnel are all available today. That is the cheapest and fastest work available, it carries no deployment risk, and it should not wait behind the instrumentation programme.
