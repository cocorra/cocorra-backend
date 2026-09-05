# 15 — Event Implementation Contracts

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 4
> **Depends on**: `06a-recommended-event-taxonomy.md` (taxonomy), `11-current-state-validation.md` (verified pipeline), `13-data-trust-correction-plan.md`
> **Scope**: Documentation only. No events were added and no code was modified.

---

## Envelope

Every event carries this envelope. Fields marked **NEW** do not exist on `UserEvent` today and are specified in `16-raw-event-storage-strategy.md`.

```
EventId          NEW   Guid — stamped at enqueue; UNIQUE in storage. The idempotency key.
EventName              string(64) — EventTypes constant
SchemaVersion    NEW   byte — event payload version, starts at 1
OccurredAtUtc          DateTime — server clock at the moment of the domain action
UserId                 Guid? — the acting user
RoomId                 Guid? — promoted from properties by ExtractRoomId (existing behaviour)
SessionId              Guid? — HTTP context, or explicit for hub calls (TRUST-08)
CorrelationId    NEW   Guid? — only where a cross-event chain must be reconstructed
PropertiesJson         string? — event-specific payload
IpHash                 string(64)? — existing; null for hub-emitted events
UserAgent              string(256)? — existing; null for hub-emitted events
```

**FACT** — `ExtractRoomId` already promotes a `roomId` property into the indexed `RoomId` column case-insensitively (`EventTracker.cs`). Every room-scoped event below relies on this existing behaviour and requires no new promotion logic.

## Producer interface change

**RECOMMENDATION** — extend `IEventTracker` additively. The existing three-argument signature must remain so all ~24 current call sites compile untouched.

```csharp
// EXISTING — unchanged
void Track(string eventType, Guid? userId = null, object? properties = null);

// NEW — idempotency key and explicit context for the SignalR path
void Track(string eventType, Guid? userId, object? properties,
           string? eventKey = null, Guid? sessionId = null, Guid? correlationId = null);
```

`eventKey` is a caller-supplied natural key. When present, `EventTracker` derives `EventId` deterministically from it (a stable hash into a GUID) rather than calling `Guid.NewGuid()`. This is what makes "at most once" events enforceable at the database rather than by a racing read (TRUST-10).

---

## Idempotency — the general model

**FACT — the current state.** `UserEvent.Id` is a `bigint` identity assigned by the database at insert. A duplicate enqueue produces two rows with two different ids. There is no deduplication anywhere in the pipeline.

**RECOMMENDATION** — three idempotency classes. Every event below is assigned exactly one.

| Class | Meaning | `EventId` derivation | Storage behaviour |
|---|---|---|---|
| **EXACTLY-ONCE** | The event may appear at most once for its natural key, ever. | Deterministic from `eventKey` | `UNIQUE(EventId)`; duplicate insert is swallowed |
| **AT-LEAST-ONCE, DEDUP-ON-REPLAY** | Genuine repeats are meaningful; pipeline retries must not double-count. | `Guid.NewGuid()` at enqueue | `UNIQUE(EventId)` makes a retried *batch* safe; a genuine second occurrence gets a new id and is kept |
| **NATURALLY-UNIQUE** | The domain action cannot repeat for its key. | `Guid.NewGuid()` | Unique index provides retry safety only |

**INFERENCE — why `EventId` is stamped at enqueue, not at flush.** The flush service retries whole batches. If ids were assigned during persistence, a retried batch would carry new ids and the unique constraint would not recognise it as a replay. Stamping at enqueue is what makes TRUST-07's retry safe, which is why `16-` treats the column as a hard prerequisite of the retry work rather than a parallel improvement.

### The four duplicate sources, and how each is handled

| Source | Verified mechanism | Handling |
|---|---|---|
| **SignalR reconnect** | **FACT** — `RoomHub.cs:270` emits `room_joined` on the unconditional path of `JoinRoom`; every reconnect re-emits. | Keep emitting. Add `isRejoin`. Metrics count **distinct users** (M-100). A rejoin is real information, not noise. |
| **Room rejoin after leaving** | **FACT** — `RoomHub.cs:245-253` re-activates a `Left` participant and overwrites `JoinedAt`. | Same as above. `isRejoin = true` lets analysis separate the two cases, which is impossible today. |
| **Flush retry** | **NEW** in TRUST-07 — the batch is re-applied after a transient DB failure. | `UNIQUE(EventId)` — the replayed batch collides and is discarded. |
| **Duplicate client request** | **FACT** — `EventsController.Track` has no idempotency and `TrackEventDto.Properties` is an unvalidated `object?`. | Client supplies `eventKey`; server derives a deterministic `EventId`. |

---

# P0 — Core Loop Events

The eight events that close GAP-01 and GAP-06. All are emitted from `Cocorra.API/Hubs/RoomHub.cs`, in methods that already write to the database and already have `IEventTracker` injected (`RoomHub.cs:24`).

---

## E-01 — `hand_raised`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | A participant raises their hand; fired **after** `SaveChangesAsync` succeeds. |
| **Producer** | `RoomHub.RaiseHand(string roomId)` — `Cocorra.API/Hubs/RoomHub.cs:381-400` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per raise cycle |

**Insertion point (FACT)** — the method currently sets `participant.IsHandRaised = true`, calls `UpdateParticipantAsync` + `SaveChangesAsync`, then broadcasts `"HandRaised"`. The emit belongs after the save and before or after the broadcast; it must not precede the save (INV-2).

**Payload**

```
roomId                Guid    → promoted to the indexed RoomId column
secondsSinceJoin      int     computed from participant.JoinedAt
currentStageOccupancy int     count of participants with IsOnStage in this room
stageCapacity         int     Room.StageCapacity
selectionMode         string  Room.SelectionMode.ToString()
```

**INFERENCE — why `currentStageOccupancy` and `stageCapacity` are on the event rather than joined later.** They are *point-in-time* values. Joining to `Room.StageCapacity` at query time recovers the configured capacity but not how full the stage actually was when the hand went up — and that is precisely the question M-402 asks. Occupancy is unrecoverable after the fact because `IsOnStage` is a live boolean (TRUST-04).

**Cost note (FACT)** — `RaiseHand` currently loads only the single participant via `GetParticipantAsync`. Computing occupancy requires a count over the room's participants. **RECOMMENDATION** — use a `COUNT` projection, not `GetRoomParticipantsAsync` (which materialises full entities), to avoid turning a one-row read into a full participant load on a hot path.

**Idempotency** — `eventKey = "hand_raised:{roomId}:{userId}:{raiseSequence}"`, where `raiseSequence` is the count of prior raises in this room for this user. **INFERENCE** — a plain `{roomId}:{userId}` key would collapse a legitimate raise → lower → raise into one event, discarding real signal; the sequence preserves genuine repeats while still blocking double-fire from a duplicated hub invocation.

**Ordering** — matters relative to `stage_promoted` (M-402 requires the raise to precede the promotion). `OccurredAtUtc` at server clock, single instance, is sufficient. No sequence number needed.

**Failure behaviour**

| Aspect | Behaviour |
|---|---|
| Product action | Hand is raised and broadcast — completes regardless |
| Analytics failure | `Track` swallows all exceptions (INV-1); a full channel drops the event with a warning |
| Retry | None at emit; the flush service retries persistence |
| Data loss risk | **LOW** — a dropped event undercounts stage demand and slightly overstates the promotion rate in M-402 |

---

## E-02 — `hand_lowered`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | A participant lowers their hand, or it is lowered by promotion/room end. |
| **Producer** | `RoomHub.LowerHand` — `RoomHub.cs:402-419`; also the implicit lowering inside `ApproveToStage` and `EndRoomAsync` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per raise cycle |

**Payload**

```
roomId        Guid
secondsRaised int      OccurredAtUtc − the matching hand_raised time
wasApproved   bool     true if lowered because of promotion, false if withdrawn
reason        string   explicit | promoted | room_ended | kicked
```

**INFERENCE — `wasApproved` is the field that makes this event worth emitting.** Without it, a withdrawn hand and a promoted hand are indistinguishable, and M-402's denominator silently mixes "gave up waiting" with "got what they wanted." Those are opposite findings.

**FACT — a required upstream change.** `ApproveToStage` (`RoomHub.cs:421+`) and `EndRoomAsync` (`RoomService.cs:526-538`) both reset `IsHandRaised = false` today without any notion of *why*. Emitting this event correctly requires threading a reason through those paths. This is the one P0 event that touches more than its own method.

**Failure behaviour** — as E-01. Data loss risk **LOW**; a missing `hand_lowered` leaves an unterminated raise, which the aggregation must tolerate rather than treat as still-raised.

---

## E-03 — `stage_promoted`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | The host promotes a participant to the stage. |
| **Producer** | `RoomHub.ApproveToStage(string roomId, string targetUserId)` — `RoomHub.cs:421+` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per promotion |

**Payload**

```
roomId         Guid
targetUserId   Guid    the promoted participant
byHostId       Guid    the acting host
secondsWaiting int     OccurredAtUtc − matching hand_raised, or -1 if never raised
selectionMode  string
stageOccupancyAfter int
```

**UserId semantics — a deliberate decision.** `UserId` is set to `targetUserId` (the promoted participant), **not** the host.

**INFERENCE — why.** M-400 is a per-participant funnel: `room_joined → hand_raised → stage_promoted → mic_activated`, all keyed on the same `UserId`. Setting `UserId = byHostId` would break the chain and make the funnel unjoinable. The host is preserved in `byHostId`, so host-side analysis remains possible. **FACT** — this is the opposite convention to the existing `room_join_approved`, which is tracked against the host (`RoomService.cs:311`). That inconsistency is itself worth noting: it means the existing event cannot participate in a per-user funnel.

**Ordering** — must follow `hand_raised` for the same `(roomId, userId)`. Enforced by `OccurredAtUtc` comparison at query time.

**Failure behaviour** — data loss risk **MEDIUM**. A dropped promotion breaks the M-400 chain for that user, who would then appear to have activated a mic without reaching the stage. **RECOMMENDATION** — the aggregation must tolerate this by treating step order as "no later than," not by discarding the user.

---

## E-04 — `stage_demoted`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | Host moves a speaker back to the audience. |
| **Producer** | `RoomHub.MoveToAudience` — `RoomHub.cs:~460-497` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per demotion |

**Payload**

```
roomId       Guid
targetUserId Guid
byHostId     Guid
stageSeconds int     time between the matching stage_promoted and now
didSpeak     bool    whether any mic_activated occurred during this stage period
```

**INFERENCE** — `didSpeak` answers whether promotion actually converted into participation. A high promotion rate with a low `didSpeak` rate would mean the stage flow works and something *after* it does not — a distinction no other metric can draw.

**Failure behaviour** — data loss risk **LOW**.

---

## E-05 — `mic_deactivated`

> The event that closes TRUST-01, the only active self-contradiction in Cocorra's data.

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | A speaker's microphone closes: explicit mute, stage demotion, leaving, or room end. |
| **Producer** | Primary: `RoomHub.ToggleMic` — `RoomHub.cs:522-532`. Also `RoomService.LeaveRoomCleanupAsync` (`RoomService.cs:556-576`) and `RoomService.EndRoomAsync` (`RoomService.cs:526-538`). |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | AT-LEAST-ONCE, DEDUP-ON-REPLAY |

**FACT — the three existing close sites.** All three already compute the segment. `ToggleMic`:

```csharp
if (participant.LastUnmutedAt.HasValue)
{
    var spokenSeconds = (DateTime.UtcNow - participant.LastUnmutedAt.Value).TotalSeconds;
    participant.TotalSpokenSeconds += spokenSeconds;
    participant.LastUnmutedAt = null;
}
```

`LeaveRoomCleanupAsync` and `EndRoomAsync` contain the same calculation. **RECOMMENDATION** — emit at all three, or speaking segments will be systematically lost for every participant who leaves or is still present at room end, which is the majority.

**Payload**

```
roomId           Guid
segmentSeconds   double   the closed segment
isHost           bool     userId == Room.HostId
cumulativeSeconds double  participant.TotalSpokenSeconds after this segment
closeReason      string   explicit_mute | left_room | room_ended | demoted
wasInitialHostMic bool    true only for the host's auto-opened mic (Finding A)
```

**INFERENCE — `wasInitialHostMic` is the field that makes the historical contamination legible.** `isHost` alone allows exclusion. `wasInitialHostMic` additionally identifies segments that were never a deliberate act of speaking — the ones responsible for a silent host booking 2–3 hours. Keeping them separable rather than merely excluded means the distortion can be measured rather than assumed.

**Idempotency** — `Guid.NewGuid()` per segment. Segments genuinely repeat (a speaker may unmute and mute repeatedly), so they must not be collapsed. The unique constraint provides retry safety only.

**Ordering** — must follow the matching `mic_activated`. **FACT — an unavoidable asymmetry**: the host's initial open mic emits **no** `mic_activated` (`RoomHub.cs:518-521` fires only on a `true → false` transition), so the host's first `mic_deactivated` has no matching activation. Aggregation must handle an orphan deactivation rather than treating it as a data error. `wasInitialHostMic` marks exactly this case.

**Failure behaviour** — data loss risk **MEDIUM**. A dropped segment undercounts M-401. **INFERENCE** — the loss is not random: it correlates with load, so the busiest rooms lose the most, which is the same bias TRUST-07 identifies at the pipeline level.

---

## E-06 — `speaker_time_exhausted`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | A speaker attempts to unmute after exhausting their time budget. |
| **Producer** | `RoomHub.ToggleMic` — `RoomHub.cs:513-516`, the `HubException("Your time is up!...")` path |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | AT-LEAST-ONCE |

**FACT — the exact condition:**

```csharp
if (muteStatus == false && remainingSeconds <= 0 && userId != room.HostId)
    throw new HubException("Your time is up! The host needs to grant you more time.");
```

**INFERENCE** — this is currently a completely invisible failure. A participant who hits the wall generates no data at all, so "the speaker time budget is too tight" is unfalsifiable today.

**Payload**

```
roomId         Guid
allowedSeconds int    (DefaultSpeakerDurationMinutes + ExtraMinutesGranted) * 60
usedSeconds    double participant.TotalSpokenSeconds
extraGranted   int    participant.ExtraMinutesGranted
attemptNumber  int    how many times this user has hit the wall in this room
```

**Emit ordering — an exception to INV-2.** This event must be emitted **before** the `throw`, since there is no successful path afterwards. This is consistent with INV-2's intent: the rule prevents recording actions that were rolled back, and here the *rejection itself* is the fact being recorded.

**Failure behaviour** — the product action already fails (by design); the event records the rejection. Data loss risk **LOW**.

---

## E-07 — `extra_time_granted`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | Host grants a speaker additional time. |
| **Producer** | `RoomHub.GrantExtraTime` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | AT-LEAST-ONCE |

**Payload**

```
roomId          Guid
targetUserId    Guid
byHostId        Guid
minutesGranted  int
totalExtraAfter int    participant.ExtraMinutesGranted after the grant
followedExhaustion bool  whether a speaker_time_exhausted preceded it in this room
```

**FACT** — `RoomParticipant.ExtraMinutesGranted` holds only the final cumulative total, with no timestamp and no record of who granted it. The event is the only way to recover grant frequency.

**INFERENCE** — `followedExhaustion` paired with E-06 answers the actual product question directly: if most grants follow an exhaustion event, the default budget is too tight and hosts are routinely compensating for it.

**Failure behaviour** — data loss risk **LOW**.

---

## E-08 — `user_kicked`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | Host removes a participant from the room. |
| **Producer** | `RoomHub.KickUser` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per kick |

**Payload**

```
roomId        Guid
targetUserId  Guid
byHostId      Guid
secondsInRoom int    from participant.JoinedAt
wasOnStage    bool
```

**INFERENCE** — in-room moderation is currently entirely unmeasured; the report-based safety view (M-500/M-501) sees only what users escalate. A host who kicks frequently is a signal that never reaches Trust & Safety today.

**Failure behaviour** — data loss risk **LOW**.

---

# P0 — Lifecycle & Transition Events

---

## E-09 — `room_went_live`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | A room transitions to `Live`, via scheduled start or live creation. |
| **Producer** | `RoomService.StartScheduledRoomAsync` — `RoomService.cs:422-460`; and `RoomService.CreateRoomAsync` when `status == RoomStatus.Live` (`RoomService.cs:115`) |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per room |

**FACT** — `StartScheduledRoomAsync` currently emits nothing and writes neither `StartDate` nor `UpdatedAt`. The go-live moment exists nowhere in the system (Finding C).

**Payload**

```
roomId                Guid
wasScheduled          bool
minutesLateVsSchedule int    UtcNow − Room.StartDate; 0 for live-created rooms
remindersSet          int    count of RoomReminder rows for this room
category              string
selectionMode         string
stageCapacity         int
totalCapacity         int
durationHours         int
```

**Idempotency** — `eventKey = "room_went_live:{roomId}"`. **FACT** — `StartScheduledRoomAsync` already guards against re-starting a Live room, but the deterministic key makes at-most-once enforceable at the database rather than relying on that guard, which is the general rule TRUST-10 establishes.

**INFERENCE** — this single event supplies the actual start time that TRUST-09 needs, the scheduled-to-live conversion rate that GAP-07 needs, and the reminder-conversion denominator that GAP-09 needs. It is the highest leverage-per-line event in the P0 set.

**Failure behaviour** — data loss risk **MEDIUM**. A dropped event leaves a room with no recorded start, making its duration uncomputable. **RECOMMENDATION** — aggregation falls back to `MIN(RoomParticipant.JoinedAt)` and flags the row as estimated rather than dropping the room.

---

## E-10 — `user_status_changed`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | An admin changes a user's verification status. |
| **Producer** | `AdminService.ChangeUserStatusAsync` — `AdminService.cs:77`, after `UpdateAsync` succeeds (`AdminService.cs:135`) |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | AT-LEAST-ONCE |

**Payload**

```
fromStatus       string  the status before the change
toStatus         string
changedByAdminId Guid
isBulkOperation  bool
reason           string?
```

**FACT — a required signature change, precisely localised.** `IAdminService.ChangeUserStatusAsync(Guid userId, UserStatus newStatus)` (`IAdminService.cs:13`) has no `adminId` parameter. The controller *has* the value in both paths — `AdminController.cs:54` (single) and `AdminController.cs:92` (bulk) — and `BulkChangeUserStatusAsync` receives it but does not forward it at `AdminService.cs:289`.

**RECOMMENDATION** — add `Guid adminId` and `bool isBulk = false` to the interface method. This touches: one interface, one implementation, two call sites. **INFERENCE** — `07-decision-framework.md` recorded reviewer identity as "not recorded anywhere," implying it was unavailable. It is available and dropped at exactly one boundary, which makes this a materially smaller change than previously assumed.

**INFERENCE** — this one event closes three gaps: historical status reconstruction (TRUST-02), backlog history (GAP-05), and reviewer consistency (GAP-08).

**Failure behaviour** — data loss risk **HIGH**. **FACT** — `ApplicationUser` has no `UpdatedAt` and there is no status-history table, so this event is the *only* record of the transition. A dropped event is permanently unrecoverable. **RECOMMENDATION** — this is the strongest single argument for the TRUST-07 dead-letter work; it is the one event whose loss cannot be reconstructed from any other source.

---

## E-11 — `room_ended` (EXTEND)

**FACT** — exists at `RoomService.cs:543` with `{ roomId, durationHours, participantCount }`, where `durationHours = (UtcNow − room.StartDate).TotalHours` and `StartDate` is the *scheduled* time.

**Added properties**

```
actualDurationSeconds int     OccurredAtUtc − room_went_live.OccurredAtUtc
endReason             string  host_ended | host_disconnected | expired
peakParticipants      int
speakerCount          int     distinct non-host mic activators
```

**INFERENCE** — `actualDurationSeconds` derived from the paired `room_went_live` event is self-verifying: the two timestamps can be cross-checked, which is what makes the replacement duration trustworthy rather than merely asserted (M-401 validation).

---

## E-12 — `room_joined` (EXTEND)

**FACT** — exists at `RoomHub.cs:270` with `{ roomId }` only.

**Added properties**

```
entrySource string  feed | reminder_push | deep_link | profile | search | direct
isHost      bool    userId == room.HostId
isRejoin    bool    participant.Status was Left before this call
```

**RECOMMENDATION — the highest-value extension in the programme.** `entrySource` converts every discovery question (GAP-09) from unanswerable to trivial, on an event that already fires in a code path that already runs.

**FACT — both new booleans are free at the emit point.** `RoomHub.JoinRoom` already loads `room` (so `room.HostId == userId` is in hand) and already branches on `participant.Status == ParticipantStatus.Left` at `RoomHub.cs:245`. No additional query is required.

**Client dependency** — `entrySource` must be supplied by the Flutter client as a `JoinRoom` parameter. **RECOMMENDATION** — default to `"direct"` when absent so the server-side properties ship independently of the client release. **INFERENCE** — `isHost` alone is worth deploying immediately: it makes the host exclusion required by INV-7 a column filter rather than a join, which simplifies every room metric.

---

# P1 — Discovery, Delivery & Safety

---

## E-13 / E-14 — `reminder_set` / `reminder_removed`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | `RoomReminder` row created / deleted |
| **Producer** | `RoomService.ToggleReminderAsync` — `RoomService.cs:~490-510` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per toggle |

**FACT** — the method already branches cleanly: an existing reminder is removed, otherwise one is added. Both branches are single-line emit sites.

**Payload**

```
roomId          Guid
hoursUntilStart double   Room.StartDate − UtcNow
roomCategory    string
hoursHeld       double   (removal only)
```

**INFERENCE** — because un-toggled reminders are hard-deleted, the current `RoomReminder` table is a snapshot of *surviving* intent. Reminder-conversion computed from it therefore reads optimistically by an unknown margin. These two events convert it from a snapshot into a log, which is what makes the conversion rate honest.

---

## E-15 / E-16 — `push_send_attempted` / `push_send_result`

| Field | Value |
|---|---|
| **Event Type** | **SYSTEM EVENT** |
| **Trigger** | Immediately before the FCM call; immediately after the response |
| **Producer** | `PushNotificationService.SendPushNotificationAsync` |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per notification, keyed on `notificationId` |

**Payload — attempted**

```
notificationId   Guid    Notification.Id — the correlation key
notificationType string  NotificationType
hasToken         bool
targetUserId     Guid
```

**Payload — result**

```
notificationId   Guid
success          bool
errorCode        string?
tokenInvalidated bool
latencyMs        int
```

**Correlation** — `CorrelationId = notificationId` on both, forming the chain `attempted → result → notification_opened`.

**INFERENCE** — this is a regression guard, not a growth metric. Commit `dc1c933` fixed *reversed FCM delivery*; an identical regression today would be invisible to the dashboard and would surface only through user complaints, exactly as it did the first time.

**Failure behaviour** — data loss risk **LOW** individually; the rate metric (M-602) tolerates sampling loss.

---

## E-17 — `moderation_action_taken`

| Field | Value |
|---|---|
| **Event Type** | **DOMAIN EVENT** |
| **Trigger** | An admin acts on a report |
| **Producer** | `SupportService` — the admin report-action path |
| **Authority** | **SERVER AUTHORITATIVE** |
| **Idempotency class** | EXACTLY-ONCE per `(reportId, action)` |

**Payload**

```
reportId      Guid
action        string  WarnUser | Mute24h | BanUser | RejectReport
targetUserId  Guid
byAdminId     Guid
hoursToAction double  UtcNow − Report.CreatedAt
reportCategory string
```

**Correlation** — `CorrelationId = reportId`, chaining `user_reported → moderation_action_taken → subsequent behaviour`.

**FACT** — `AdminReportAction` outcomes currently mutate user state and are never recorded, so enforcement effectiveness and recidivism are unmeasurable.

---

## E-18 / E-19 — `app_session_started` / `app_session_ended`

| Field | Value |
|---|---|
| **Event Type** | **ANALYTICS EVENT** |
| **Trigger** | App foreground / background or close |
| **Producer** | Flutter client → `POST api/events/track` |
| **Authority** | **CLIENT OBSERVED** |
| **Idempotency class** | EXACTLY-ONCE per client `sessionId` |

**Payload**

```
sessionId       Guid    client-generated, persisted in app storage (NOT a cookie)
deviceId        string
platform        string  ios | android
appVersion      string
durationSeconds int     (ended only)
```

**FACT — why this replaces `session_started`.** The existing event is keyed on the `CocorraSessionId` cookie (`HttpOnly`, `Secure`, `SameSite=Strict`, 7-day expiry) set by `SessionTrackingMiddleware`, with deduplication in in-process `IMemoryCache` lost on every restart. The client is a Flutter mobile app.

**INFERENCE** — the correct identity for a mobile session is authenticated user + device + client-generated session id persisted in app storage. Cocorra already collects device metadata for `BlockedDevices`, so the concept exists in the system.

**RECOMMENDATION** — run both signals in parallel through Phase B of the rollout (`21-`) and compare. Deprecate `session_started` only once the new signal is demonstrably better, not on the strength of the inference alone.

**Failure behaviour** — **client-emitted, therefore lossy by nature.** `app_session_ended` will be missing whenever the app is force-quit or crashes, so session durations are biased toward sessions that ended cleanly. **This bias must appear in the metric contract**, not be discovered later.

**Allowlist (FACT)** — both must be added to `ClientAllowedEvents` in `EventsController.cs:22-27`.

---

# P2 — Discovery & Social Context

Specified more briefly; the pattern is established above.

| Event | Type | Producer | Authority | Key payload | Idempotency |
|---|---|---|---|---|---|
| **E-20** `room_feed_viewed` | ANALYTICS | Flutter → `EventsController` | CLIENT OBSERVED | `roomIdsShown[]`, `filterApplied`, `resultCount` | AT-LEAST-ONCE |
| **E-21** `room_detail_viewed` | ANALYTICS | Flutter → `EventsController` | CLIENT OBSERVED | `roomId`, `sourceSurface`, `feedPosition` | AT-LEAST-ONCE |
| **E-22** `message_sent` (EXTEND) | DOMAIN | `ChatService.SaveMessageAsync:92` | SERVER | add `originSurface` (room \| friends_list \| profile), optional `roomId` | NATURALLY-UNIQUE |
| **E-23** `friend_request_sent` (EXTEND) | DOMAIN | `FriendService.SendFriendRequestAsync:132` | SERVER | add `originSurface`, optional `sharedRoomId` | NATURALLY-UNIQUE |
| **E-24** `friend_request_rejected` | DOMAIN | `FriendService.RespondToFriendRequestAsync` | SERVER | `senderId`, `hoursToRespond` | EXACTLY-ONCE |
| **E-25** `user_unblocked` | DOMAIN | `BlockService.UnblockUserAsync` | SERVER | `blockedUserId`, `daysBlocked` | EXACTLY-ONCE |
| **E-26** `room_group_message_sent` | DOMAIN | `RoomHub.SendRoomGroupMessage:654-694` | SERVER | `roomId`, `messageLength`, `isOnStage`, `secondsSinceJoin` | NATURALLY-UNIQUE |
| **E-27** `registration_started` | ANALYTICS | Flutter → `EventsController` | CLIENT OBSERVED | `platform`, `appVersion`, `referralSource` | AT-LEAST-ONCE |

**FACT — E-22 rationale.** `RoomHub.SendRoomPrivateMessage` and `ChatHub.SendMessage` both call `ChatService.SaveMessageAsync`, which emits an identical `message_sent` with only `{receiverId}` (`ChatService.cs:92`). The two surfaces are indistinguishable in the data, which is exactly the distinction a messaging investment decision needs.

**RECOMMENDATION — E-26 sequencing.** Before implementing, run the cheap existence check from `05-analytics-gap-analysis.md` GAP-15 to establish whether in-room chat volume is material. If negligible, the gap closes without code. If substantial, the metric matters *and* the Active-vs-Passive interpretation needs revisiting. **Never persist message content** — only the metadata above. `UserEvent.PropertiesJson` carries an explicit model-level warning against storing message bodies.

---

# Ordering Requirements

**Ordering matters for exactly three chains.** Everywhere else, `OccurredAtUtc` alone is sufficient.

| Chain | Events | Strategy |
|---|---|---|
| **Stage funnel** (M-400) | `room_joined` → `hand_raised` → `stage_promoted` → `mic_activated` | Timestamp comparison per `(RoomId, UserId)`. Single API instance means one clock, so no sequence number is required. |
| **Mic segments** (M-401) | `mic_activated` → `mic_deactivated` | Pair by `(RoomId, UserId)` in timestamp order. **Must tolerate an orphan deactivation** for the host's initial mic, which has no matching activation. |
| **Notification chain** (M-602) | `push_send_attempted` → `push_send_result` → `notification_opened` | Explicit `CorrelationId = notificationId`. Timestamps are insufficient here because the open can arrive hours later, from a different process, out of order. |

**FACT** — Cocorra runs a single API container (`docker-compose.yml`), so there is one server clock and no cross-instance skew. **INFERENCE** — this is why no sequence-number infrastructure is needed today. It becomes a genuine problem on horizontal scaling, alongside the existing `RoomHub._connections` and session-dedup constraints. Recorded in `24-dependency-graph.md` as a scaling blocker rather than a current one.

---

# Failure Behaviour — Global Contract

**INV-1 restated as an implementation rule.** For every event in this document:

| Aspect | Contract |
|---|---|
| **Product action** | Completes regardless of analytics outcome. No emit call may be inside a domain transaction, and no emit failure may propagate. |
| **Analytics failure** | `EventTracker.Track` swallows all exceptions (**FACT** — existing try/catch with the comment *"Tracking must NEVER throw back to the user"*). A full channel drops the event and logs a warning. |
| **Retry** | None at the emit site. `EventFlushService` retries persistence with bounded backoff, then dead-letters (TRUST-07). |
| **Emit timing** | **After** the domain write succeeds (INV-2). The single exception is `speaker_time_exhausted`, where the rejection *is* the fact being recorded. |

## Data-loss risk by event

| Risk | Events | Rationale |
|:--:|---|---|
| **HIGH** | `user_status_changed` | The only record of the transition; `ApplicationUser` has no `UpdatedAt` and no history table exists. Unrecoverable if lost. |
| **MEDIUM** | `room_went_live`, `mic_deactivated`, `stage_promoted` | Break a chain or make a duration uncomputable. Partially recoverable by fallback (e.g. `MIN(JoinedAt)`). |
| **LOW** | All others | Undercount a rate without breaking a chain. |

**RECOMMENDATION** — dead-lettering (TRUST-07) should be prioritised by this table. If it must ship incrementally, `user_status_changed` is the event that justifies it.

---

# Implementation Summary

| Group | Events | Producers touched | New client work? |
|---|---|---|:--:|
| **P0 core loop** | E-01 … E-08 | `RoomHub` (8 methods), `RoomService` (2 close sites) | No |
| **P0 lifecycle** | E-09 … E-12 | `RoomService`, `AdminService` (+ signature), `RoomHub` | `entrySource` only |
| **P1 delivery & safety** | E-13 … E-19 | `RoomService`, `PushNotificationService`, `SupportService`, `EventsController` allowlist | Session events |
| **P2 discovery & social** | E-20 … E-27 | `ChatService`, `FriendService`, `BlockService`, `RoomHub`, allowlist | Feed/detail/registration |

**INFERENCE — three observations for sequencing.**

**All eight P0 core-loop events land in `RoomHub`**, in methods that already save to the database and already have `IEventTracker` injected. They are a single, coherent, self-contained change with no client dependency — which makes them both the highest-value and the lowest-coordination work in the programme.

**Only one event requires a signature change**: `user_status_changed` needs `adminId` threaded into `ChangeUserStatusAsync`. Everything else emits from data already in scope at the emit point.

**`EventId` with a unique constraint blocks the retry work.** Retry without it would actively create duplicates rather than preventing loss, which is why `16-` treats it as the first schema change rather than one of several.
