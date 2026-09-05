# 06a — Recommended Event Taxonomy

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Depends on**: `05-event-tracking-audit.md` (current inventory), `05-analytics-gap-analysis.md` (gap IDs), `07-decision-framework.md` (Findings A–E)
> **Scope**: Documentation only. **No events were added. No code was modified.** Everything below is a **RECOMMENDATION**.

---

## Design Rules

These rules constrain every event proposed here.

### Rule 1 — Every event must answer a real question

An event earns its place only if a decision in `07-decision-framework.md` or a gap in `05-analytics-gap-analysis.md` is blocked without it. Each entry below names the gap it closes and the decision it enables.

### Rule 2 — No generic events

Explicitly **not** recommended:

```
ButtonClicked
PageViewed
FeatureUsed
ScreenOpened
```

**INFERENCE** — these push interpretation to query time, when the meaning of `elementId = "btn_3"` has already been lost. Domain events carry their meaning with them.

**FACT** — Cocorra already has one instance of this anti-pattern: `feature_viewed` is allowlisted in `EventsController.cs:22` with entirely client-defined properties and no standardised feature list. It cannot support an analysis, because nothing constrains what a "feature" is. **RECOMMENDATION** — deprecate it in favour of the specific events below rather than trying to standardise it retroactively.

### Rule 3 — Prefer domain events over analytics events

| Kind | Definition | Trust | Cocorra examples |
|---|---|---|---|
| **Domain event** | Something that happened in the business, emitted server-side from the code that performed it. | Server-authoritative. | `room_joined`, `user_registered`, `user_reported` |
| **Analytics event** | Something collected purely for measurement, usually client-emitted. | Untrusted. Client-controlled. | `notification_opened`, `feature_viewed`, `room_create_started` |

**FACT** — `EventsController` gates client events behind an allowlist (`ClientAllowedEvents`, `EventsController.cs:22,45`), which is a sound design.

**RECOMMENDATION** — keep the ratio heavily weighted to domain events. Of the 24 events proposed here, **20 are server-emitted domain events** and 4 are client analytics events. **INFERENCE** — every client event depends on the separate Flutter repository implementing it correctly and continuing to; a server event depends only on this codebase. Where a behaviour can be captured server-side, it should be.

**Critical rule** — never mix client and server events in the same funnel without labelling which steps are which. **INFERENCE** — a drop between a server step and a client step is ambiguous: it may be user behaviour, or it may be the client failing to emit. Those are different problems and the funnel cannot distinguish them.

### Rule 4 — Mandatory envelope

Every event carries:

```
EventName          — snake_case, past tense, domain language
OccurredAtUtc      — server timestamp at the moment of the action
UserId             — the acting user (nullable only for genuinely anonymous actions)
SessionId          — session context where available
RoomId             — promoted to its own indexed column when room-scoped
PropertiesJson     — event-specific properties
```

**FACT** — this envelope already exists on `UserEvent`: `Id`, `UserId`, `EventType`, `PropertiesJson`, `SessionId`, `RoomId`, `OccurredAtUtc`, `IpHash`, `UserAgent`, with indexes on `(EventType, OccurredAtUtc)`, `(UserId, OccurredAtUtc)`, and `(RoomId, EventType, OccurredAtUtc)`. **INFERENCE** — the existing infrastructure is well designed and every recommendation below fits it without schema change. The gap is in *emit coverage*, not in the event system's architecture.

### Rule 5 — State transitions emit events

**INFERENCE** — this is the single rule that would close the largest share of Cocorra's gaps. `05-analytics-gap-analysis.md` GAP-05 established the pattern: the schema stores **current state** where analytics needs **transition history**. Booleans like `IsHandRaised`, `IsOnStage`, and `IsRead`, and deleted rows like `RoomReminder` and `UserBlock`, all discard the transition that created them.

The rule: **when a state changes, emit an event carrying the old value, the new value, and who caused it.**

### Rule 6 — Correlation identifiers where a chain must be reconstructed

Only where a multi-step chain has to be stitched: `notificationId` for send → open → action; `reportId` for report → action → outcome; `roomId` for everything room-scoped (already promoted to a column).

**RECOMMENDATION** — do not add correlation ids speculatively. Each one is a contract that must be maintained across the server and the Flutter client.

---

## Existing Events — Keep, Extend, or Deprecate

The 24 currently-emitted events, assessed.

| Event | Verdict | Reason |
|---|:--:|---|
| `user_registered` | **KEEP** | Sound. Server-emitted. |
| `voice_verification_submitted` | **KEEP** | Sound. Fires on both initial and re-record. |
| `email_confirmed` | **KEEP** | Sound. |
| `mbti_submitted` | **KEEP** | Sound, carries `{mbti}`. |
| `voice_verification_result` | **EXTEND** | Add `changedByAdminId`, `isBulkOperation`, `previousStatus` — closes GAP-02, GAP-05, GAP-08 reviewer consistency. |
| `activation_completed` | **KEEP** | Sound, deduplicated at emit. |
| `account_deleted` | **KEEP** | Carries `{reason}`. **INFERENCE** — currently the only surviving trace of a churned user, since the row is hard-deleted. |
| `room_created` | **EXTEND** | Add `selectionMode`, `stageCapacity`, `totalCapacity`, `durationHours`, `isScheduled` — enables room-configuration outcome analysis without a join. |
| `room_join_requested` | **KEEP** | Sound. |
| `room_join_approved` | **KEEP** | Sound. |
| `room_joined` | **EXTEND** | Add `entrySource`, `isHost`, `isRejoin` — **the single highest-value extension in this document** (GAP-09). |
| `room_left` | **EXTEND** | Add `secondsInRoom`, `wasOnStage`, `didSpeak`, `leaveReason` — closes GAP-14. |
| `mic_activated` | **KEEP** | Sound for non-hosts. See `mic_deactivated` below for the host problem. |
| `speaking_time_logged` | **REPLACE** | **FACT, Correction 2** — emitted only inside `EndRoomAsync`, only for participants still `Active`, carrying a cumulative total. A room never formally ended emits none. Superseded by `mic_deactivated` segments. |
| `room_ended` | **EXTEND** | Add `actualDurationSeconds`, `endReason`, `peakParticipants` — **FACT, Finding C**: the current `durationHours` is computed from the *scheduled* `StartDate`. |
| `message_sent` | **EXTEND** | Add `originSurface`, optional `roomId` — closes GAP-16. |
| `friend_request_sent` | **EXTEND** | Add `originSurface`, optional `sharedRoomId` — closes GAP-17. |
| `friend_request_accepted` | **KEEP** | Sound. |
| `user_reported` | **KEEP** | Already the richest event in the system. |
| `user_blocked` | **KEEP** | Sound. |
| `session_started` | **REPLACE** | **FACT** — cookie-based on a Flutter client; deduplication uses in-process `IMemoryCache` lost on restart. Replaced by `app_session_started` (GAP-04). |
| `room_create_started` | **KEEP** | Client-emitted; acceptable as a directional signal. |
| `notification_opened` | **EXTEND** | **Require** `notificationId` — without it, opens cannot be attributed to sends and no rate is computable (GAP-11). |
| `feature_viewed` | **DEPRECATE** | Violates Rule 2. No standardised feature list; cannot support an analysis. |

---

# Recommended New Events

Grouped by priority. Priorities match `05-analytics-gap-analysis.md`.

---

## P0 — Core Loop Instrumentation

These close GAP-01 and GAP-06. **INFERENCE** — this is the highest-value group in the document: they instrument Cocorra's core value loop and its designated North Star input (`07b`, Input 3), where the team can currently observe an outcome and none of its causes.

| Event | Actual Trigger | Required Properties | Why It Matters | Decision Enabled | Priority |
|---|---|---|---|---|:--:|
| `hand_raised` | `RoomHub.RaiseHand` sets `IsHandRaised = true` (`RoomHub.cs:381-400`) | `roomId`, `secondsSinceJoin`, `currentStageOccupancy`, `stageCapacity`, `selectionMode` | **FACT** — `IsHandRaised` is a live boolean reset by `LowerHand`, so the shipped `UsersWhoRaisedHand` count is near-permanently ~0 for any past window. This is stage *demand*, currently unmeasured. | Is the stage the bottleneck? Should `StageCapacity` defaults rise? | **P0** |
| `hand_lowered` | `RoomHub.LowerHand` | `roomId`, `secondsRaised`, `wasApproved` | Distinguishes "changed their mind" from "gave up waiting" — opposite problems requiring opposite fixes. | Redesign the hand-raise flow? Is approval latency driving abandonment? | **P0** |
| `stage_promoted` | `RoomHub.ApproveToStage` | `roomId`, `targetUserId`, `byHostId`, `secondsWaiting`, `selectionMode` | **FACT** — `IsOnStage` is a live boolean; promotions are invisible. `secondsWaiting` is the host-responsiveness measure. | Change the default `SelectionMode`? Coach hosts on responsiveness? | **P0** |
| `stage_demoted` | `RoomHub.MoveToAudience` | `roomId`, `targetUserId`, `byHostId`, `stageSeconds`, `didSpeak` | Time-on-stage, and whether promoted users actually spoke. | Is promotion converting into participation? | **P0** |
| `mic_deactivated` | `RoomHub.ToggleMic` transitions to muted (`RoomHub.cs:522-532`) | `roomId`, `segmentSeconds`, `isHost`, `cumulativeSeconds` | **FACT, Finding A** — the host is inserted with `IsMuted=false` and `LastUnmutedAt=UtcNow`, so a silent host accrues the room's full 2–3 hours as "spoken time," while emitting no `mic_activated`. Paired segments replace a contaminated mutable total with an auditable ledger, and `isHost` makes the contamination explicit. | Who genuinely contributes? Fixes the Top Speakers ↔ Active-vs-Passive contradiction. | **P0** |
| `speaker_time_exhausted` | `ToggleMic` throws `"Your time is up!"` (`RoomHub.cs:513-516`) | `roomId`, `allowedSeconds`, `extraGranted`, `attemptNumber` | **FACT** — the throw is currently invisible; a user hitting the wall produces no data at all. | Is `DefaultSpeakerDurationMinutes` too tight? | **P0** |
| `extra_time_granted` | `RoomHub.GrantExtraTime` | `roomId`, `targetUserId`, `byHostId`, `minutesGranted`, `totalExtraSoFar` | **FACT** — `ExtraMinutesGranted` holds only a final cumulative value with no timestamp and no grantor. Frequent grants are evidence the default budget is wrong. | Adjust the default speaker duration? | **P0** |
| `user_kicked` | `RoomHub.KickUser` | `roomId`, `targetUserId`, `byHostId`, `secondsInRoom` | In-room moderation is entirely unmeasured; complements the report-based safety view. | Is in-room moderation working? Do certain rooms need support? | **P0** |
| `room_went_live` | `RoomService.StartScheduledRoomAsync` and live creation | `roomId`, `wasScheduled`, `minutesLateVsSchedule`, `remindersSet`, `category` | **FACT, Finding C** — `StartScheduledRoomAsync` emits nothing and writes no timestamp; the actual go-live moment exists nowhere. This also supplies the real start time needed for actual room duration. | Do scheduled rooms go live? Is scheduling worth keeping? | **P0** |
| `user_status_changed` | `AdminService.ChangeUserStatusAsync` | `fromStatus`, `toStatus`, `changedByAdminId`, `isBulkOperation`, `reason` | Closes three gaps at once: historical status (GAP-02), backlog history (GAP-05), reviewer consistency (GAP-08). **FACT** — `ApplicationUser` has no `UpdatedAt`, so this event is the only possible record. | Is the queue a bottleneck? Are reviewers consistent? | **P0** |

### Extensions to existing P0 events

| Event | Added Properties | Why |
|---|---|---|
| `room_left` | `secondsInRoom`, `wasOnStage`, `didSpeak`, `leaveReason` ∈ {explicit, disconnect, kicked, room_ended} | **FACT** — no `LeftAt` on `RoomParticipant`, and `JoinedAt` is overwritten on rejoin (`RoomHub.cs:245-253`). Time-in-room is unrecoverable without this (GAP-14). |
| `room_ended` | `actualDurationSeconds`, `endReason` ∈ {host_ended, host_disconnected, expired}, `peakParticipants` | **FACT** — current `durationHours` is computed from the *scheduled* `StartDate`, overstating duration for rooms started late (Finding C). |
| `voice_verification_result` | `changedByAdminId`, `isBulkOperation`, `previousStatus` | Reviewer consistency and transition history. |

---

## P1 — Discovery, Delivery & Reliability

| Event | Actual Trigger | Required Properties | Why It Matters | Decision Enabled | Priority |
|---|---|---|---|---|:--:|
| `room_joined` **(extend)** | unchanged | add `entrySource` ∈ {feed, reminder_push, deep_link, profile, search, direct}, `isHost`, `isRejoin` | **INFERENCE — the highest-value single property in this document.** It converts every discovery question from unanswerable to trivial, and `isHost` makes Finding A's host exclusion possible at query time without a join. One string on an event that already fires. | Invest in feed, reminders, or search? | **P1** |
| `reminder_set` | `RoomService.ToggleReminderAsync` creates a row | `roomId`, `hoursUntilStart`, `roomCategory` | **FACT** — `ToggleReminder` emits nothing and rows are *deleted* on un-toggle, so the table is a snapshot of intent, not a log. Reminder→attendance conversion currently reads optimistically. | Is the reminder loop worth investing in? | **P1** |
| `reminder_removed` | Same method removes a row | `roomId`, `hoursHeld` | Distinguishes lost interest from a satisfied reminder. | Same. | **P1** |
| `push_send_attempted` | Before the FCM call in `PushNotificationService` | `notificationId`, `notificationType`, `hasToken`, `targetUserId` | **FACT** — the FCM response is discarded entirely. | Is delivery working? | **P1** |
| `push_send_result` | FCM response received | `notificationId`, `success`, `errorCode`, `tokenInvalidated` | **INFERENCE** — commit `dc1c933` fixed *reversed FCM delivery*. An identical regression today would be invisible to the dashboard and would surface only through user complaints, exactly as it did before. For a defect class that has already occurred once, this is a regression guard, not a nice-to-have. | Is push infrastructure healthy? | **P1** |
| `notification_opened` **(extend)** | client | **require** `notificationId` | Without the correlation id, opens cannot be attributed to sends and no conversion rate exists. | Do notifications drive action? | **P1** |
| `moderation_action_taken` | `SupportService` admin report action | `reportId`, `action` ∈ {WarnUser, Mute24h, BanUser, RejectReport}, `targetUserId`, `byAdminId`, `hoursToAction` | **FACT** — `AdminReportAction` outcomes mutate user state and are never recorded. Enforcement effectiveness and recidivism are unmeasurable. | Which enforcement actions work? | **P1** |
| `app_session_started` | Client, on app foreground | `deviceId`, `platform`, `appVersion`, `sessionId` (client-generated UUID, app-storage persisted) | **FACT** — `session_started` is cookie-based (`SessionTrackingMiddleware:53`) on a Flutter client, and deduplication uses in-process `IMemoryCache` lost on restart. **INFERENCE** — the correct identity for a mobile session is authenticated user + device, not an HTTP cookie; Cocorra already collects device metadata for `BlockedDevices`, so the concept exists. | Measure genuine app engagement. | **P1** |
| `app_session_ended` | Client, on background/close | `sessionId`, `durationSeconds`, `screensVisited` | **FACT** — no session-end signal exists, so session duration is unmeasurable. **Client-emitted and therefore lossy**: a force-quit or crash sends nothing, so durations are biased toward sessions that ended cleanly. | Measure engagement depth. | **P1** |

---

## P2 — Deeper Product Intelligence

| Event | Actual Trigger | Required Properties | Why It Matters | Decision Enabled | Priority |
|---|---|---|---|---|:--:|
| `room_feed_viewed` | Client, when `/Room/Feed` results render | `roomIdsShown[]`, `filterApplied`, `resultCount` | **FACT** — `GET /Room/Feed` emits nothing; there is no record a room was ever *displayed*. Without impressions, a low join count cannot distinguish "nobody saw it" from "everyone saw it and passed" — opposite problems requiring opposite fixes. | Invest in feed ranking or search? | **P2** |
| `room_detail_viewed` | Client, room detail opened | `roomId`, `sourceSurface`, `feedPosition` | The intent step between impression and join. | Where does the discovery funnel leak? | **P2** |
| `room_group_message_sent` | `RoomHub.SendRoomGroupMessage` | `roomId`, `messageLength`, `isOnStage`, `secondsSinceJoin` | **FACT** — neither persists nor emits. **INFERENCE** — Active-vs-Passive labels most participants "passive." If many are typing, that label is wrong and so is the conclusion drawn from it. Content need not be stored for the analytics question. | Is chat how the silent majority participates? | **P2** |
| `message_sent` **(extend)** | unchanged | add `originSurface` ∈ {room, friends_list, profile}, optional `roomId` | **FACT** — `SendRoomPrivateMessage` and `ChatHub.SendMessage` both call `ChatService.SaveMessageAsync` and emit identically (`ChatService.cs:92`). One property separates "messaging is a room feature" from "messaging is standalone" — the entire decision. | Strengthen the room→DM bridge? | **P2** |
| `friend_request_sent` **(extend)** | unchanged | add `originSurface`, optional `sharedRoomId` | **FACT** — friend search requires a pre-known exact user ID, so requests must originate from a room list or profile, neither of which emits. | Build people discovery? | **P2** |
| `friend_request_rejected` | `FriendService.RespondToFriendRequestAsync` with `accept=false` | `senderId`, `hoursToRespond` | **FACT, Finding D** — re-sending after rejection *mutates the existing row*, overwriting `Status` and `CreatedAt`. The rejection is erased. | Is the acceptance rate real? | **P2** |
| `friend_removed` | `FriendService.RemoveFriendAsync` | `targetUserId`, `daysFriends`, `messagesExchanged` | Relationship churn is invisible; the row is deleted. | Are friendships durable? | **P2** |
| `user_unblocked` | `BlockService.UnblockUserAsync` | `blockedUserId`, `daysBlocked` | **FACT** — no unblock event, and the row is deleted, so block prevalence is only ever a snapshot. | Is peer friction growing? | **P2** |
| `registration_started` | Client, registration form opened | `platform`, `appVersion`, `referralSource` | **FACT** — the funnel currently starts at submission; pre-submission abandonment is invisible (GAP-13). | Where does onboarding really begin to leak? | **P2** |

---

## P3 — Advanced

| Event | Actual Trigger | Required Properties | Why It Matters | Decision Enabled | Priority |
|---|---|---|---|---|:--:|
| `room_join_failed` | Join or SignalR connect throws | `roomId`, `reason`, `stage` ∈ {rest_join, hub_connect, livekit_token} | **FACT** — no failure events exist anywhere; errors reach `ILogger` → Docker stdout and are never persisted. **INFERENCE** — a failure at the LiveKit token stage is invisible today and would present as a user who simply never joined. | How reliable is the core loop? | **P3** |
| `livekit_participant_event` | LiveKit webhook ingestion | `roomId`, `participantIdentity`, `eventType`, `connectionQuality` | **FACT** — `ILiveKitService` exposes only `GenerateToken` and `UpdateStagePermissionAsync`; a repository-wide search for "webhook" returns nothing. **INFERENCE** — Cocorra is blind to its own media layer. A room where audio failed for half the participants is indistinguishable from one where half chose not to speak. The correlation key already exists: Cocorra sets `participantIdentity` when generating tokens. | Invest in media infrastructure? | **P3** |
| `experiment_exposure` | Variant assignment | `experimentKey`, `variant`, `assignedAt` | **FACT** — no feature flags, variant assignment, or experiment table exist anywhere in the solution. Prerequisite for any causal claim (`07a`). | Did our change actually work? | **P3** |

---

# Domain vs Analytics Event Classification

| Classification | Count | Trust | Events |
|---|:--:|---|---|
| **Domain (server-emitted)** | 20 | Authoritative | `hand_raised`, `hand_lowered`, `stage_promoted`, `stage_demoted`, `mic_deactivated`, `speaker_time_exhausted`, `extra_time_granted`, `user_kicked`, `room_went_live`, `user_status_changed`, `reminder_set`, `reminder_removed`, `push_send_attempted`, `push_send_result`, `moderation_action_taken`, `room_group_message_sent`, `friend_request_rejected`, `friend_removed`, `user_unblocked`, `room_join_failed` |
| **Analytics (client-emitted)** | 4 | Untrusted; allowlist required | `app_session_started`, `app_session_ended`, `room_feed_viewed`, `room_detail_viewed`, `registration_started` |
| **Infrastructure (webhook)** | 1 | External | `livekit_participant_event` |

**INFERENCE — why the ratio matters.** Twenty of twenty-four recommended events are server-emitted. Every client event depends on the separate Flutter repository implementing it, keeping it correct, and shipping it to users who then have to update. A server event depends only on this codebase. The four client events proposed are ones that *cannot* be captured server-side — the server cannot know that a feed rendered, that a form was opened, or that an app went to background.

**RECOMMENDATION — allowlist changes.** The four new client events must be added to `ClientAllowedEvents` in `EventsController.cs:22`, and `feature_viewed` should be removed from it as part of its deprecation.

---

# Implementation Sequencing

**RECOMMENDATION** — implement in this order. Rationale follows each stage.

### Stage 1 — Six events that close the core-loop gap

`hand_raised`, `hand_lowered`, `stage_promoted`, `stage_demoted`, `mic_deactivated`, plus `room_joined.entrySource` + `isHost`.

**Why first (INFERENCE)** — these convert the core funnel from two measurable steps to six, close GAP-01's active contradiction, and make GAP-09's discovery question answerable via a single added property. They are all in `RoomHub`, in methods that already write to the database and already have `_eventTracker` injected, so the change is a call added alongside an existing save.

### Stage 2 — Transition history

`user_status_changed`, `room_went_live`, `room_left` extension, `room_ended` extension.

**Why second (INFERENCE)** — these close the snapshot-versus-history problem for the entities where it does most damage: user status (GAP-02, GAP-05) and room lifecycle (Finding C).

### Stage 3 — Delivery and safety

`push_send_attempted`, `push_send_result`, `notification_opened.notificationId`, `moderation_action_taken`.

**Why third** — the push pair guards a defect class that has already occurred once in this codebase; the moderation event makes enforcement effectiveness measurable.

### Stage 4 — Session replacement

`app_session_started`, `app_session_ended`.

**Why fourth (INFERENCE)** — this requires Flutter-side work and a client release, so it has the longest lead time and the least local control. Importantly, it is **not** a blocker for retention measurement: `05-analytics-gap-analysis.md` GAP-04 establishes that room-join-based return is available today and is more reliable than anything session-based would be. Session events buy engagement *depth*, not retention.

### Stage 5 — Discovery and social

`room_feed_viewed`, `room_detail_viewed`, `message_sent.originSurface`, `friend_request_sent.originSurface`, `registration_started`.

### Stage 6 — Advanced

`room_join_failed`, `livekit_participant_event`, `experiment_exposure`.

---

# Operational Considerations

Four constraints on the existing pipeline that any expansion must respect.

### 1 — Event volume and the DropWrite policy

**FACT** — `EventTracker` writes to a bounded `Channel<UserEvent>` of 10,000 capacity with `BoundedChannelFullMode.DropWrite`. When full, events are **silently dropped** (logged as a warning). `EventFlushService` batches up to 100 per insert.

**INFERENCE** — the P0 room events are high-frequency: `mic_deactivated` fires on every mute, `hand_raised`/`hand_lowered` on every toggle. In a busy room these could multiply per-room event volume several-fold. Dropped events do not fail loudly; they undercount, and the undercount is worst precisely during the busiest rooms — the ones most worth analysing.

**RECOMMENDATION** — before adding P0 events, measure current channel utilisation and the frequency of the drop warning. If headroom is thin, raise the bound or the flush batch size first. Adding events to a saturated channel would degrade the events that already work.

### 2 — The 180-day retention window

**FACT** — `EventCleanupService` purges events older than 180 days every 24 hours, with the period hardcoded (`EventCleanupService.cs:33`). No archive or export occurs before deletion.

**INFERENCE** — every event recommended here inherits a six-month ceiling. Year-over-year analysis will never be possible under this policy, and cohort depth is permanently capped.

**RECOMMENDATION** — before expanding event volume, decide the retention policy deliberately. Options: extend the window; archive to cold storage before purge; or maintain daily aggregate rollups that survive the purge. **INFERENCE** — the rollup option is cheapest and would also close GAP-05's need for snapshot history, so it addresses two problems at once.

### 3 — Storage growth

**INFERENCE** — the P0 additions are the highest-frequency events proposed. `PropertiesJson` is unbounded text. Room-scoped events already have a promoted, indexed `RoomId`, which is the right design, but index maintenance cost grows with row count.

**RECOMMENDATION** — keep property payloads small and typed. Prefer `secondsSinceJoin` (an integer) over an embedded timestamp; prefer enums over free text.

### 4 — Client event trust

**FACT** — client events pass through the `ClientAllowedEvents` allowlist but their **properties are entirely client-defined**. There is no property-schema validation.

**RECOMMENDATION** — validate required properties server-side for the client events that carry correlation ids, most importantly `notification_opened.notificationId`. **INFERENCE** — an allowlisted event with a missing or malformed correlation id is worse than a rejected one, because it produces a row that looks like data and cannot be joined to anything.

---

# Summary

| Priority | New events | Extensions | Gaps closed |
|:--:|:--:|:--:|---|
| **P0** | 10 | 3 | GAP-01, GAP-02, GAP-05, GAP-06 |
| **P1** | 7 | 2 | GAP-04, GAP-08, GAP-09, GAP-11, GAP-12 |
| **P2** | 8 | 2 | GAP-13, GAP-15, GAP-16, GAP-17 |
| **P3** | 3 | 0 | GAP-20, GAP-21, GAP-22 |
| **Total** | **28** | **7** | **17 of 23 gaps** |

**Six gaps are not closed by events**, because events are the wrong instrument for them: GAP-03 (hard deletes → soft delete), GAP-07's supply views and GAP-10's support views (queries against existing data), GAP-14's `LeftAt` (schema), GAP-18 (timezone display), GAP-19 (MBTI analysis). **INFERENCE** — this is worth stating plainly, because it means an event-tracking programme alone would not close the gap. The cheapest wins in `05-analytics-gap-analysis.md` are queries against data that already exists and has already been verified correct, and those should not wait behind this instrumentation work.

**The single highest-value change in this document** is one property: `entrySource` on `room_joined`. It converts every room-discovery question from unanswerable to trivial, on an event that already fires, in a code path that already runs.

**The single most urgent change** is `mic_deactivated` with `isHost`, because it closes GAP-01 — the one place where Cocorra's data does not merely fall silent but actively contradicts itself, reporting the same passive host as both the platform's top speaker and a silent listener.
