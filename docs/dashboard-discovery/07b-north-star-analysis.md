# 07b — Cocorra North Star Analysis

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Depends on**: `07-decision-framework.md` (Findings A–E), `07a-feature-investment-framework.md`
> **Scope**: Documentation only.

---

## What Cocorra's Product Model Actually Is

Before proposing a North Star, the product model has to be stated accurately from the code, because the wrong model produces the wrong metric.

**FACT — Cocorra is a supply-constrained, gate-controlled, live-scheduled voice marketplace.**

Four structural properties, all verified:

1. **Supply-constrained.** Value exists only while a room is `Live`. A room requires a host. Hosts are a small set (the `Coach` role exists explicitly in `RoleSeeder`). No host, no product. **INFERENCE** — user-side metrics are therefore downstream of host activity, and a user-only North Star would hide the platform's most fragile dependency.

2. **Gate-controlled.** **FACT** — the default authorization policy requires `VerificationStatus=Active`, granted only by manual admin review. Registration is not access. Any North Star counting registered users would count people who cannot use the product.

3. **Synchronous and ephemeral.** **FACT** — rooms are live-only, of exactly 2 or 3 hours (`AllowedDurations`), and group chat is not persisted. There is no asynchronous content: no recordings, no feed of past rooms to consume. Value is created and destroyed in real time. **INFERENCE** — this rules out any consumption-based North Star (content viewed, sessions, time-in-app on stored material). Cocorra has nothing to consume between rooms.

4. **Participation-graded, not binary.** **FACT** — the product's design distinguishes audience from stage through hand-raising, host approval, stage capacity, and per-speaker time budgets. It deliberately treats speaking as a scarcer, higher-order act than listening.

**INFERENCE — what "value delivered" means here.** A unit of Cocorra value is: *a verified user takes part in a live conversation.* Not a registration, not an app open, not a message. The North Star should count that, at the user level, over a period long enough to accommodate a scheduled-room product.

**Why not daily.** **INFERENCE** — rooms are scheduled events of 2–3 hours, hosted by a small coach pool. Most users cannot plausibly attend daily; a DAU-style metric would mostly measure the room schedule, not user value. A **weekly** window matches the product's natural rhythm. It also happens to be the only choice that is measurable, since daily activity depends on the broken `session_started` signal.

---

## Candidate North Star Metrics

Five candidates, each derived from an actual Cocorra behaviour.

### Candidate 1 — Weekly Participating Users (WPU)

**Definition** — Count of distinct `UserId` with at least one `room_joined` event in a rolling 7-day window, excluding each room's own host for that room.

| | |
|---|---|
| **Why it represents product value** | Joining a live room is the moment a verified user receives the thing Cocorra exists to provide. It is the narrowest event that is unambiguously "used the product for its purpose," and it excludes registration, browsing, and messaging — none of which are the product. |
| **Supporting features** | Voice Rooms (F5), discovery/feed (F4), reminders (F4), onboarding gate (F1) — all upstream inputs. |
| **Current measurability** | **HIGH.** **FACT** — `room_joined` is server-emitted from `RoomHub.JoinRoom:270` with a promoted, indexed `RoomId`; the prior audit marks the derived Active Rooms metric **VERIFIED**. Distinct-user counting neutralises the per-reconnect duplication. Host exclusion is a join to `Room.HostId`. |
| **Problems** | (1) **FACT** — history is capped at 180 days by `EventCleanupService`; no year-over-year view. (2) **INFERENCE** — treats a 3-minute drop-in identically to a 3-hour participation, because time-in-room is unmeasurable (no `LeftAt`; `JoinedAt` overwritten on rejoin). (3) It measures attendance, not conversation — a room where nobody but the host spoke still counts every attendee. |

---

### Candidate 2 — Weekly Speaking Participants (WSP)

**Definition** — Count of distinct non-host `UserId` with at least one `mic_activated` event in a rolling 7-day window.

| | |
|---|---|
| **Why it represents product value** | Speaking is the behaviour Cocorra's entire design is built to produce. The stage, the hand-raise, the approval flow, the time budgets, the extra-time grants — all of it exists to move people from audience to stage. This counts the design succeeding. |
| **Supporting features** | Stage flow (F5), selection mode (F3), stage capacity, speaker time budget. |
| **Current measurability** | **MEDIUM-HIGH.** **FACT** — `mic_activated` is server-emitted from `RoomHub.ToggleMic:521` on genuine `muted → unmuted` transitions. Reliable for non-hosts. |
| **Problems** | (1) **FACT, Finding A** — hosts never emit it for their initial open mic, so hosts must be excluded rather than merely being absent; a host who speaks after muting once would be counted inconsistently. (2) **INFERENCE** — this counts a minority behaviour. Cocorra's own Active-vs-Passive metric exists because most participants never speak. A North Star that ignores the majority of users would make the majority of the product invisible, and would push effort toward speaker-conversion at the expense of listener experience. (3) **FACT, Finding B** — an activated mic is not audible speech; there is no LiveKit telemetry. |

---

### Candidate 3 — Weekly Conversational Rooms

**Definition** — Count of rooms in a 7-day window with at least N distinct non-host speakers (N to be set from the observed distribution, not assumed).

| | |
|---|---|
| **Why it represents product value** | Shifts the unit from users to **successful conversations**. A room where five people spoke is the product working; a room where a coach monologued to forty silent listeners is a broadcast, which is not what Cocorra was designed to be. |
| **Supporting features** | Room creation (F3), stage flow (F5), selection mode, host moderation. |
| **Current measurability** | **MEDIUM.** Computable today by grouping `mic_activated` by `RoomId` and excluding `Room.HostId`. |
| **Problems** | (1) **INFERENCE** — it is a room-level metric, so it grows with room supply and can rise while user value falls (more small rooms, same total participation). It cannot serve alone. (2) The threshold N would need empirical grounding, and none exists yet. (3) Inherits Finding B: speakers are unmuted, not necessarily audible. (4) **INFERENCE** — with a small coach pool, this number will be small and noisy week to week, making trend reading unreliable at current scale. |

---

### Candidate 4 — Weekly Speaking Minutes Delivered

**Definition** — Sum of `TotalSpokenSeconds` accrued by non-host participants in rooms ending within the window.

| | |
|---|---|
| **Why it represents product value** | Depth rather than breadth: total minutes of actual airtime given to community members is arguably the truest measure of a voice platform's output. |
| **Supporting features** | Stage flow, time budgets, extra-time grants. |
| **Current measurability** | **LOW — this candidate is disqualified by data quality, not by concept.** |
| **Problems** | (1) **FACT, Finding B** — `TotalSpokenSeconds` measures *unmuted time*, not speech. A speaker who unmutes and stays silent for twenty minutes books twenty minutes. (2) **FACT, Finding A** — the host contaminates any aggregate that fails to exclude them, by the full room duration. (3) **FACT, Correction 1** — finalisation depends on `RoomHub`'s static in-memory `_connections` dictionary; an API restart during live rooms leaves participants unfinalised, silently under-counting. (4) **FACT, Correction 2** — the `speaking_time_logged` event is emitted only inside `EndRoomAsync`, only for participants still `Active` at that moment; a room never formally ended emits none at all. **INFERENCE** — a North Star must be robust to operational events like a deploy. This one is not: it would move because the server restarted. |

---

### Candidate 5 — Weekly Returning Participants

**Definition** — Count of distinct users who joined a room this week **and** also joined a room in a prior week.

| | |
|---|---|
| **Why it represents product value** | Combines value delivery with evidence that the value was worth repeating. Growth in this number cannot be bought with a one-off acquisition push. |
| **Supporting features** | All of them — return is the product's cumulative verdict. |
| **Current measurability** | **MEDIUM.** **INFERENCE, important** — this is computable **without** `session_started`, using only `room_joined` events. It therefore avoids the cookie-reliability problem entirely and is far sounder than anything the shipped `/Analytics/Retention` endpoint produces. Note explicitly: **do not** compute this with that endpoint — **FACT**, it matches activity on *exactly* day N (`AnalyticsRepository.cs:324-392`) and defaults to the cookie-dependent `session_started`. |
| **Problems** | (1) **FACT** — the 180-day window caps cohort depth. (2) **FACT** — hard deletes (`AuthServices.DeleteAccountAsync`) remove the most-churned users, biasing every return rate upward. (3) **INFERENCE** — it is a lagging indicator that needs several weeks of history before it moves meaningfully, making it a poor primary metric for a team that needs to steer weekly. (4) It requires room supply to have existed in the prior week; a week with few rooms depresses the following week's number for reasons unrelated to user satisfaction. |

---

## Candidate Comparison

| Candidate | Why It Represents Product Value | Supporting Features | Current Measurability | Problems |
|---|---|---|:--:|---|
| **1. Weekly Participating Users (WPU)** | Counts verified users receiving the core product experience | Rooms, feed, reminders, onboarding | **HIGH** — VERIFIED event source, indexed, server-emitted | 180-day cap; no depth dimension; counts attendance not conversation |
| **2. Weekly Speaking Participants (WSP)** | Counts the behaviour the whole product design targets | Stage flow, selection mode, capacity | **MEDIUM-HIGH** — reliable event, host exclusion required | Minority behaviour; would render most users invisible; unmuted ≠ audible |
| **3. Weekly Conversational Rooms** | Counts successful conversations, not attendance | Room creation, stage flow, moderation | **MEDIUM** — computable, threshold ungrounded | Room-level; grows with supply; small and noisy at current scale |
| **4. Weekly Speaking Minutes Delivered** | Truest depth measure for a voice platform | Stage, time budgets | **LOW** | Host inflation; unmuted-time ≠ speech; breaks on server restart |
| **5. Weekly Returning Participants** | Value plus evidence it was worth repeating | All | **MEDIUM** — avoids the cookie problem entirely | Lagging; hard-delete bias; needs prior-week supply |

---

## Recommendation

# PRIMARY NORTH STAR: Weekly Participating Users (WPU)

**Definition (precise, so it can be implemented unambiguously):**

> The count of distinct `UserEvents.UserId` where `EventType = 'room_joined'` and `OccurredAtUtc` falls within a rolling 7-day window, **excluding** rows where that `UserId` equals the `HostId` of the `Room` identified by the event's promoted `RoomId` column.

Reported weekly, in UTC, alongside the count of distinct rooms and distinct hosts that produced it.

### Why this one

**It is the only candidate that is simultaneously value-representing and reliably measurable.** Candidates 2 and 3 describe value more precisely but measure a minority or a noisy room-level aggregate. Candidate 4 is disqualified by three independent data-quality defects. Candidate 5 is sound but lagging.

**Its data source is the strongest in the system.** **FACT** — `room_joined` is server-emitted (not client-reported, unlike `notification_opened` and `feature_viewed`), carries a promoted and indexed `RoomId`, and was independently marked VERIFIED in `07-metric-verification.md`. It does not depend on the cookie mechanism that undermines `session_started`, does not depend on the Flutter client implementing anything, and does not depend on any field that Finding A, B, C, or D contaminates.

**It is robust to the known duplication.** `room_joined` fires per SignalR reconnect. Counting distinct users over a week makes that irrelevant — the flaw that ruins raw join counts is neutralised by the metric's own definition.

**It sits where a North Star should sit** — downstream of everything the team controls (onboarding throughput, host supply, discovery, reminders) and upstream of the outcomes the business cares about. It moves when any input moves, which is precisely what makes it steerable.

### What must be stated every time it is reported

These are conditions, not disclaimers to bury:

1. **It counts attendance, not conversation.** WPU can rise while every attendee sits silent. It must always be reported with **Speaking Conversion Rate** (WSP ÷ WPU) beside it, so breadth is never mistaken for depth. A rising WPU with a falling conversion rate is a warning, not a win.

2. **It has a 180-day horizon.** **FACT** — `EventCleanupService` purges older events. There is no year-over-year comparison and there never will be under the current retention policy.

3. **It is supply-bounded.** **INFERENCE** — WPU cannot exceed what the room schedule permits. A flat WPU during a week with fewer rooms is not a demand problem. It must always be read against **Rooms Gone Live** and **Distinct Active Hosts**.

4. **Host exclusion is mandatory, not cosmetic.** **FACT, Finding A** — hosts are auto-joined to their own rooms. Including them makes WPU partly a count of rooms.

5. **It is UTC.** **FACT** — all analytics are UTC-only; the user base is MENA (UTC+2/+3). Week boundaries do not align with local weeks, which matters when reading week-over-week movement.

### Why not "no reliable North Star can be selected"

That verdict was seriously considered and rejected. It would be the right answer if Cocorra's *value event* were unmeasurable — but it is not. What is broken in this system is **depth measurement** (time in room, real speaking, session length), **transition history** (hand raises, stage promotions, go-live), and **retention** (cookie-based sessions, exact-day matching, hard deletes). The **arrival of a verified user in a live room** is intact, verified, server-authoritative, and indexed.

Declaring no North Star possible would overstate the damage and leave the team with nothing to steer by while the gaps are closed. The honest position is: *a North Star is available; a complete metric tree is not.* Three of the four input branches below have missing or unreliable pieces, and those gaps are the roadmap.

---

## Supporting Metric Tree

Every node connects to an actual Cocorra feature or behaviour. Availability is marked per node.

```
                    NORTH STAR
        Weekly Participating Users (WPU)
        distinct non-host users with room_joined / 7d
                  [AVAILABLE — HIGH]
                          ↑
        ┌─────────────────┼─────────────────┬──────────────────┐
        │                 │                 │                  │
   INPUT 1           INPUT 2           INPUT 3            INPUT 4
 VERIFIED USER      ROOM SUPPLY      PARTICIPATION      RETURN RATE
    SUPPLY                             QUALITY
 New activated    Rooms gone live   Speaking          % of prior-week
 users / week      / week            Conversion        WPU who return
                                     WSP ÷ WPU
 [PARTIAL —       [PARTIAL —        [AVAILABLE —      [PARTIAL —
  see below]       see below]        MEDIUM-HIGH]      MEDIUM]
        ↑                 ↑                 ↑                  ↑
        │                 │                 │                  │
   FEATURE /          FEATURE /         FEATURE /          FEATURE /
   BEHAVIOUR          BEHAVIOUR         BEHAVIOUR          BEHAVIOUR
```

### Input 1 — Verified User Supply

*New users reaching `Active` status per week.* Sets the ceiling on WPU: an unverified user cannot join anything.

```
INPUT 1: New Activated Users / week          [AVAILABLE — activation_completed, deduplicated at emit]
   ↑
   ├── Registrations / week                  [AVAILABLE — user_registered + ApplicationUser.CreatedAt (indexed)]
   ├── Email confirmation rate               [AVAILABLE — email_confirmed]
   ├── Voice submission rate                 [AVAILABLE — voice_verification_submitted]
   ├── Admin approval rate                   [AVAILABLE — voice_verification_result {status}]
   ├── Admin review latency (median, p90)    [AVAILABLE — gap between the two events above;
   │                                            NOT COMPUTED by any endpoint]
   ├── Pending queue depth over time         [NOT AVAILABLE — stats endpoint is a snapshot with no date filter]
   └── Activation → first room join rate     [AVAILABLE — activation_completed → room_joined]
                                                ⚠ INFERENCE: the most important and least-watched
                                                  onboarding metric. Approving users who never join
                                                  is wasted throughput.
```

**Feature/behaviour root:** registration, email OTP, voice recording, MBTI submission, admin review (F1, F2).

### Input 2 — Room Supply

*Live rooms available for users to join.* WPU is bounded by this; a user cannot participate in a room that does not exist.

```
INPUT 2: Rooms Gone Live / week              [PARTIAL — see the gap below]
   ↑
   ├── Rooms created / week                  [AVAILABLE — Room.CreatedAt + room_created]
   ├── Distinct active hosts / week          [AVAILABLE — Room.HostId]
   │      ⚠ INFERENCE: the platform's true leading indicator. Unwatched today.
   ├── Host retention (hosted again)         [AVAILABLE — Room.HostId across periods]
   ├── Rooms per host (concentration)        [AVAILABLE]
   ├── Scheduled → went-live conversion      [NOT AVAILABLE — FACT, Finding C:
   │                                            StartScheduledRoomAsync emits nothing,
   │                                            writes no timestamp]
   ├── Category mix                          [AVAILABLE — Room.Category, 3 values only]
   └── Room schedule coverage by hour        [PARTIAL — UTC only; MENA base is UTC+2/+3]
```

**Feature/behaviour root:** room creation, scheduling, coach activity (F3).

**Gap that matters:** "Rooms gone live" is the correct input, and it is exactly the number Finding C makes unmeasurable. Today it must be approximated by rooms with ≥1 non-host participant — a workable proxy that undercounts rooms that went live and drew nobody, which is precisely the failure case worth seeing.

### Input 3 — Participation Quality

*Of those who arrive, how many actually take part.* Prevents WPU from being gamed by attendance without value.

```
INPUT 3: Speaking Conversion Rate (WSP ÷ WPU)   [AVAILABLE — MEDIUM-HIGH]
   ↑
   ├── Distinct non-host speakers / week        [AVAILABLE — mic_activated, host-excluded]
   ├── Speakers per room                        [AVAILABLE — mic_activated grouped by RoomId]
   ├── Conversion by SelectionMode              [AVAILABLE — join to Room.SelectionMode]
   ├── Conversion by Category                   [AVAILABLE — join to Room.Category]
   ├── Hand raises / week                       [NOT AVAILABLE — FACT: RaiseHand emits no event;
   │                                                IsHandRaised is a live boolean]
   ├── Hand-raise → stage approval rate         [NOT AVAILABLE — ApproveToStage emits no event]
   ├── Time in room per participant             [NOT AVAILABLE — FACT: no LeftAt; JoinedAt
   │                                                overwritten on rejoin]
   ├── In-room chat participation               [NOT AVAILABLE — FACT: SendRoomGroupMessage
   │                                                neither persists nor emits]
   └── Real speaking duration                   [NOT AVAILABLE — FACT, Findings A & B:
                                                    host inflation + unmuted-time ≠ audio]
```

**Feature/behaviour root:** hand raise, host approval, stage capacity, mic, time budgets, extra-time grants, in-room chat (F5, F7).

**INFERENCE — this branch is the roadmap.** The top-level rate is measurable; six of the nine sub-drivers that would explain a movement in it are not. When Speaking Conversion drops, Cocorra will see the drop and have no instrumented path to its cause.

### Input 4 — Return Rate

*Whether participation was worth repeating.*

```
INPUT 4: % of prior-week WPU who participate again   [PARTIAL — MEDIUM]
   ↑
   ├── Repeat participation (room_joined based)      [AVAILABLE — avoids the cookie problem entirely]
   ├── Distinct rooms per user                       [AVAILABLE]
   ├── Return by first-room category                 [AVAILABLE — join to Room.Category]
   ├── Return by whether they spoke                  [AVAILABLE — correlational only;
   │                                                    see 07a for why this is not causal]
   ├── Return by first-room host                     [AVAILABLE — coach quality signal, uncomputed]
   ├── Session-based retention                       [UNRELIABLE — FACT: session_started is
   │                                                    cookie-dependent on a Flutter client;
   │                                                    shipped endpoint matches exactly day N]
   └── True churn                                    [NOT AVAILABLE — FACT: hard deletes remove
                                                        the most-churned users from every cohort]
```

**Feature/behaviour root:** the whole product; specifically room quality, reminders, notifications, and the social graph (F4, F5, F8, F9).

**Important (RECOMMENDATION)** — compute this branch from `room_joined`, **not** from `session_started` and **not** via `/Analytics/Retention`. The room-join-based version is materially more reliable than either, because it depends on a server-authoritative event rather than a cookie, and because it uses "active in a later week" rather than "active on exactly day N."

---

## How the Tree Is Used

The tree is diagnostic, not decorative. When WPU moves, it is read top-down:

| Observation | Branch to check first | Interpretation |
|---|---|---|
| WPU down, Rooms Gone Live down | Input 2 | Supply problem. Check host count and host retention before touching anything user-facing. |
| WPU down, room supply flat | Input 1 or 4 | Either fewer verified users arriving, or existing users not returning. Check activation throughput and review latency first — it is the cheaper of the two to fix. |
| WPU flat, Speaking Conversion down | Input 3 | Attendance holding, participation degrading. **INFERENCE** — the diagnosis stops here today; the sub-drivers that would explain it are uninstrumented. |
| WPU up, Speaking Conversion down | Input 3 | Growth in passive attendance. Do not read the WPU rise as a win without checking whether the new arrivals participate. |
| WPU up, Rooms Gone Live up, hosts flat | Input 2 | Existing hosts working harder. **INFERENCE** — supply concentration risk, worth watching even though the headline number looks good. |

**RECOMMENDATION** — Do not set numeric alert thresholds yet. No baseline exists, and inventing thresholds without one produces false alarms that train people to ignore the dashboard. Establish four to six weeks of WPU history first, then derive thresholds from observed variance. This point is developed in `09-recommended-dashboard.md`.

---

## Summary

**PRIMARY NORTH STAR — Weekly Participating Users (WPU):** distinct non-host users with a `room_joined` event in a rolling 7-day window.

**Selected because** it is the only candidate that both represents Cocorra's actual value event and rests on a data source the audit independently verified. Its source is server-authoritative, indexed, immune to the cookie problem, and untouched by Findings A–E.

**Never report it alone.** It requires three companions on the same view:
- **Speaking Conversion Rate (WSP ÷ WPU)** — guards against attendance without participation.
- **Rooms Gone Live** and **Distinct Active Hosts** — the supply bound that determines what WPU could have been.
- **Return Rate** (room-join-based, not session-based) — whether the value was worth repeating.

**Known limits, stated plainly:** 180-day history cap; no depth dimension (time in room is unmeasurable); UTC-only weeks against a MENA user base; upward bias in all return rates from hard deletes.

**The tree's own diagnosis:** Input 3 (Participation Quality) is the branch where Cocorra can see the outcome and none of the causes. That is where instrumentation work should go first, and it is the same conclusion `07a` reaches from a different direction.
