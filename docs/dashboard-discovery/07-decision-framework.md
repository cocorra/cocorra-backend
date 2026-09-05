# 07 — Cocorra Decision Framework

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Evidence base**: `00-repository-overview.md` … `07-metric-verification.md`, plus targeted code re-verification (see "Corrections to Prior Findings").
> **Scope**: Documentation only. No application code, database, events, or packages were modified.

---

## How to read this document

Every claim is tagged:

- **FACT** — directly verified in `cocorra-backend` source or schema. File/line given where useful.
- **INFERENCE** — a reasonable reading of verified evidence. Could be wrong; stated as a judgement.
- **RECOMMENDATION** — a proposed future change. Nothing here is implemented.

The framework this document enforces:

```
ACTUAL COCORRA FEATURE
        ↓
PRODUCT QUESTION
        ↓
DECISION
        ↓
REQUIRED EVIDENCE
        ↓
METRIC / ANALYSIS
```

A metric that cannot be traced back up that chain to a real decision does not appear in this phase.

---

## Corrections to Prior Findings

Three items in the earlier audit were re-checked against source this session, because they materially change the decision analysis.

### Correction 1 — Speaking time IS finalised on disconnect

`04-data-flow-traceability.md` flagged as **HIGH** risk that speaking time might be lost on unclean disconnect, "depending on `LeaveRoomCleanupAsync` implementation."

**FACT** — It is not lost. `RoomService.LeaveRoomCleanupAsync` (`RoomService.cs:556-576`) explicitly finalises the open mic segment:

```csharp
if (!participant.IsMuted && participant.LastUnmutedAt.HasValue)
{
    participant.TotalSpokenSeconds += (DateTime.UtcNow - participant.LastUnmutedAt.Value).TotalSeconds;
    participant.LastUnmutedAt = null;
}
```

`RoomService.EndRoomAsync` (`RoomService.cs:526-538`) does the same for every `Active` or `PendingApproval` participant at room end.

**Residual risk (INFERENCE)** — finalisation on disconnect depends on `RoomHub`'s **static in-memory** `_connections` dictionary (`RoomHub.cs`, `OnDisconnectedAsync` → `LeaveRoomCleanupAsync` at line 100). If the API process restarts while rooms are live, that dictionary is gone, `OnDisconnectedAsync` cleanup never runs for those connections, and those participants keep `IsMuted=false` with a stale `LastUnmutedAt`. They will only be finalised if the host later ends the room through `EndRoomAsync`. Rooms orphaned by a restart never get finalised at all.

### Correction 2 — `speaking_time_logged` IS emitted

`04-data-flow-traceability.md` stated "No `speaking_time_logged` emit found." That is wrong.

**FACT** — It is emitted at `RoomService.cs:549`, inside `EndRoomAsync`, once per participant with `TotalSpokenSeconds > 0`, carrying the **cumulative** total:

```csharp
_eventTracker.Track(EventTypes.SpeakingTimeLogged, p.UserId, new { roomId, spokenSeconds = p.TotalSpokenSeconds });
```

**INFERENCE** — Because it fires only inside `EndRoomAsync`, and only for participants still `Active`/`PendingApproval` at that moment, this event is **not** a complete log of speaking. Users who left before the room ended have their time in `RoomParticipant.TotalSpokenSeconds` but **no** `speaking_time_logged` event. A room that is never formally ended produces no `speaking_time_logged` events at all. The event is a partial, room-end-conditional snapshot, not a ledger.

### Correction 3 — `FriendRequest.UpdatedAt` is not usable

`01-product-feature-inventory.md` stated friend-request response latency is blocked, but `02-data-model.md` said "`UpdatedAt` available via BaseEntity."

**FACT** — `UpdatedAt` is assigned in exactly **three** places in the entire solution: `AuthServices.cs:538`, `SupportService.cs:140`, and `SupportService.cs:275`. There is no `SaveChanges` override in `AppDbContext` that populates it. Therefore `FriendRequest.UpdatedAt`, `Room.UpdatedAt`, `Message.UpdatedAt`, and `Notification.UpdatedAt` are **never written** and are NULL in practice.

Two consequences:

- Friend-request response latency is **NOT AVAILABLE**, not "available via BaseEntity."
- **FACT** — `RoomRepository.cs:129` sorts the ended-rooms history page with `.OrderByDescending(r => r.UpdatedAt)`. Since that column is NULL for effectively every room, the admin "room history" ordering is arbitrary and unstable. Any decision read off the *order* of that list is reading noise.

---

## New Findings That Change the Decision Picture

These were verified this session and are not in the earlier documents. They are the most important additions, because they invalidate the two headline room-engagement metrics.

### Finding A — The host's microphone is open from the moment the room exists

**FACT** — When a room is created Live (`RoomService.cs:115-127`) or a scheduled room is started (`RoomService.cs:439-449`), the host is inserted as a `RoomParticipant` with:

```csharp
IsOnStage     = true,
IsMuted       = false,
JoinedAt      = DateTime.UtcNow,
LastUnmutedAt = DateTime.UtcNow
```

The host's mic is therefore registered as **open** from room start. `TotalSpokenSeconds` accrues from that instant until the host mutes or the room ends.

**Consequence 1 (FACT, by construction)** — A host who never touches their mic accumulates `TotalSpokenSeconds` equal to the **entire wall-clock life of the room**. Since `RoomService.cs:73-77` restricts `DurationHours` to exactly 2 or 3, a single passive host can book 7,200–10,800 seconds of "speaking."

**Consequence 2 (FACT)** — That initial open-mic state emits **no** `mic_activated` event. `RoomHub.ToggleMic` (`RoomHub.cs:518-521`) only emits on a transition from `IsMuted = true` to `false`. The host was created at `IsMuted = false`, so the transition never happens unless they mute first.

**Consequence 3 (INFERENCE, and it is a strong one)** — The two headline room metrics now contradict each other by construction:

| Metric | How the host appears | Source |
|---|---|---|
| Participation Stats → **Top Speakers** | Host dominates the leaderboard with hours of time | `RoomParticipant.TotalSpokenSeconds` |
| Participation → **Active vs Passive** | Host is counted as a **passive listener** | absence of `mic_activated` event |

The same person, in the same room, is simultaneously the platform's top speaker and a silent listener. Any decision that reads "who are our most engaged users" off Top Speakers is reading a list of coaches ranked by how long their rooms ran.

### Finding B — "Spoken time" means "unmuted time," not "speaking time"

**FACT** — `TotalSpokenSeconds` is derived purely from mute/unmute state transitions in `RoomHub.ToggleMic`. It has no connection to whether audio was actually produced.

**FACT** — Cocorra ingests **nothing** from LiveKit. `ILiveKitService` exposes only `GenerateToken` and `UpdateStagePermissionAsync` (`LiveKitService.cs:36, 116`). A repository-wide search for "webhook" returns zero matches in code or configuration. There is no LiveKit webhook endpoint, no participant-event ingestion, no track-published/unpublished handling, and no connection-quality capture.

**INFERENCE** — Cocorra is blind to its own media layer. It knows who was *permitted and unmuted*; it does not know who was *audible*, whether audio actually flowed, whether anyone dropped for network reasons, or whether the room sounded broken. For a voice-first product, this is the largest single blind spot in the system.

### Finding C — "Go live" is a completely untracked moment

**FACT** — `StartScheduledRoomAsync` (`RoomService.cs:422-460`) sets `Status = Live`, adds the host participant, and fires reminder notifications. It emits **no** `UserEvent` and it does **not** update `Room.StartDate`.

**INFERENCE** — For scheduled rooms, the actual go-live timestamp exists nowhere. `Room.StartDate` remains the scheduled time; `Room.UpdatedAt` is never written. The only recoverable proxy is `MIN(RoomParticipant.JoinedAt)` for that room, which is the host's insertion time — usable, but undocumented and fragile.

**FACT** — `room_ended` reports `durationHours = (DateTime.UtcNow - room.StartDate).TotalHours` (`RoomService.cs:543`). For rooms **created live** this is approximately correct, because `StartDate` was set to `UtcNow` at creation. For rooms **scheduled and started late**, it overstates duration by the lateness. The metric is conditionally correct, and the condition is not recorded anywhere.

### Finding D — Rejecting a friend request destroys its own history

**FACT** — `FriendService.SendFriendRequestAsync` (`FriendService.cs:97-105`) does not create a new row when a previously rejected relationship is re-attempted. It **mutates the existing row**, overwriting `SenderId`, `ReceiverId`, `Status`, and `CreatedAt`.

**INFERENCE** — The rejection is erased from the relational data. Only the `friend_request_sent` event retains a trace, and only for 180 days. Rejection rate computed from the `FriendRequest` table systematically undercounts.

### Finding E — Topic Requests & Voting is schema-only

**FACT** — `RoomTopicRequest` and `TopicVote` exist as entities and have full fluent configuration in `AppDbContext.cs:16-17, 58-97`. A repository-wide search finds **no** controller, **no** service, **no** repository, **no** route in `Router.cs`, and **no** event type referencing them.

**INFERENCE** — The tables are empty and will remain empty. This is not an under-measured feature; it is an unbuilt one. It must be excluded from any "which features deserve investment" analysis, because there is no feature to evaluate — only a decision about whether to build it.

---

## Feature-by-Feature Decision Analysis

Each real Cocorra feature is taken in turn, ordered by its position in the user's actual journey.

---

### F1 — Registration & Voice Verification Onboarding

**Feature (FACT)** — Multi-step gated onboarding: register with name/email/password/age/voice recording → email OTP confirmation → MBTI submission → **manual admin review of the voice recording** → status becomes `Active`, `Rejected`, or `ReRecord`. Until `Active`, the default authorization policy blocks the user from every `[Authorize]` endpoint. Events: `user_registered`, `voice_verification_submitted`, `email_confirmed`, `mbti_submitted`, `voice_verification_result`, `activation_completed`.

**Product Question** — Where in this five-step gate do prospective users disappear, and is the gate itself the reason?

**Why this matters more for Cocorra than for a typical product (INFERENCE)** — Most products lose users to indifference. Cocorra additionally loses them to a **human queue**. A user who registers at 2am and is approved at 6pm the next day has been locked out for 16 hours with no product access at all. That is a structural conversion risk that ordinary funnel-optimisation instincts would not look for.

**Possible Decisions**

| Decision | What would justify it |
|---|---|
| Keep manual voice review unchanged | Approval is fast and drop-off between submission and activation is low |
| Add staged access (let `Pending` users browse the feed read-only) | Large drop-off occurs *while waiting*, not at any submission step |
| Invest in admin review tooling / SLA | Approval latency is long or highly variable |
| Reorder the funnel (MBTI after activation) | MBTI submission is a measurable abandonment point |
| Investigate email deliverability | `user_registered` → `email_confirmed` drop is large |
| Reduce priority on onboarding work | Every step converts well and losses are elsewhere |

**Required Evidence**
1. Sequential per-user progression through the six events, in order, with per-step elapsed time.
2. Time from `voice_verification_submitted` to `voice_verification_result` (admin review latency), with distribution — not just the mean.
3. Outcome split of `voice_verification_result` (`Active` / `Rejected` / `ReRecord`).
4. Post-`ReRecord` recovery rate.
5. Whether users who waited longer for approval are less likely to ever join a room.

**What Cocorra actually has**
- **AVAILABLE (FACT)** — All six events exist and are server-emitted, so raw per-user progression is reconstructable directly from `UserEvents` with `OccurredAtUtc`. Review latency **is** derivable as the gap between the `voice_verification_submitted` and `voice_verification_result` events for the same `UserId`. The earlier audit called review latency unmeasurable; that is true of the *relational* data (no `ApplicationUser.UpdatedAt`) but **not** of the event stream, within its 180-day window.
- **PARTIALLY AVAILABLE (FACT)** — The shipped `/Analytics/Funnel` endpoint counts each step independently rather than sequentially (`AnalyticsRepository.cs:300-322`), so it can report a later step with *more* users than an earlier one. The data supports a true funnel; the endpoint does not compute one.
- **NOT AVAILABLE (FACT)** — Anything older than 180 days (`EventCleanupService`). No abandonment reason. No client-side signal for users who opened the registration screen and never submitted.

**Metric / Analysis**
- Sequential onboarding funnel with per-step median elapsed time.
- Admin review latency distribution (median, p90) by day of week and hour.
- `ReRecord` recovery rate.
- Cross-analysis: activation-wait bucket → subsequent first-room-join rate.

---

### F2 — Admin Verification Review Queue

**Feature (FACT)** — Admins change user status individually (`ChangeStatus`) or in bulk (`BulkChangeStatus`) via `AdminController`. Status change triggers side effects: lockout, voice-file deletion, token invalidation, FCM token clearing, and force-disconnect from live rooms on ban.

**Product Question** — Is the human review queue a throughput bottleneck, and is review quality consistent?

**Possible Decisions** — Staff the queue differently; batch review at predictable times; build reviewer tooling; introduce partial automation; accept current performance.

**Required Evidence** — Queue depth over time (`Pending` count as a time series); review latency distribution; per-admin throughput and rejection rate; whether bulk operations are used as a catch-up mechanism after backlog builds.

**Current Data Availability**
- **PARTIALLY AVAILABLE (FACT)** — Latency is derivable from the event pair. Current queue depth is available from `AdminService.GetDashboardStatsAsync` (`AdminService.cs:383-401`).
- **NOT AVAILABLE (FACT)** — Queue depth **over time**. The stats endpoint is a pure `GroupBy(Status)` snapshot with no date filter; yesterday's pending count is unrecoverable.
- **NOT AVAILABLE (FACT)** — Which admin performed a review. `voice_verification_result` is tracked against the *reviewed user's* `UserId` with properties `{status}` only (`AdminService.cs:137`). The acting admin's identity is not recorded anywhere. Per-reviewer consistency analysis is impossible.
- **NOT AVAILABLE (FACT)** — Whether a status change was part of a bulk action.

---

### F3 — Voice Room Creation (Supply Side)

**Feature (FACT)** — A host creates a room with title, description, category (`Relationships` / `MentalHealth` / `Others` — only three values), total capacity, stage capacity, per-speaker default duration in minutes, selection mode (`Automatic_FirstComeFirstServed` or `Manual_CoachDecision`), privacy flag, optional image, and duration of **exactly 2 or 3 hours** (`AllowedDurations`, `RoomService.cs:73`). Rooms created with a future `ScheduledStartDate` are `Scheduled`; otherwise they go `Live` immediately.

**Product Question** — Is room supply sufficient and healthy, and do the host-configurable settings actually change outcomes?

**Why this matters (INFERENCE)** — Cocorra is a two-sided marketplace with a very small supply side. If coaches stop scheduling rooms, demand-side metrics collapse regardless of how good the app is. Supply health is a leading indicator; participation is a lagging one.

**Possible Decisions**
- Recruit or activate more coaches vs. help existing coaches run better rooms.
- Change or expand the three-value category taxonomy.
- Default new rooms to one selection mode over the other.
- Reconsider the 2-or-3-hour constraint.
- Change stage capacity or per-speaker duration defaults.

**Required Evidence**
1. Rooms created per week and **distinct active hosts** per week (supply concentration).
2. Scheduled → actually-went-live conversion.
3. Outcome by `SelectionMode` — do manual-approval rooms produce more or fewer distinct speakers than first-come-first-served?
4. Outcome by `Category`.
5. Whether the configured `DefaultSpeakerDurationMinutes` binds — i.e. how often speakers hit their limit and need `GrantExtraTime`.

**Current Data Availability**
- **AVAILABLE (FACT)** — Rooms created, host distribution, category, privacy, selection mode: all columns on `Room`, plus the `room_created` event carries `{roomId, category, isPrivate}` with a promoted, indexed `RoomId`.
- **NOT AVAILABLE (FACT)** — Scheduled → live conversion. `StartScheduledRoomAsync` emits no event and writes no timestamp (Finding C). A scheduled room that was never started is indistinguishable from one still awaiting its slot, except by comparing `StartDate` to now.
- **NOT AVAILABLE (FACT)** — `GrantExtraTime` is a `RoomHub` method but emits no event. `RoomParticipant.ExtraMinutesGranted` holds only the final cumulative value, with no timestamp and no record of who granted it. Whether the speaker-duration setting is a real constraint cannot be answered.
- **PARTIALLY AVAILABLE** — Selection-mode outcome comparison is possible using `mic_activated` events joined to `Room.SelectionMode`, but see F5 for the reliability caveats on speaker counting.

---

### F4 — Room Discovery, Feed & Reminders

**Feature (FACT)** — `GET /Api/V1/Room/Feed` returns rooms. `POST /Api/V1/Room/{id}/ToggleReminder` creates or removes a `RoomReminder` row (composite PK `UserId, RoomId`, with `CreatedAt`). When a scheduled room starts, `StartScheduledRoomAsync` creates a `RoomReminder`-driven `Notification` for every user who set one, plus an FCM push.

**Product Question** — How do users find rooms, and does the reminder mechanism actually convert into attendance?

**Why this matters (INFERENCE)** — Reminders are Cocorra's only built-in re-engagement loop for scheduled content. If reminders convert well, they are a cheap growth lever. If they do not, the scheduled-room model itself is questionable.

**Possible Decisions** — Invest in the reminder loop (better copy, better timing); improve feed ranking; add search or filtering; deprioritise scheduling in favour of spontaneous live rooms.

**Required Evidence**
1. Feed impressions per room and impression → join rate.
2. Reminder set rate on scheduled rooms.
3. **Reminder → attendance conversion**: of users who set a reminder, what fraction joined once the room went live?
4. Entry path attribution for each join: feed, reminder push, deep link, or in-app navigation.

**Current Data Availability**
- **NOT AVAILABLE (FACT)** — Feed impressions. `GET /Room/Feed` emits no event. There is no record that a room was ever shown to anyone. Impression-to-join conversion, the core discovery metric, cannot be computed at all.
- **NOT AVAILABLE (FACT)** — Reminder set/unset events. `ToggleReminder` emits nothing; `RoomReminder` rows are *deleted* on un-toggle, so the current table is a snapshot of intent, not a log of it.
- **PARTIALLY AVAILABLE (INFERENCE)** — Reminder → attendance is *reconstructable today* by joining surviving `RoomReminder` rows against `room_joined` events for the same `(UserId, RoomId)`. Because un-toggled reminders are hard-deleted, this over-states conversion: it can only see reminders that were still set at query time. Direction of bias is known (optimistic), magnitude is not.
- **NOT AVAILABLE (FACT)** — Entry path. `room_joined` carries only `{roomId}` (`RoomHub.cs:270`). There is no source or referrer property.

---

### F5 — Room Attendance & Stage Participation (The Core Loop)

**Feature (FACT)** — Users join via REST (`RoomService.JoinRoomAsync`, creating a `RoomParticipant`), then connect over SignalR (`RoomHub.JoinRoom`) which issues a LiveKit token. In-room they can `RaiseHand` / `LowerHand`; the host can `ApproveToStage`, `MoveToAudience`, `GrantExtraTime`, `KickUser`, and `EndRoom`. On stage, `ToggleMic` governs a per-speaker time budget of `DefaultSpeakerDurationMinutes + ExtraMinutesGranted`; exceeding it throws `"Your time is up!"` for everyone except the host.

**Product Question** — Do audience members convert into speakers, and what stops them when they do not?

**Why this is *the* question for Cocorra (INFERENCE)** — The entire product design — stage capacity, hand raising, host approval, per-speaker time budgets, extra-time grants — exists to manage the transition from listener to speaker. That transition is Cocorra's core value event. Every one of the design's control points is a place where the transition can fail.

**The funnel that actually matters:**

```
room_joined  →  hand raised  →  approved to stage  →  mic activated  →  spoke meaningfully  →  stayed
```

**Possible Decisions**
- Redesign the hand-raise → stage flow if approval is the bottleneck.
- Default rooms to `Automatic_FirstComeFirstServed` if manual approval throttles participation.
- Increase `StageCapacity` defaults if stage slots are the constraint.
- Raise `DefaultSpeakerDurationMinutes` if speakers routinely hit the wall.
- Investigate technical failure if joins do not become audio sessions.
- Leave it alone if conversion is already healthy.

**Current Data Availability — step by step**

| Funnel step | Status | Evidence |
|---|:---:|---|
| `room_joined` | **AVAILABLE** | Event with indexed `RoomId`; verified reliable. Fires per SignalR connect, so *count distinct users*, never raw events. |
| Hand raised | **NOT AVAILABLE** | **FACT** — `RoomHub.RaiseHand` (`RoomHub.cs:381-400`) writes `IsHandRaised = true` and broadcasts, but emits **no** event. `LowerHand` resets it. `RoomParticipant.IsHandRaised` is therefore a live boolean, not a history. The shipped `UsersWhoRaisedHand` metric counts hands *raised at the instant of the query* — for any historical window it is effectively always near zero. |
| Approved to stage | **NOT AVAILABLE** | **FACT** — `ApproveToStage` and `MoveToAudience` emit no events. `IsOnStage` is a live boolean. Stage promotions are invisible. |
| Mic activated | **AVAILABLE, with a host exclusion** | Event fires on true `muted → unmuted` transitions. Reliable for **non-host** participants. Excludes the host's initial open mic entirely (Finding A). |
| Spoke meaningfully | **NOT AVAILABLE** | **FACT** — `TotalSpokenSeconds` is unmuted-time, not audio. Host values are inflated by the full room duration (Finding A). No LiveKit telemetry exists (Finding B). |
| Stayed | **NOT AVAILABLE** | **FACT** — No `LeftAt` on `RoomParticipant`, and `RoomHub.JoinRoom` overwrites `JoinedAt` when re-activating a `Left` participant (`RoomHub.cs:245-253`). Time-in-room is unrecoverable. `room_left` exists but carries only `{roomId}`, no duration. |

**Metric / Analysis (with today's data)** — Only the two ends of the funnel are trustworthy: **distinct joiners** and **distinct non-host mic activators**. The conversion rate between them is measurable and meaningful. Everything between them — the part that would tell you *why* — is dark.

**INFERENCE** — This is the single most valuable gap in the product. Cocorra can currently observe that listener→speaker conversion is some number, and can observe when that number moves, but has no instrumented path to diagnose *which control point* caused the movement.

---

### F6 — Direct Messaging

**Feature (FACT)** — Friends-only 1:1 persistent messaging over `ChatHub`. Messages persist to `Message` (`SenderId`, `ReceiverId`, `Content`, `IsRead`, `CreatedAt`). `RoomHub.SendRoomPrivateMessage` routes in-room DMs through the same `ChatService.SaveMessageAsync`. Event: `message_sent` with `{receiverId}`.

**Product Question** — Is messaging a genuine retention surface, or an under-used appendage to the room experience?

**Possible Decisions** — Invest in messaging; leave it as a utility; deprioritise it; investigate whether room-originated DMs are the real use case.

**Required Evidence** — Messages per active user; conversation reciprocity (does the recipient reply?); whether DMs originate in-room or from the friends list; read latency.

**Current Data Availability**
- **AVAILABLE (FACT)** — Message volume, sender/receiver pairs, reciprocity (both directions are rows in the same table), and timing from `CreatedAt`.
- **NOT AVAILABLE (FACT)** — Read latency. `IsRead` is a bare boolean and `Message.UpdatedAt` is never written (Correction 3).
- **NOT AVAILABLE (FACT)** — Origin surface. `SendRoomPrivateMessage` and `ChatHub.SendMessage` both funnel into `ChatService.SaveMessageAsync`, which emits an identical `message_sent` with only `{receiverId}` (`ChatService.cs:92`). In-room DMs and friends-list DMs are indistinguishable in the data — which is precisely the distinction a decision about messaging would need.

---

### F7 — In-Room Group Chat

**Feature (FACT)** — `RoomHub.SendRoomGroupMessage` (`RoomHub.cs:654-694`) broadcasts text to the SignalR room group. It writes nothing to the database and emits no event.

**Product Question** — Is text chat a meaningful part of the room experience, especially for the majority of users who never take the stage?

**Why this matters (INFERENCE)** — Cocorra's own Active-vs-Passive metric establishes that most participants never speak. Group chat is plausibly how those users actually participate. If so, it is a major engagement surface that the analytics stack cannot see at all — and "passive listener" would be a mislabel for a large, actively-chatting cohort.

**Possible Decisions** — Invest in group chat as a first-class participation channel; leave it ephemeral by design; investigate whether it substitutes for or leads to stage participation.

**Required Evidence** — Message volume per room, share of participants who post, and whether chatting precedes hand-raising.

**Current Data Availability** — **NOT AVAILABLE (FACT)**. Zero persistence, zero events. This is a total blind spot on what may be the majority participation behaviour.

---

### F8 — Friends System

**Feature (FACT)** — Search by target user ID, send request, accept/reject, remove. `FriendRequest` with unique index on `(SenderId, ReceiverId)`. Events: `friend_request_sent` `{targetUserId}`, `friend_request_accepted` `{senderId}`.

**Product Question** — Does forming friendships change subsequent behaviour, and where do users find people to friend?

**Possible Decisions** — Invest in social graph features (suggestions, mutual friends); leave friends as a messaging prerequisite only; investigate the discovery path.

**Required Evidence** — Request volume, acceptance rate, response latency, discovery source, and behaviour change after first friendship.

**Current Data Availability**
- **AVAILABLE** — Send volume and accept volume, from both events and the table.
- **PARTIALLY AVAILABLE (FACT)** — Acceptance *rate* from the table is biased: rejected-then-re-sent rows are mutated in place, erasing the rejection (Finding D). The event-based rate (`friend_request_accepted` ÷ `friend_request_sent`) is sounder but has no explicit rejection event to validate against.
- **NOT AVAILABLE (FACT)** — Response latency (`UpdatedAt` never written, and `CreatedAt` is reset on re-send).
- **NOT AVAILABLE (FACT)** — Discovery source. `GET /api/Friends/search/{targetId}` requires the requester to already possess the target's exact user ID. **INFERENCE** — this means friending is almost certainly initiated from a room participant list or a profile view, neither of which emits an event. How the social graph actually forms is unobservable.

---

### F9 — Notifications & Push

**Feature (FACT)** — In-app `Notification` rows plus FCM push via `PushNotificationService`. Types: `System`, `RoomReminder`, `FriendRequest`, `FriendAccept`, `AdminWarning`. Read state via `IsRead`. Client-side `notification_opened` event is allowlisted in `EventsController`.

**Product Question** — Do notifications reach users, and do they drive the action they were sent to drive?

**Possible Decisions** — Invest in notification strategy; reduce volume if it correlates with disengagement; fix delivery infrastructure; investigate token health.

**Required Evidence** — Sent → delivered → opened → acted-upon, per notification type, tied to the specific notification instance.

**Current Data Availability**
- **AVAILABLE** — Sent volume by type, and read rate from `IsRead`.
- **NOT AVAILABLE (FACT)** — Delivery. The FCM response from `SendPushNotificationAsync` is not persisted. Whether a push arrived is unknown.
- **NOT AVAILABLE (FACT)** — Token health as a metric. **Context**: commit `dc1c933` addressed reversed FCM delivery by clearing stale tokens on logout and ban and enforcing device exclusivity. **INFERENCE** — a bug that severe should be observable in the dashboard afterwards, and currently is not: there is no metric for tokenless active users, token churn, or send failures. A regression would be invisible until users complained again.
- **PARTIALLY AVAILABLE / UNRELIABLE (FACT)** — `notification_opened` is client-emitted through the allowlisted `POST api/events/track` path. Its properties are entirely client-defined, so there is no guarantee it carries the `Notification.Id` needed to attribute an open to a send. Un-linkable opens cannot produce a conversion rate.

---

### F10 — Reporting, Moderation & Blocking

**Feature (FACT)** — Users submit reports with category, description, optional screenshot. Admins list, update status, and act: `WarnUser`, `Mute24h`, `BanUser`, `RejectReport`. Separately, users block each other (`UserBlock`), optionally tied to a `BlockedDevices` row enforced by `DeviceBlockingMiddleware`. Events: `user_reported` (rich: `{reportedUserId, reportedRoomId, category, description}`), `user_blocked` `{blockedUserId}`.

**Product Question** — Is the platform safe enough that safety problems are not driving users away, and is moderation keeping up?

**Why this matters for Cocorra specifically (INFERENCE)** — Two of three room categories are `Relationships` and `MentalHealth`. Rooms in those categories carry elevated duty-of-care. A safety failure in a mental-health room is not an ordinary trust-and-safety incident. Safety measurement here is disproportionately important relative to a general-purpose social app.

**Possible Decisions** — Invest in proactive moderation; add category-specific room rules; tune report categories; adjust the action ladder; leave it.

**Required Evidence** — Report volume and rate per 1,000 room joins; **report rate by room category**; time to resolution; action distribution; repeat-offender concentration; whether reported users churn or are churned.

**Current Data Availability**
- **AVAILABLE (FACT)** — Volume, category, status, most-reported users. `07-metric-verification.md` marks Report Insights as VERIFIED, and it is the highest-quality metric in the shipped dashboard.
- **AVAILABLE (INFERENCE, uncomputed)** — Report rate **by room category** is derivable today: `user_reported` carries `reportedRoomId`, which joins to `Room.Category`. Given the `MentalHealth` category, this is arguably the highest-value safety metric available and it is not currently computed anywhere.
- **PARTIALLY AVAILABLE (FACT)** — Resolution time. `SupportService.cs:140, 275` *does* set `Report.UpdatedAt` — one of only three sites in the solution that writes the column. But `UpdatedAt` is overwritten by *any* subsequent update, so it means "last touched," not "resolved at."
- **NOT AVAILABLE (FACT)** — Which admin action was taken, as a queryable event. Actions mutate user state; `AdminReportAction` outcomes are not recorded in `UserEvents`.
- **NOT AVAILABLE (FACT)** — Unblock. `BlockService` emits `user_blocked` but there is no `user_unblocked` event, and unblocking deletes the row. Block prevalence is only ever a current snapshot.
- **NOT AVAILABLE (FACT)** — `Report.Status` is a free-form string, and `AnalyticsRepository` recognises only "Open", "Resolved", "InProgress" by case-insensitive comparison. Any other value is silently dropped from every status count.

---

### F11 — Support (Tickets & Live Chat)

**Feature (FACT)** — `SupportTicket` (anonymous submission permitted) and real-time `SupportChat` with a `Pending → Active → Closed` lifecycle: admin claims, replies, closes. `SupportChat` has both `CreatedAt` and `ClosedAt`. Note `SupportChat.UserId`/`AdminId` are `string`, not `Guid`, unlike every other user reference in the schema.

**Product Question** — What are users struggling with, and is support volume a symptom of a fixable product defect?

**Why this matters (INFERENCE)** — Support tickets are Cocorra's only structured channel for *qualitative* failure signal. Given the complete absence of client-side error tracking (blind spot 9 in `06-blind-spots.md`), the support queue is currently the product's de-facto error monitor. Ticket volume by type is therefore a proxy for reliability that nothing else in the stack provides.

**Possible Decisions** — Fix whatever generates ticket volume; invest in self-service; staff support differently; treat `TechnicalProblem` volume as a reliability alarm.

**Current Data Availability**
- **AVAILABLE (FACT)** — Ticket volume by `SupportTicketType`, chat volume, and chat resolution time (`ClosedAt − CreatedAt`).
- **NOT AVAILABLE (FACT)** — There is **no analytics endpoint for support at all**. All eleven `AnalyticsController` routes cover users, rooms, participation, reports, funnel, retention, active rooms, peak hours, voice drop-off, and active-vs-passive. Support is absent. The data exists and is unexposed.
- **NOT AVAILABLE (FACT)** — First-response time. `SupportMessage` has `CreatedAt` and `IsFromAdmin`, so time-to-first-admin-reply is computable, but nothing computes it.
- **NOT AVAILABLE (FACT)** — Ticket resolution. `SupportTicket.Status` is a string with no `ResolvedAt`.

---

### F12 — User Profiles

**Feature (FACT)** — View own profile, view another user's, update fields, upload a picture, select an avatar preset. Fields include `FirstName`, `LastName`, `Age`, `MBTI`, `Bio`, `ProfilePicturePath`.

**Product Question** — Does profile completeness affect social outcomes, and are profiles viewed enough to matter?

**Possible Decisions** — Prompt for completion; invest in richer profiles; deprioritise; investigate whether MBTI display drives connection.

**Current Data Availability**
- **PARTIALLY AVAILABLE (FACT)** — Current field completeness is queryable as a snapshot (`Bio != null`, etc.).
- **NOT AVAILABLE (FACT)** — Zero profile events exist: no `profile_viewed`, `profile_updated`, or `avatar_changed`. `ApplicationUser` has no `UpdatedAt`. When a profile was completed, or whether it ever changed, is unknowable.
- **NOT AVAILABLE (INFERENCE)** — The interesting question — does a complete profile lead to more friend requests received or more stage approvals — requires a completion *timestamp* to establish before/after. Snapshot completeness against lifetime outcomes would confound tenure with completeness and is not a valid analysis.

---

### F13 — MBTI as a Product Dimension

**Feature (FACT)** — MBTI type is collected as a mandatory onboarding step (`SubmitMbti`), stored on `ApplicationUser.MBTI`, emitted as `mbti_submitted` `{mbti}`, and surfaced as a distribution in the User Growth analytics response.

**Product Question** — Is MBTI a decoration, or does it predict behaviour strongly enough to drive matching, room recommendations, or stage selection?

**Why this deserves its own entry (INFERENCE)** — MBTI is one of Cocorra's two stated differentiators. It is collected from every single user at a real cost in onboarding friction. Today it is used for exactly one thing: a pie chart. Either it earns that friction by predicting something, or the step should be reconsidered. That is a genuine, currently-unanswered product decision.

**Possible Decisions** — Build MBTI-based room or people recommendations; use MBTI in stage-selection guidance for coaches; move MBTI collection after activation to reduce onboarding friction; drop it.

**Required Evidence** — Participation, speaking-conversion, and return rates segmented by MBTI type, with enough users per cell for the comparison to mean anything.

**Current Data Availability**
- **AVAILABLE** — MBTI is on every user and joinable to every behavioural table.
- **PARTIALLY AVAILABLE (INFERENCE)** — The join is trivial and nothing prevents the analysis today. What is missing is statistical: sixteen types split across a small user base produces cells too small for any difference to be distinguishable from noise. **RECOMMENDATION** — if this analysis is run, group MBTI into the four dichotomies (E/I, S/N, T/F, J/P) rather than sixteen types. The E/I axis in particular has an obvious prior relationship to speaking-up behaviour and would be the honest first test.

---

### F14 — Topic Requests & Voting (NOT IMPLEMENTED)

**Feature status (FACT)** — Entities and `AppDbContext` configuration exist. No controller, service, repository, route, or event. See Finding E.

**Product Question** — Should this be built at all?

**Possible Decisions** — Build it; remove the dead schema.

**Required Evidence** — Evidence that room *topic supply* is a constraint: are the same topics repeating, are users requesting topics through support tickets, do rooms in under-served categories fill faster?

**Current Data Availability** — **NOT AVAILABLE**. No data can exist for an unbuilt feature.

**RECOMMENDATION** — Do not treat this as an analytics gap. It is a product backlog item. The cheapest available evidence is `SupportTicket` free-text: if users are asking for topics there, that is a real demand signal obtainable without building anything.

---

## Required Decision Matrix

Confidence values mean:

- **HIGH** — the decision can be made today on data the audit verified as correct.
- **MEDIUM** — data exists and is directionally usable, but has a known bias whose direction is understood.
- **LOW** — data exists but is distorted in ways that could reverse the conclusion.
- **NOT POSSIBLE TODAY** — the required evidence does not exist in any form.

| # | Feature | Product Question | Possible Decision | Evidence Required | Current Data Availability | Decision Confidence |
|:--:|---|---|---|---|:--:|:--:|
| 1 | Voice verification onboarding | Where do prospects drop out of the 5-step gate? | Restructure onboarding vs. leave it | Sequential per-user event progression with elapsed time | **PARTIALLY AVAILABLE** — events exist and are sequential-capable; shipped `/Analytics/Funnel` counts steps independently | **MEDIUM** |
| 2 | Admin review queue | Is manual review a throughput bottleneck? | Staff / tool / automate review | Latency distribution from `voice_verification_submitted` → `voice_verification_result` | **AVAILABLE** (within 180 days) — not currently computed by any endpoint | **MEDIUM** |
| 3 | Admin review queue | Is review quality consistent across admins? | Reviewer training / calibration | Acting admin identity per decision | **NOT AVAILABLE** — admin ID not in event properties | **NOT POSSIBLE TODAY** |
| 4 | Admin review queue | Is the pending backlog growing? | Add review capacity | `Pending` count as a time series | **NOT AVAILABLE** — stats endpoint is snapshot-only, no history | **NOT POSSIBLE TODAY** |
| 5 | Registration gate | Does approval wait time cost us activated users? | Add staged/read-only pre-approval access | Wait-time bucket → subsequent first-join rate | **PARTIALLY AVAILABLE** — both sides derivable from events; correlational only | **MEDIUM** |
| 6 | Room creation (supply) | Is room supply healthy and concentrated? | Recruit coaches vs. enable existing ones | Rooms/week, distinct hosts/week, host concentration | **AVAILABLE** — `Room.CreatedAt` + `HostId` | **HIGH** |
| 7 | Room scheduling | Do scheduled rooms actually go live? | Drop or fix scheduling | Go-live event or timestamp | **NOT AVAILABLE** — `StartScheduledRoomAsync` emits nothing, writes no timestamp (Finding C) | **NOT POSSIBLE TODAY** |
| 8 | Room categories | Which of the 3 categories works? | Rebalance or expand taxonomy | Joins and speaker conversion per category | **AVAILABLE** — `Room.Category` joins to `room_joined` via indexed `RoomId` | **HIGH** |
| 9 | Selection mode | Does manual approval suppress speaking? | Change the default mode | Distinct non-host speakers per room, split by `SelectionMode` | **PARTIALLY AVAILABLE** — computable from `mic_activated`; host exclusion required (Finding A) | **MEDIUM** |
| 10 | Stage capacity | Is the stage the binding constraint? | Raise default `StageCapacity` | Hand-raise volume vs. stage slots over time | **NOT AVAILABLE** — `RaiseHand` emits no event; `IsHandRaised` is a live boolean | **NOT POSSIBLE TODAY** |
| 11 | Speaker time budget | Do speakers hit the time wall? | Adjust `DefaultSpeakerDurationMinutes` | Time-up rejections and `GrantExtraTime` frequency | **NOT AVAILABLE** — neither emits an event; `ExtraMinutesGranted` is a final total only | **NOT POSSIBLE TODAY** |
| 12 | Room discovery | Does the feed convert to joins? | Invest in feed ranking / search | Feed impressions per room | **NOT AVAILABLE** — `/Room/Feed` emits nothing | **NOT POSSIBLE TODAY** |
| 13 | Reminders | Do reminders drive attendance? | Invest in or drop the reminder loop | Reminder-set → join conversion | **PARTIALLY AVAILABLE** — joinable today, but un-toggles are hard-deleted so it reads optimistically | **LOW** |
| 14 | Core room loop | Do listeners become speakers? | Redesign the stage flow | Distinct joiners → distinct non-host mic activators | **AVAILABLE** — both endpoints of the funnel are verified-reliable | **HIGH** |
| 15 | Core room loop | *Where* in the stage flow do they fail? | Target the specific broken step | Hand-raise and stage-promotion events | **NOT AVAILABLE** — the entire middle of the funnel is uninstrumented | **NOT POSSIBLE TODAY** |
| 16 | Speaking depth | Who genuinely contributes most? | Recognise / promote top contributors | Real speaking duration | **NOT AVAILABLE** — host inflated to full room duration; unmuted-time ≠ audio (Findings A, B) | **NOT POSSIBLE TODAY** |
| 17 | Room quality | Do users stay or leave early? | Change room length or format | Time-in-room per participant | **NOT AVAILABLE** — no `LeftAt`; `JoinedAt` overwritten on rejoin | **NOT POSSIBLE TODAY** |
| 18 | Media reliability | Does audio actually work? | Invest in media infrastructure | LiveKit participant/track/quality telemetry | **NOT AVAILABLE** — no webhook ingestion whatsoever (Finding B) | **NOT POSSIBLE TODAY** |
| 19 | Direct messaging | Is DM a real retention surface? | Invest in vs. deprioritise messaging | Volume, reciprocity, per-user rate | **AVAILABLE** — `Message` table is complete and indexed | **MEDIUM** |
| 20 | Direct messaging | Do DMs originate in rooms? | Strengthen the room→DM bridge | Origin surface on `message_sent` | **NOT AVAILABLE** — both paths emit identical events | **NOT POSSIBLE TODAY** |
| 21 | In-room group chat | Is chat how silent users participate? | Make chat first-class vs. leave ephemeral | Group message volume and poster share | **NOT AVAILABLE** — zero persistence, zero events (F7) | **NOT POSSIBLE TODAY** |
| 22 | Friends | Does friending change behaviour? | Invest in the social graph | Before/after behaviour around first accepted friendship | **PARTIALLY AVAILABLE** — timestamps exist; strictly correlational | **LOW** |
| 23 | Friends | How does the graph form? | Add people discovery | Friend-request origin surface | **NOT AVAILABLE** — search requires a known exact user ID; no origin event | **NOT POSSIBLE TODAY** |
| 24 | Push notifications | Do pushes reach users? | Fix delivery infrastructure | Persisted FCM send results | **NOT AVAILABLE** — response discarded | **NOT POSSIBLE TODAY** |
| 25 | Push notifications | Do pushes drive action? | Invest in notification strategy | `Notification.Id` correlation from send to open to action | **NOT AVAILABLE** — `notification_opened` is client-defined with no guaranteed correlation id | **NOT POSSIBLE TODAY** |
| 26 | Moderation | Is the platform safe? | Invest in proactive moderation | Report volume and category mix | **AVAILABLE** — VERIFIED in the prior audit | **HIGH** |
| 27 | Moderation | Are `MentalHealth` rooms riskier? | Category-specific safeguards | Report rate per room category | **AVAILABLE** — `reportedRoomId` joins to `Room.Category`; not currently computed | **HIGH** |
| 28 | Moderation | Which enforcement actions work? | Tune the action ladder | Recorded `AdminReportAction` outcomes + recidivism | **NOT AVAILABLE** — actions are not evented | **NOT POSSIBLE TODAY** |
| 29 | Blocking | Is peer-level friction widespread? | Address interpersonal safety | Block prevalence over time | **PARTIALLY AVAILABLE** — `user_blocked` exists; no unblock event, rows deleted | **LOW** |
| 30 | Support | What are users struggling with? | Fix the top ticket driver | Ticket volume by type over time | **AVAILABLE** in the database, **NOT EXPOSED** by any analytics endpoint | **MEDIUM** |
| 31 | Support | Is support responsive? | Staff support differently | First-response and resolution time | **PARTIALLY AVAILABLE** — computable from `SupportMessage`/`ClosedAt`; nothing computes it | **MEDIUM** |
| 32 | Profiles | Does profile completeness drive outcomes? | Prompt completion | Completion timestamp for before/after analysis | **NOT AVAILABLE** — no profile events, no `ApplicationUser.UpdatedAt` | **NOT POSSIBLE TODAY** |
| 33 | MBTI | Does MBTI predict behaviour? | Build MBTI features vs. drop the step | Behaviour segmented by MBTI dichotomy | **AVAILABLE** technically; cell sizes likely too small (F13) | **LOW** |
| 34 | Retention | Are users coming back? | Prioritise retention work | Reliable recurring-activity signal per user | **NOT AVAILABLE** — cookie-based `session_started` on a mobile client; shipped retention query uses exact-day matching | **NOT POSSIBLE TODAY** |
| 35 | Growth | Is the user base growing? | Adjust acquisition investment | Registrations over time | **AVAILABLE** — `ApplicationUser.CreatedAt`, indexed | **HIGH**, degrading — hard deletes erode history |
| 36 | Growth | Where do users come from? | Allocate acquisition spend | Acquisition source on the user record | **NOT AVAILABLE** — no source/referral field exists | **NOT POSSIBLE TODAY** |
| 37 | Topic requests | Should this be built? | Build vs. delete the dead schema | Evidence of topic-supply constraint | **NOT AVAILABLE** — feature does not exist (Finding E) | **NOT POSSIBLE TODAY** |
| 38 | Reliability | How often do users hit errors? | Prioritise stability work | Failure events for join/send/register | **NOT AVAILABLE** — no failure events; errors go to `ILogger` → Docker stdout only | **NOT POSSIBLE TODAY** |
| 39 | Scheduling | When should rooms be scheduled? | Guide coaches on timing | Activity by *local* hour | **PARTIALLY AVAILABLE** — UTC only; MENA user base implies UTC+2/+3 | **MEDIUM** |
| 40 | Engagement depth | How long do users use the app? | Judge engagement quality | Session duration | **NOT AVAILABLE** — no `session_ended`, no heartbeat | **NOT POSSIBLE TODAY** |

---

## Summary of the Decision Landscape

**Counting the matrix:**

| Confidence | Count | Share |
|---|:--:|:--:|
| HIGH | 6 | 15% |
| MEDIUM | 9 | 22.5% |
| LOW | 5 | 12.5% |
| NOT POSSIBLE TODAY | 20 | 50% |

**INFERENCE — the shape of the gap.** Half of Cocorra's real product decisions cannot be made from data at all today. But the distribution is not random, and that is the useful part:

**What Cocorra *can* see:** things that produce a **row** — a user, a room, a report, a message, a friend request. The relational schema is sound and those counts are trustworthy.

**What Cocorra *cannot* see:** things that produce only a **state change** — a hand raised then lowered, a mic opened then closed, a participant who joined then left, a scheduled room that went live, a reminder set then un-set, a block later undone. Cocorra's schema consistently stores **current state** where analytics needs **transition history**. Every one of these is a boolean or a mutable column where an event or a timestamp was needed.

This single pattern explains the majority of the NOT POSSIBLE rows. It is one architectural habit, not twenty unrelated oversights — which is why it is addressable by one consistent change (emit an event at every state transition) rather than twenty separate ones.

**The one place the pattern turns actively misleading rather than merely absent:** the host's open microphone (Finding A). Absence of data is survivable — you know you don't know. A metric that reports a passive host as the platform's top speaker while simultaneously classifying them as a silent listener is worse than no metric, because someone will act on it.

---

## What Follows

- `07a-feature-investment-framework.md` — how to decide whether each feature deserves more investment, and where the causal claims break down.
- `07b-north-star-analysis.md` — candidate North Star metrics and a recommendation.
- `05-analytics-gap-analysis.md` — decision-by-decision gap analysis with priorities.
- `09-recommended-dashboard.md` — the dashboard derived from these decisions.
- `10a-decision-safety-matrix.md` — what is safe to decide today, and what is not.
