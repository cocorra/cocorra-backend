# 10a — Decision Safety Matrix

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Depends on**: all preceding discovery documents
> **Scope**: Documentation only.

---

## Purpose

This document is deliberately blunt. Its job is to tell a product owner, before they act, whether the data behind a decision can carry the weight they are about to put on it.

Three categories:

| Category | Meaning |
|---|---|
| **SAFE TODAY** | The data supports this decision. The metric is verified, the source is server-authoritative, and no known bias could reverse the conclusion. |
| **USE WITH CAUTION** | The data is directionally useful but has a known bias. Safe only if the bias is understood, its direction is stated, and the decision is reversible. |
| **NOT SAFE TODAY** | The data does not support this decision. Either the metric is wrong, or the evidence does not exist. Acting on it means acting on nothing while believing you are acting on something. |

**One distinction that runs through everything below (INFERENCE)** — there are two different ways a decision can be unsafe, and they are not equally dangerous:

- **Absence** — the data does not exist. Survivable: you know you do not know, and the question stays open.
- **Contradiction** — the data exists, looks credible, and is wrong. Dangerous: it produces a confident decision in the wrong direction.

Cocorra has a great deal of the first and a small, concentrated amount of the second. The second is where the harm is.

---

# SAFE TODAY

Decisions the current data can reasonably support.

| Decision | Supporting Data | Limitation | Confidence |
|---|---|---|:--:|
| **Recruit more coaches, or focus on enabling existing ones** | `Room.HostId` + `Room.CreatedAt`: distinct active hosts per week, rooms per host, host retention, supply concentration. Pure relational data. | **FACT** — "rooms gone live" must be proxied by rooms with ≥1 non-host participant, since `StartScheduledRoomAsync` emits nothing (Finding C). Undercounts rooms that went live and drew nobody. | **HIGH** |
| **Judge whether the platform is growing in registrations** | `ApplicationUser.CreatedAt`, indexed (`IX_Users_CreatedAt`). `user_registered` events corroborate. | **FACT** — hard deletes shrink historical counts over time, so the trend erodes. Registration ≠ activation; use `activation_completed` for usable growth. | **HIGH** |
| **Judge whether room participation is growing** | `room_joined` events — server-emitted, indexed `RoomId`, marked **VERIFIED** in `07-metric-verification.md`. | **FACT** — count distinct users, not raw events; the event fires per SignalR reconnect. 180-day history cap. | **HIGH** |
| **Identify which of the three room categories draws participation** | `Room.Category` joined to `room_joined` via the promoted `RoomId`. | Only three categories exist (`Relationships`, `MentalHealth`, `Others`), so the analysis is coarse by design. | **HIGH** |
| **Identify which rooms and hosts draw the largest audiences** | `room_joined` distinct joiners grouped by `RoomId` → `Room.HostId`. | **FACT** — use `UniqueJoiners`, not `JoinEvents`. **INFERENCE** — a room with poor network conditions can rank highly on `JoinEvents` through reconnects alone, inverting the metric's meaning for exactly the rooms that went worst. | **HIGH** |
| **Judge whether safety problems are increasing** | `Report` table + `user_reported` events. `07-metric-verification.md` marks Report Insights **VERIFIED** — the highest-quality metric in the shipped dashboard. | **FACT** — normalise per 1,000 room joins; raw counts rise with growth. Status counts drop any string outside "Open"/"Resolved"/"InProgress". | **HIGH** |
| **Decide whether `MentalHealth` rooms need category-specific safeguards** | `user_reported.reportedRoomId` → `Room.Category`. Both inputs verified. | **FACT** — not currently computed anywhere. This is a query away, not a data gap. | **HIGH** |
| **Identify repeat-offending users for enforcement** | `Report.ReportedUserId` grouped. | **FACT** — `SetNull` on delete: a reported user who deletes their account disappears from the grouping, so a bad actor can erase their own moderation history. | **HIGH** |
| **Judge whether admin review is a throughput bottleneck** | Per-user gap between `voice_verification_submitted` and `voice_verification_result`, both server-emitted with `OccurredAtUtc`. | **FACT** — 180-day window. Reviewer identity is absent, so *which* reviewer is slow is unanswerable. Report the distribution, not the mean. | **HIGH** |
| **Judge whether onboarding steps convert** | Six server-emitted onboarding events with `UserId` and `OccurredAtUtc`. | **FACT** — the shipped `/Analytics/Funnel` endpoint is non-sequential and can render a widening funnel. Compute from raw events, not from that endpoint. | **HIGH** |
| **Judge whether messaging is used and reciprocated** | `Message` table, indexed on `(SenderId, ReceiverId, CreatedAt)`. Both directions are rows. | **FACT** — read latency unavailable (`IsRead` is a bare boolean; `Message.UpdatedAt` is never written). Origin surface unavailable. | **HIGH** |
| **Judge whether listeners convert to speakers, in aggregate** | Distinct `room_joined` users vs distinct `mic_activated` users, both server-emitted. | **FACT, Finding A** — hosts **must** be excluded from the denominator; as shipped, every room contributes one artificial "passive listener." Correct the exclusion and this is sound. | **HIGH** with the correction; **MEDIUM** as currently computed |
| **Judge whether users return to join more rooms** | `room_joined` across weeks — cookie-independent, server-authoritative. | **FACT** — biased upward by hard deletes; capped at 180 days. **RECOMMENDATION** — never compute this via `/Analytics/Retention`. | **MEDIUM-HIGH** |

**INFERENCE — the pattern in this list.** Everything safe today rests on either a relational row (`Room`, `Report`, `Message`, `ApplicationUser`) or on one of the two verified room events. Notably, **five of these thirteen decisions are not computed anywhere in the current dashboard** — supply health, report-rate-by-category, review latency, sequential onboarding, and room-join-based return. The safest available evidence is largely evidence nobody is looking at.

---

# USE WITH CAUTION

Decisions supported by incomplete or biased data. Usable if the bias is stated and the decision is reversible.

| Decision | Supporting Data | Limitation | Confidence |
|---|---|---|:--:|
| **Change the default `SelectionMode`** | `mic_activated` per room joined to `Room.SelectionMode`. | **INFERENCE** — hosts *choose* the mode, so the comparison confounds the mode with the kind of host who picks it. Directionally useful; not causal. Requires host exclusion. | **MEDIUM** |
| **Judge whether reminders drive attendance** | Surviving `RoomReminder` rows joined to `room_joined` for the same `(UserId, RoomId)`. | **FACT** — un-toggled reminders are hard-deleted, so this only sees reminders still set at query time and reads **optimistically**. Direction of bias known; magnitude unknown. Reminder-setters are also self-selected as already interested. | **LOW-MEDIUM** |
| **Judge whether friendships form and get used** | `FriendRequest` + `friend_request_sent`/`accepted`; utilisation via `Message` pairs. | **FACT, Finding D** — re-sending after rejection *mutates the existing row*, overwriting `Status` and `CreatedAt`. Table-derived acceptance rates undercount rejections; use the event-derived rate. Response latency unavailable. | **MEDIUM** |
| **Judge whether support volume signals a product defect** | `SupportTicket` by `SupportTicketType` and `CreatedAt`. | **INFERENCE** — a lagging proxy filtered by users' willingness to complain and biased toward loud failure modes. A silent audio failure that drives users away produces no signal. It is nonetheless the only reliability signal the platform has. | **MEDIUM** |
| **Judge support responsiveness** | `SupportChat.ClosedAt − CreatedAt`; first response from `SupportMessage.CreatedAt` + `IsFromAdmin`. | **FACT** — `SupportTicket` has no `ResolvedAt`; `Status` is a free-form string. `SupportChat.UserId` is a `string`, not a `Guid`, making joins to behavioural data error-prone. | **MEDIUM** |
| **Judge whether users are blocking each other more** | `user_blocked` events. | **FACT** — no `user_unblocked` event and rows are deleted on unblock, so prevalence is only ever a snapshot. The event stream gives volume but not net state. | **LOW-MEDIUM** |
| **Decide when coaches should schedule rooms** | Peak Hours by `OccurredAtUtc.Hour`. | **FACT** — UTC only, user base is UTC+2/+3, so the displayed peak is 2–3 hours off the local one. **INFERENCE** — it also weights all event types equally, so it partly measures API traffic rather than user activity. | **MEDIUM** — usable only after local conversion |
| **Judge whether push notifications are worth investing in** | `Notification.IsRead` for in-app; `Notification.ReferenceId` → `room_joined` for reminder attribution. | **FACT** — push *delivery* is entirely unmeasured; the FCM response is discarded. `notification_opened` has no guaranteed `notificationId`. **INFERENCE** — the downside of notifications (uninstall, disable) is completely invisible, so any positive finding is structurally over-optimistic. | **LOW** |
| **Judge whether profile completeness matters** | Snapshot completeness joined to lifetime outcomes. | **FACT** — no completion timestamp, no `ApplicationUser.UpdatedAt`, no profile events. **INFERENCE** — snapshot completeness correlates with tenure, so this measures how long someone has been around, not what a profile does. | **LOW** |
| **Judge whether MBTI predicts behaviour** | `ApplicationUser.MBTI` joined to any behavioural table. | **INFERENCE** — sixteen types across a small user base gives cells too thin to separate signal from noise. Test the four dichotomies instead. | **LOW** |
| **Judge whether the platform is growing in *active* users** | `activation_completed` events per week. | **FACT** — bounded by the manual review queue, so a flat number may reflect reviewer availability rather than demand. Read alongside registrations and review latency. | **MEDIUM** |

---

# NOT SAFE TODAY

Decisions the current data should **not** support. Acting on these means acting on nothing, or worse, on something wrong.

| Decision | Supporting Data | Limitation | Confidence |
|---|---|---|:--:|
| **Identify and reward the platform's most engaged speakers** | Top Speakers (`RoomParticipant.TotalSpokenSeconds`) | **FACT, Finding A** — the host is inserted with `IsMuted=false` and `LastUnmutedAt=UtcNow` at room start, so a silent host accrues the room's full 2–3 hours. The leaderboard ranks coaches by room length. **The same hosts are classified as passive listeners in Active-vs-Passive at the same time.** | **NOT POSSIBLE** |
| **Judge how long users stay in rooms, or whether they leave early** | — | **FACT** — no `LeftAt` on `RoomParticipant`; `RoomHub.JoinRoom:245-253` overwrites `JoinedAt` on rejoin. `room_left` carries no duration. | **NOT POSSIBLE** |
| **Judge actual room duration, or change room length** | `Room.DurationHours` | **FACT** — that is the *configured* value and only ever 2 or 3. **Finding C** — no actual go-live or end timestamp exists; `Room.UpdatedAt` is never written. `room_ended.durationHours` is computed from the *scheduled* `StartDate`. | **NOT POSSIBLE** |
| **Judge D1/D7/D30 retention** | `/Analytics/Retention` | **FACT** — two independent defects: exact-day matching (`== day`), and a cookie-dependent `session_started` on a Flutter client with `IMemoryCache` dedup lost on restart. Marked **INCORRECT** by the prior audit. | **NOT POSSIBLE** |
| **Judge historical user-status composition or cohort quality** | User Growth status breakdown | **FACT** — current status is backdated into historical buckets. **INFERENCE** — the distortion grows with bucket age, producing a false "our early users were worse" gradient that is purely an artefact of elapsed time. | **NOT POSSIBLE** |
| **Judge how many users raised their hand historically** | `UsersWhoRaisedHand` | **FACT** — `IsHandRaised` is a live boolean reset by `LowerHand`; the count reflects hands raised at the instant of the query and is near-permanently ~0 for any past window. | **NOT POSSIBLE** |
| **Diagnose *where* the listener→speaker journey breaks** | — | **FACT** — hand raises, stage promotions, stage demotions, and time-budget exhaustion emit nothing. Four of six funnel steps are dark. The outcome is visible; every cause is not. | **NOT POSSIBLE** |
| **Judge whether the stage or the speaker time budget is the constraint** | — | **FACT** — `GrantExtraTime` emits nothing; `ExtraMinutesGranted` holds only a final total with no timestamp or grantor; the "Your time is up!" throw is invisible. | **NOT POSSIBLE** |
| **Judge whether scheduled rooms actually go live** | — | **FACT, Finding C** — `StartScheduledRoomAsync` emits no event and writes no timestamp. A scheduled room never started is indistinguishable from one awaiting its slot. | **NOT POSSIBLE** |
| **Judge whether the feed converts to joins, or invest in feed ranking** | — | **FACT** — `GET /Room/Feed` emits nothing. There is no record a room was ever displayed. **INFERENCE** — a low join count cannot distinguish "nobody saw it" from "everyone saw it and passed," which are opposite problems requiring opposite fixes. | **NOT POSSIBLE** |
| **Attribute joins to a discovery source** | — | **FACT** — `room_joined` carries only `{roomId}`. No source or referrer. | **NOT POSSIBLE** |
| **Judge whether in-room group chat is how silent users participate** | — | **FACT** — `SendRoomGroupMessage` neither persists nor emits. **INFERENCE** — if many "passive listeners" are chatting, the Active-vs-Passive label and the conclusion drawn from it are both wrong. | **NOT POSSIBLE** |
| **Judge whether push notifications reach users** | — | **FACT** — the FCM response is discarded. **INFERENCE** — commit `dc1c933` fixed reversed FCM delivery; an identical regression today would be invisible until users complained. | **NOT POSSIBLE** |
| **Attribute a notification open to a specific send** | `notification_opened` | **FACT** — client-emitted with entirely client-defined properties and no guaranteed `notificationId`. Un-linkable opens produce no conversion rate. | **NOT POSSIBLE** |
| **Judge whether enforcement actions change behaviour** | — | **FACT** — `AdminReportAction` outcomes mutate user state and are never recorded as events. | **NOT POSSIBLE** |
| **Judge true churn, or why users leave** | — | **FACT** — `DeleteAccountAsync` hard-deletes the row. **INFERENCE** — the users most worth understanding are precisely those guaranteed absent from the data. No event fixes this; it requires soft deletion. | **NOT POSSIBLE** |
| **Judge DAU/MAU, stickiness, or session duration** | `session_started` | **FACT** — cookie-based on a Flutter client; dedup via in-process `IMemoryCache` lost on restart; no `session_ended` event, no heartbeat. | **NOT POSSIBLE** |
| **Judge whether audio quality affects participation** | — | **FACT** — zero LiveKit telemetry ingestion; `ILiveKitService` exposes only token generation and permission updates. **INFERENCE** — for a voice-first product, the largest single blind spot: a room where audio failed is indistinguishable from one where nobody chose to speak. | **NOT POSSIBLE** |
| **Judge how reliable the product is for users** | — | **FACT** — no failure events; errors reach `ILogger` → Docker stdout and are never persisted. Support tickets are the only proxy. | **NOT POSSIBLE** |
| **Claim any feature *causes* retention** | — | **FACT** — no experimentation infrastructure of any kind, no reliable per-user activity signal, and hard deletes removing the most-churned users. See `07a`. | **NOT POSSIBLE** |
| **Judge how the friend graph forms, or invest in people discovery** | — | **FACT** — friend search requires a pre-known exact user ID; no origin event on `friend_request_sent`. | **NOT POSSIBLE** |
| **Judge whether DMs originate in rooms** | — | **FACT** — in-room and friends-list DMs emit identical `message_sent` events (`ChatService.cs:92`). | **NOT POSSIBLE** |
| **Decide whether to build Topic Requests based on usage** | — | **FACT, Finding E** — the feature does not exist. Entities and `AppDbContext` config only; no controller, service, repository, route, or event. The tables are empty. | **NOT POSSIBLE** |
| **Judge whether profile completion drives outcomes** | — | **FACT** — no profile events, no `ApplicationUser.UpdatedAt`, so no completion timestamp exists for before/after analysis. | **NOT POSSIBLE** |
| **Compare anything to more than 180 days ago** | — | **FACT** — `EventCleanupService` purges beyond 180 days with no archive. No year-over-year view exists or can be recovered. | **NOT POSSIBLE** |
| **Judge whether the verification backlog is growing** | `/Admin/Dashboard/Stats` | **FACT** — a bare `GroupBy(Status)` with no date filter. Yesterday's pending count is unrecoverable. | **NOT POSSIBLE** |
| **Judge whether reviewers are consistent** | — | **FACT** — `voice_verification_result` records only `{status}` against the reviewed user. The acting admin is recorded nowhere. | **NOT POSSIBLE** |
| **Judge where users come from** | — | **FACT** — `ApplicationUser` has no source, referral, or campaign field. | **NOT POSSIBLE** |

---

# The Four Decisions Most Likely to Be Made Wrongly

**INFERENCE** — these are ranked not by how badly the data is broken, but by the product of *how wrong the data is* and *how likely someone is to act on it*. A broken metric nobody reads is harmless.

### 1. "Our coaches are our most engaged users — let's build creator tools around the top speakers"

**Why it will be made** — Top Speakers is on the dashboard, it looks authoritative, and the ranking is stable week to week (which reads as reliability but is actually just room duration being consistent).

**Why it is wrong** — **FACT, Finding A** — it ranks hosts by how long their rooms ran. A silent host outranks a genuinely prolific community speaker.

**What makes it dangerous (INFERENCE)** — the conclusion is *plausible*. Coaches probably are highly engaged. The metric would "confirm" a reasonable prior with evidence that contains no information about speaking at all, and the same dashboard classifies those coaches as passive listeners two panels away.

### 2. "Retention is terrible — drop everything and fix retention"

**Why it will be made** — the retention endpoint returns a number, and **FACT** — exact-day matching makes it artificially low. A number that looks like a crisis prompts crisis behaviour.

**Why it is wrong** — the number is wrong in a known direction, and its underlying signal (cookie-based `session_started` on a Flutter client) may be near-meaningless regardless of the formula.

**What makes it dangerous (INFERENCE)** — it would redirect the whole roadmap toward a problem whose true size is unknown. The correct response is to compute room-join-based return first, which is available today and materially more reliable — a query, not a quarter.

### 3. "Our early cohorts were lower quality — something has improved since"

**Why it will be made** — **FACT** — User Growth shows a systematic downward slope in "Active" for older cohorts, and that slope is the chart's most visually obvious feature.

**Why it is wrong** — status is backdated. Older cohorts have had more time to be banned, rejected, or deleted. The slope is elapsed time, not quality.

**What makes it dangerous (INFERENCE)** — it invites a search for what "changed" between cohorts, and a search of that kind will always find something to credit. The team would then attribute an improvement that never happened to a change that did nothing.

### 4. "Most of our users are passive — we need to push people onto the stage"

**Why it will be made** — Active-vs-Passive reports a low active rate, and the conclusion follows naturally.

**Why it is partly wrong** — **FACT, Finding A** — hosts never emit `mic_activated`, so every room adds one artificial passive listener; in small rooms this materially depresses the rate. **FACT, GAP-15** — in-room group chat is neither persisted nor evented, so participants who are actively typing are counted as passive.

**What makes it dangerous (INFERENCE)** — the direction of the conclusion may well be right, but the magnitude is unknown and both known biases push the same way (understating participation). A team could invest heavily in stage-conversion against a problem smaller than the number implies, while the actual participation channel — text chat — remains unbuilt and unmeasured.

---

# What Would Move Decisions Between Categories

| Change | Moves | From → To |
|---|---|---|
| Exclude hosts from Top Speakers and Active-vs-Passive (analysis only) | Speaker recognition; participation-rate reading | NOT SAFE → USE WITH CAUTION |
| `mic_deactivated` with `isHost` | Judging genuine speaking contribution | NOT SAFE → SAFE |
| `hand_raised` + `stage_promoted` | Diagnosing *where* the core loop breaks | NOT SAFE → SAFE |
| `room_left.secondsInRoom` + `LeftAt` | Time in room; leaving early | NOT SAFE → SAFE |
| `room_went_live` | Scheduled→live conversion; real duration | NOT SAFE → SAFE |
| `entrySource` on `room_joined` | Discovery attribution | NOT SAFE → SAFE |
| Soft delete | True churn; unbiased retention | NOT SAFE → USE WITH CAUTION |
| Room-join-based return metric | Retention judgement | NOT SAFE → USE WITH CAUTION |
| `user_status_changed` | Backlog history; reviewer consistency | NOT SAFE → SAFE |
| FCM send-result persistence | Push delivery health | NOT SAFE → SAFE |
| LiveKit webhook ingestion | Audio quality impact | NOT SAFE → USE WITH CAUTION |
| Staged rollout with a holdout | Causal claims about a feature | NOT SAFE → USE WITH CAUTION |

**INFERENCE** — the first row costs nothing. Excluding hosts is a query-level filter that removes the dashboard's only self-contradiction, and it can be done before any code is written.

---

# Summary

| Category | Count | Character |
|---|:--:|---|
| **SAFE TODAY** | 13 | Relational rows and two verified room events. **Five are not computed anywhere today.** |
| **USE WITH CAUTION** | 11 | Known-direction biases. Usable for reversible decisions when the bias is stated. |
| **NOT SAFE TODAY** | 28 | Mostly *absence*; a small, concentrated amount of *contradiction*. |

**The distinction that matters most (INFERENCE)** — of the 28 unsafe decisions, 24 rest on data that simply does not exist. Those are survivable: the question stays open and the team knows it does not know.

Four rest on data that exists, looks credible, and is wrong — Top Speakers, historical user status, hand-raise counts, and the retention cohort. **Those four are the actual danger**, because each will produce a confident decision in the wrong direction, and each is currently rendered on the dashboard with exactly the same visual authority as the metrics that are sound.

**The cheapest safety improvement available (RECOMMENDATION)** — remove those four metrics, or attach the trust badges from `08a` so a reader can tell them apart. Neither requires touching the application. Both are worth more than any new instrumentation, because a dashboard that admits what it does not know is safer than one that answers every question.
