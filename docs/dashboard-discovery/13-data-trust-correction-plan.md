# 13 — Data Trust Correction Plan

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 2
> **Depends on**: `11-current-state-validation.md`, `12-target-analytics-architecture.md`
> **Scope**: Documentation only. No code, schema, or migrations were produced.

---

## Purpose

Every confirmed P0 trust issue, with its exact code cause, the metrics it contaminates, a correction strategy, a decision on the historical data, its implementation dependencies, and the test that proves the correction worked.

**Correction strategies:**

| Strategy | Meaning |
|---|---|
| **FIX** | The metric's intent is right; the implementation is wrong. Correct the computation. |
| **REDEFINE** | The metric measures something real but is named or scoped misleadingly. Restate what it means. |
| **DEPRECATE** | The metric cannot be made correct with available data. Remove it. |
| **REPLACE** | Retire the metric and introduce a different, sounder one serving the same decision. |

**Historical data impact:**

| Verdict | Meaning |
|---|---|
| **KEEP** | Existing stored values remain valid. |
| **RECALCULATE** | Values are recomputable from data that still exists. |
| **MARK AS UNRELIABLE** | Values stay queryable but must be flagged; a corrected series starts at a cutover date. |
| **CANNOT RECOVER** | The underlying data was never captured or has been destroyed. |

---

# TRUST-01 — Host microphone open from room start

> The only place in Cocorra's analytics where two shipped metrics contradict each other about the same person at the same moment.

## Problem

**FACT** — A room host who never touches their microphone accumulates `RoomParticipant.TotalSpokenSeconds` equal to the room's entire wall-clock life (7,200 or 10,800 seconds, since `AllowedDurations` permits only 2 or 3 hours — `RoomService.cs:73`). The same host emits **no** `mic_activated` event.

Consequently:
- **Top Speakers** ranks hosts at the top, ordered by how long their rooms ran.
- **Active vs Passive** counts those same hosts as passive listeners.

## Root Cause

Two code facts in combination.

**Cause 1 — the host is inserted with an open mic.** `RoomService.cs:115-127` (room created Live) and `RoomService.cs:439-449` (`StartScheduledRoomAsync`):

```csharp
var hostParticipant = new RoomParticipant
{
    UserId        = hostId,
    Status        = ParticipantStatus.Active,
    IsOnStage     = true,
    IsMuted       = false,          // ← mic registered as open
    JoinedAt      = DateTime.UtcNow,
    LastUnmutedAt = DateTime.UtcNow // ← accrual starts here
};
```

Accrual is then closed by `EndRoomAsync` (`RoomService.cs:526-538`) or `LeaveRoomCleanupAsync` (`RoomService.cs:556-576`), both of which add `(UtcNow − LastUnmutedAt)` to `TotalSpokenSeconds`.

**Cause 2 — the opening transition emits nothing.** `RoomHub.ToggleMic` (`RoomHub.cs:518-521`) emits only on `IsMuted: true → false`:

```csharp
if (muteStatus == false && participant.IsMuted == true)
{
    participant.LastUnmutedAt = DateTime.UtcNow;
    _eventTracker.Track(EventTypes.MicActivated, userId, new { roomId = roomGuid });
}
```

The host is created at `IsMuted = false`, so this branch never runs for the initial state.

## Affected Metrics

| Metric | Source | Effect |
|---|---|---|
| Top Speakers | `AnalyticsRepository.cs:166-231` | **Invalid.** Ranks hosts by room duration. |
| Avg Spoken Time / Total Spoken Hours | same | **Inflated** by the full duration of every room. |
| Users Who Spoke (`TotalSpokenSeconds > 0`) | same | **Inflated** by one host per room. |
| Active vs Passive | `AnalyticsRepository.cs:501-540` | **Deflated.** Every room adds one artificial passive listener. |
| Platform Summary | composes Participation | Inherits both. |
| `speaking_time_logged` payload | `RoomService.cs:549` | Carries the contaminated cumulative total. |

**INFERENCE** — the Active-vs-Passive bias is relative to room size. With five participants, one artificial passive listener moves the reported active rate by 20 percentage points. At Cocorra's current scale this is not a rounding error.

## Correction Strategy

Three metrics, three different strategies.

| Metric | Strategy | Rationale |
|---|:--:|---|
| Top Speakers | **DEPRECATE**, then **REPLACE** | No available data measures genuine speaking. Remove it now; reintroduce as *Non-Host Speaking Minutes* once `mic_deactivated` exists. |
| Active vs Passive | **FIX** | Sound once hosts are excluded from the denominator. |
| Users Who Spoke | **REDEFINE** | Rename to *Non-Host Participants Who Activated a Mic* and compute from `mic_activated`, not `TotalSpokenSeconds`. |

### Correction in two stages

**Stage 1 — analysis-layer only, no application change.** Exclude the host from every room-participation metric by joining `RoomParticipant.UserId`/`UserEvents.UserId` against `Room.HostId` and dropping the match. This removes the contradiction using data that already exists.

**Stage 2 — emit `mic_deactivated`** with `segmentSeconds` and `isHost`, converting a mutable running total into a per-segment ledger and making the host's initial open-mic state explicit rather than invisible.

## Historical Data Impact

| Data | Verdict | Reason |
|---|:--:|---|
| `RoomParticipant.TotalSpokenSeconds` for hosts | **CANNOT RECOVER** | The value conflates real speaking with idle open-mic time. No stored field separates them. |
| `RoomParticipant.TotalSpokenSeconds` for non-hosts | **KEEP** | Non-hosts only accrue after an explicit unmute. Still unmuted-time rather than audio (Finding B), which the metric contract must state. |
| `mic_activated` events | **KEEP** | Correct as emitted; never fired for the host's initial state, which is now a documented exclusion rather than a silent gap. |
| Historical Active-vs-Passive series | **RECALCULATE** | Both inputs are events with `RoomId`; excluding hosts is a join, applied retroactively across the full 180-day window. |
| Historical Top Speakers | **CANNOT RECOVER** | — |

## Implementation Dependencies

- **Stage 1**: none. Pure query change.
- **Stage 2**: `mic_deactivated` event (`15-`), `EventId` idempotency column (`16-`).
- **Stage 2 replacement metric**: aggregation layer (`17-`) for non-host speaking minutes.

## Validation

1. **Unit** — a fixture room with one silent host and two speakers; assert the host is absent from Top Speakers' successor and present in neither side of a mis-stated Active/Passive split.
2. **Query assertion** — for every room, `COUNT(participants) − COUNT(DISTINCT mic_activated users) ≥ 1` must **no longer** hold trivially because of the host.
3. **Cross-metric consistency check (the decisive test)** — no `UserId` may simultaneously appear in the top-speakers result and the passive-listener set for the same window. **INFERENCE** — this single assertion is the direct executable statement of the contradiction, and it is the test that proves TRUST-01 is closed.
4. **Regression** — golden-file test over a fixed event fixture; any formula change that moves the numbers must be an explicit, reviewed diff.

---

# TRUST-02 — User growth history rewritten by current status

## Problem

**FACT** — `GET /Api/V1/Analytics/Users/Growth` buckets users by `CreatedAt` but counts them by their **current** `Status`. A user who registered in January and was banned in June is reported as Banned in January's bucket.

## Root Cause

**FACT** — `AnalyticsRepository.GetUserGrowthAsync` (`AnalyticsRepository.cs:21-93`) selects `CreatedAt, Status, MBTI, Age`, materialises with `.ToList()`, then groups client-side by date bucket and counts by the `Status` value carried on the row — which is the value as of query time.

**FACT — the deeper cause.** `ApplicationUser` does not extend `BaseEntity` and has no `UpdatedAt`. There is no status-history table. The row holds only the latest state, so no query over `AspNetUsers` alone can reconstruct a past status.

## Affected Metrics

| Metric | Effect |
|---|---|
| User Growth — status breakdown | **Invalid** for every historical bucket |
| User Growth — registration counts | **Valid** — `CreatedAt` bucketing is correct |
| Platform Summary | Inherits the invalid breakdown |
| MBTI distribution / average age | Window-scoped rather than population-wide; misleading label, not a wrong number |

**INFERENCE — why this is worse than a plain error.** The distortion is time-dependent. Recent buckets look accurate because few of their users have changed status yet; older buckets are heavily rewritten. The chart therefore shows a systematic downward slope in "Active" across older cohorts that is purely an artefact of elapsed time. Its most natural reading — *"our early users were lower quality"* — is false, and it is the reading almost any viewer will reach.

## Correction Strategy

**Split the metric.**

| Component | Strategy |
|---|:--:|
| Registration count per bucket | **KEEP** — extract as its own VERIFIED metric |
| Status breakdown per bucket | **REPLACE** — recompute from `voice_verification_result` events |
| MBTI / average age | **REDEFINE** — label explicitly as *"users registered in this window"* |

**Replacement definition** — status-at-time is reconstructed by taking, for each user and each bucket boundary, the most recent `voice_verification_result` event at or before that boundary; users with no such event are `Pending`. This is correct within the raw-event retention window and nowhere else.

## Historical Data Impact

| Data | Verdict | Reason |
|---|:--:|---|
| Registration counts | **KEEP** | `CreatedAt` is immutable and indexed |
| Status breakdown, within 180 days | **RECALCULATE** | Reconstructable from `voice_verification_result` |
| Status breakdown, beyond 180 days | **CANNOT RECOVER** | Events purged; `ApplicationUser` has no history |
| Registration counts for deleted users | **CANNOT RECOVER** | Hard-deleted rows (TRUST-05) |

**RECOMMENDATION** — the reconstructed status series must carry an explicit start date. Presenting a series that silently changes methodology at the 180-day boundary would reproduce the original defect in a subtler form.

## Implementation Dependencies

- Reconstruction: none — `voice_verification_result` events exist today.
- Durable long-term fix: `user_status_changed` event (`15-`) plus `DailyStateSnapshots` (`17-`), so future status history survives the raw purge.

## Validation

1. **Unit** — fixture user registered in month 1, status-changed in month 3; assert month 1 reports the status held in month 1.
2. **Reconciliation** — reconstructed *current* status (latest event per user) must equal `AspNetUsers.Status` for every user with at least one status event. A mismatch indicates a missing emit site.
3. **Monotonicity** — cumulative registrations must be non-decreasing over time. **INFERENCE** — a decrease is a direct detector for TRUST-05 (hard deletes) and is worth asserting continuously, not once.
4. **Boundary** — querying a window that starts before the retention cutoff must return the explicit start date, not silently truncated data.

---

# TRUST-03 — Retention calculation wrong in two independent ways

## Problem

**FACT** — `GET /Api/V1/Analytics/Retention` returns systematically understated retention, computed over an activity signal that may be near-meaningless on mobile.

## Root Cause

**Cause 1 — exact-day matching.** `AnalyticsRepository.GetRetentionCohortAsync` (`AnalyticsRepository.cs:324-392`):

```csharp
var timeDiff = e.OccurredAtUtc.Date - cohortDate.Date;
return timeDiff.Days == day;
```

A user active on days 2, 3, and 5 but not day 1 contributes **zero** to D1 retention. Standard practice is "active on day N or later," or a window around N.

**Cause 2 — the activity signal.** The default `activeEvent` is `session_started`, emitted by `SessionTrackingMiddleware:53` and keyed on the `CocorraSessionId` cookie (`HttpOnly`, `Secure`, `SameSite=Strict`, 7-day expiry). The client is a Flutter mobile app; cookie persistence across launches depends entirely on the HTTP client's cookie jar. Deduplication uses in-process `IMemoryCache` with a 1-day TTL, lost on every restart.

**Cause 3 — an unbounded activity fetch.** The activity query has no upper time bound and loads every matching event for every cohort user into memory.

## Affected Metrics

Retention D1/D7/D30, and any downstream reasoning about churn or engagement.

## Correction Strategy

**REPLACE.** Not a repair — a different, better metric.

**FACT** — `room_joined` is server-emitted (`RoomHub.cs:270`), carries an indexed `RoomId`, is independent of cookies, and was marked **VERIFIED** by `07-metric-verification.md`.

**Replacement — Weekly Return Rate:**

> Of users with a `room_joined` event in week *N*, the share with a `room_joined` event in any week after *N*.

Three advantages, each addressing one cause: it uses "in a later week" not "exactly day N"; it depends on no cookie; and it measures return to the product's actual value event rather than return to the app.

**RECOMMENDATION** — fixing `== day` to `>= day` while leaving `session_started` as the signal would be a false repair. It would produce a plausible number resting on an unvalidated signal, which is exactly the failure mode this programme exists to eliminate.

## Historical Data Impact

| Data | Verdict | Reason |
|---|:--:|---|
| Existing retention outputs | **MARK AS UNRELIABLE** | Not stored; computed per request. No stored artefact to correct. |
| Room-join-based return, within 180 days | **RECALCULATE** | Fully computable from existing events — a baseline is available immediately |
| Return rate beyond 180 days | **CANNOT RECOVER** | Events purged |
| Any return rate, all periods | **Biased upward** | Hard deletes remove the most-churned users (TRUST-05). Must appear in the metric contract's `Known Limitations`. |

## Implementation Dependencies

- Replacement metric: none. Computable today.
- Reliable *general* (non-room) activity: `app_session_started` (`15-`), which requires a Flutter release.
- Unbiased return: soft delete (TRUST-05).

## Validation

1. **Unit** — a user active on days 2 and 5 must count toward D1 retention under the "or later" definition. This is the direct regression test for the old bug.
2. **Comparison** — run old and new definitions over the same cohort; the new value must be ≥ the old. **INFERENCE** — a new value *below* the old would mean the replacement introduced a different error, since "or later" is strictly more inclusive than "exactly N."
3. **Independence** — assert the new computation reads no `session_started` rows at all.
4. **Bound** — assert the activity query carries an upper time bound (regression against Cause 3).

---

# TRUST-04 — Hand-raise metric is a live boolean

## Problem

**FACT** — `UsersWhoRaisedHand` counts `RoomParticipant.IsHandRaised == true` at the instant of the query. `RoomHub.LowerHand` (`RoomHub.cs:402-419`) resets it to `false`, and `EndRoomAsync` resets it for every participant. For any historical window the count is effectively always ~0.

## Root Cause

**FACT** — `RoomHub.RaiseHand` (`RoomHub.cs:381-400`) writes the boolean and broadcasts to the group but emits **no** `UserEvent`. The transition is never recorded; only the current state exists.

## Affected Metrics

`UsersWhoRaisedHand`, Platform Summary (inherits it), and — more importantly — the entire stage-demand analysis that would explain movements in Speaking Conversion.

## Correction Strategy

**DEPRECATE**, then **REPLACE**.

- Remove `UsersWhoRaisedHand` from the response. It answers no question correctly.
- Introduce `hand_raised` / `hand_lowered` events and rebuild the metric as *Hand Raises per Room* and *Hand-Raise → Stage Promotion Rate*.

## Historical Data Impact

| Data | Verdict |
|---|:--:|
| All historical hand-raise counts | **CANNOT RECOVER** |
| Current `IsHandRaised` values | **KEEP** as live state only — valid for the live room UI, never for analytics |

**INFERENCE** — this is the clearest instance of the snapshot-versus-history pattern. No amount of query correction recovers it, because the data was never written. It is a pure instrumentation gap.

## Implementation Dependencies

`hand_raised` and `hand_lowered` events; `stage_promoted` for the conversion rate.

## Validation

1. **Event test** — `RaiseHand` produces exactly one `hand_raised` event with `roomId` promoted to the indexed column.
2. **Lifecycle test** — raise → lower → raise produces two `hand_raised` and one `hand_lowered`, in order.
3. **Contract test** — the deprecated field no longer appears in the API response (a consumer relying on it must fail loudly rather than silently read zero).

---

# TRUST-05 — Hard deletes destroy churn evidence

> The only correction where **delay has an irreversible cost**.

## Problem

**FACT** — `AuthServices.DeleteAccountAsync` hard-deletes the `ApplicationUser` row. **FACT** — `UserEvent.UserId` and `Report.ReportedUserId` are configured `OnDelete(DeleteBehavior.SetNull)`, so associated rows survive but are anonymised.

## Root Cause

A data-model decision: deletion is physical rather than logical.

## Affected Metrics

| Metric | Effect |
|---|---|
| All retention and return rates | **Biased upward** — computed only over survivors |
| Registration history | **Decreases retroactively** as users delete |
| Most Reported Users | Loses reported users who delete (`SetNull`) |
| Cohort analysis | The most-churned members are absent from every cohort |
| Any churn analysis | **Structurally impossible** |

**INFERENCE** — this is not one broken metric but a bias applied to every longitudinal analysis Cocorra will ever run. The users most worth understanding — those who disliked the product enough to erase themselves — are precisely those guaranteed absent.

**INFERENCE — a second-order effect worth naming.** Because `Report.ReportedUserId` is `SetNull`, a user with an adverse moderation history can erase it by deleting their account. That is a moderation integrity issue, not only an analytics one.

## Correction Strategy

**FIX** — logical deletion.

- Add `IsDeleted` and `DeletedAt` to `ApplicationUser`.
- Scrub personal fields in place (name, email, bio, profile picture, voice path, FCM token).
- Retain `Id`, `CreatedAt`, `Status`, and behavioural foreign keys.
- Apply a global query filter so all existing product queries exclude deleted users by default; analytics opts in explicitly.

**RECOMMENDATION — the blocking question is legal, not technical.** Whether scrub-in-place satisfies Cocorra's deletion obligations must be decided by whoever owns data protection. This is the one P0 item that cannot be unblocked by engineering alone, and it should be raised immediately rather than at implementation time, because the cost of waiting compounds daily.

**Interim (FACT)** — `account_deleted` (`AuthServices.cs:565`, with `{reason}`) already survives deletion and provides a partial anonymised record. It supports a deletion *count* today, but not cohort attribution.

## Historical Data Impact

| Data | Verdict | Reason |
|---|:--:|---|
| Already-deleted users | **CANNOT RECOVER** | Rows physically gone |
| Their events | **PARTIALLY** available with `UserId = NULL` — countable, not attributable |
| Deletion count over time | **RECALCULATE** from `account_deleted` events, within 180 days |
| Future churn | **KEEP** once implemented |

## Implementation Dependencies

1. Legal decision on scrub-in-place (**blocking**).
2. Global query filter on `ApplicationUser`, plus an audit of every query that must opt out.
3. `DeleteAccountAsync` rewrite.

**INFERENCE — the risk that makes this MEDIUM rather than LOW effort.** A global query filter changes the behaviour of *every* existing query touching `ApplicationUser`, including `UserManager` operations. Login, role checks, friend search, and admin listings must all be verified against it. The analytics benefit is large; the blast radius is wider than the analytics surface, and the implementation must be scoped accordingly.

## Validation

1. **Unit** — after deletion, the row exists with `IsDeleted = true` and personal fields nulled.
2. **Filter** — default queries exclude deleted users; an explicit `IgnoreQueryFilters()` path includes them.
3. **Monotonicity (the key regression)** — cumulative registration counts must never decrease across two runs separated by a deletion.
4. **Referential** — `Report.ReportedUserId` and `UserEvent.UserId` retain their values after a deletion, rather than becoming NULL.
5. **Privacy** — an integration test asserting no personal field survives; this is the test that makes the legal position auditable.

---

# TRUST-06 — Funnel is not sequential

## Problem

**FACT** — `GET /Api/V1/Analytics/Funnel` counts each step independently. It can report a later step with **more** users than an earlier one — impossible in a real funnel, and a visible symptom of the defect.

## Root Cause

**FACT** — `AnalyticsRepository.GetFunnelAsync` (`AnalyticsRepository.cs:300-322`) executes a single `GROUP BY EventType` with `COUNT(DISTINCT UserId)` per type. There is no per-user ordering constraint linking step *N* to step *N+1*.

## Affected Metrics

The onboarding funnel, and every conclusion drawn about where users abandon the verification gate.

## Correction Strategy

**FIX.** The data fully supports a sequential funnel; only the query is wrong.

For each user, require step *N*'s `OccurredAtUtc` to precede step *N+1*'s. Report per-step conversion **and median elapsed time**.

**RECOMMENDATION** — elapsed time is not an optional extra. A step converting at 90% but taking 18 hours is a different problem from one converting at 60% instantly, and a conversion-only funnel renders them identically. For Cocorra specifically, the 18-hour case is the likely one, because one step is a human review queue.

## Historical Data Impact

| Data | Verdict |
|---|:--:|
| Funnel outputs | **MARK AS UNRELIABLE** — not stored; computed per request |
| Sequential funnel, within 180 days | **RECALCULATE** — fully computable now |
| Beyond 180 days | **CANNOT RECOVER** |

## Implementation Dependencies

None for correction. `registration_started` (`15-`) would extend the funnel above the first server-side step but is not required.

## Validation

1. **Monotonicity (the definitive test)** — for any input, each step's count must be ≤ the previous step's. A violation means the ordering constraint is not applied. This assertion is impossible to satisfy under the current implementation, which makes it a precise acceptance criterion.
2. **Ordering** — a user with `activation_completed` *before* `email_confirmed` must not count toward the later step.
3. **Comparison** — sequential counts must be ≤ current independent counts at every step.

---

# TRUST-07 — Event pipeline loses events silently, twice

> Not a metric defect. A correctness precondition for every event-derived metric.

## Problem

**FACT** — two independent silent loss paths.

**Path 1 — channel overflow.** `Program.cs:210-211` — bounded 10,000 with `BoundedChannelFullMode.DropWrite`. On overflow `TryWrite` returns false and the event is discarded with a warning.

**Path 2 — flush failure.** `EventFlushService.cs`:

```csharp
catch (Exception dbEx) { _logger.LogError(dbEx, "Failed to persist batch of {BatchCount} user events.", batch.Count); }
finally { batch.Clear(); }
```

`batch.Clear()` runs on the failure path. Any transient DB fault — connection blip, deadlock, timeout, failover — permanently discards up to 100 events. No retry, no dead-letter, no checkpoint.

## Root Cause

The pipeline was designed to protect the product action from analytics failure (correct, and preserved), but not to protect analytics data from infrastructure failure.

## Affected Metrics

**Every event-derived metric**: funnel, retention, active rooms, peak hours, voice drop-off, active-vs-passive, and every metric proposed in `14-metric-contracts.md`.

**INFERENCE — why this outranks the individual metric fixes in sequencing.** A formula correction is worthless if the input is incomplete by an unknown amount. Worse, the loss is **correlated with load**: channel overflow happens during the busiest rooms, which are exactly the rooms most worth analysing. The bias is not random noise; it systematically removes peak activity.

## Correction Strategy

**FIX**, in three parts.

| Part | Change | Closes |
|---|---|---|
| **Retry** | Bounded retry with exponential backoff around `SaveChangesAsync`, distinguishing transient from permanent failures | Path 2 |
| **Dead-letter** | After exhausted retries, append the batch to a dead-letter sink instead of discarding it | Path 2 residual |
| **Observability** | Counters for dropped-on-enqueue, failed batches, and dead-lettered events, exposed as a metric | Both — makes loss visible |

**Idempotency prerequisite (FACT)** — retry is only safe if re-applying a batch cannot double-count. `UserEvent.Id` is a database identity assigned at insert, so a retried batch produces duplicate rows with distinct ids. `EventId` with a unique constraint (`16-`) is therefore a **hard dependency** of the retry work, not a parallel improvement.

**RECOMMENDATION on capacity** — do not raise the channel bound before measuring (R-1). Increasing capacity without knowing the current drop rate trades a known bounded loss for unbounded memory growth, and hides the signal that would tell you whether the change was needed.

## Historical Data Impact

| Data | Verdict | Reason |
|---|:--:|---|
| Existing `UserEvents` | **MARK AS UNRELIABLE** for absolute completeness | An unknown number of events were dropped. Every historical count is a lower bound. |
| Relative trends | **KEEP** | **INFERENCE** — if the drop rate is low and roughly stable, period-over-period comparison remains directionally valid even though absolute counts do not. R-2 tells you whether this assumption holds. |
| Lost events | **CANNOT RECOVER** | Never persisted |

## Implementation Dependencies

1. **R-1 and R-2** (runtime observation from `11-`) — size the problem before choosing retry parameters.
2. `EventId` + unique constraint — **blocking** for retry safety.
3. Dead-letter table or file sink.

## Validation

1. **Retry** — a mocked `DbContext` failing twice then succeeding must persist all events exactly once.
2. **Idempotency** — replaying an identical batch must not create duplicate rows. **Must run against SQLite in-memory or SQL Server**: `EFCore.InMemory` does not enforce unique indexes and would pass vacuously.
3. **Dead-letter** — a permanently failing context must route the batch to the sink with zero rows lost.
4. **Non-blocking contract** — assert `Track` still returns without throwing when the channel is full (regression against INV-1).
5. **Counters** — assert the drop counter increments on a forced overflow.

---

# TRUST-08 — SignalR events lack session context

## Problem

**FACT** — every event emitted from `RoomHub` (`room_joined`, `room_left`, `mic_activated`, and all eight proposed room events) is persisted with `SessionId = NULL`, `IpHash = NULL`, `UserAgent = NULL`.

## Root Cause

**FACT** — `EventTracker.Track` sources all three from `_httpContextAccessor.HttpContext` and skips them when it is null. In SignalR, `HttpContext` is available only during the initial negotiate request, not during hub method invocations.

**FACT — confirmed by the codebase itself.** `Cocorra.Tests/EventTrackingSmokeTests.cs:28-30`:

```csharp
// No HttpContext (as when firing from a SignalR hub) → enrichment is skipped,
// but userId/roomId still flow through explicitly.
```

## Affected Metrics

Any metric requiring session-scoped correlation across the room lifecycle. **FACT** — `UserEvent.SessionId` is documented as *"Groups events into a single app session for funnel analysis,"* and is always NULL for exactly the events a room funnel would use.

**INFERENCE** — this has not yet caused a visible defect only because no shipped metric uses `SessionId`. It would surface the moment session-scoped analysis is attempted, which the target architecture does attempt.

## Correction Strategy

**FIX** — pass context explicitly.

Add an overload accepting an explicit context (session id, correlation id), so hub call sites supply what `HttpContext` cannot. The session id is obtainable at hub connection time from the negotiate request and cached against the connection, alongside the existing `_connections` dictionary.

**RECOMMENDATION** — the existing 3-argument `Track` signature must remain, so all ~24 current call sites compile unchanged. This keeps the change additive and the diff reviewable.

**Design note (INFERENCE)** — this correction interacts with the `session_started` replacement (TRUST-03). If sessions move to a client-generated id persisted in app storage, the hub can receive it as a `JoinRoom` parameter, which is simpler and more reliable than caching negotiate-time state per connection. **RECOMMENDATION** — sequence the session-identity decision before this fix, or the work will be done twice.

## Historical Data Impact

| Data | Verdict |
|---|:--:|
| Existing room events | **CANNOT RECOVER** — `SessionId` was never captured |
| `UserId` and `RoomId` on those events | **KEEP** — always populated correctly |

## Implementation Dependencies

`IEventTracker` overload; session-identity decision (TRUST-03).

## Validation

1. **Unit** — a hub-context `Track` call with an explicit session id persists a non-null `SessionId`.
2. **Regression** — the existing 3-argument overload continues to behave identically with an HTTP context present.
3. **Coverage query** — after cutover, assert the share of `room_joined` events with a non-null `SessionId` exceeds a threshold; a sustained zero means the wiring is broken.

---

# TRUST-09 — Room duration is the configured value

## Problem

**FACT** — `AvgDurationHours` averages `Room.DurationHours`, which is the *configured* duration and can only be 2 or 3 (`AllowedDurations`, `RoomService.cs:73`). It is a near-constant presented as a measurement.

**FACT** — `room_ended` reports `durationHours = (DateTime.UtcNow - room.StartDate).TotalHours` (`RoomService.cs:543`), where `StartDate` is the **scheduled** time. For rooms created live this approximates reality; for scheduled rooms started late it overstates duration by the lateness — and nothing records which case applies.

## Root Cause

**FACT** — `StartScheduledRoomAsync` (`RoomService.cs:422-460`) sets `Status = Live` but emits no event and writes neither `StartDate` nor `UpdatedAt`. **FACT** — `Room.UpdatedAt` is never assigned anywhere (only three `UpdatedAt` writes exist solution-wide). There is no `StartedAt` or `EndedAt` column.

## Affected Metrics

`AvgDurationHours`, `room_ended.durationHours`, and any per-room engagement rate using duration as a denominator.

## Correction Strategy

**DEPRECATE**, then **REPLACE**.

- Remove `AvgDurationHours` — it measures a configuration setting, not behaviour.
- Add `room_went_live` (with `wasScheduled`, `minutesLateVsSchedule`) and extend `room_ended` with `actualDurationSeconds`.

**Interim (INFERENCE)** — actual start is approximable as `MIN(RoomParticipant.JoinedAt)` per room, because the host is inserted as a participant at start. Usable as a stopgap and must be labelled as a proxy; it is undocumented behaviour that a future change to host insertion would silently break.

## Historical Data Impact

| Data | Verdict |
|---|:--:|
| `AvgDurationHours` | **CANNOT RECOVER** — never measured actual duration |
| `room_ended.durationHours` for rooms created Live | **KEEP with caveat** — approximately correct, but which rooms qualify is not recorded |
| `room_ended.durationHours` for scheduled rooms | **MARK AS UNRELIABLE** — overstated by an unknown lateness |
| Actual start, within 180 days | **PARTIALLY RECONSTRUCTABLE** via `MIN(JoinedAt)` |

## Implementation Dependencies

`room_went_live` event; `room_ended` extension.

## Validation

1. **Unit** — a scheduled room started 40 minutes late emits `room_went_live` with `minutesLateVsSchedule ≈ 40`.
2. **Consistency** — `actualDurationSeconds` from `room_ended` must match `room_ended.OccurredAtUtc − room_went_live.OccurredAtUtc` within tolerance. **INFERENCE** — this cross-event check is what makes the new duration self-verifying rather than merely asserted.
3. **Contract** — the deprecated field is absent from the response.

---

# TRUST-10 — `activation_completed` deduplication races the async flush

## Problem

**FACT** — `AdminService.cs:141-147` guards emission by querying the `UserEvents` **table**:

```csharp
var alreadyActivated = await _context.UserEvents
    .AnyAsync(e => e.UserId == user.Id && e.EventType == EventTypes.ActivationCompleted);
if (!alreadyActivated) { _eventTracker.Track(EventTypes.ActivationCompleted, user.Id); }
```

`Track` only enqueues; the row does not exist until `EventFlushService` persists the batch. Two activations of the same user inside that window both observe `false` and both emit.

## Root Cause

A read-then-write guard against an **asynchronous** writer. The guard is correct in intent and unsound in mechanism.

## Affected Metrics

Voice Verification Drop-Off (completion rate), the onboarding funnel's final step, and any count of activated users.

**Scope (FACT)** — narrow. `BulkChangeUserStatusAsync` de-duplicates ids with `.Distinct()` and processes sequentially with `await`, so the bulk path cannot race itself. Exposure is two concurrent admin requests for the same user.

**INFERENCE — why it is documented despite low probability.** It establishes a general rule the new events must follow: **an emission guard must never depend on reading a table written asynchronously.** Several proposed events (`room_went_live`, `push_send_result`) have the same "emit at most once" requirement and would fail the same way if implemented by analogy with this code.

## Correction Strategy

**FIX** — replace the read-then-write guard with a deterministic idempotency key.

Derive `eventKey = $"activation_completed:{userId}"`, stamp it on `EventId` as a deterministic GUID, and let the unique constraint on `EventId` (`16-`) reject the duplicate at the database. This removes the race, removes a DB round-trip per activation, and generalises to every "at most once" event.

**INFERENCE** — this correction is genuinely cheaper than the current code as well as correct, because it eliminates the `AnyAsync` query on the activation path.

## Historical Data Impact

| Data | Verdict |
|---|:--:|
| Existing `activation_completed` events | **KEEP** — duplicates are possible but unlikely and detectable |
| Detection | A `GROUP BY UserId HAVING COUNT(*) > 1` query quantifies actual occurrences |

**RECOMMENDATION** — run that detection query during validation. It converts an inferred risk into a measured one, and if the count is zero the historical data needs no caveat at all.

## Implementation Dependencies

`EventId` column with a unique constraint (`16-`) — **blocking**.

## Validation

1. **Concurrency** — two parallel activations of the same user produce exactly one persisted event.
2. **Determinism** — the same logical event produces the same `EventId` across processes and restarts.
3. **Detection** — the duplicate-count query returns the historical incidence.

---

# Correction Summary

| ID | Issue | Strategy | Historical Impact | Blocking Dependency | Priority |
|---|---|:--:|:--:|---|:--:|
| **TRUST-01** | Host mic open from room start | DEPRECATE + FIX + REDEFINE | CANNOT RECOVER (hosts) / RECALCULATE (Active-Passive) | None for stage 1 | **P0** |
| **TRUST-02** | Growth history rewritten by current status | REPLACE + KEEP registrations | RECALCULATE ≤180d | None | **P0** |
| **TRUST-03** | Retention wrong twice over | REPLACE | RECALCULATE ≤180d | None | **P0** |
| **TRUST-04** | Hand-raise is a live boolean | DEPRECATE + REPLACE | CANNOT RECOVER | `hand_raised` event | **P0** |
| **TRUST-05** | Hard deletes destroy churn evidence | FIX (soft delete) | CANNOT RECOVER (past) | **Legal decision** | **P0** |
| **TRUST-06** | Funnel not sequential | FIX | RECALCULATE ≤180d | None | **P0** |
| **TRUST-07** | Pipeline loses events silently, twice | FIX | MARK AS UNRELIABLE (absolute) | `EventId` unique; R-1/R-2 | **P0** |
| **TRUST-08** | SignalR events lack session context | FIX | CANNOT RECOVER | Session-identity decision | **P1** |
| **TRUST-09** | Room duration is the configured value | DEPRECATE + REPLACE | CANNOT RECOVER | `room_went_live` | **P1** |
| **TRUST-10** | `activation_completed` dedup race | FIX | KEEP (verify incidence) | `EventId` unique | **P1** |

---

## Sequencing Consequences

**INFERENCE — three orderings follow from the dependency column and are not negotiable.**

**1. `EventId` with a unique constraint is the first schema change.** TRUST-07 (retry) and TRUST-10 (dedup) both require it, and retry without it would actively create duplicates. It gates the pipeline hardening that everything else depends on.

**2. Four corrections need no code change at all.** TRUST-01 stage 1, TRUST-02 (registration split), TRUST-03 (replacement metric), and TRUST-06 are query-layer corrections over data already verified as correct. They deliver the largest immediate trust improvement at zero deployment risk and should not wait behind the instrumentation work.

**3. TRUST-05 must be raised now, decided later.** It is the only P0 blocked on a non-engineering decision, and the only one where each day of delay permanently destroys evidence. Raising it at implementation time would be too late.
