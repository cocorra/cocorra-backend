# 10 — Prioritized Recommendations Roadmap

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Depends on**: `05-analytics-gap-analysis.md` (GAP IDs), `06a-recommended-event-taxonomy.md`, `08a-metric-trust-framework.md`, `09-recommended-dashboard.md`
> **Scope**: Documentation only. Nothing here has been implemented.

---

## How to Read This Roadmap

Ordered by dependency, not by appeal:

| Tier | Purpose | Rule |
|---|---|---|
| **P0 — DATA TRUST** | Stop the dashboard from being wrong. | Nothing downstream is safe until these are done. |
| **P1 — DECISION VISIBILITY** | Surface what the team needs to decide now. | Highest business value per unit of effort. |
| **P2 — PRODUCT INTELLIGENCE** | Deeper analysis: funnels, cohorts, segmentation. | Valuable once P0/P1 are trustworthy. |
| **P3 — ADVANCED INTELLIGENCE** | Anomaly detection, automated insight, experiments. | Requires baselines and infrastructure that do not exist yet. |

**Impact** — effect on Cocorra's ability to make correct product decisions: HIGH / MEDIUM / LOW.
**Effort** — relative implementation cost: HIGH / MEDIUM / LOW. **No development-hour estimates are given**, deliberately — the team knows its own velocity; this document knows only relative size.

**Only issues actually found in the audit appear here.** No generic analytics-maturity recommendations.

---

# P0 — DATA TRUST

Everything that must happen before major decisions rely on this dashboard. Three of these items concern metrics that are **actively wrong**, and two concern **evidence being destroyed daily**.

---

## P0-1 — Remove or correct the three UNRELIABLE metrics

| | |
|---|---|
| **Problem Solved** | Three shipped metrics are wrong and look exactly as credible as the nine that are not. **FACT** — `08a` grades User Growth (M-02), Participation Stats / Top Speakers (M-05), and Retention Cohort (M-08) as **UNRELIABLE**. |
| **Decisions Enabled** | Prevents wrong decisions rather than enabling right ones — which is why it is first. **INFERENCE** — a plausible-looking wrong number is more dangerous than a missing one, because a missing number prompts a question and a wrong number prompts action. |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **LOW** — removal or relabelling; no data model change. |
| **Priority** | **P0** |

**Specifics:**
- **Top Speakers** — remove. **FACT, Finding A** — hosts are inserted with `IsMuted=false` and `LastUnmutedAt=UtcNow`, so a silent host accrues the room's full 2–3 hours. The leaderboard ranks coaches by room length, and simultaneously classifies those same coaches as passive listeners in Active-vs-Passive.
- **Users Who Raised Hand** — remove. **FACT** — `IsHandRaised` is a live boolean reset by `LowerHand`; the historical count is near-permanently ~0.
- **User Growth status breakdown** — remove the status dimension; keep the registration count, which is sound. **FACT** — current status is backdated into historical buckets.
- **Retention (D1/D7/D30)** — remove. **FACT** — exact-day matching plus a cookie-dependent activity signal.
- **Avg Room Duration** — remove. **FACT** — it averages the *configured* duration, which is only ever 2 or 3.

---

## P0-2 — Stop hard-deleting users

| | |
|---|---|
| **Problem Solved** | GAP-03. **FACT** — `AuthServices.DeleteAccountAsync` hard-deletes the `ApplicationUser` row. `UserEvent.UserId` and `Report.ReportedUserId` are `SetNull` on delete. |
| **Decisions Enabled** | Any retention, churn, or cohort decision. **INFERENCE** — every retention rate Cocorra computes is conditioned on the user still existing, so all of them are biased upward, and biased most for the least-engaged segments. Registration history also *decreases* retroactively as users leave. |
| **Dependencies** | Requires a decision on data-protection obligations — see below. |
| **Impact** | **HIGH** |
| **Effort** | **MEDIUM** — soft-delete flag, `DeletedAt`, in-place personal-data scrubbing, and query filters across the codebase. |
| **Priority** | **P0** |

**Why P0 rather than P1 (INFERENCE)** — this is the only item on the roadmap where *waiting has an irreversible cost*. Every day it remains unaddressed permanently destroys evidence that no future work can recover. A wrong metric can be fixed retroactively; a deleted user cannot be restored.

**Note** — whether a soft delete satisfies the applicable deletion obligation is a legal question, not an analytics one, and needs a decision from whoever owns that. **FACT** — the `account_deleted` event (`AuthServices.cs:565`, with `{reason}`) already survives deletion and provides a partial anonymised record in the meantime.

---

## P0-3 — Instrument the core loop (six events)

| | |
|---|---|
| **Problem Solved** | GAP-01 and GAP-06. **FACT** — of six steps in the listener→speaker journey, only two emit events. |
| **Decisions Enabled** | Where does the core loop break? Is the stage the bottleneck? Is the speaker time budget too tight? Should the default `SelectionMode` change? Who genuinely contributes most? |
| **Dependencies** | Should follow P0-6 (channel capacity check). |
| **Impact** | **HIGH** |
| **Effort** | **MEDIUM** — six `_eventTracker.Track` calls in `RoomHub`, in methods that already save to the database and already have the tracker injected. |
| **Priority** | **P0** |

**Events:** `hand_raised`, `hand_lowered`, `stage_promoted`, `stage_demoted`, `mic_deactivated` (with `isHost`), `speaker_time_exhausted`.

**Why P0 rather than P1 (INFERENCE)** — this data does not exist anywhere and is not accruing. Every week without it is a week that can never be analysed. It also sits at Cocorra's designated North Star input (`07b`, Input 3), where the team can currently observe an outcome move and has no instrumented path to its cause. `mic_deactivated` additionally resolves the active contradiction in P0-1.

---

## P0-4 — Emit `user_status_changed`

| | |
|---|---|
| **Problem Solved** | GAP-02, GAP-05, and the reviewer half of GAP-08 — three gaps, one event. |
| **Decisions Enabled** | Is the verification backlog growing? Are reviewers consistent? What did each cohort's status history actually look like? |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **LOW** — one event in `AdminService.ChangeUserStatusAsync`, which already emits `voice_verification_result` at the same point. |
| **Priority** | **P0** |

**Properties:** `fromStatus`, `toStatus`, `changedByAdminId`, `isBulkOperation`, `reason`.

**FACT** — `ApplicationUser` has no `UpdatedAt` and there is no status-history table, so this event is the *only* possible record of a status transition. **FACT** — the acting admin's identity is currently recorded nowhere in the system.

---

## P0-5 — Replace the retention metric with room-join-based return

| | |
|---|---|
| **Problem Solved** | GAP-04. **FACT** — two independent defects: exact-day matching (`AnalyticsRepository.cs:324-392`) and a cookie-dependent activity signal on a Flutter client. |
| **Decisions Enabled** | Should Cocorra prioritise retention work? |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **LOW** — a new query against existing verified data. No application change, no client release. |
| **Priority** | **P0** |

**INFERENCE — the important point.** This is not a repair of the broken metric but a *replacement with a better one*. `room_joined` is server-authoritative, indexed, cookie-independent, and marked VERIFIED. "Did this user join a room in a later week?" is both more reliable and more meaningful than "did a cookie survive," because it measures return to the product's actual value event. Fixing the exact-day bug in the existing query would still leave it resting on `session_started`.

---

## P0-6 — Verify event-pipeline headroom before expanding it

| | |
|---|---|
| **Problem Solved** | A prerequisite, not a gap. **FACT** — `EventTracker` uses a bounded `Channel<UserEvent>` of 10,000 with `BoundedChannelFullMode.DropWrite`. When full, events are **silently dropped**. |
| **Decisions Enabled** | Whether the P0-3 events can be added safely. |
| **Dependencies** | None. Must precede P0-3. |
| **Impact** | **MEDIUM** — protects everything else. |
| **Effort** | **LOW** — inspect the drop-warning frequency and channel utilisation. |
| **Priority** | **P0** |

**INFERENCE — why this is not skippable.** `mic_deactivated` fires on every mute and `hand_raised`/`hand_lowered` on every toggle. In a busy room these could multiply per-room event volume several-fold. Dropped events do not fail loudly — they undercount, and the undercount is worst during the busiest rooms, which are exactly the ones most worth analysing. Adding events to a saturated channel would silently degrade the events that already work.

---

## P0-7 — Publish the Metric Trust Register

| | |
|---|---|
| **Problem Solved** | **INFERENCE** — the dashboard carries no information distinguishing a trustworthy number from an untrustworthy one. `08a` shows only 1 of 12 metrics is VERIFIED, yet all twelve render identically. |
| **Decisions Enabled** | Every decision, indirectly — it tells the reader what the number can bear. |
| **Dependencies** | P0-1 (so the register describes the corrected set). |
| **Impact** | **HIGH** |
| **Effort** | **LOW** — the register content already exists in `08a`; the work is surfacing badges at the point of use. |
| **Priority** | **P0** |

**RECOMMENDATION** — the trust badge must travel with the number, inline. A wiki page about metric definitions is not read; a badge beside a KPI is.

---

## P0-8 — Start daily snapshot rollups

| | |
|---|---|
| **Problem Solved** | GAP-05 for quantities that are genuinely state rather than events: pending queue depth, active user count, FCM token coverage, open report count. |
| **Decisions Enabled** | "Is anything getting better or worse?" — currently unanswerable for every snapshot metric. |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **LOW** — a scheduled job writing counts to a rollup table. Reads existing tables only. |
| **Priority** | **P0** |

**Why P0 (INFERENCE)** — like P0-2, waiting has an irreversible cost. History that is not captured today cannot be reconstructed tomorrow. It is also the cheapest partial mitigation for the 180-day event purge, since rollups survive it.

---

# P1 — DECISION VISIBILITY

The highest-value improvements. **INFERENCE — the striking property of this tier: five of eight items require no application code change at all.** They are queries against data that already exists and has already been verified correct.

---

## P1-1 — Build the Supply Health view

| | |
|---|---|
| **Problem Solved** | GAP-07. **FACT** — distinct active hosts, host retention, rooms per host, distinct non-host speakers per room, and audience return per host are all computable today from verified data, and **none** of the eleven analytics endpoints computes any of them. |
| **Decisions Enabled** | Recruit more coaches, or help existing coaches run better rooms? Is supply concentrating dangerously? |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **LOW** — queries against `Room.HostId`, `Room.CreatedAt`, and existing events. |
| **Priority** | **P1** |

**INFERENCE — the highest value-to-effort ratio on this roadmap.** Cocorra is a two-sided marketplace with a very small supply side. Losing two active coaches is a larger event than losing two hundred listeners, and it is visible weeks earlier. This is the platform's leading indicator, it costs a handful of queries, and nobody is looking at it.

---

## P1-2 — Compute report rate by room category

| | |
|---|---|
| **Problem Solved** | GAP-12. **FACT** — `user_reported` carries `reportedRoomId`, which joins to `Room.Category`. Both inputs exist and are verified; the segmentation has never been run. |
| **Decisions Enabled** | Do `MentalHealth` rooms need category-specific safeguards? |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **LOW** — one `GROUP BY`. |
| **Priority** | **P1** |

**INFERENCE — why this outranks larger items.** Two of Cocorra's three room categories are `Relationships` and `MentalHealth`. Rooms discussing mental health carry a duty of care that a general social product does not. If reports concentrate there, it is a safety finding requiring a product response. The highest-stakes available analysis in the product costs one query.

---

## P1-3 — Compute admin review latency

| | |
|---|---|
| **Problem Solved** | GAP-08. **FACT, correcting the earlier audit** — `06-blind-spots.md` §3 concluded review latency was unmeasurable because `ApplicationUser` has no `UpdatedAt`. That holds for the *relational* data but not the *event stream*: the gap between `voice_verification_submitted` and `voice_verification_result` for the same `UserId` is a straightforward query. |
| **Decisions Enabled** | Is the manual verification queue a throughput bottleneck? |
| **Dependencies** | None for latency. Reviewer consistency needs P0-4. |
| **Impact** | **HIGH** |
| **Effort** | **LOW** |
| **Priority** | **P1** |

**RECOMMENDATION** — report the **distribution** (median, p90, p99), never the mean. **INFERENCE** — the tail is the story: if most reviews take 20 minutes and 15% take 3 days, the mean describes nobody and hides the users being harmed. This queue is also a hard serialisation point on the entire growth funnel — no acquisition effort can produce more active users than it approves.

---

## P1-4 — Fix the funnel to be sequential

| | |
|---|---|
| **Problem Solved** | GAP-13. **FACT** — `AnalyticsRepository.cs:300-322` counts each step independently, so the "funnel" can *widen* downward, which is impossible in a real funnel. |
| **Decisions Enabled** | Where do prospective users abandon the five-step verification gate? |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **MEDIUM** — a real sequential funnel with per-user time ordering. |
| **Priority** | **P1** |

**RECOMMENDATION** — include **median elapsed time per step**, not just conversion. **INFERENCE** — a step converting at 90% but taking 18 hours is a different problem from one converting at 60% instantly, and a conversion-only funnel makes them look like the same kind of problem.

---

## P1-5 — Add `entrySource` to `room_joined`

| | |
|---|---|
| **Problem Solved** | GAP-09. **FACT** — `room_joined` carries only `{roomId}`; there is no source attribution anywhere. |
| **Decisions Enabled** | Invest in feed ranking, search, or the reminder loop? |
| **Dependencies** | Requires a client-side value (the app knows where the user came from). |
| **Impact** | **HIGH** |
| **Effort** | **LOW** on the server; requires a Flutter release. |
| **Priority** | **P1** |

**INFERENCE — the highest-value single property in the entire programme.** One string on an event that already fires, in a code path that already runs, converts every room-discovery question from unanswerable to trivial. Add `isHost` at the same time so Finding A's host exclusion becomes a filter rather than a join.

---

## P1-6 — Persist FCM send results

| | |
|---|---|
| **Problem Solved** | GAP-11. **FACT** — the Firebase response from `SendPushNotificationAsync` is discarded. |
| **Decisions Enabled** | Is push delivery working? Is notification strategy worth investing in? |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **LOW** — `push_send_attempted` and `push_send_result`. |
| **Priority** | **P1** |

**INFERENCE — why this is not a nice-to-have.** Commit `dc1c933` fixed *reversed FCM delivery* — notifications reaching the wrong user. An identical regression today would be **invisible to the dashboard** and would surface only through user complaints, exactly as it did the first time. For a defect class that has already occurred once in this codebase, this is a regression guard.

**RECOMMENDATION** — also track FCM token coverage among `Active` users daily (via P0-8). Cheap, no new event, and it would have made the original bug visible.

---

## P1-7 — Expose support analytics

| | |
|---|---|
| **Problem Solved** | GAP-10. **FACT** — ticket volume by type, chat volume, resolution time, and first-response time are all in the database, and **no analytics endpoint covers support at all**. |
| **Decisions Enabled** | What are users struggling with? Is support responsive? Is ticket volume a reliability signal? |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **LOW** — a query and a route. |
| **Priority** | **P1** |

**INFERENCE — the reason this matters more than it appears.** With no error tracking anywhere in the stack (errors reach `ILogger` → Docker stdout and are never persisted), `SupportTicketType.TechnicalProblem` volume is currently Cocorra's **only** systematic reliability signal — the closest thing the platform has to an outage alarm — and it is invisible on the dashboard.

---

## P1-8 — Add reminder events

| | |
|---|---|
| **Problem Solved** | The measurable half of GAP-09. **FACT** — `ToggleReminder` emits nothing and `RoomReminder` rows are *deleted* on un-toggle, so the table is a snapshot of intent rather than a log of it. |
| **Decisions Enabled** | Is the reminder loop worth investing in? |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **LOW** — `reminder_set`, `reminder_removed`. |
| **Priority** | **P1** |

**INFERENCE** — reminders are Cocorra's only built-in re-engagement loop for scheduled content. Today the conversion can be approximated by joining surviving `RoomReminder` rows to `room_joined`, but because un-toggles are hard-deleted, that reads **optimistically** by an unknown margin.

---

# P2 — PRODUCT INTELLIGENCE

---

## P2-1 — Build weekly participation cohorts

| | |
|---|---|
| **Problem Solved** | No cohort analysis exists. |
| **Decisions Enabled** | Is retention improving across cohorts? Which first-room experiences predict return? |
| **Dependencies** | P0-5 (correct return metric); requires ~8 weeks of history. |
| **Impact** | **HIGH** |
| **Effort** | **MEDIUM** |
| **Priority** | **P2** |

**RECOMMENDATION** — do not display a sparse cohort grid. **INFERENCE** — a grid with mostly empty cells invites over-interpretation of tiny samples; hide it until the history exists.

---

## P2-2 — Segment the core funnel by room configuration

| | |
|---|---|
| **Problem Solved** | Speaking conversion is measurable in aggregate but not by the settings hosts actually control. |
| **Decisions Enabled** | Should the default `SelectionMode` change? Does `StageCapacity` bind? Do categories differ? |
| **Dependencies** | P0-3 for the intermediate steps. |
| **Impact** | **HIGH** |
| **Effort** | **MEDIUM** |
| **Priority** | **P2** |

**RECOMMENDATION** — label these comparisons **correlational**. **INFERENCE** — hosts *choose* the selection mode, so the kind of host who chooses manual approval may differ systematically from one who does not. See `07a`.

---

## P2-3 — Room → friendship and room → DM conversion

| | |
|---|---|
| **Problem Solved** | Whether rooms produce relationships. Derivable today via `RoomParticipant` co-attendance joined to `FriendRequest` timing; never computed. |
| **Decisions Enabled** | Is the social layer fed by the core loop, or running parallel to it? |
| **Dependencies** | Partial today; full version needs P2-4. |
| **Impact** | **MEDIUM** |
| **Effort** | **MEDIUM** |
| **Priority** | **P2** |

**Caveat (FACT, Finding D)** — friend-request re-send after rejection overwrites `CreatedAt`, corrupting the ordering for that subset.

---

## P2-4 — Add origin-surface properties to social events

| | |
|---|---|
| **Problem Solved** | GAP-16 and GAP-17. **FACT** — in-room and friends-list DMs emit identical `message_sent` events; friend requests carry no origin. |
| **Decisions Enabled** | Strengthen the room→DM bridge? Build people discovery? |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **LOW** |
| **Priority** | **P2** |

---

## P2-5 — Add `LeftAt` and stop overwriting `JoinedAt`

| | |
|---|---|
| **Problem Solved** | GAP-14. **FACT** — no `LeftAt`, and `RoomHub.JoinRoom:245-253` overwrites `JoinedAt` on rejoin, destroying the original. |
| **Decisions Enabled** | Do users stay or leave early? Should room length or format change? |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **MEDIUM** — schema change plus rework of the rejoin path. |
| **Priority** | **P2** |

**RECOMMENDATION** — model each attendance as its own row rather than mutating one. **INFERENCE** — this rises to P1 the moment room-format changes are on the roadmap, since it is the only way to evaluate them.

---

## P2-6 — Resolve the in-room group chat question cheaply

| | |
|---|---|
| **Problem Solved** | GAP-15. **FACT** — `SendRoomGroupMessage` neither persists nor emits. |
| **Decisions Enabled** | Whether Active-vs-Passive is mislabelling an actively-chatting cohort as passive. |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **LOW** for the existence check; MEDIUM for full instrumentation. |
| **Priority** | **P2** |

**RECOMMENDATION** — before building anything, run a one-week existence check (a counter, or SignalR volume inspection) to establish whether the behaviour is material. If negligible, close the gap. If substantial, instrument it *and* reinterpret Active-vs-Passive. **INFERENCE** — this is a cheap measurement that resolves a question currently distorting a headline metric.

---

## P2-7 — Convert string statuses to enums; add resolution timestamps

| | |
|---|---|
| **Problem Solved** | **FACT** — `Report.Status` and `SupportTicket.Status` are free-form strings, and `AnalyticsRepository` recognises only "Open", "Resolved", "InProgress", silently dropping anything else from every status count. No `ResolvedAt` on either. |
| **Decisions Enabled** | Accurate moderation and support responsiveness. |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **MEDIUM** — schema migration plus a data backfill. |
| **Priority** | **P2** |

---

## P2-8 — Display analytics in local time

| | |
|---|---|
| **Problem Solved** | GAP-18. **FACT** — all analytics are UTC; the user base is MENA (UTC+2/+3). |
| **Decisions Enabled** | When should coaches schedule rooms? |
| **Dependencies** | None. |
| **Impact** | **MEDIUM** |
| **Effort** | **LOW** — presentation layer only; no data change. |
| **Priority** | **P2** |

**INFERENCE** — a coach acting on the current UTC peak-hours chart would schedule 2–3 hours off the real local peak.

---

## P2-9 — Test whether MBTI predicts anything

| | |
|---|---|
| **Problem Solved** | GAP-19. **FACT** — MBTI is collected from every user at real onboarding cost and used for exactly one thing: a distribution chart. |
| **Decisions Enabled** | Build MBTI-based features, or drop the onboarding step? |
| **Dependencies** | None — existing data. |
| **Impact** | **LOW** |
| **Effort** | **LOW** |
| **Priority** | **P2** |

**RECOMMENDATION** — test the four dichotomies (E/I, S/N, T/F, J/P), not sixteen types, and test one hypothesis with a clear prior: do E-types activate the mic at a higher rate than I-types? **INFERENCE** — sixteen cells across a small user base is too thin to distinguish signal from noise. If nothing shows at adequate sample size, MBTI is decoration and the step should be reconsidered on friction grounds.

---

# P3 — ADVANCED INTELLIGENCE

---

## P3-1 — Build the Decision Center

| | |
|---|---|
| **Problem Solved** | Change detection: what moved, where, and what to investigate. |
| **Decisions Enabled** | Faster diagnosis across every area. |
| **Dependencies** | **Hard dependency**: P0-1 through P0-8, plus 4–6 weeks of stable metric history. |
| **Impact** | **HIGH** |
| **Effort** | **HIGH** |
| **Priority** | **P3** |

**RECOMMENDATION — do not build this early, however appealing it is.** **FACT** — no baseline exists for any Cocorra metric. **INFERENCE** — detection requires knowing what normal looks like. Shipping signal detection without a baseline produces alerts on ordinary variance, and a dashboard that cries wolf in its first month is ignored permanently. Building it on unverified metrics would also reproduce the existing dashboard's failure in a more sophisticated and more persuasive form.

**Note** — `09-recommended-dashboard.md` establishes that all ten proposed signals are detectable from data available today. The Decision Center is gated on **history and trust**, not on instrumentation.

---

## P3-2 — Ingest LiveKit webhooks

| | |
|---|---|
| **Problem Solved** | GAP-20. **FACT** — `ILiveKitService` exposes only `GenerateToken` and `UpdateStagePermissionAsync`; a repository-wide search for "webhook" returns nothing. |
| **Decisions Enabled** | Does audio actually work? Should Cocorra invest in media infrastructure? |
| **Dependencies** | LiveKit configuration; a new endpoint. |
| **Impact** | **HIGH** |
| **Effort** | **HIGH** |
| **Priority** | **P3** |

**INFERENCE** — for a voice-first product this is the largest single blind spot: a room where audio failed for half the participants is indistinguishable from one where half chose not to speak, and those require opposite responses. It sits at P3 only because the P0/P1 items are cheaper and address decisions the team faces sooner. **It escalates to P1 immediately if `TechnicalProblem` tickets rise** — which is the current de-facto detector. The correlation key already exists: Cocorra sets `participantIdentity` when generating tokens.

---

## P3-3 — Add failure-path events and error tracking

| | |
|---|---|
| **Problem Solved** | GAP-22. **FACT** — no failure events exist; errors reach `ILogger` → Docker stdout with 10MB/3-file rotation and are never persisted or aggregated. No APM, no error-tracking service, no structured logging sink. |
| **Decisions Enabled** | How reliable is the experience, and where does it break? |
| **Dependencies** | None. |
| **Impact** | **HIGH** |
| **Effort** | **MEDIUM** |
| **Priority** | **P3** |

**RECOMMENDATION** — a structured logging sink or an error-tracking service is the more conventional and probably better solution than analytics events for this. Add `room_join_failed` as an analytics event only where the failure is user-meaningful and worth funnel-joining.

---

## P3-4 — Introduce experiment capability

| | |
|---|---|
| **Problem Solved** | GAP-21. **FACT** — no feature flags, variant assignment, experiment table, or bucketing logic exist anywhere in the solution. |
| **Decisions Enabled** | Any causal claim at all (`07a`). |
| **Dependencies** | Meaningful user volume. |
| **Impact** | **MEDIUM** |
| **Effort** | **HIGH** for full infrastructure; **LOW** for staged rollouts. |
| **Priority** | **P3** |

**RECOMMENDATION**, cheapest first:
1. **Exploit natural experiments already in the data** — the approval-latency variation described in `07a` FI-3 costs nothing but a query and is genuinely close to exogenous.
2. **Staged rollouts with a deterministic `UserId`-hash holdout** — needs no framework, only the decision to do it before shipping.
3. **Full A/B infrastructure** — only when volume justifies it. **INFERENCE** — with the manual approval gate throttling intake, Cocorra is unlikely to have the volume for well-powered A/B tests on secondary features soon. Treating A/B as the answer would be premature.

---

## P3-5 — Acquisition attribution

| | |
|---|---|
| **Problem Solved** | GAP-23. **FACT** — `ApplicationUser` has no source, referral, or campaign field. |
| **Decisions Enabled** | Where should acquisition effort go? |
| **Dependencies** | None. |
| **Impact** | **LOW** at current scale |
| **Effort** | **MEDIUM** |
| **Priority** | **P3** |

**INFERENCE** — this becomes important only once there is a deliberate acquisition budget to allocate. Before that, there is nothing to attribute.

---

# Roadmap Summary

| Item | Problem Solved | Decisions Enabled | Dependencies | Impact | Effort | Priority |
|---|---|---|---|:--:|:--:|:--:|
| **P0-1** | 3 UNRELIABLE metrics on the dashboard | Prevents wrong decisions | None | HIGH | LOW | **P0** |
| **P0-2** | Hard deletes destroy churn evidence | All retention/churn analysis | Legal decision | HIGH | MEDIUM | **P0** |
| **P0-3** | Core loop 4 of 6 steps uninstrumented | Where the core loop breaks | P0-6 | HIGH | MEDIUM | **P0** |
| **P0-4** | No status transition history | Backlog, reviewer consistency, cohort history | None | HIGH | LOW | **P0** |
| **P0-5** | Retention wrong twice over | Prioritise retention work | None | HIGH | LOW | **P0** |
| **P0-6** | Event channel headroom unknown | Protects all event metrics | None | MEDIUM | LOW | **P0** |
| **P0-7** | No trust signal on any metric | Every decision, indirectly | P0-1 | HIGH | LOW | **P0** |
| **P0-8** | Snapshot metrics have no history | "Is anything improving?" | None | MEDIUM | LOW | **P0** |
| **P1-1** | Supply health unwatched | Recruit vs enable coaches | None | HIGH | LOW | **P1** |
| **P1-2** | Report rate by category uncomputed | MentalHealth safeguards | None | HIGH | LOW | **P1** |
| **P1-3** | Review latency uncomputed | Is the queue a bottleneck? | None | HIGH | LOW | **P1** |
| **P1-4** | Funnel is not sequential | Where onboarding leaks | None | HIGH | MEDIUM | **P1** |
| **P1-5** | No entry-source attribution | Feed vs reminders vs search | Flutter release | HIGH | LOW | **P1** |
| **P1-6** | FCM delivery unmeasured | Notification investment; regression guard | None | HIGH | LOW | **P1** |
| **P1-7** | Support data unexposed | What users struggle with | None | MEDIUM | LOW | **P1** |
| **P1-8** | Reminder intent not logged | Reminder loop investment | None | MEDIUM | LOW | **P1** |
| **P2-1** | No cohort analysis | Retention across cohorts | P0-5, 8wk history | HIGH | MEDIUM | **P2** |
| **P2-2** | Funnel not segmented by config | Room default settings | P0-3 | HIGH | MEDIUM | **P2** |
| **P2-3** | Room→social conversion uncomputed | Is the social layer fed by rooms? | Partial | MEDIUM | MEDIUM | **P2** |
| **P2-4** | Social event origin missing | Room→DM bridge; people discovery | None | MEDIUM | LOW | **P2** |
| **P2-5** | Time in room unrecoverable | Room length and format | None | MEDIUM | MEDIUM | **P2** |
| **P2-6** | Group chat leaves no trace | Is chat how silent users participate? | None | MEDIUM | LOW | **P2** |
| **P2-7** | String statuses, no resolution timestamps | Moderation/support accuracy | None | MEDIUM | MEDIUM | **P2** |
| **P2-8** | UTC-only for a UTC+2/+3 base | When coaches should schedule | None | MEDIUM | LOW | **P2** |
| **P2-9** | MBTI unused | Build MBTI features or drop the step | None | LOW | LOW | **P2** |
| **P3-1** | No change detection | Faster diagnosis everywhere | All P0 + 4–6wk history | HIGH | HIGH | **P3** |
| **P3-2** | No media telemetry | Media infrastructure investment | LiveKit config | HIGH | HIGH | **P3** |
| **P3-3** | No error tracking | Reliability investment | None | HIGH | MEDIUM | **P3** |
| **P3-4** | No experimentation | Any causal claim | User volume | MEDIUM | HIGH | **P3** |
| **P3-5** | No acquisition attribution | Acquisition allocation | None | LOW | MEDIUM | **P3** |

---

## Two Observations About This Roadmap

**1. Thirteen items require no application code change (INFERENCE).**
P0-1, P0-5, P0-7, P0-8, P1-1, P1-2, P1-3, P1-4, P1-7, P2-8, P2-9, and parts of P2-3 and P2-6 are queries, removals, or presentation changes against data that already exists and has already been verified correct. They carry no deployment risk to the running application. **They should not wait behind the instrumentation programme**, and they include the two highest value-to-effort items on the list — Supply Health (P1-1) and report-rate-by-category (P1-2).

**2. The P0 tier is mostly cheap, and that is the point (INFERENCE).**
Six of eight P0 items are LOW effort. P0 is not where the hard work is; it is where the *dependencies* are. The expensive items sit in P2 and P3 and are worth nothing until the trust problems above them are resolved — because analysis built on a metric graded UNRELIABLE inherits that grade, however sophisticated the analysis.
