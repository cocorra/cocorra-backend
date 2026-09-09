# Cocorra Analytics — Mobile Integration Guide

> **For**: the Cocorra Flutter / Mobile team
> **Generated**: 2026-09-02 | **Backend state**: `main` @ post-hardening
> **Supersedes**: `MOBILE_TRACKING_GUIDE.md` in the repository root, which remains accurate but predates the read models, trust metadata, core-loop events and the timezone contract.

---

# SECTION 1 — Executive Summary

## What the backend now has

Over the last phase the backend went from "eleven endpoints with one trustworthy metric" to a decision-driven analytics platform:

| Layer | State |
|---|---|
| **Event pipeline** | Every event has a stable `EventId` with a unique constraint. Flush failures retry with backoff, duplicates fall back to per-row inserts, exhausted batches go to a dead-letter table. Nothing is silently discarded. |
| **Read models** | Five aggregate tables retained **indefinitely**, giving Cocorra trend history beyond the 180-day raw window for the first time. |
| **Metric contracts** | 26 metrics with an executable contract — formula, population, exclusions, limitations, validation method. A metric without one fails the build. |
| **Trust metadata** | Every analytics response carries `Meta` with a per-metric trust level. Previously always `null`. |
| **Core-loop events** | Ten new events instrumenting the stage flow — hand raises, promotions, mic segments, room go-live. **Shipped but currently disabled** behind two feature flags. |
| **Observability** | `GET /Analytics/System/Health` reports drop counts, dead letters, aggregation lag and snapshot gaps. |

## What this means for you

### ✅ NO MOBILE WORK REQUIRED

**Everything in the core product loop is already captured server-side from the calls you already make.** Room joins, mic toggles, hand raises, stage promotions, kicks, room lifecycle, registration, voice verification, activation, MBTI, friend requests, messages, reports, push delivery — all of it.

**Do not add client-side events for any of these.** Doing so would double-count and corrupt the dashboards. The backend derives *who* from the JWT, never from your payload.

### ⚠ MOBILE WORK REQUIRED

Exactly **two** items, and only one is urgent:

| # | Item | Priority |
|:--:|---|---|
| **1** | **Persist the `CocorraSessionId` cookie across requests and app restarts** — or tell us you cannot, so we can replace the mechanism. See §4. | **HIGH** |
| **2** | Apply `Meta.display.suggestedDisplayOffsetMinutes` when displaying any hour-of-day or daily analytics figure. Only relevant if the app shows analytics. See §6. | MEDIUM |

### 🔵 OPTIONAL FUTURE MOBILE WORK

Three client-observed events are already allowlisted and ready whenever you want the funnel data they unlock. Nothing breaks without them. See §3.

### 🔴 BLOCKED / DEFERRED

| Item | Blocked on |
|---|---|
| **AN-035** — replacing the session signal | A production measurement (R-4) **plus** your answer to §4 |
| **AN-032** — group-chat analytics | A production query that has not been run |
| **AN-044** — acquisition attribution | A product decision about what sources exist |
| **AN-013** — soft delete | A **data-protection decision**, not engineering |

---

# SECTION 2 — Mobile Responsibilities

| Requirement | Why | Priority | Mobile Action Required | Backend Already Handles? |
|---|---|:--:|---|:--:|
| **Persist `CocorraSessionId` cookie** | Session grouping and the legacy retention metric depend on it. If it is not persisted, every request looks like a new session | **HIGH** | Configure a persistent cookie jar on your HTTP client, **or confirm you cannot** so AN-035 can proceed | Cookie is issued; **persistence is client-side and unverified** |
| **Do not duplicate server events** | Double counting corrupts every rate | **HIGH** | Audit for any client-side `room_joined` / `mic_on` / `registered` analytics calls and remove them | ✅ Yes — all emitted server-side |
| **Apply the display timezone offset** | Server computes in UTC; users are UTC+2/+3. An unconverted hour chart points at a slot 2–3h off | MEDIUM | Read `Meta.display.suggestedDisplayOffsetMinutes`, apply for display only | ✅ Offset returned on every response |
| **Never send local times** | Mixed timezones in one column is unrecoverable | MEDIUM | Send UTC ISO-8601 for any `from` / `to` parameter | ✅ Server treats input as UTC |
| **Render "not measured" as a gap, never 0** | A 0 for an uninstrumented step is a fabricated finding | **HIGH** *(if the app shows analytics)* | Branch on `isMeasured` / `null`, never coerce | ✅ Server returns `null` + a reason |
| **AN-032** — in-room chat volume | If in-room chat is substantial, "passive" participants may be actively typing, and the Active-vs-Passive metric needs reinterpreting | LOW | **Answer one question**: does the app have in-room group chat, and roughly how much is it used? See §5 | ❌ Measurement not run |
| **AN-035** — session signal replacement | The cookie mechanism may not survive on mobile | MEDIUM | Depends on your answer to §4. If cookies cannot persist, implement a client-generated session ID header | ❌ Blocked on evidence |
| **AN-036** — local-time context | See timezone row above | MEDIUM | Display-side only | ✅ Backend complete this phase |
| **AN-045** — `UserEvents` partitioning | Table growth | NONE | **No mobile involvement.** Listed here only because it appeared on a mobile-adjacent list; it is a database decision deferred pending a volume measurement | N/A |
| Client UI funnel events | Where users abandon *before* hitting an API | LOW | Optional — see §3 | ✅ Endpoint and allowlist ready |
| FCM token lifecycle | Push delivery analytics | NONE | **Already correct.** Keep calling `UpdateFcmToken`; commit `dc1c933` fixed the delivery bug and AN-024 added the regression guard | ✅ Yes |

---

# SECTION 3 — Event Integration Contracts

## 3.1 Server-authoritative events — DO NOT DUPLICATE

These fire the moment your existing request reaches the API or Hub. There is no client code and there must not be.

| Event | Your trigger | Notes |
|---|---|---|
| `user_registered` | Register | |
| `email_confirmed` | Email confirmation | |
| `voice_verification_submitted` | Register + re-record voice | |
| `voice_verification_result` | Admin review | |
| `activation_completed` | Admin approval | **Idempotent** — deterministic key, duplicate-safe |
| `user_status_changed` | Admin status change | Only durable record of a transition |
| `mbti_submitted` | `SubmitMbti` | |
| `room_create_started` | **CLIENT** — see §3.2 | |
| `room_created` | Create-room REST | |
| `room_join_requested` | Join REST | |
| `room_join_approved` | Host-approve REST | Tracked against the **host** |
| `room_joined` | `RoomHub.JoinRoom` | **Schema v2** — carries `isHost`, `isRejoin`, `entrySource` |
| `room_left` | `RoomHub.LeaveRoom` + auto on socket drop | |
| `mic_activated` | `RoomHub.ToggleMic(muteStatus: false)` | |
| `speaking_time_logged` | Host ends room | |
| `room_ended` | Host ends room / disconnects | |
| `message_sent` | Send message | Carries `isFirstMessageToRecipient` |
| `friend_request_sent` / `_accepted` | Friend endpoints | |
| `user_reported` / `user_blocked` | Report / block endpoints | |
| `account_deleted` | Delete-account flow | |
| `push_send_attempted` / `push_send_result` | Server-side FCM call | Regression guard for `dc1c933` |
| `media_session_event` | **LiveKit webhook** — server to server | |

### Flag-gated core-loop events — still server-authoritative, currently off

Behind `Analytics:EnableNewEventEmission` and `Analytics:EnableHighFrequencyEvents`. **You need do nothing when they turn on** — they fire from Hub methods you already call.

| Event | Your trigger | Gate |
|---|---|---|
| `room_went_live` | Create-as-live or start-scheduled | New events |
| `stage_promoted` | `RoomHub.ApproveToStage` | New events |
| `stage_demoted` | `RoomHub.MoveToAudience` | New events |
| `participant_kicked` | `RoomHub.KickUser` | New events |
| `speaker_time_extended` | `RoomHub.GrantExtraTime` | New events |
| `speaker_time_exhausted` | `RoomHub.ToggleMic` when time is up | New events |
| `operation_failed` | A refused join or promotion | New events |
| `hand_raised` | `RoomHub.RaiseHand` | High-frequency |
| `hand_lowered` | `RoomHub.LowerHand` + approval path | High-frequency |
| `mic_deactivated` | Five close paths: self-mute, demotion, kick, room end, disconnect | High-frequency |
| `room_reminder_toggled` | Toggle-reminder REST | New events |
| `moderation_action_taken` | Admin moderation | New events |

**One behaviour worth knowing about**: `hand_raised` fires on **every** call, including a repeat raise. Raise → lower → raise is deliberately two events, because two asks for the stage is two units of real demand. Do not debounce client-side to "help" — that would understate demand.

## 3.2 Client-observed events — YOUR responsibility

Only three event types are accepted from clients. Anything else is rejected with `400`, deliberately: a client that could emit `activation_completed` could forge the funnel.

```
POST /api/events/track
Authorization: Bearer <JWT>          ← required
Content-Type: application/json

{ "eventType": "feature_viewed", "properties": { "feature": "room_list" } }
```

| Field | Contract |
|---|---|
| `eventType` | **Must** be one of the three below. Anything else → `400`. |
| `properties` | Free-form JSON object, optional. Keep keys stable — they become queryable. |

**Never send `userId`** — it is read from the JWT. A payload `userId` is ignored.

| Event | Purpose | When it happens | Exact trigger | Required data | Optional data | Priority |
|---|---|---|---|---|---|:--:|
| `room_create_started` | Measures host-side abandonment: how many hosts open the create-room screen and never finish. **The only way to see it** — the server sees nothing until submit | User opens the create-room screen | Screen mount / navigation event | none | `{ "entryPoint": "home" \| "profile" \| … }` | **Highest value of the three** |
| `notification_opened` | Push effectiveness. Attributes a room join to the notification that caused it | User taps a push notification | Notification tap handler, **before** navigation | none | `{ "notificationId": "<guid>", "type": "room_reminder" }` | Medium |
| `feature_viewed` | Screen-level funnel visibility | User opens a tracked screen | Screen mount | `{ "feature": "<stable_snake_case_name>" }` | any | Low |

### Behavioural contract for client events

| Property | Requirement |
|---|---|
| **Authority** | Client. The server does not verify these happened. |
| **Duplicate behaviour** | **Not deduplicated.** No `eventKey` is set for client events. Two calls = two rows. Debounce screen-mount events yourself if a screen remounts on rotation or tab switch. |
| **Offline behaviour** | **Do not queue and replay.** See the warning below. |
| **Failure behaviour** | `POST /api/events/track` is fire-and-forget. **Ignore the response.** Never block UI, never retry, never show an error. A tracking failure must be invisible to the user — the backend holds the same contract internally (`Track` never throws). |
| **Backend dependency** | None. The endpoint has existed since the original event work. |

> **⚠ Do not implement an offline event queue that replays on reconnect.**
>
> Events are timestamped **server-side at receipt** (`OccurredAtUtc`), not from a client-supplied time. A queue flushed after two hours offline would stamp every event with the reconnect time, producing a spike that looks like a burst of real activity at a moment nothing happened.
>
> If offline coverage genuinely matters for a specific event, tell us and we will add a client-supplied `occurredAtUtc` to the contract with server-side clock-skew bounds. **Do not work around it client-side** — a silently wrong timestamp is worse than a missing event, because it produces a confident false finding rather than an obvious gap.

## 3.3 Hybrid flows — responsibility boundaries

| Flow | Client | Server |
|---|---|---|
| **Create a room** | `room_create_started` on screen open | `room_created` on submit; `room_went_live` on start |
| **Push → join** | `notification_opened` on tap | `room_joined` on Hub join; `push_send_attempted` / `push_send_result` at send |
| **Join a room** | *(nothing)* | Everything. `room_join_requested` → `room_join_approved` → `room_joined` |
| **Take the stage** | *(nothing)* | `hand_raised` → `stage_promoted` → `mic_activated` → `mic_deactivated` |

**The boundary rule**: the client reports *intent and attention* — things only the device can see. The server reports *outcomes* — things it can verify. If the server can observe it, the server owns it.

---

# SECTION 4 — Session / Activity Measurement

## ⚠ This is the one item that needs your attention

`SessionTrackingMiddleware` issues an HTTP cookie:

```
CocorraSessionId = <guid>
HttpOnly, Secure, SameSite=Strict, Expires: +7 days
```

Every event stamps that GUID into `UserEvent.SessionId`, and `session_started` is emitted once per session ID per authenticated user.

## What the backend cannot know

**Whether the Flutter client persists that cookie.** From the server, a request without the cookie and a request from a client that discarded it are identical.

This matters because many Flutter HTTP setups do **not** persist cookies by default:

| Client setup | Behaviour |
|---|---|
| `package:http` with no cookie handling | Cookie dropped. **Every request is a new session.** |
| `dio` without `CookieManager` | Cookie dropped. Same outcome. |
| `dio` + `PersistCookieJar` | Correct — survives restarts |
| `HttpOnly` + `SameSite=Strict` | Not enforced by native HTTP clients the way a browser enforces them, but also not automatically handled |

### If the cookie is not persisted

| Consequence | Severity |
|---|---|
| `session_started` fires roughly per request burst instead of per session | **Counts inflated by an unknown multiple** |
| `UserEvent.SessionId` is near-unique per event | Useless as a grouping key |
| The legacy retention metric (M-102-LEGACY) rests on `session_started` | Already graded **UNRELIABLE** for exactly this reason |

## What the backend already did about it

**It routed around the problem rather than waiting.** The replacement metric **M-102 Weekly Return Rate** deliberately uses server-emitted `room_joined` instead of `session_started`, and its contract says so in writing:

> *Exclusions: "`session_started` is not consulted: the signal is server-emitted `room_joined`."*

**No metric in the served set depends on `session_started` today.** That is what makes this safe to leave unresolved — but it also means Cocorra currently has **no session-duration or app-open metric at all**.

## What we need from you

**One answer, and it is a five-minute check:**

1. Does the app's HTTP client persist cookies across requests **and** across app restarts?
2. If not, can it, without side effects?

| Your answer | What happens next |
|---|---|
| **Yes, persisted** | We run R-4 in production (distinct `SessionId` per user per day vs distinct active users per day). If the ratio is near 1, the mechanism works and AN-035 closes with no code change. |
| **No / cannot** | AN-035 proceeds: you send a client-generated `X-Cocorra-Session-Id` header — a GUID created on cold start, persisted in secure storage, rotated after a defined idle period. We accept it in place of the cookie. **~15 lines on your side, one middleware change on ours.** |

## What client-side sessions would enable

Currently unmeasurable, and would become measurable:

- Session count and duration per user
- App opens that lead to a room join versus app opens that do not
- Whether push notifications drive sessions or only in-session navigation
- Time-to-first-room-join within a session

**Is it required now?** **No.** Every metric on the dashboard works without it. It is the difference between measuring *participation* (working today) and measuring *engagement* (not measured at all).

> **We are not asking you to add a tracking SDK.** There is no SDK, and the architecture does not want one — a third-party analytics SDK was explicitly rejected because `MentalHealth` rooms and voice recordings raise the bar on sending behavioural data to an external processor. The ask is one header, or a cookie jar.

---

# SECTION 5 — Group Chat / Social Analytics

## AN-032 status: **NOT APPLICABLE to engineering — DEFERRED pending one measurement**

The backlog scopes AN-032 as **"measurement only — establish whether the behaviour is material before building."** It is a production query, not code, and it has not been run.

## The actual question

**Does Cocorra have in-room group chat, and is it used?**

## Why anyone cares

The **Active vs Passive Participation** metric classifies a participant as "passive" if they never activated a microphone. If in-room text chat exists and is substantial, then a meaningful share of "passive" participants are **actively participating by typing** — and the metric's name is wrong in a way that would change product conclusions.

| If in-room chat volume is… | Then |
|---|---|
| **Negligible** | The gap closes with **no code at all**. Active-vs-Passive stands as-is. |
| **Substantial** | Active-vs-Passive needs renaming and reinterpreting, and in-room messages need instrumenting as a participation signal |

**Deciding which before building is the entire point of the item.** Building message persistence and instrumentation for a feature nobody uses would be the more expensive mistake.

## What we need from you

Not code. **Answers:**

1. Does the app expose in-room group chat during a live room?
2. If yes — is it prominent or buried? Roughly what share of participants use it?
3. Is in-room chat persisted through the same `Messages` path as DMs, or is it ephemeral / SignalR-only?

Question 3 decides whether the backend can measure this at all: if in-room chat is ephemeral and never written to `Messages`, no query can recover it and instrumentation becomes genuinely necessary rather than optional.

## What IS measured today

Direct messaging and the friend graph — `GET /Analytics/Social` (M-701):

- Friend requests sent, accepted, acceptance rate, median hours to accept
- Distinct senders, max requests by a single sender *(spam-wave detection)*
- Messages sent, distinct message senders
- `conversationsStarted` — from `isFirstMessageToRecipient` (AN-030)

**Reciprocity is reported before volume, deliberately.** A high volume of one-directional messages is a warning sign — plausibly unwanted contact — not engagement growth.

---

# SECTION 6 — Timezone / Local-Time Context

## The contract

| Layer | Rule |
|---|---|
| **Storage** | **UTC, always.** Every timestamp column is UTC. Non-negotiable. |
| **Server calculation** | **UTC, always.** Daily buckets are UTC days; hourly buckets are UTC hours. A server that bucketed in local time would produce series that shift under it twice a year. |
| **Request parameters** | Send **UTC ISO-8601**. `from` / `to` are treated as UTC. **Never send local time.** |
| **Response timestamps** | UTC. Field names ending `Utc` are explicit about it. |
| **Display** | **Client converts.** The server states the offset; it never applies it. |

## The offset

Every analytics response now carries:

```json
{
  "data": { },
  "meta": {
    "computedAtUtc": "2026-09-02T11:04:00Z",
    "trustLevel": "Verified",
    "metrics": [ ],
    "display": {
      "timeZone": "UTC",
      "suggestedDisplayOffsetMinutes": 180
    }
  }
}
```

`suggestedDisplayOffsetMinutes` defaults to **180** (UTC+3) and is configurable server-side via `Analytics:DisplayTimeZoneOffsetMinutes`.

`GET /Analytics/Supply/Health` also returns `suggestedDisplayOffsetMinutes` on the payload itself, for the schedule heatmap.

## What mobile should do

| Do | Don't |
|---|---|
| Add the offset to any **hour-of-day** figure before display | Convert stored values or send converted values back |
| Add it to **daily buckets** if you label them as local days | Use the **device** timezone for these charts |
| Label the axis so the reader knows which it is | Silently mix UTC and local on one chart |

> **Use the server's offset, not `DateTime.now().timeZoneOffset`.**
>
> This is counter-intuitive and it is deliberate. The server offset represents the **audience's** predominant timezone; the device offset represents **one viewer's**. An admin in London reading Cocorra's peak-hours chart needs to know when the *Cocorra audience* is active, not what time it is in London. Using the device offset would shift the chart by the viewer's location and make two admins in different countries disagree about when the platform's peak is.
>
> For a user-facing "your room starts in 20 minutes", the device timezone is correct. For analytics, it is not.

## Not yet served

`Meta` does **not** yet carry `window.isPartialPeriod` or `freshness`, which the dashboard blueprint's frontend contract expects. Partial-period detection is available on `GET /Analytics/Platform/Health` as a top-level `isPartialPeriod` field, and freshness is available via `GET /Analytics/System/Health`. Listed as a known limitation rather than implied.

---

# SECTION 7 — API Integration

**All routes below are real and verified against `Router.AnalyticsRouting` at HEAD.**

## Common contract

| Property | Value |
|---|---|
| **Authentication** | JWT bearer. Default policy requires `VerificationStatus = Active`. |
| **Authorization** | `Admin` **or** `Coach`, except where marked Admin-only |
| **Date parameters** | `from`, `to` — UTC, optional. Default: last 30 days *(except Platform Health: 7 days)* |
| **Timezone** | All UTC. Apply `Meta.display.suggestedDisplayOffsetMinutes` for display |
| **Caching** | 10 minutes server-side; concurrent identical requests share one query |
| **Errors** | `400` invalid range or parameter · `401` no/expired token · `403` wrong role · `200` with empty payload when there is simply no data |
| **Trust metadata** | `Meta.trustLevel` (weakest component) + `Meta.metrics[]` (per-metric contract) on every response |

> **Most Cocorra mobile apps will consume none of these.** They are admin/coach analytics, not end-user features. Integrate only what a Coach or Admin screen in the app actually shows.

## Endpoints

| Route | Purpose | Auth | Key parameters | Trust |
|---|---|---|---|---|
| `GET /Analytics/Platform/Health` | **North star + inputs, with period comparison.** The landing view | Admin, Coach | `from`, `to`, `compareTo=previous_period\|none` | Mixed, per metric |
| `GET /Analytics/Supply/Health` | Active hosts, host retention, concentration, schedule heatmap | Admin, Coach | `granularity`, `from`, `to` | VERIFIED |
| `GET /Analytics/Participation/StageFunnel` | **Stage funnel** (M-400) | Admin, Coach | `from`, `to` | **EXPERIMENTAL** |
| `GET /Analytics/Activation/Funnel` | Sequential onboarding funnel with elapsed time | Admin, Coach | `steps`, `from`, `to` | VERIFIED |
| `GET /Analytics/Return/Weekly` | Weekly return rate by cohort | Admin, Coach | `from`, `to` | VERIFIED |
| `GET /Analytics/Return/CohortGrid` | 8-week cohort grid | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Safety/ReportRate` | Reports per 1,000 joins by room category | **Admin only** | `from`, `to` | VERIFIED |
| `GET /Analytics/Review/Latency` | Review latency percentiles + queue depth | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Support` | Support volume and response times | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Social` | Friend graph and message volume | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Mbti/Dichotomies` | MBTI vs speaking, four dichotomies | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Decisions` | Decision Center change detection | Admin, Coach | — | Inherited |
| `GET /Analytics/Metrics/Registry` | **All 26 metric contracts** | Admin, Coach | — | N/A |
| `GET /Analytics/System/Health` | Pipeline health, drops, dead letters, lag, gaps | Admin, Coach | — | VERIFIED |
| `POST /Analytics/System/Backfill` | Replay read models over a range | **Admin only** | `from`, `to`, `force` | — |
| `GET /Analytics/Summary` | Legacy composite | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Users/Growth` | Registrations + reconstructed status | Admin, Coach | `granularity`, `from`, `to` | Mixed |
| `GET /Analytics/Rooms` | Room counts and category mix | Admin, Coach | `from`, `to`, `limit` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Participation` | Spoken time, distinct speakers, peak hours | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/Reports` | Report counts by status and category | Admin, Coach | `from`, `to`, `limit` | VERIFIED |
| `GET /Analytics/Rooms/Active` | Rooms ranked by joins | Admin, Coach | `from`, `to`, `limit` | VERIFIED |
| `GET /Analytics/PeakHours` | Joins by UTC hour | Admin, Coach | `from`, `to` | CONDITIONALLY RELIABLE |
| `GET /Analytics/VoiceVerification/DropOff` | Verification stage counts | Admin, Coach | `from`, `to` | VERIFIED |
| `GET /Analytics/Participation/ActiveVsPassive` | Speakers vs listeners | Admin, Coach | `from`, `to` | Mixed |
| `GET /Analytics/Funnel` | **Legacy** non-sequential funnel | Admin, Coach | `steps`, `from`, `to` | Superseded |
| `GET /Analytics/Retention` | **Legacy** exact-day retention | Admin, Coach | `cohortEvent`, `activeEvent`, … | **UNRELIABLE — do not display** |
| `POST /api/events/track` | **Client event ingestion** | Any authenticated user | body: `eventType`, `properties` | — |
| `POST /Api/V1/Notifications/UpdateFcmToken` | FCM token registration | Authenticated | token in query or body | — |

## Two response rules that will bite if ignored

**1. `null` is not `0`.**

```json
{
  "step": "Raised hand",
  "eventType": "hand_raised",
  "isMeasured": false,
  "count": null,
  "observedParticipations": null,
  "notMeasuredReason": "'hand_raised' has never been recorded. This step is not instrumented, so its count is unknown rather than zero."
}
```

Render a labelled gap. Most charting libraries coerce `null` to `0` by default — **that default is a fabricated finding.** `notMeasuredReason` is the text to display.

**2. Read `Meta.trustLevel` before rendering a number.**

| Level | Treatment |
|---|---|
| `Verified` | Normal, small badge |
| `ConditionallyReliable` | Badge **plus the condition inline** from `Meta.metrics[].limitations` — one line next to the number, **not a tooltip** |
| `Experimental` | Badge plus de-emphasis, "not yet validated" note |
| `Unreliable` | **Do not display at all** |

A tooltip is not read by someone scanning a screen and does not survive a screenshot.

---

# SECTION 8 — Rollout Plan for Mobile

## Phase 1 — What you can safely ignore, right now

**Everything.** No mobile change is required for the analytics platform to work.

The backend's core-loop instrumentation is complete and server-side. Your existing calls already produce every event the dashboard needs. Ship your normal roadmap.

**One 5-minute task**, and it is a *check*, not a change:

- [ ] Confirm no client-side analytics events duplicate a server event from §3.1

## Phase 2 — Minimum required integration

**Total: one small change plus one answer.**

- [ ] **Answer §4**: does the HTTP client persist cookies across requests and restarts? *(the answer alone unblocks AN-035)*
- [ ] If not, and if session metrics are wanted: implement `X-Cocorra-Session-Id` — GUID on cold start, secure storage, rotate after idle
- [ ] **Answer §5**: does in-room group chat exist and is it used?
- [ ] If the app shows analytics: apply `Meta.display.suggestedDisplayOffsetMinutes` to hour-of-day charts
- [ ] If the app shows analytics: render `isMeasured: false` / `null` as a labelled gap, never `0`

## Phase 3 — Optional / advanced instrumentation

Do these when the funnel questions they answer become worth asking:

- [ ] `room_create_started` on create-room screen open — **highest value.** The only way to see host-side abandonment; the server sees nothing until submit
- [ ] `notification_opened` on push tap — attributes joins to notifications
- [ ] `feature_viewed` on tracked screens — screen-level funnel visibility
- [ ] Client-supplied `occurredAtUtc` (**needs a backend change first — talk to us**) if offline coverage matters

**Not on any roadmap, and deliberately**: a third-party analytics SDK. Rejected on privacy grounds — `MentalHealth` rooms and voice recordings raise the bar on sending behavioural data to an external processor.

---

# SECTION 9 — Testing Checklist

Scenarios that correspond to real Cocorra behaviour. Verify each against a staging database with the flags enabled.

## Room join and reconnect

| Scenario | Action | Expected event / behaviour | How to verify |
|---|---|---|---|
| Normal join | REST join, then `RoomHub.JoinRoom` | One `room_joined`, `isHost=false`, `isRejoin=false`, `schemaVersion=2` | `SELECT * FROM UserEvents WHERE EventType='room_joined' AND RoomId=@id ORDER BY OccurredAtUtc DESC` |
| Host joins own room | Host calls `JoinRoom` | `room_joined` with `isHost=true` | Same query; confirm `isHost` in `PropertiesJson` |
| **Reconnect after network drop** | Kill network, restore, Hub reconnects | A **second** `room_joined` with `isRejoin=true`. **This is expected and correct** — distinct-user counting neutralises it | Two rows, second has `isRejoin=true` |
| **Reconnect does not lose the join time** | Join, drop, rejoin | `RoomParticipant.JoinedAt` is the **original** join, not the rejoin (AN-031) | `SELECT JoinedAt, LeftAt FROM RoomParticipants` |
| Leave | `LeaveRoom` | `room_left`; `LeftAt` populated | `LeftAt IS NOT NULL` |
| Kill app without leaving | Force-quit | `room_left` from the disconnect handler | Row appears within the disconnect timeout |
| Join a non-live room | `JoinRoom` on a Scheduled room | `HubException` + `operation_failed` `{operation:"room_join", reason:"room_not_live"}` | `WHERE EventType='operation_failed'` |
| Join without REST join first | `JoinRoom` directly | `HubException` + `operation_failed` `reason:"not_a_participant"` — **this reason indicates a client sequencing bug, so it should be zero in a correct client** | Same query |
| Join while pending approval | `JoinRoom` before host approves | `operation_failed` `reason:"pending_host_approval"` | Same query |

## Voice interaction

| Scenario | Action | Expected | How to verify |
|---|---|---|---|
| Unmute on stage | `ToggleMic(false)` | One `mic_activated` | `WHERE EventType='mic_activated'` |
| Mute again | `ToggleMic(true)` | `mic_deactivated` with `segmentSeconds`, `reason:"self_muted"` | Check `segmentSeconds` ≈ elapsed |
| Unmute → mute → unmute | Toggle twice | **2** `mic_activated`, **1** `mic_deactivated` | Counts must match exactly |
| Time exhausted | Unmute past the allowance | `speaker_time_exhausted` **then** the exception | Event exists **despite** the throw |
| Demoted while unmuted | Host demotes a live speaker | `mic_deactivated` `reason` reflecting demotion **and** `stage_demoted` | Both rows present |
| Kicked while unmuted | Host kicks a live speaker | `mic_deactivated` **and** `participant_kicked` | Both rows |
| Room ends while unmuted | Host ends the room | `mic_deactivated` — **easy to miss, verify explicitly** | Row exists for the open segment |

## Stage flow

| Scenario | Action | Expected | How to verify |
|---|---|---|---|
| Raise hand | `RaiseHand` | `hand_raised`, `wasAlreadyRaised=false` | |
| Raise twice | `RaiseHand` × 2 | **2** `hand_raised`, second with `wasAlreadyRaised=true`. **Do not debounce** | |
| Lower own hand | `LowerHand` | `hand_lowered` `wasApproved=false`, `reason:"self_lowered"` | |
| Promoted after raising | Host approves | `stage_promoted` **against the participant** + `hand_lowered` `wasApproved=true` | `UserId` = participant, not host |
| Promoted without raising | Host invites directly | `stage_promoted` with `viaHandRaise=false`; appears in `directPromotionsWithoutHandRaise` | `GET /Analytics/Participation/StageFunnel` |
| **Stage full** | Approve when at `StageCapacity` | `operation_failed` `{operation:"stage_promotion", reason:"stage_at_capacity"}` **against the participant** | Confirms whether the stage or the host is the bottleneck |
| Funnel monotonicity | Run several journeys | Each step ≤ the previous | `GET /Analytics/Participation/StageFunnel` |

## Duplicate prevention and app lifecycle

| Scenario | Action | Expected | How to verify |
|---|---|---|---|
| Duplicate client event | Same `POST /api/events/track` twice | **2 rows.** Client events are not deduplicated — debounce screen mounts yourself | `WHERE EventType='feature_viewed'` |
| Disallowed event type | `POST` with `eventType: "activation_completed"` | `400`, no row written | Confirms the allowlist |
| Unauthenticated track | `POST` with no JWT | `401` | |
| Tracking failure invisible | Kill the network, trigger a client event | **No user-visible error, no retry, no UI block** | Manual observation |
| App restart | Cold start, authenticated | One `session_started` **if the cookie persisted**. If a new one fires on every launch, see §4 | `SELECT COUNT(DISTINCT SessionId) FROM UserEvents WHERE UserId=@id AND OccurredAtUtc >= @today` — **this is R-4** |
| Background → foreground | Background 10 min, return | **No** new `session_started` if the cookie persisted | Same query |
| Push tap | Tap notification | `notification_opened` **before** navigation, then `room_joined` | Both rows, ordered |
| **Push delivery** | Send a push to a valid token | `push_send_attempted` **and** `push_send_result` with `success=true`, correlated by `CorrelationId` | Attempt and result counts must reconcile |
| Push to a stale token | Send to a revoked token | `push_send_result` `success=false` with an `errorCode`, `tokenInvalidated=true` | Regression guard for `dc1c933` |

## Pipeline sanity

| Scenario | Action | Expected | How to verify |
|---|---|---|---|
| Burst load | Many rapid Hub actions | `eventsDroppedOnEnqueue` stays **0** | `GET /Analytics/System/Health` |
| Aggregation keeps up | Leave running an hour | `aggregationLagHours < 3`, `pipelineHealthy: true` | Same |
| No dead letters | Normal operation | `deadLetterBacklog: 0` | Same |

---

# SECTION 10 — Final Mobile Checklist

```
REQUIRED

[ ] No client-side event duplicates a server-authoritative event from §3.1
[ ] Answered §4 — does the HTTP client persist cookies across requests AND restarts?
[ ] Answered §5 — does in-room group chat exist, and is it used?
[ ] Analytics failures never surface to the user (no retry, no error, no UI block)
[ ] Any from/to parameters sent are UTC ISO-8601, never local time

REQUIRED IF THE APP DISPLAYS ANALYTICS

[ ] Meta.display.suggestedDisplayOffsetMinutes applied to hour-of-day charts
[ ] Server offset used, NOT DateTime.now().timeZoneOffset
[ ] isMeasured:false / null renders as a labelled gap using notMeasuredReason — never 0
[ ] Meta.trustLevel respected: Unreliable is not displayed at all
[ ] ConditionallyReliable conditions rendered inline, not in a tooltip

REQUIRED IF §4 ANSWER IS "COOKIES NOT PERSISTED" AND SESSION METRICS ARE WANTED

[ ] X-Cocorra-Session-Id implemented — GUID on cold start, secure storage, idle rotation
[ ] Backend middleware change agreed before shipping

TESTED

[ ] Reconnect: second room_joined with isRejoin=true; JoinedAt preserved
[ ] App restart: exactly one session_started if the cookie persisted
[ ] Background → foreground: no new session_started
[ ] Duplicate client events: understood as 2 rows; screen mounts debounced
[ ] Room join / leave / force-quit all produce the expected rows
[ ] Voice: unmute-mute-unmute yields 2 mic_activated, 1 mic_deactivated
[ ] Stage: raise twice yields 2 hand_raised (not debounced)
[ ] Stage full produces operation_failed against the participant
[ ] Push: attempted and result rows reconcile by CorrelationId

OPTIONAL — PHASE 3

[ ] room_create_started on create-room screen open   ← highest value
[ ] notification_opened on push tap
[ ] feature_viewed on tracked screens
```

---

# Questions, and who owns them

| Question | Owner | Blocks |
|---|---|---|
| Do cookies persist on the Flutter client? | **Mobile** | AN-035, all session metrics |
| Does in-room group chat exist and is it used? | **Mobile / Product** | AN-032, Active-vs-Passive interpretation |
| Is offline event coverage needed? | **Mobile / Product** | A backend contract change |
| Soft delete: what is the retention obligation for deleted users? | **Product / Legal** | AN-013 — **and the cost of delay is irreversible.** Every hard delete until this is answered biases every user rate upward, permanently |
| What acquisition sources exist? | **Product** | AN-044 |

**The soft-delete question is the only one on this list where waiting has a running cost.** The others can be answered whenever. That one destroys evidence daily.
