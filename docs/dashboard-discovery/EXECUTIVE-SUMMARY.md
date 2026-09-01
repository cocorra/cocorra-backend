# Cocorra — Decision Intelligence: Executive Summary

> **Generated**: 2026-09-01 | **Audience**: technical product owner
> **Purpose**: what to build, what to improve, what to investigate, what not to invest in
> **Basis**: full source inspection of `cocorra-backend`, plus the Phase 1 discovery documents in this directory
> **Scope**: Documentation only. No application code, database, events, or packages were modified.

---

## 1. Current Dashboard Trust Level

# NOT TRUSTED FOR MAJOR DECISIONS

### Why

**FACT** — Of the twelve metrics the current dashboard exposes, exactly **one** — Report Insights — can be used as the sole basis for a decision without a stated condition. Three are wrong. Seven are usable only if the reader knows a specific caveat, and the dashboard does not tell them what it is.

The three wrong ones:

| Metric | Defect |
|---|---|
| **Top Speakers** | **FACT** — the host is inserted as a participant with `IsMuted=false` and `LastUnmutedAt=UtcNow` at room start (`RoomService.cs:115-127, 439-449`). A host who never touches their mic accrues the room's entire 2–3 hour duration as "spoken time." The leaderboard ranks coaches by room length. |
| **User Growth (status breakdown)** | **FACT** — users are bucketed by `CreatedAt` but counted by **current** `Status` (`AnalyticsRepository.cs:21-93`). A January registrant banned in June appears as Banned in January. |
| **Retention (D1/D7/D30)** | **FACT** — matches activity on *exactly* day N (`AnalyticsRepository.cs:324-392`), and rests on a cookie-based `session_started` signal emitted to a Flutter mobile client. |

### The finding that decides the verdict

**FACT** — The same passive host appears simultaneously as the platform's **#1 top speaker** and as a **passive listener**, because the initial open-mic state emits no `mic_activated` event (`RoomHub.cs:518-521`). Two headline metrics on the same dashboard contradict each other about the same person at the same moment.

**INFERENCE** — Absence of data is survivable; you know you do not know. A metric that is confidently, plausibly, and consistently wrong is not, because someone will act on it. That is why the verdict is NOT TRUSTED rather than PARTIALLY TRUSTED: the failure is not that some numbers are missing, it is that the dashboard provides no way to tell a sound number from an unsound one, and three unsound ones are rendered with full authority.

### What is *not* wrong

**FACT** — the underlying architecture is sound. The `UserEvent` schema is well designed: promoted indexed `RoomId`, indexes on `(EventType, OccurredAtUtc)` and `(UserId, OccurredAtUtc)`, a non-blocking channel-based tracker, batched flush, and a client-event allowlist. The relational model is coherent. **INFERENCE** — this is a coverage-and-query problem, not a foundations problem. That is good news: the fixes are mostly additive, and several require no code change at all.

---

## 2. What Cocorra Can Safely Learn Today

Thirteen decisions rest on verified data. The notable point: **five of them are not computed anywhere in the current system.**

| # | Insight | Source | Computed today? |
|:--:|---|---|:--:|
| 1 | Whether room supply is healthy, and whether it is concentrating into too few coaches | `Room.HostId` + `Room.CreatedAt` | **No** |
| 2 | Whether reports concentrate in `MentalHealth` rooms | `user_reported.reportedRoomId` → `Room.Category` | **No** |
| 3 | How long admin voice review takes | Gap between `voice_verification_submitted` and `voice_verification_result` | **No** |
| 4 | Where users abandon the five-step onboarding gate, sequentially | Six server-emitted onboarding events | **No** (endpoint is non-sequential) |
| 5 | Whether users return to join more rooms | `room_joined` across weeks | **No** |
| 6 | Whether room participation is growing | `room_joined`, distinct users | Yes |
| 7 | Which rooms and hosts draw the largest audiences | `room_joined` grouped by `RoomId` | Yes |
| 8 | Which of the three categories draws participation | `Room.Category` join | Partially |
| 9 | Whether safety problems are increasing | `Report` table — the one VERIFIED metric | Yes |
| 10 | Who the repeat-reported users are | `Report.ReportedUserId` | Yes |
| 11 | Whether registrations are growing | `ApplicationUser.CreatedAt` (indexed) | Yes |
| 12 | Whether messaging is used and reciprocated | `Message` table | No |
| 13 | Whether listeners convert to speakers, in aggregate | `room_joined` vs `mic_activated` | Yes — **but requires host exclusion to be correct** |

**INFERENCE** — the safest evidence Cocorra has is largely evidence nobody is looking at. Item 1 is the platform's leading indicator; item 2 is its highest-stakes safety question; item 3 gates the entire growth funnel. All three are queries against data already verified as correct.

---

## 3. What Cocorra Cannot Know Today

The critical blind spots, ranked by how much they constrain product decisions.

### 3.1 The middle of the core loop is dark

**FACT** — the listener→speaker journey has six steps. Only two emit events.

| Step | Instrumented |
|---|:--:|
| `room_joined` | ✅ |
| Hand raised | ❌ `RaiseHand` writes a boolean, emits nothing |
| Approved to stage | ❌ `ApproveToStage` emits nothing |
| `mic_activated` | ✅ |
| Spoke meaningfully | ❌ host-contaminated; unmuted-time ≠ audio |
| Stayed | ❌ no `LeftAt`; `JoinedAt` overwritten on rejoin |

**INFERENCE** — Cocorra can see *that* its core conversion moved and has no instrumented path to *why*. Every possible response — change the selection mode, raise stage capacity, extend speaker time, redesign the hand-raise affordance — is a guess.

### 3.2 Cocorra is blind to its own media layer

**FACT** — `ILiveKitService` exposes only `GenerateToken` and `UpdateStagePermissionAsync`. A repository-wide search for "webhook" returns zero matches in code or configuration.

**INFERENCE** — for a voice-first product this is the largest single blind spot. A room where audio failed for half the participants is indistinguishable from one where half chose not to speak, and those demand opposite responses.

### 3.3 The most-churned users are deleted from the evidence

**FACT** — `AuthServices.DeleteAccountAsync` hard-deletes the `ApplicationUser` row.

**INFERENCE** — every retention rate is conditioned on the user still existing, so all of them are biased upward, and biased most for the least-engaged segments. This is the only blind spot where **waiting has an irreversible cost**: every day destroys evidence no future work can recover.

### 3.4 Discovery is invisible end to end

**FACT** — `GET /Room/Feed` emits nothing; `room_joined` carries no source; `ToggleReminder` emits nothing and deletes rows on un-toggle.

**INFERENCE** — a low join count cannot distinguish "nobody saw the room" from "everyone saw it and passed." Opposite problems, opposite fixes, no way to tell them apart.

### 3.5 In-room group chat leaves no trace

**FACT** — `RoomHub.SendRoomGroupMessage` neither persists nor emits.

**INFERENCE** — Active-vs-Passive labels most participants "passive." If many are typing, that label is wrong and so is the conclusion drawn from it. Cocorra may be measuring participation on one channel while it happens on another.

### 3.6 Push delivery is unmeasured, after a shipped delivery bug

**FACT** — the FCM response is discarded. Commit `dc1c933` fixed *reversed FCM delivery*.

**INFERENCE** — an identical regression today would be invisible to the dashboard and would surface only through user complaints, exactly as it did the first time.

### 3.7 State transitions are not recorded anywhere

**FACT** — `UpdatedAt` is assigned in exactly **three** places in the entire solution. `Room.UpdatedAt`, `FriendRequest.UpdatedAt`, `Message.UpdatedAt`, and `Notification.UpdatedAt` are never written. `ApplicationUser` has no `UpdatedAt` at all.

**INFERENCE — this is one architectural habit, not many separate oversights.** The schema consistently stores **current state** where analytics needs **transition history**: `IsHandRaised`, `IsOnStage`, `IsRead`, deleted `RoomReminder` rows, deleted `UserBlock` rows. It explains the majority of what Cocorra cannot answer, and it is addressable by one consistent rule — emit an event at every state transition — rather than twenty separate patches.

### 3.8 No causal capability at all

**FACT** — no feature flags, variant assignment, experiment table, or bucketing logic exist anywhere in the solution.

**INFERENCE** — no claim of the form "Feature X drives retention" is supportable today. See `07a-feature-investment-framework.md` for why this is more than a technicality: Cocorra's manual approval gate makes every feature-user cohort severely self-selected.

### 3.9 Nothing older than 180 days

**FACT** — `EventCleanupService` purges events beyond 180 days with no archive or export. Year-over-year analysis will never be possible under this policy.

---

## 4. The Top Product Decisions Cocorra Should Be Able to Make

Ranked by product impact.

### Decision 1 — Do listeners become speakers, and where does that break?

- **Current Ability**: **PARTIAL.** The aggregate conversion rate is measurable (distinct `room_joined` vs distinct `mic_activated`, hosts excluded). *Where* it breaks is not.
- **Missing Data**: `hand_raised`, `hand_lowered`, `stage_promoted`, `stage_demoted`, `mic_deactivated`, `speaker_time_exhausted`.
- **Recommended Next Step**: Add those six events in `RoomHub`, in methods that already save to the database and already have `_eventTracker` injected.

### Decision 2 — Recruit more coaches, or help existing ones run better rooms?

- **Current Ability**: **FULL, and entirely unexercised.** Distinct active hosts, host retention, rooms per host, supply concentration, distinct non-host speakers per room, and audience return per host are all computable today from verified relational data.
- **Missing Data**: Only "rooms gone live" — `StartScheduledRoomAsync` emits nothing.
- **Recommended Next Step**: Build the supply view from existing data. No instrumentation required.

### Decision 3 — Is the manual verification queue throttling growth?

- **Current Ability**: **FULL for latency, uncomputed.** The gap between the two events is a straightforward query. *(This corrects `06-blind-spots.md` §3, which concluded it was impossible — true of the relational data, not of the event stream.)*
- **Missing Data**: Queue depth over time; reviewer identity.
- **Recommended Next Step**: Compute the latency **distribution** — median, p90, p99, never the mean. This queue is a hard serialisation point: no acquisition effort can produce more active users than it approves.

### Decision 4 — Do `MentalHealth` rooms need category-specific safeguards?

- **Current Ability**: **FULL, uncomputed.** `user_reported` carries `reportedRoomId`, which joins to `Room.Category`. Both inputs verified.
- **Missing Data**: None.
- **Recommended Next Step**: Run the query. One `GROUP BY`. Given the category, this is the highest-stakes available analysis in the product.

### Decision 5 — Are users coming back?

- **Current Ability**: **PARTIAL but better than it appears.** The shipped retention metric is unusable, but a room-join-based return metric is available today and is materially more reliable — server-authoritative, cookie-independent, and measuring return to the product's actual value event.
- **Missing Data**: Unbiased churn (blocked by hard deletes); history beyond 180 days.
- **Recommended Next Step**: Replace the retention metric with room-join-based weekly return. A query, not a project.

### Decision 6 — Invest in messaging, friends, or neither?

- **Current Ability**: **PARTIAL.** Volume and reciprocity are measurable. Origin surface is not.
- **Missing Data**: `originSurface` on `message_sent` and `friend_request_sent`.
- **Recommended Next Step**: Measure **reciprocity**, not volume, first. **INFERENCE** — a high volume of one-directional messages is a warning sign, plausibly unwanted contact, not an engagement win.

### Decision 7 — Is push notification investment worthwhile?

- **Current Ability**: **NONE for delivery.**
- **Missing Data**: Persisted FCM send results; a `notificationId` correlation on opens.
- **Recommended Next Step**: Persist the FCM response. **INFERENCE** — optimising the copy of messages that may not be arriving is unfalsifiable work.

### Decision 8 — Should Topic Requests be built?

- **Current Ability**: **NONE, and correctly so.** **FACT** — entities and `AppDbContext` configuration exist; no controller, service, repository, route, or event. The tables are empty and will stay empty.
- **Missing Data**: This is not an analytics gap; it is a backlog item.
- **Recommended Next Step**: Read `SupportTicket` free text for topic-demand signal. Costs nothing. Then decide to build or delete the dead schema.

---

## 5. Recommended Dashboard Philosophy

```
DATA
→ OBSERVATION
→ INVESTIGATION
→ EVIDENCE
→ DECISION
```

The dashboard should not pretend to make decisions. It should help humans investigate and decide better.

**What this means concretely for Cocorra:**

| Stage | What the dashboard does | What it must not do |
|---|---|---|
| **DATA** | Carry each metric's trust level, historical reliability, and known biases *at the point of use* | Present all numbers with equal authority — the current system's core failure |
| **OBSERVATION** | Surface what changed, where | Explain why. It cannot. |
| **INVESTIGATION** | Offer drill-down paths from summary → segment → entity → raw events | Stop at a chart with no path downward |
| **EVIDENCE** | Distinguish correlation from causation explicitly, on the chart | Imply causation from co-movement |
| **DECISION** | Leave it to the human | Automate it |

**Why this framing rather than "automated insights" (INFERENCE)** — half of Cocorra's real product decisions cannot be made from data at all today. A system that asserted causes would be asserting them from evidence that cannot support them, and it would be most confident exactly where the data is thinnest. Given that the existing dashboard's failure mode is *plausible wrongness*, a more sophisticated layer producing more plausible wrongness would be worse, not better.

**The design consequence, stated as a rule** — where a funnel step is uninstrumented, render the gap visibly and label it "not measured." Hiding it would make a two-step funnel look complete and would quietly misrepresent how much Cocorra knows about its own core loop. A dashboard that admits what it does not know is safer than one that answers every question.

---

## 6. Top 10 Recommendations

Ranked by **product impact × decision value**.

### 1. Remove or correct the three UNRELIABLE metrics
Top Speakers, User Growth's status breakdown, and Retention. **Impact HIGH / Effort LOW.** **INFERENCE** — the highest-value action available is subtraction. Removing the dashboard's self-contradiction requires no code and prevents the four decisions most likely to be made wrongly (`10a`).

### 2. Instrument the core loop — six events in `RoomHub`
`hand_raised`, `hand_lowered`, `stage_promoted`, `stage_demoted`, `mic_deactivated` (with `isHost`), `speaker_time_exhausted`. **Impact HIGH / Effort MEDIUM.** Converts the product's central question from unanswerable to routine, and resolves the Top Speakers contradiction at source. This data is not accruing anywhere: every week without it is a week that can never be analysed.

### 3. Build the Supply Health view from existing data
Distinct active hosts, host retention, rooms per host, supply concentration, audience return per host. **Impact HIGH / Effort LOW.** The best value-to-effort ratio in the programme. In a marketplace this small, losing two coaches matters more than losing two hundred listeners, and it is visible weeks earlier.

### 4. Stop hard-deleting users
Soft delete with `DeletedAt` and in-place scrubbing. **Impact HIGH / Effort MEDIUM.** The only recommendation where **delay has an irreversible cost**. Requires a data-protection decision from whoever owns that.

### 5. Emit `user_status_changed`
`fromStatus`, `toStatus`, `changedByAdminId`, `isBulkOperation`. **Impact HIGH / Effort LOW.** One event closes three gaps: historical status, backlog history, reviewer consistency. It is the only possible record — `ApplicationUser` has no `UpdatedAt` and there is no history table.

### 6. Replace retention with room-join-based weekly return
**Impact HIGH / Effort LOW.** Not a repair but a replacement with a better metric: server-authoritative, cookie-independent, measuring return to the actual value event.

### 7. Add `entrySource` and `isHost` to `room_joined`
**Impact HIGH / Effort LOW.** One string on an event that already fires converts every discovery question from unanswerable to trivial; `isHost` makes host exclusion a filter rather than a join.

### 8. Compute report rate by room category
**Impact HIGH / Effort LOW.** One `GROUP BY` on verified data. The highest-stakes available analysis, given the `MentalHealth` category.

### 9. Persist FCM send results
`push_send_attempted`, `push_send_result`, plus daily token coverage. **Impact HIGH / Effort LOW.** A regression guard for a defect class that has already occurred once in this codebase.

### 10. Publish the Metric Trust Register with inline badges
**Impact HIGH / Effort LOW.** **INFERENCE** — the register content already exists in `08a`. Trust information must travel with the number; a wiki page about metric definitions is not read, a badge beside a KPI is.

**Deliberately not in the top 10** — the Decision Center, LiveKit webhooks, and experimentation infrastructure. All are valuable; all are gated on trust and baselines that do not exist yet. Building sophisticated intelligence on unverified metrics would reproduce the current dashboard's failure in a more persuasive form.

---

## 7. One Next Action

# Exclude hosts from every room-participation metric, then remove the three UNRELIABLE metrics from the dashboard.

**Why this one.**

It is a query-level change. No code deployment, no schema migration, no client release, no risk to the running application. It can be done today.

It removes the only place in Cocorra's data where the system does not merely fall silent but **actively contradicts itself** — reporting the same passive host as both the platform's top speaker and a silent listener.

It simultaneously corrects the Active-vs-Passive rate, which is currently biased downward by exactly one artificial passive listener per room — a bias that grows in relative terms as rooms get smaller, which is to say, precisely at Cocorra's current scale.

**Why it must come before anything else (INFERENCE)** — every subsequent recommendation adds instrumentation, and instrumentation is only worth building if the resulting numbers will be trusted. Today the dashboard contains three metrics that are wrong and no way for a reader to tell. Adding better data to a system that presents wrong data with equal authority does not produce better decisions; it produces more confident ones. Trust has to be established before breadth, and this action establishes it at zero cost.

**What follows immediately after** — the Supply Health view (Recommendation 3), which is likewise pure query work and answers the platform's most consequential unwatched question.

---

## Document Index

| Document | Contents |
|---|---|
| `00-repository-overview.md` | Stack, structure, architecture |
| `01-product-feature-inventory.md` | Features and their measurability |
| `02-data-model.md` | Entities, timestamps, availability matrix |
| `03-current-dashboard.md` | Existing metrics and their reliability |
| `04-data-flow-traceability.md` | Data paths and loss risks |
| `05-event-tracking-audit.md` | Current event inventory |
| **`05-analytics-gap-analysis.md`** | 23 decision-driven gaps with priorities |
| `06-blind-spots.md` | Missing data by category |
| **`06a-recommended-event-taxonomy.md`** | 28 proposed events, 7 extensions |
| `07-metric-verification.md` | SQL-equivalent verification of each metric |
| **`07-decision-framework.md`** | 40-row decision matrix; Findings A–E |
| **`07a-feature-investment-framework.md`** | Per-feature investment ladders; the causation warning |
| **`07b-north-star-analysis.md`** | 5 candidates; WPU recommended; metric tree |
| **`08a-metric-trust-framework.md`** | Trust metadata for all 12 current metrics |
| **`09-recommended-dashboard.md`** | 10 sections + the Decision Center |
| **`10-recommendations-roadmap.md`** | P0–P3 roadmap, 30 items |
| **`10a-decision-safety-matrix.md`** | 52 decisions classified by safety |
| **`EXECUTIVE-SUMMARY.md`** | This document |

Bold entries were produced in this phase.
