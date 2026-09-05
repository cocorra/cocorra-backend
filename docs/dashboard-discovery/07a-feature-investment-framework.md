# 07a — Feature Investment Framework

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Depends on**: `07-decision-framework.md` (Findings A–E and Corrections 1–3 are assumed here, not repeated)
> **Scope**: Documentation only.

---

## Purpose

This document answers one question per feature: **how would Cocorra know whether this feature deserves more engineering investment?**

It uses a fixed six-stage ladder. A feature must clear each stage before the next one means anything. A feature with high adoption and no repeat usage is not a successful feature — it is a novelty. A feature with high engagement and no user-value signal is not a successful feature — it is a time sink.

```
FEATURE
   ↓
ADOPTION            — did users try it?
   ↓
REPEAT USAGE        — did they come back to it?
   ↓
ENGAGEMENT QUALITY  — did they use it properly, or just touch it?
   ↓
USER VALUE SIGNAL   — did it produce something the user wanted?
   ↓
RETENTION IMPACT    — did using it change whether they came back to Cocorra?
   ↓
INVESTMENT DECISION
```

For each stage: what should be measured, whether Cocorra can measure it today, what is missing, and how reliable the resulting conclusion would be.

---

## The Causation Warning — Read This Before Any Stage 5 Claim

This applies to every feature in this document, so it is stated once, here, in full.

### The trap

The natural analysis is: *"Users who used Feature X returned more often than users who didn't. Therefore Feature X drives retention. Therefore invest in Feature X."*

Every step of that chain is wrong except the first observation.

### Why it is wrong for Cocorra specifically

**Selection effect.** Cocorra's onboarding gate is unusually strong: register → email OTP → voice recording → MBTI → **manual human approval**. **INFERENCE** — anyone who completes that gate has already demonstrated far above-average motivation. The subset who then go on to use a secondary feature (send a DM, add a friend, take the stage) is *even more* self-selected. Those users would very likely have returned anyway. The feature is a **marker** of a committed user, not necessarily a **cause** of commitment.

**Reverse causation.** A user who intends to keep using Cocorra adds friends *because* they intend to keep using it. The friendship did not create the intent; the intent created the friendship. Time-ordering the events does not resolve this — the intent precedes both.

**Confounding by exposure.** Users who attend more rooms have more opportunities to hit every secondary feature: more chances to meet someone worth friending, more chances to be reported, more chances to take the stage. Room attendance confounds nearly every feature-vs-retention comparison on the platform. **RECOMMENDATION** — any such comparison must at minimum stratify by number of rooms attended, and even then the result is suggestive, not causal.

**Survivorship in the data itself (FACT)** — `AuthServices.DeleteAccountAsync` hard-deletes the user row. Users who churned hardest — hard enough to delete their account — are physically absent from every cohort analysis. Every retention number Cocorra computes is conditioned on the user still existing. This biases all retention estimates upward, and biases them *most* for the least-engaged segments.

### What each level of evidence actually licenses

| Evidence available | Legitimate claim | Illegitimate claim |
|---|---|---|
| Cross-sectional correlation (users of X return more) | "X-users are a higher-value segment." Useful for *targeting*. | "X causes retention." |
| Time-ordered correlation (X precedes the return) | "X is a plausible driver; it is at least not caused by the return." | "X causes retention." Reverse causation and confounding both survive. |
| Correlation + stratification on room attendance and tenure | "The association survives the two obvious confounders." Reasonable basis for a **bounded, reversible** investment. | "X causes retention." Unmeasured confounders remain. |
| Randomised exposure (A/B) or a staged rollout with a genuine control | "X causes the observed change, within this population and period." | Generalising beyond the tested population. |
| Natural experiment (an outage, a forced rollout, a version cutover) | "X plausibly causes the change" — quasi-experimental, weaker than A/B but far stronger than correlation. | Treating it as equivalent to an A/B test. |

### What Cocorra can support today

**FACT** — There is no experimentation infrastructure in the repository: no feature-flag system, no variant assignment, no experiment table, no bucketing logic. A search of `Program.cs` service registrations and the `Cocorra.BLL/Services` tree returns nothing of the kind.

**FACT** — There is no `LastLoginAt`, no reliable per-user activity signal (`session_started` is cookie-dependent on a Flutter client), and the shipped retention query uses exact-day matching (`AnalyticsRepository.cs:324-392`), which undercounts.

**Therefore**: Cocorra cannot currently make **any** causal claim about any feature. It can, at best, make time-ordered correlational claims about room participation, and only within the 180-day event window.

**RECOMMENDATION** — Until an experiment mechanism exists, all Stage 5 conclusions in this document should be phrased as *"is associated with"* and never *"drives"* or *"causes."* Language discipline here is not pedantry; the whole point of this phase is that someone will read these numbers and spend engineering months on them.

### The cheapest honest path to causality for Cocorra

**RECOMMENDATION**, in ascending cost:

1. **Natural experiments already in the data.** The FCM fix in commit `dc1c933` is a real intervention with a real before/after boundary. If push notifications were misdelivered before that commit and correct after, that is a quasi-experiment on notification effectiveness that costs nothing but a query — *provided* the deployment date is known. Note the honest limit: nothing else was held constant across that boundary.
2. **Staged rollout with a holdout.** For the next user-visible change, release to a defined subset and keep a control. This requires no framework — a simple deterministic hash of `UserId` into buckets would do — but it does require deciding to do it *before* shipping.
3. **Interrupted time series.** For a platform-wide change with no holdout, compare a sufficiently long pre-period to a post-period and check whether the trend broke at the boundary rather than merely being higher after. Weak, but honest, and cheap.
4. **Full A/B infrastructure.** Only worth it once user volume makes it statistically viable. **INFERENCE** — given the manual approval gate throttling user intake, Cocorra is unlikely to have the volume for well-powered A/B tests on secondary features in the near term. This should temper any plan that treats A/B testing as the answer.

---

## Feature Investment Analyses

Ordered by how central the feature is to the product.

---

## FI-1 — Voice Rooms: Stage Participation (The Core Loop)

The single most important feature. Everything else in the product is scaffolding around it.

### Stage 1 — ADOPTION

**What should be measured** — Of activated users, what share ever joins a room; and of those, what share ever activates a mic.

**Can Cocorra measure it today?** — **YES, with a correction.**
- **FACT** — `room_joined` is server-emitted from `RoomHub.JoinRoom:270` with a promoted, indexed `RoomId`. `07-metric-verification.md` marks the derived Active Rooms metric VERIFIED.
- **FACT** — `mic_activated` is server-emitted from `RoomHub.ToggleMic:521`.
- **Required correction (FACT, Finding A)** — hosts must be excluded from the denominator or handled separately. A host is auto-joined to their own room and never emits `mic_activated` for their initial open mic, so including hosts deflates the measured speaker rate. Exclusion is straightforward: join `room_joined.RoomId` to `Room.HostId` and drop rows where `UserId = HostId`.
- **Required correction (FACT)** — `room_joined` fires on every SignalR reconnect. Count **distinct users**, never raw events.

**What data is missing** — Nothing essential for this stage.

**How reliable would the conclusion be?** — **HIGH**, once host exclusion and distinct-counting are applied. This is the most trustworthy behavioural measurement Cocorra has.

### Stage 2 — REPEAT USAGE

**What should be measured** — Distribution of rooms joined per user; share of joiners who join a second distinct room; median gap between first and second room.

**Can Cocorra measure it today?** — **YES.** Every `room_joined` event carries `UserId`, `RoomId`, and `OccurredAtUtc`. Repeat behaviour is a group-and-count over distinct `RoomId` per `UserId`.

**What data is missing** — **FACT** — history beyond 180 days (`EventCleanupService`). Long-horizon repeat behaviour for tenured users is unobservable.

**How reliable would the conclusion be?** — **HIGH** within the window. This does *not* depend on `session_started` and therefore sidesteps the cookie problem entirely — a point worth emphasising, because it means room-based repeat usage is measurable even though general retention is not.

### Stage 3 — ENGAGEMENT QUALITY

**What should be measured** — Did the participant do more than appear? Time in room, hand raised, promoted to stage, mic used, chatted.

**Can Cocorra measure it today?** — **LARGELY NO.** Of five quality signals, one works:

| Signal | Measurable | Why |
|---|:---:|---|
| Mic activated | ✅ | Event exists (non-hosts) |
| Hand raised | ❌ | **FACT** — `RaiseHand` emits no event; `IsHandRaised` is a live boolean reset by `LowerHand` |
| Promoted to stage | ❌ | **FACT** — `ApproveToStage` emits no event; `IsOnStage` is a live boolean |
| Time in room | ❌ | **FACT** — no `LeftAt`; `JoinedAt` overwritten on rejoin (`RoomHub.cs:245-253`) |
| Chatted in room | ❌ | **FACT** — `SendRoomGroupMessage` neither persists nor emits |

**What data is missing** — Four of five. This is the emptiest stage in the entire framework and it sits at the centre of the core feature.

**How reliable would the conclusion be?** — **LOW.** Cocorra can distinguish "spoke" from "did not speak" and nothing else. The rich middle of the experience — the hand raised and never called on, the twenty minutes of attentive listening, the active text participation — is invisible. **INFERENCE** — any judgement of "room quality" made today is really a judgement about mic usage, which describes a small minority of participants.

### Stage 4 — USER VALUE SIGNAL

**What should be measured** — Did participation produce something the user wanted: returning to another room, a friendship formed with a co-participant, a DM to someone met in the room, an extra-time grant received (host recognition), or simply staying to the end.

**Can Cocorra measure it today?** — **PARTIALLY.**
- **AVAILABLE** — Return to another room (Stage 2).
- **PARTIALLY AVAILABLE (INFERENCE)** — Room-to-friendship conversion is *derivable today* and nobody derives it. `RoomParticipant` gives the co-attendance set per room; `FriendRequest.CreatedAt` gives the request timing. A request between two users who co-attended a room, sent after that room, is a strong signal. **Caveat (FACT)** — Finding D means a rejected-then-resent request has its `CreatedAt` overwritten, corrupting the ordering for that subset.
- **NOT AVAILABLE** — Room-to-DM conversion. **FACT** — `message_sent` carries only `{receiverId}` and both in-room and friends-list DMs emit identically (`ChatService.cs:92`). The room context is lost precisely where it would be most informative.
- **NOT AVAILABLE (FACT)** — Extra-time grants emit no event.
- **NOT AVAILABLE (FACT)** — Stayed-to-the-end: no `LeftAt`, no reliable room-end timestamp.

**How reliable would the conclusion be?** — **MEDIUM** for the co-attendance→friendship analysis, which is the one genuinely valuable and currently-unexploited signal here. **LOW** for everything else.

### Stage 5 — RETENTION / RETURN IMPACT

**What should be measured** — Do users who take the stage return more than users who only listen?

**Can Cocorra measure it today?** — **CORRELATION ONLY, and even that needs care.**
- The *comparison* is computable: split joiners into spoke/did-not-speak by `mic_activated`, then measure subsequent distinct-room joins for each group.
- **Do not use the shipped `/Analytics/Retention` endpoint for this.** **FACT** — it matches activity on *exactly* day N (`AnalyticsRepository.cs:324-392`) and defaults to the cookie-dependent `session_started`. A room-join-based return measure is strictly better and avoids both flaws.

**Why the causal claim fails here specifically (INFERENCE)** — Speaking requires: noticing the hand-raise affordance, being willing to speak publicly in Arabic to strangers, being selected by the host, and being present long enough to be selected. Each is a marker of an already-committed user. Confidently-engaged users speak; speaking may contribute nothing independent. Confounding is severe and unmeasured.

**What additional analysis would be required before a causal claim**
1. Stratify by room count (a user in their 5th room is not comparable to one in their 1st).
2. Restrict to a fixed exposure window (first-3-rooms behaviour → next-30-days return) so tenure cannot leak in.
3. Establish time ordering rigorously — first speaking event strictly before the measured return window.
4. Then, and only then, a staged intervention: for a subset of rooms, prompt or nudge listeners toward the stage, hold out the rest, and compare. **RECOMMENDATION** — this is the cheapest genuine experiment available to Cocorra and it targets the product's most important question.

**How reliable would the conclusion be?** — **LOW as causal. MEDIUM as descriptive segmentation.**

### INVESTMENT DECISION

**What the data supports today** — Cocorra can determine *whether* listener→speaker conversion is healthy and *whether it is moving*. It cannot determine *why*, and cannot determine whether speaking is worth engineering effort to encourage.

**RECOMMENDATION** — This is the one feature where instrumentation should be added *before* investment is decided, not after. Three events (`hand_raised`, `stage_promoted`, `room_left` with duration) would convert Stage 3 from LOW to HIGH and turn the product's central question from unanswerable into routine. Every other item in this document is worth less than these three.

---

## FI-2 — Voice Room Creation & Hosting (Supply Side)

### Stage 1 — ADOPTION

**What should be measured** — Distinct hosts creating rooms per week; share of `Coach`-role users who have ever hosted; new-host rate.

**Can Cocorra measure it today?** — **YES.** **FACT** — `Room.HostId` + `Room.CreatedAt`, plus `room_created` events. Role membership is queryable via ASP.NET Identity.

**Reliability** — **HIGH.**

### Stage 2 — REPEAT USAGE

**What should be measured** — Rooms per host per period; host retention (share of last month's hosts who hosted again); host churn.

**Can Cocorra measure it today?** — **YES.** Directly from `Room.HostId` grouped by month.

**INFERENCE — this is the most under-valued measurement Cocorra currently has.** It is fully available, entirely reliable, requires no new instrumentation, and is a leading indicator for the entire platform. In a two-sided marketplace this small, losing two active coaches is a larger event than losing two hundred listeners, and it is visible weeks earlier.

**Reliability** — **HIGH.**

### Stage 3 — ENGAGEMENT QUALITY

**What should be measured** — Do hosts run *good* rooms: attract participants, get people on stage, use their moderation tools, run the full scheduled length?

**Can Cocorra measure it today?** — **PARTIALLY.**
- **AVAILABLE** — Participants per room (with the caveat from `07-metric-verification.md` that `Participants.Count` includes `Left`/`Kicked`/`Rejected`, inflating it — filter by status).
- **AVAILABLE** — Distinct non-host speakers per room, from `mic_activated`.
- **NOT AVAILABLE (FACT)** — Moderation tool usage. `ApproveToStage`, `MoveToAudience`, `GrantExtraTime`, `KickUser` all emit nothing.
- **NOT AVAILABLE (FACT)** — Actual room duration (Finding C).
- **UNUSABLE (FACT, Finding A)** — Host `TotalSpokenSeconds` measures room length, not hosting effort. It must not be used as a hosting-quality metric under any circumstance.

**Reliability** — **MEDIUM.** Distinct non-host speakers per room is a genuinely good hosting-quality proxy and it works today.

### Stage 4 — USER VALUE SIGNAL (value to the host)

**What should be measured** — Does hosting reward the host: growing audiences across their rooms, returning attendees, friend requests received?

**Can Cocorra measure it today?** — **PARTIALLY.**
- **AVAILABLE (INFERENCE)** — Audience-return-per-host is computable and valuable: for each host, what share of a room's participants attend a later room by the same host? This is the closest thing Cocorra has to a "coach quality" measure, it requires only `room_joined` and `Room.HostId`, and nothing computes it today.
- **PARTIALLY AVAILABLE** — Friend requests received by hosts, via `FriendRequest.ReceiverId`, though without origin attribution (F8).

**Reliability** — **MEDIUM-HIGH** for audience return.

### Stage 5 — RETENTION IMPACT

**What should be measured** — Does a host's audience-return rate predict whether that host keeps hosting?

**Can Cocorra measure it today?** — **Correlationally, yes**, and this is the one place where the causal story is comparatively less fraught. **INFERENCE** — the confounding here is milder than in FI-1: a host's audience size is substantially determined by factors outside the host's own motivation (scheduling slot, category, platform traffic that day), so it functions as a partially exogenous input. Still not causal, but the reverse-causation worry ("motivated hosts attract audiences") is weaker than the analogous worry for listeners.

**Reliability** — **MEDIUM.**

### INVESTMENT DECISION

**RECOMMENDATION** — Host-side analytics are the **best available return on zero instrumentation work** in the entire product. Host count, host retention, rooms-per-host, distinct-speakers-per-room, and audience-return-per-host are all computable today from existing verified data, none are currently computed, and all bear directly on the platform's most fragile dependency. Build this before building anything requiring new events.

---

## FI-3 — Voice Verification Onboarding

Onboarding is not a feature users adopt — it is a gate they pass. The ladder is therefore adapted: **completion** replaces adoption, **recovery** replaces repeat usage.

### Stage 1 — COMPLETION (Adoption analogue)

**What should be measured** — Sequential completion rate at each of the five steps.

**Can Cocorra measure it today?** — **YES for the data, NO for the shipped endpoint.** **FACT** — all six events exist server-side with `UserId` and `OccurredAtUtc`, so a true sequential funnel is computable. **FACT** — `/Analytics/Funnel` counts steps independently (`AnalyticsRepository.cs:300-322`) and can therefore report a later step with more users than an earlier one, which is impossible in a real funnel and is a visible symptom of the flaw.

**What data is missing** — Pre-submission abandonment (opened the form, never submitted). Requires a client-side event.

**Reliability** — **HIGH** if computed correctly from raw events. **LOW** if read off the current endpoint.

### Stage 2 — RECOVERY (Repeat usage analogue)

**What should be measured** — Of users set to `ReRecord`, how many resubmit, and how many are then approved?

**Can Cocorra measure it today?** — **YES.** **FACT** — `voice_verification_result` carries `{status}`, and `voice_verification_submitted` fires again from `AuthServices.ReRecordVoiceAsync:504`. The resubmission loop is fully traceable per user.

**Reliability** — **HIGH.**

### Stage 3 — ENGAGEMENT QUALITY (Process quality analogue)

**What should be measured** — Admin review latency distribution; approval/rejection/re-record mix; consistency across reviewers.

**Can Cocorra measure it today?** — **PARTIALLY.**
- **AVAILABLE (FACT, and contradicting the earlier audit)** — Latency **is** measurable as the per-user gap between `voice_verification_submitted` and `voice_verification_result`. `06-blind-spots.md` §3 concluded this was impossible; that holds for the relational data but not for the event stream. Within 180 days, this is a solved measurement waiting to be queried.
- **NOT AVAILABLE (FACT)** — Reviewer identity. `AdminService.cs:137` records only `{status}` against the reviewed user.

**Reliability** — **HIGH** for latency, **NOT POSSIBLE** for consistency.

### Stage 4 — USER VALUE SIGNAL

**What should be measured** — Do activated users actually reach the product? Activation → first room join, and the elapsed time between.

**Can Cocorra measure it today?** — **YES.** Both `activation_completed` and `room_joined` are reliable server events.

**INFERENCE** — This is the most important onboarding metric and the one most likely to be overlooked. Approving a user is not the goal; a user who joins a room is. If a meaningful share of activated users never join a single room, the onboarding investment is being wasted downstream of the gate, and no amount of funnel optimisation above the gate will help.

**Reliability** — **HIGH.**

### Stage 5 — RETENTION IMPACT

**What should be measured** — Does approval latency predict whether the user ever engages?

**Can Cocorra measure it today?** — **Correlationally, yes**, and this is the strongest natural-experiment candidate in the product.

**Why (INFERENCE)** — Approval latency is largely **exogenous to the user**. Whether someone registered on a Thursday evening when reviewers were active, or a Friday night when they were not, is essentially random with respect to that user's motivation. That is close to a natural randomisation, which is exactly what causal inference needs and almost never gets for free.

**Caveats that must be stated (INFERENCE)** — It is not perfectly random: registration time correlates with user type (night-owl users may differ systematically), and reviewer availability may correlate with periods of high registration volume, which correlate with marketing pushes, which change the user mix. The claim to make is *"long waits are associated with lower first-join rates, and the association is not obviously explained by user self-selection"* — genuinely stronger than ordinary correlation, still short of proof.

**RECOMMENDATION** — Analyse approval-latency buckets against first-join rate before running any A/B test on onboarding. It is nearly free and it is the best causal leverage currently available anywhere in the product.

**Reliability** — **MEDIUM**, unusually good for a Stage 5 claim.

### INVESTMENT DECISION

**RECOMMENDATION** — Onboarding is Cocorra's **best-instrumented** flow, and the gap is analytical rather than architectural: the endpoints do not compute what the data can already answer. Two queries (true sequential funnel; review-latency distribution) would move this from LOW to HIGH decision confidence without touching the application.

---

## FI-4 — Direct Messaging

### Stage 1 — ADOPTION
**Measure** — Share of activated users who have ever sent a DM. **Can measure?** **YES** — `Message` table plus `message_sent` events. **Reliability: HIGH.**

### Stage 2 — REPEAT USAGE
**Measure** — Messages per sender per week; share of senders with a second distinct conversation partner. **Can measure?** **YES** — `Message` is indexed on `(SenderId, ReceiverId, CreatedAt)`. **Reliability: HIGH.**

### Stage 3 — ENGAGEMENT QUALITY
**Measure** — Reciprocity: does the recipient reply? Conversation depth (messages per pair); read latency.
**Can measure?** **PARTIALLY.** Reciprocity and depth: **YES**, both directions are rows in the same table. Read latency: **NO** — **FACT**, `IsRead` is a bare boolean and `Message.UpdatedAt` is never written (Correction 3).
**INFERENCE** — Reciprocity is the metric that matters, and it works. A high volume of one-directional messages would be a *warning* sign — plausibly unwanted contact — not an engagement success. Raw message volume alone could mask a harassment pattern as healthy usage.
**Reliability: MEDIUM-HIGH.**

### Stage 4 — USER VALUE SIGNAL
**Measure** — Does a DM conversation lead to co-attendance in later rooms?
**Can measure?** **PARTIALLY (INFERENCE)** — computable by joining message pairs to subsequent shared `RoomParticipant` rows. Not currently computed. Would establish whether DM is a genuine social layer or an isolated utility.
**Reliability: MEDIUM.**

### Stage 5 — RETENTION IMPACT
**Measure** — Do DM users return more?
**Can measure?** **CORRELATION ONLY, and heavily confounded.**
**INFERENCE — the confounding is unusually severe here.** DM requires an accepted friendship, which requires knowing the target's exact user ID (F8), which in practice requires having met them — almost certainly in a room. So "DM users" is very nearly a subset of "users who attend rooms and engage socially." The comparison of DM-users to non-DM-users is largely a comparison of engaged users to disengaged users, with the DM playing little independent role. Room-attendance stratification is mandatory, and even then the residual selection is large.
**Reliability: LOW as causal.**

### INVESTMENT DECISION
**RECOMMENDATION** — Measure reciprocity and DM→co-attendance before investing. Do not treat DM volume as an engagement win on its own. **INFERENCE** — the friends-only + exact-ID-search design means DM adoption is structurally capped by friend-graph formation; if adoption is low, the constraint is most likely upstream in people-discovery, not in the messaging feature itself. Investing in messaging UI would then be solving the wrong problem.

---

## FI-5 — Friends System

### Stage 1 — ADOPTION
**Measure** — Share of activated users who send or receive a request; share with ≥1 accepted friend. **Can measure?** **YES** — `FriendRequest` plus both events. **Reliability: HIGH.**

### Stage 2 — REPEAT USAGE
**Measure** — Friends added over time; distribution of friend count. **Can measure?** **YES** for current state; **PARTIALLY** over time — **FACT**, Finding D: re-sending after rejection overwrites `CreatedAt` on the existing row, so the request timeline is corrupted for that subset. **Reliability: MEDIUM.**

### Stage 3 — ENGAGEMENT QUALITY
**Measure** — Acceptance rate; response latency; whether friendships get used (DMs exchanged).
**Can measure?** Acceptance rate: **PARTIALLY** (Finding D biases the table-derived rate; the event-derived rate is better but has no rejection event to validate against). Response latency: **NO** (Correction 3). Friendship utilisation: **YES** — join accepted friendships to `Message` pairs.
**INFERENCE** — Friendship utilisation is the metric worth having. A large graph of unused friendships means friending is a low-cost gesture, not a relationship, and investment in graph-growth features would produce more of the same.
**Reliability: MEDIUM.**

### Stage 4 — USER VALUE SIGNAL
**Measure** — Do friends co-attend rooms afterwards? **Can measure?** **YES (INFERENCE)** — joinable via `RoomParticipant` and not currently computed. This would establish whether the social graph feeds the core loop or runs parallel to it. **Reliability: MEDIUM.**

### Stage 5 — RETENTION IMPACT
**Measure** — Do users with friends return more?
**Can measure?** **CORRELATION ONLY.** **INFERENCE** — this is the textbook case where the "social graph drives retention" narrative from other platforms is most tempting and least warranted here. Cocorra's friending mechanism requires possessing an exact user ID; the friend-havers are therefore users who met someone in a room and pursued it. Almost the entire retention difference could be explained by "attended rooms and liked them." **Mandatory stratification: rooms attended, and tenure.**
**Reliability: LOW as causal.**

### INVESTMENT DECISION
**RECOMMENDATION** — The binding question is not "does friending help" but "**how does anyone find a person to friend?**" — and that is **NOT AVAILABLE** (F8: search requires a pre-known ID, no origin event). Answering the discovery question requires one event (`friend_request_sent` with an origin property). Until then, an investment decision on this feature rests on nothing.

---

## FI-6 — Notifications & Push

### Stage 1 — ADOPTION (reach, not user choice)
**Measure** — Share of active users with a valid FCM token. **Can measure?** **YES** — `ApplicationUser.FcmToken` non-null, as a snapshot. **NOT AVAILABLE** over time. **Reliability: MEDIUM** (snapshot only).

### Stage 2 — REPEAT USAGE (delivery consistency)
**Measure** — Send success rate over time; token churn.
**Can measure?** **NO.** **FACT** — the FCM response is not persisted. **INFERENCE** — given that commit `dc1c933` fixed *reversed FCM delivery* (messages reaching the wrong user), the absence of any delivery metric means an identical regression today would be invisible to the dashboard and would surface only through user complaints. For a defect class that has already occurred once in this codebase, that is the clearest instrumentation gap in the notification stack.
**Reliability: NOT POSSIBLE.**

### Stage 3 — ENGAGEMENT QUALITY
**Measure** — Open rate by notification type.
**Can measure?** **PARTIALLY / UNRELIABLY.** In-app read rate from `IsRead`: **YES**. Push open rate: **FACT** — depends on the client-emitted `notification_opened`, whose properties are entirely client-defined, with no guaranteed `Notification.Id`. Without that correlation id, opens cannot be attributed to sends and no rate can be computed.
**Reliability: LOW.**

### Stage 4 — USER VALUE SIGNAL
**Measure** — Notification → intended action (room reminder → join; friend request → respond).
**Can measure?** **PARTIALLY (INFERENCE)** — for `RoomReminder` specifically, `Notification.ReferenceId` holds the room id, so a subsequent `room_joined` for the same `(UserId, RoomId)` is a reasonable attribution *without any client cooperation*. This is the one notification-effectiveness measurement available today. Its limit is honest: it cannot distinguish "joined because of the push" from "joined and would have anyway," since reminder-setters are self-selected as already interested.
**Reliability: MEDIUM for reminders, LOW for other types.**

### Stage 5 — RETENTION IMPACT
**Measure** — Do notified users return more?
**Can measure?** **CORRELATION ONLY, with an added twist.** **INFERENCE** — notifications can have *negative* effects (uninstall, notification disable) that Cocorra cannot observe at all: an uninstall looks identical to an inactive-but-installed user, and there is no `notification_disabled` signal. The measurement is asymmetric — the upside is partly visible, the downside is entirely invisible. Any "notifications improve retention" conclusion from this data is structurally over-optimistic.
**Reliability: LOW.**

### INVESTMENT DECISION
**RECOMMENDATION** — Do not invest in notification *strategy* (copy, timing, volume) until delivery is measurable. Optimising the content of messages that may not be arriving is unfalsifiable work. Persisting FCM send results is the prerequisite.

---

## FI-7 — Reporting, Moderation & Safety

The investment ladder fits awkwardly here: growth in usage is a *bad* sign, not a good one. Stages are reinterpreted accordingly.

### Stage 1 — ADOPTION (report rate)
**Measure** — Reports per 1,000 room joins, over time. **Can measure?** **YES** — both numerator and denominator are verified-reliable. **Reliability: HIGH.**

### Stage 2 — REPEAT USAGE (recidivism)
**Measure** — Users reported more than once; reporters who report repeatedly. **Can measure?** **YES** — `Report.ReportedUserId` grouped. **Caveat (FACT)** — `SetNull` on delete means a deleted reported user drops out of the grouping. **Reliability: MEDIUM-HIGH.**

### Stage 3 — ENGAGEMENT QUALITY (moderation responsiveness)
**Measure** — Time to resolution; action distribution; report validity (share rejected).
**Can measure?** **PARTIALLY.** **FACT** — `Report.UpdatedAt` *is* written by `SupportService.cs:140, 275`, so a rough resolution time exists — but it means "last touched," not "resolved." **NOT AVAILABLE** — which `AdminReportAction` was applied, as a queryable record. **FACT** — `Report.Status` is a free-form string and the analytics layer recognises only three literal values, silently dropping anything else.
**Reliability: MEDIUM.**

### Stage 4 — USER VALUE SIGNAL (does moderation protect people)
**Measure** — Does a reported user's behaviour change after action? Does the reporter stay?
**Can measure?** **PARTIALLY.** Post-action behaviour is observable through subsequent room joins and further reports. **NOT AVAILABLE** — reporter outcome is confounded by the churn-invisibility problem (hard deletes) exactly where it matters most: a reporter who was harassed and then deleted their account is the single most important case, and it is the one case guaranteed to be absent from the data.
**Reliability: LOW.**

### Stage 5 — RETENTION IMPACT
**Measure** — Does exposure to reportable behaviour drive churn?
**Can measure?** **NO, and this is a structural limit rather than a fixable gap.** **INFERENCE** — the users this question is about are disproportionately the users who deleted their accounts, whose rows are gone (`DeleteAccountAsync` hard-deletes). Cocorra cannot measure harm-driven churn using data from which the harmed users have been removed. No additional event fixes this; it requires soft deletion.
**Reliability: NOT POSSIBLE.**

### INVESTMENT DECISION
**RECOMMENDATION** — Compute **report rate by room `Category`** immediately. It is available today (`user_reported` carries `reportedRoomId`, which joins to `Room.Category`), it is not computed anywhere, and given that one of three categories is `MentalHealth`, it is the highest-stakes available metric in the product. Safety investment should not wait for the retention question, which is structurally unanswerable.

---

## FI-8 — In-Room Group Chat

### All stages — NOT MEASURABLE

**FACT** — `RoomHub.SendRoomGroupMessage` neither persists nor emits. Every stage of the ladder returns NOT AVAILABLE.

**INFERENCE — why this matters more than its zero-data status suggests.** Cocorra's own Active-vs-Passive metric establishes that most room participants never activate a mic. The product currently labels them "passive listeners." If a substantial share of them are typing in group chat, that label is wrong, and the conclusion drawn from it — *"most users don't participate"* — is also wrong. The product would be measuring participation on one channel while participation happens on another.

**How to test this cheaply (RECOMMENDATION)** — Before building group-chat persistence, count group-chat messages for a short window with a single counter, or inspect SignalR volume, to establish whether the behaviour exists at all. If in-room chat traffic is negligible, no further work is warranted. If it is substantial, the Active-vs-Passive metric needs reinterpretation and the feature needs instrumentation. This is a one-off measurement, not a data model change, and it resolves a question that currently distorts a headline metric.

### INVESTMENT DECISION
**RECOMMENDATION** — Do not invest in the feature yet. Do resolve the ambiguity, cheaply, because it changes the interpretation of an existing dashboard metric.

---

## FI-9 — User Profiles & MBTI

### Stage 1 — ADOPTION
**Measure** — Profile completion rate (bio, picture, MBTI). **Can measure?** **PARTIALLY** — current-state snapshot only; **FACT**, no `ApplicationUser.UpdatedAt` and no profile events. **Reliability: LOW** (snapshot conflates "completed at signup" with "completed later").

### Stage 2 — REPEAT USAGE
**Measure** — Do users revisit and update profiles? **Can measure?** **NO.** Zero profile events. **Reliability: NOT POSSIBLE.**

### Stage 3 — ENGAGEMENT QUALITY
**Measure** — Profile view volume; view→friend-request conversion. **Can measure?** **NO.** No `profile_viewed` event. **Reliability: NOT POSSIBLE.**

### Stage 4 — USER VALUE SIGNAL
**Measure** — Do complete profiles receive more friend requests or stage approvals?
**Can measure?** **CORRELATION ONLY, and a badly confounded one (INFERENCE)** — without a completion timestamp, "complete profile" correlates with tenure and engagement. Comparing lifetime outcomes across snapshot completeness measures how long someone has been around, not what a profile does.
**Reliability: LOW.**

### Stage 5 — RETENTION IMPACT
**Measure** — Do profile-completers retain better? **Can measure?** **CORRELATION ONLY**, with the same confound compounded. **Reliability: LOW.**

### MBTI specifically
**FACT** — Collected from every user at real onboarding cost; used for exactly one thing, a distribution chart in `/Analytics/Users/Growth`.
**Can Cocorra test whether it predicts anything?** — Technically yes, the join is trivial. **INFERENCE** — practically constrained by cell size: sixteen types across a small user base yields cells too thin to distinguish signal from noise.
**RECOMMENDATION** — Test the four dichotomies (E/I, S/N, T/F, J/P), not sixteen types. Test one hypothesis with a clear prior: do E-types activate the mic at a higher rate than I-types? If even that shows nothing at adequate sample size, MBTI is decoration and the onboarding step should be reconsidered on friction grounds alone.

### INVESTMENT DECISION
**RECOMMENDATION** — Profiles are near-completely uninstrumented, but this is correctly a **low priority**: profiles are a supporting surface, not the core loop. Do not spend instrumentation effort here ahead of FI-1's three room events. The one exception is the MBTI dichotomy test, which requires no new instrumentation and interrogates one of the product's two stated differentiators.

---

## FI-10 — Support System

### Stage 1 — ADOPTION
**Measure** — Tickets and chats per 1,000 active users. **Can measure?** **YES** in the database; **FACT** — no analytics endpoint exposes it. **Reliability: HIGH** (data), **NOT EXPOSED** (surface).

### Stage 2 — REPEAT USAGE
**Measure** — Repeat contacters. **Can measure?** **PARTIALLY** — `SupportTicket.UserId` is nullable (anonymous submission is permitted), and `SupportChat.UserId` is a `string` rather than a `Guid`, unlike every other user reference in the schema. **INFERENCE** — that type mismatch makes joining support activity to the rest of a user's behaviour awkward and error-prone, which is likely part of why no analytics endpoint exists for it. **Reliability: MEDIUM.**

### Stage 3 — ENGAGEMENT QUALITY
**Measure** — First response time; resolution time; reopen rate. **Can measure?** **PARTIALLY** — chat resolution from `ClosedAt − CreatedAt` **YES**; first response computable from `SupportMessage.CreatedAt` + `IsFromAdmin` but **not computed**; ticket resolution **NO** (string status, no `ResolvedAt`). **Reliability: MEDIUM.**

### Stage 4 — USER VALUE SIGNAL
**Measure** — Ticket volume by type as a product-defect signal.
**INFERENCE** — With no error tracking anywhere in the stack (`06-blind-spots.md` §9: errors go to `ILogger` → Docker stdout, unpersisted), `SupportTicketType.TechnicalProblem` volume is currently Cocorra's **only** systematic reliability indicator. A spike is the closest thing the platform has to an outage alarm. That is a fragile position, but while it holds, the metric deserves prominence rather than the invisibility it currently has.
**Reliability: MEDIUM as a proxy; the underlying signal is real but lagging, filtered by users' willingness to complain, and biased toward loud failure modes over silent ones.**

### Stage 5 — RETENTION IMPACT
**Measure** — Do users who contact support churn more? **Can measure?** **CORRELATION ONLY**, with the churn-invisibility problem again: the users who churned after a bad support experience may have deleted their accounts. **Reliability: LOW.**

### INVESTMENT DECISION
**RECOMMENDATION** — Expose support metrics on the dashboard. The data exists, it is reasonably reliable, and it is currently the product's only reliability signal. This is a **query and a route**, not a data model change.

---

## Cross-Feature Investment Summary

| Feature | Adoption | Repeat | Quality | Value | Retention | Overall confidence | Recommended posture |
|---|:--:|:--:|:--:|:--:|:--:|:--:|---|
| **Room stage participation** (FI-1) | HIGH | HIGH | **LOW** | MEDIUM | LOW | **MEDIUM** | Instrument first, then decide. Highest priority. |
| **Room hosting / supply** (FI-2) | HIGH | HIGH | MEDIUM | MEDIUM | MEDIUM | **MEDIUM-HIGH** | Analyse now. Best ROI at zero instrumentation cost. |
| **Onboarding** (FI-3) | HIGH | HIGH | HIGH | HIGH | MEDIUM | **HIGH** | Fix the queries, not the code. |
| **Direct messaging** (FI-4) | HIGH | HIGH | MEDIUM | MEDIUM | LOW | **MEDIUM** | Measure reciprocity before investing. |
| **Friends** (FI-5) | HIGH | MEDIUM | MEDIUM | MEDIUM | LOW | **MEDIUM** | Blocked on the discovery question. |
| **Notifications** (FI-6) | MEDIUM | NOT POSSIBLE | LOW | MEDIUM | LOW | **LOW** | Fix delivery measurement first. |
| **Safety / moderation** (FI-7) | HIGH | MEDIUM-HIGH | MEDIUM | LOW | NOT POSSIBLE | **MEDIUM** | Add report-rate-by-category now. |
| **In-room group chat** (FI-8) | — | — | — | — | — | **NONE** | Cheap existence check first. |
| **Profiles / MBTI** (FI-9) | LOW | NOT POSSIBLE | NOT POSSIBLE | LOW | LOW | **LOW** | Deprioritise, except the MBTI dichotomy test. |
| **Support** (FI-10) | HIGH | MEDIUM | MEDIUM | MEDIUM | LOW | **MEDIUM** | Expose it. Query + route only. |

---

## The Three Conclusions That Matter

**1. Cocorra measures the edges of its core loop and none of its middle (FACT + INFERENCE).**
Joins and mic activations are reliable. Hand raises, stage promotions, time in room, and in-room chat are entirely absent. The product can see *that* listener→speaker conversion moved and never *why*. Three events would close this.

**2. The most valuable currently-available analysis is on the supply side, and nobody is running it (INFERENCE).**
Host count, host retention, rooms per host, distinct non-host speakers per room, and audience return per host are all computable today from verified data, and none appear in any endpoint. In a marketplace this small, supply is the leading indicator and it is unwatched.

**3. No causal claim about any feature is currently supportable (FACT).**
No experimentation infrastructure, no reliable per-user activity signal, and hard deletes that remove the most-churned users from every cohort. The single best causal opportunity available without new infrastructure is the **approval-latency natural experiment** (FI-3, Stage 5), because review latency is close to exogenous to the user. It should be run before any A/B framework is contemplated.
