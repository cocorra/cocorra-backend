# 27 — AN-041 `operation_failed`: Decision Record

> **Generated**: 2026-09-02 | **Item**: AN-041 — Failure-path events (P3)
> **Decision**: **IMPLEMENT, with the scope redefined.**

---

# 1. The state we found

`EventTypes.OperationFailed` was declared with a full doc comment and **zero emission sites**. `git grep "EventTypes.OperationFailed"` returned nothing outside its own declaration. It was the only event constant in the file that nobody fired.

**This is the worst of the four possible states.** Not "unfinished" — actively misleading:

- Its presence in `EventTypes.cs` implies coverage that does not exist.
- A query for `operation_failed` returns an empty set, and an empty set is **indistinguishable from "no operations failed"**.
- Anyone auditing instrumentation coverage by reading the constants file would conclude error tracking exists.

A declared-but-unemitted event is a false claim in the codebase, which is the same class of problem as a metric that renders as `0` when it means "not measured".

---

# 2. The original purpose, examined

AN-041's stated problem was **GAP-22: no error tracking anywhere**, with the goal:

> "Every funnel currently measures only the happy path: a drop-off caused by a bug is indistinguishable from a user changing their mind."

That diagnosis is correct and worth acting on. The proposed *solution* — "failure-path events" as a general category — is not, for three reasons:

| Problem with the original scope | Why it matters |
|---|---|
| **Wrong storage for the job** | General error tracking needs an APM or a structured log sink, both of which are unbounded-cardinality, short-retention, developer-facing. `UserEvents` is a bounded-cardinality analytics table with 180-day retention and a channel that drops on overflow. Routing every exception through it would fill the channel that the *working* events share — precisely risk R-1. |
| **Unqueryable by construction** | An event emitted "wherever an exception happens to be thrown" has an open vocabulary. Open vocabularies produce a pile, not a metric: no dashboard can enumerate the reasons, because a deploy can invent new ones. |
| **AN-042 already covers the real need** | The structured log sink shipped. Errors that need developer diagnosis belong there, with full context and no retention obligation to an analytics contract. |

**But the part that serves a decision survives.** There is a specific, bounded set of places where a *user-initiated core-loop operation is refused*, and where that refusal is today indistinguishable from the user simply not proceeding. Those are funnel steps, not exceptions, and they belong in the analytics store.

---

# 3. Decision: **IMPLEMENT (redefined)**

`operation_failed` now means:

> **A user-initiated core-loop operation did not complete, carrying a reason code from a closed set.**

Not "an error occurred". The distinction is deliberate and is the whole reason the redefinition works: two of the five recorded reasons are not errors at all — a pending host approval and an enforcement block are both *correct system behaviour*. What they have in common with the genuine failures is that the user tried to do something in the core loop and it did not happen, which is exactly what a funnel needs to know.

## Exact operations covered

Declared in `Cocorra.DAL/Models/OperationFailures.cs` as `TrackedOperations`:

| Operation | Meaning |
|---|---|
| `room_join` | Connecting to a live room over the hub, after the REST-side join |
| `stage_promotion` | A host promoting a participant to the stage |

**Two, not "all operations".** Adding a third is a deliberate act with a documentation obligation attached.

## Exact failure conditions

Declared as `OperationFailureReasons`. Five values, closed set:

| Reason | Emission site | Is it an error? | Decision it informs |
|---|---|---|---|
| `room_not_live` | `RoomHub.JoinRoom` — room scheduled, ended, or missing | No — a timing mismatch | Are users arriving before hosts start? Scheduling and reminder UX. |
| `not_a_participant` | `RoomHub.JoinRoom` — no `RoomParticipant` row | **Yes — a client sequencing bug** | The client reached the hub without completing `POST /Room/{id}/Join`. The one reason here that indicates a defect. |
| `pending_host_approval` | `RoomHub.JoinRoom` — status `PendingApproval` | No — a product state | How much join volume is lost waiting on hosts? Is approval worth its friction? |
| `blocked_from_room` | `RoomHub.JoinRoom` — status `Kicked` or `Rejected` | No — an enforcement outcome | Are blocked users repeatedly attempting re-entry? |
| `stage_at_capacity` | `RoomHub.ApproveToStage` — stage already at `Room.StageCapacity` | No — a capacity limit | **The highest-value one.** See §4. |

## Producer and authority

| Property | Value |
|---|---|
| **Producer** | Server only — `Cocorra.API/Hubs/RoomHub.cs`, via the single private helper `TrackOperationFailed` |
| **Authority** | **Server-authoritative.** Not on the client allowlist in `EventsController`, and must never be added: a client that could forge a refusal could forge the funnel. |
| **Subject (`UserId`)** | The user the operation was *for*. For `stage_promotion` that is the **promoted participant, not the host who called it** — matching `stage_promoted`, deliberately opposite to `room_join_approved`. |
| **Timing** | Immediately **before** the rejection is thrown. The rejection *is* the fact; emitting afterwards would mean never emitting at all. Same pattern as `speaker_time_exhausted`. |
| **Gate** | `Analytics:EnableNewEventEmission` (the AN-017 low-frequency increment). A refused join is rare next to a successful one, so this does not belong in the high-frequency increment. |

## Error classification

There is no severity field and no exception type, by design. Classification is carried entirely by the `(operation, reason)` pair, both drawn from constants. The consumer classifies:

- `not_a_participant` → **defect signal**, route to engineering
- `pending_host_approval`, `stage_at_capacity` → **product-design signal**, route to product
- `room_not_live` → **scheduling signal**
- `blocked_from_room` → **safety signal**

## Correlation strategy

`roomId` is on the payload and is promoted into the indexed `RoomId` column by `EventTracker.ExtractRoomId`, so a failure joins to the room, its host, its category, and every other event in the same room without a JSON scan.

**No `CorrelationId` is set.** `CorrelationId` exists to tie a multi-step operation together (`push_send_attempted` → `push_send_result`). A refusal is terminal — there is no second half to correlate with. Setting one would imply a pairing that does not exist.

## Privacy and security constraints

| Constraint | Enforcement |
|---|---|
| **No exception messages, ever** | Reason codes come from constants only. Exception text is written by developers for developers, is not a closed set, and could carry user data into a table with 180-day retention. Asserted by `OperationFailureTrackingTests.FailureProperties_CarryNoExceptionMessage`, which pins the exact property list. |
| **No new personal data** | The payload is `operation`, `reason`, `roomId`, `extra`. `UserId` is the event's subject, as on every other event. |
| **No free-text `extra`** | `extra` carries numeric context only (`stageCapacity`, `stageOccupancy`). It exists so the capacity reason can report *how full* the stage was. |
| **Deletion policy inherited** | `UserEvent.UserId` is `OnDelete(SetNull)`, so a deleted user's failures survive as anonymous rows, consistent with every other event. |

## Idempotency

**Deliberately not idempotent.** No `eventKey` is set, so a user retrying a join three times produces three rows.

This is the correct choice and the opposite of `activation_completed` (AN-010), which *is* keyed. The difference: `activation_completed` records a state transition that happened once, so a duplicate is noise. A retry records a *user experience* — someone who had to try three times had a materially worse session than someone who tried once, and collapsing them would hide the more severe case. Retry count is the signal.

---

# 4. Why `stage_at_capacity` justifies the whole item

M-400, the stage funnel shipped in this same phase, will show participations that raised a hand and were never promoted. Two very different explanations produce an identical drop:

| Explanation | Fix |
|---|---|
| The host did not notice or did not act | Host tooling — better raised-hand surfacing, notifications |
| The stage was full and the host **could not** act | Raise `Room.StageCapacity`, or change the default |

**Without this event those are indistinguishable, and the two fixes have nothing in common.** One is a UI problem, the other is a configuration default. A product team looking at the funnel alone would guess, and a 50/50 guess on which of two unrelated investments to make is the exact situation the analytics programme exists to remove.

That single reason code is worth more than a general error-tracking event would have been, because it is attached to a decision that is already on the table.

---

# 5. What was deliberately NOT done

Recorded because "we chose not to" and "we forgot" look identical in a codebase six months later.

| Not done | Why |
|---|---|
| Emission on authentication and validation failures (`Unauthorized user.`, `Invalid {field}.`) | Not core-loop steps. A malformed GUID is a client bug or an abuse probe, and neither is a funnel drop. |
| Emission on host-permission rejections (`Only the host can …`) | A non-host calling a host-only method is either a UI bug or an abuse attempt. Belongs in the security log, not the participation funnel. |
| Emission on `speaker_time_exhausted` | **Already covered** by its own dedicated event with richer properties (`allowedSeconds`, `spokenSeconds`, `extraMinutesGranted`). Adding a second event for the same fact would double-count the refusal. |
| A global exception filter emitting `operation_failed` | This is the original scope, and it is what turns the event into an unqueryable pile. Rejected explicitly. |
| A `severity` or `exceptionType` field | Would reintroduce the open vocabulary the closed sets exist to prevent. |
| A registered metric (M-6xx) over these events | **Nothing is measured yet.** The flag is off, so zero rows exist. Registering a contract now would put a metric in the trust register whose entire history is empty — the "renders as 0, means not measured" failure, one layer up. A contract is added once emission is on and the shape of real data is known. |

---

# 6. Verification

| Test | Asserts |
|---|---|
| `JoinRoom_RoomNotLive_EmitsRoomNotLive` (×2 statuses) | Correct operation, reason, subject and room |
| `JoinRoom_NoParticipantRow_EmitsNotAParticipant` | The client-bug reason fires on the right path |
| `JoinRoom_PendingApproval_EmitsPendingHostApproval` | A non-error refusal is still recorded |
| `JoinRoom_BlockedParticipant_EmitsBlockedFromRoom` (×2 statuses) | Kicked and Rejected both map to one reason |
| `ApproveToStage_StageFull_EmitsAgainstTheParticipantNotTheHost` | **The convention that would silently break M-400 if reversed** |
| `NoOperationFailedEvent_IsEmittedWhileTheFlagIsOff` | The gate actually gates |
| `FailureProperties_CarryNoExceptionMessage` | Exact property list — no exception text can leak in |
| `EveryDeclaredReason_IsDistinctAndSnakeCased` | The closed vocabulary stays enumerable |

10 test cases, all passing.

---

# 7. Status after this decision

| Item | Before | After |
|---|---|---|
| **AN-041** | PARTIALLY COMPLETE — constant declared, zero emission sites | **COMPLETE (redefined scope)** — 5 sites, closed vocabulary, tested, flag-gated |
| **GAP-22 (general error tracking)** | Open | **Still open, and now explicitly owned by AN-042 + a future APM.** Not covered by this event, and this document is the record of that boundary. |

**The honest summary**: AN-041 as originally written is *not* fully delivered. What is delivered is the subset that serves a decision, plus a documented refusal of the subset that would have degraded the pipeline. General error tracking remains a real gap, listed as such in `FINAL-ANALYTICS-IMPLEMENTATION-REPORT.md` §12.
