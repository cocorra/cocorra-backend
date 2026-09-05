# 09 — Decision-Driven Dashboard Architecture

> **Generated**: 2026-09-01 | **Phase**: Decision Intelligence
> **Depends on**: `07-decision-framework.md`, `07a-feature-investment-framework.md`, `07b-north-star-analysis.md`, `05-analytics-gap-analysis.md`
> **Scope**: Documentation only. This is a design, not an implementation.

---

## Design Principle

This dashboard is not organised by entity (Users / Rooms / Reports), which is how the current one is organised. It is organised by **decision**. Every section exists because someone has to decide something.

The derivation chain for every section:

```
DECISION
    ↓
QUESTION
    ↓
ANALYSIS
    ↓
METRIC
    ↓
VISUALIZATION
    ↓
DRILL-DOWN
```

A metric that cannot be traced back to a decision is not on this dashboard. **INFERENCE** — this is the main structural difference from the current implementation, whose eleven analytics endpoints are organised around database tables and consequently surface some metrics (MBTI distribution, average age) that no documented decision depends on.

### Availability labels

Each section states what it needs:

| Label | Meaning |
|---|---|
| **AVAILABLE NOW** | Buildable today from existing, audit-verified data. No application change. |
| **REQUIRES EVENT TRACKING** | Needs new events (see `06a-recommended-event-taxonomy.md`). |
| **REQUIRES DATA MODEL CHANGE** | Needs schema change (soft delete, `LeftAt`, enums). |
| **REQUIRES HISTORICAL DATA** | Needs accumulated history before it becomes meaningful. |

### Two rules held throughout

**Rule 1 — No invented thresholds.** **FACT** — no baseline exists for any Cocorra metric; the 180-day event window means even the available history is short. Decision rules below are therefore expressed as *directional* triggers ("declines materially against its own recent trend") rather than fabricated numbers ("drops below 40%"). **RECOMMENDATION** — set numeric thresholds only after four to six weeks of observed variance. Thresholds invented now would generate false alarms and train the team to ignore the dashboard, which is worse than having no alerts.

**Rule 2 — Charts must earn their place.** A number that is only ever read as a single current value is a KPI, not a line chart. Trends are used where the *shape* matters. Distributions are used where the *average lies* — which, for Cocorra's latency and participation metrics, it usually does.

---

## Dashboard Structure

```
┌─────────────────────────────────────────────────────────┐
│  0. DECISION CENTER          "What changed, and where?" │
├─────────────────────────────────────────────────────────┤
│  1. PLATFORM HEALTH          North Star + its 4 inputs  │
├─────────────────────────────────────────────────────────┤
│  2. SUPPLY HEALTH            Can we serve demand?       │
├─────────────────────────────────────────────────────────┤
│  3. ACTIVATION PIPELINE      Is the gate working?       │
├─────────────────────────────────────────────────────────┤
│  4. ROOM PARTICIPATION       Does the core loop work?   │
├─────────────────────────────────────────────────────────┤
│  5. SAFETY & TRUST           Is the platform safe?      │
├─────────────────────────────────────────────────────────┤
│  6. RETURN & REPEAT          Was it worth repeating?    │
├─────────────────────────────────────────────────────────┤
│  7. SOCIAL SURFACES          Do DMs and friends matter? │
├─────────────────────────────────────────────────────────┤
│  8. RELIABILITY              Does the product work?     │
├─────────────────────────────────────────────────────────┤
│  9. METRIC TRUST REGISTER    Which numbers can I trust? │
└─────────────────────────────────────────────────────────┘
```

**INFERENCE — why Supply Health sits at position 2, above the user-facing sections.** Cocorra is a two-sided marketplace with a very small supply side. Every user-side metric is bounded by room availability. Reading participation before supply invites the classic error of diagnosing a demand problem that is actually a supply problem. Supply is also the leading indicator: it moves weeks earlier.

---

# SECTION 1 — PLATFORM HEALTH

## Decision Supported
Is Cocorra delivering more value this week than last, and if not, which input broke?

## Primary Question
**How many verified users took part in a live conversation this week, and what constrained that number?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| **Weekly Participating Users (WPU)** — *North Star* | Distinct non-host `UserId` with `room_joined` in a rolling 7 days | **AVAILABLE NOW** |
| Speaking Conversion Rate | Distinct non-host `mic_activated` users ÷ WPU | **AVAILABLE NOW** |
| Rooms Gone Live | Rooms with ≥1 non-host participant (proxy) | **AVAILABLE NOW** (proxy) / **REQUIRES EVENT TRACKING** (exact) |
| Distinct Active Hosts | Distinct `Room.HostId` with a room this week | **AVAILABLE NOW** |
| New Activated Users | Distinct users with `activation_completed` | **AVAILABLE NOW** |
| Return Rate | Share of prior-week WPU who participated again | **AVAILABLE NOW** |

**Host exclusion is mandatory** — **FACT, Finding A**: hosts are auto-joined to their own rooms with an open mic. Including them makes WPU partly a room count and corrupts Speaking Conversion in both directions.

**Rooms Gone Live is a proxy** — **FACT, Finding C**: `StartScheduledRoomAsync` emits no event and writes no timestamp. The proxy undercounts rooms that went live and drew nobody, which is exactly the failure case worth seeing. Label it as a proxy on the dashboard.

## Visualization

- **KPI row** — WPU with week-over-week delta, plus the five supporting numbers.
- **One trend (line)** — WPU over the last 12 weeks. This is the only chart in the section. **INFERENCE** — the shape matters here (is growth flattening?), which is what earns a line.
- **Small multiples** — four sparklines, one per North Star input, beneath the KPI row. Enough to see direction; not enough to invite over-reading.

Deliberately **not** here: a pie chart of user statuses, MBTI distribution, average age. **INFERENCE** — none of these supports a documented decision, and all three appear in the current `/Analytics/Users/Growth` response.

## Drill-Down Path

```
WPU (this week)
   ↓
Which input moved?  →  Supply / Activation / Conversion / Return
   ↓
Section 2, 3, 4, or 6
   ↓
Per-room or per-cohort breakdown
   ↓
Underlying room_joined / mic_activated events
```

## Decision Rule

```
WPU declines while Rooms Gone Live also declines
  → SUPPLY problem. Go to Section 2. Do not change anything user-facing yet.

WPU declines while room supply holds steady
  → DEMAND or ACTIVATION problem. Check Section 3 (are new users arriving?)
    then Section 6 (are existing users returning?).

WPU rises while Speaking Conversion falls
  → Growth in passive attendance. Do NOT report this as an unqualified win.
    Go to Section 4.

WPU flat, all four inputs flat
  → Genuine plateau. This is the point at which a product intervention,
    not an analytics investigation, is warranted.
```

---

# SECTION 2 — SUPPLY HEALTH

## Decision Supported
Should Cocorra recruit more coaches, or help existing coaches run better rooms? These require entirely different investments and the data distinguishes them cleanly.

## Primary Question
**Is there enough room supply, is it concentrated in too few hosts, and are hosts staying?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| Distinct Active Hosts / week | Distinct `Room.HostId` with ≥1 room | **AVAILABLE NOW** |
| Rooms Created / week | Count of `Room` by `CreatedAt` | **AVAILABLE NOW** |
| Rooms per Host | Distribution, not mean | **AVAILABLE NOW** |
| Host Retention | Share of last month's hosts who hosted this month | **AVAILABLE NOW** |
| New Hosts / week | First-ever room for that `HostId` | **AVAILABLE NOW** |
| Supply Concentration | Share of rooms from the top 3 hosts | **AVAILABLE NOW** |
| Distinct Non-Host Speakers per Room | Hosting-quality proxy | **AVAILABLE NOW** |
| Audience Return per Host | Share of a host's participants who attend a later room by the same host | **AVAILABLE NOW** |
| Scheduled → Went Live | Conversion rate | **REQUIRES EVENT TRACKING** (GAP-07) |
| Schedule Coverage by Hour | Rooms by hour, **local time** | **AVAILABLE NOW** (UTC); local display is presentation-only |

**INFERENCE — this entire section is buildable today with zero instrumentation work, and none of it exists in the current dashboard.** `07a` FI-2 identifies it as the best available return on zero effort in the whole product.

## Visualization

- **KPI row** — Active Hosts, Rooms This Week, Host Retention.
- **Trend (line, dual axis)** — Distinct Active Hosts and Rooms Created over 12 weeks. **INFERENCE** — the two together reveal the failure mode that neither shows alone: rooms flat while hosts decline means fewer people carrying more load, which is fragile and looks fine on the headline number.
- **Distribution (histogram)** — Rooms per Host. **Not a mean.** **INFERENCE** — with a small coach pool, one prolific host makes the average meaningless; the shape is the finding.
- **Table** — Host leaderboard: rooms hosted, median participants, median distinct non-host speakers, audience return rate. Sortable.
- **Heatmap** — Room schedule coverage by day-of-week × hour-of-day, displayed in local time. **INFERENCE** — the one place a heatmap genuinely earns its place, because the decision ("when is coverage thin?") is inherently two-dimensional.

**Note on the heatmap (FACT)** — all analytics are UTC and the user base is MENA (UTC+2/+3). The heatmap must be rendered in local time or it will point coaches at the wrong slots (GAP-18).

## Drill-Down Path

```
Supply overview
   ↓
Host leaderboard
   ↓
Individual host  →  their rooms over time
   ↓
Individual room  →  participants, distinct speakers, reports filed
   ↓
Underlying room_created / room_joined / mic_activated events
```

## Decision Rule

```
Distinct Active Hosts declines for 2+ consecutive weeks
  → Highest-urgency signal on this dashboard. Investigate before any
    user-facing metric, because supply loss precedes and causes demand loss.

Rooms flat while Active Hosts declines
  → Concentration risk: fewer hosts carrying the same load.
    The headline looks healthy and the platform is more fragile.
    Investigate host burnout and single-host dependency.

A host's Audience Return rate is far below peers
  → Room-quality investigation, not a host-volume problem.
    Compare distinct non-host speakers per room.

Schedule heatmap shows sustained empty local-evening slots
  → Coverage gap. Recruit or reschedule into it.
```

---

# SECTION 3 — ACTIVATION PIPELINE

## Decision Supported
Should Cocorra restructure onboarding, invest in review capacity, or leave the gate alone?

## Primary Question
**Where do prospective users disappear between registering and joining their first room?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| Sequential onboarding funnel | Per-user, time-ordered across the 6 events | **AVAILABLE NOW** (data); the shipped endpoint is non-sequential — GAP-13 |
| Admin review latency | `voice_verification_submitted` → `voice_verification_result`, distribution | **AVAILABLE NOW** — uncomputed (GAP-08) |
| Review outcome mix | Active / Rejected / ReRecord split | **AVAILABLE NOW** |
| ReRecord recovery rate | Share of ReRecord users later activated | **AVAILABLE NOW** |
| **Activation → First Room Join** | Share of activated users who join a room within 7 days | **AVAILABLE NOW** |
| Pending queue depth over time | Time series | **REQUIRES HISTORICAL DATA** (GAP-05) |
| Pre-submission abandonment | Opened the form, never submitted | **REQUIRES EVENT TRACKING** |
| Per-reviewer consistency | Approval rate by admin | **REQUIRES EVENT TRACKING** (GAP-08) |

**INFERENCE — Activation → First Room Join is the most important metric in this section and the one most likely to be overlooked.** Approving a user is not the goal. If a meaningful share of activated users never join a single room, the gate is working and the product is failing downstream of it — and no amount of funnel optimisation above the gate would help. It is the metric that tells you *which* onboarding problem you have.

## Visualization

- **Funnel** — true sequential, six steps, with per-step conversion **and median elapsed time** on each step. **INFERENCE** — elapsed time is the point. A step that converts at 90% but takes 18 hours is a different problem from one that converts at 60% instantly, and a conversion-only funnel makes them look like the same kind of problem.
- **Distribution (histogram)** — review latency. **Not a mean.** **INFERENCE** — the tail is the story: if most reviews take 20 minutes and 15% take 3 days, the mean describes nobody and hides the users being harmed.
- **KPI** — Activation → First Room Join rate.
- **Trend (line)** — pending queue depth, once history accumulates.

**A note on the current funnel endpoint (FACT)** — `AnalyticsRepository.cs:300-322` counts each step independently, so it can render a later step *wider* than an earlier one. If the deployed dashboard ever shows a funnel widening downward, that is this defect, not a data anomaly.

## Drill-Down Path

```
Onboarding funnel
   ↓
Step with the largest drop
   ↓
Latency distribution for that step
   ↓
Cohort by registration week / day-of-week / hour
   ↓
Individual user event timeline
```

## Decision Rule

```
Review latency p90 rises materially against its recent trend
  → Queue capacity problem. Check pending depth and reviewer availability.

Large drop at voice_verification_submitted → activation_completed,
while review latency is low
  → Rejection rate, not latency, is the constraint.
    Investigate rejection reasons and voice-recording UX.

Activation → First Room Join is low while room supply is adequate
  → The problem is downstream of the gate. Onboarding is working;
    first-room discovery or the empty-handed post-approval experience is not.
    This is a Section 4 / Section 2 investigation, not an onboarding one.

Registration steady, activation declining
  → Backlog forming. Compare against pending queue depth.
```

---

# SECTION 4 — ROOM PARTICIPATION (THE CORE LOOP)

## Decision Supported
Should Cocorra redesign the stage flow, change room defaults, or leave the core loop alone?

## Primary Question
**Do listeners become speakers, and if not, where does the journey break?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| Speaking Conversion Rate | Distinct non-host speakers ÷ distinct joiners | **AVAILABLE NOW** |
| Conversion by `SelectionMode` | Split: Automatic vs Manual | **AVAILABLE NOW** |
| Conversion by `Category` | 3 categories | **AVAILABLE NOW** |
| Distinct speakers per room | Distribution | **AVAILABLE NOW** |
| Participants per room | Filtered to `Active`/`Left` only | **AVAILABLE NOW** |
| Hand-raise volume | — | **REQUIRES EVENT TRACKING** (GAP-06) |
| Hand-raise → stage approval rate | — | **REQUIRES EVENT TRACKING** (GAP-06) |
| Median wait: hand raised → promoted | — | **REQUIRES EVENT TRACKING** (GAP-06) |
| Time in room | — | **REQUIRES DATA MODEL CHANGE** (GAP-14) |
| Speaker time-budget exhaustion | — | **REQUIRES EVENT TRACKING** (GAP-06) |
| Real speaking duration | — | **REQUIRES EVENT TRACKING** (GAP-01) |
| In-room chat participation | — | **REQUIRES EVENT TRACKING** (GAP-15) |

**Participants per room must be filtered (FACT)** — `07-metric-verification.md` establishes that `Participants.Count` includes `Left`, `Kicked`, `Rejected`, and `PendingApproval`, inflating the count. Filter to `Active` and `Left` for an attendance measure.

**Top Speakers is deliberately absent.** **FACT, GAP-01** — the leaderboard ranks hosts by room duration, and hosts are simultaneously classified as passive listeners elsewhere. **RECOMMENDATION** — it should not appear on the dashboard until `mic_deactivated` exists. Removing a metric people currently look at is a real cost; shipping a confidently wrong leaderboard is a larger one.

## Visualization

- **KPI** — Speaking Conversion Rate, with week-over-week delta.
- **Funnel** — the core loop, with uninstrumented steps **rendered as visible gaps, explicitly labelled "not measured."** **INFERENCE** — this is a deliberate design choice. Hiding the missing steps would make a two-step funnel look complete and would quietly misrepresent how much Cocorra knows about its own core loop. Showing the gaps keeps the instrumentation debt visible to the people who would authorise fixing it.
- **Comparison (grouped bar)** — conversion by `SelectionMode` and by `Category`. Two small charts, not one crowded one.
- **Distribution (histogram)** — distinct speakers per room. **INFERENCE** — this reveals whether Cocorra runs conversations or broadcasts, which is the product's identity question. A distribution spiking at zero non-host speakers would be a significant finding.

## Drill-Down Path

```
Speaking Conversion Rate
   ↓
By SelectionMode / Category / Host
   ↓
Individual room  →  joiners, distinct speakers, duration
   ↓
Individual participant journey within that room
   ↓
Underlying room_joined / mic_activated events
```

## Decision Rule

```
Speaking Conversion declines materially against its recent trend
  → Investigate. Today the investigation stops at "which segment"
    (mode, category, host) because the intermediate steps are uninstrumented.
    Escalate the GAP-06 events.

Manual_CoachDecision rooms convert materially worse than
Automatic_FirstComeFirstServed, controlling for room size
  → Host approval is a throttle. Consider changing the default mode.
    Note: this is correlational — hosts choose the mode, and the kind of host
    who chooses manual may differ systematically. See 07a.

Distinct speakers per room clusters at 0–1
  → Cocorra is functioning as a broadcast platform, not a conversation platform.
    That is a product-identity finding, not a metric anomaly, and it warrants
    a product conversation rather than an analytics one.

Conversion differs sharply by Category
  → Category-specific UX or moderation-norm investigation.
```

---

# SECTION 5 — SAFETY & TRUST

## Decision Supported
Does Cocorra need proactive moderation, category-specific safeguards, or a different enforcement ladder?

## Primary Question
**Is the platform safe, and is harm concentrated anywhere in particular?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| Reports per 1,000 room joins | Normalised, not raw count | **AVAILABLE NOW** |
| **Report rate by room `Category`** | The key safety cut | **AVAILABLE NOW** — uncomputed (GAP-12) |
| Report category mix | 5 `ReportCategory` values | **AVAILABLE NOW** |
| Repeat-reported users | Concentration | **AVAILABLE NOW** |
| Time to first action | Approximate | **PARTIALLY AVAILABLE** — `Report.UpdatedAt` means "last touched" |
| Blocks per 1,000 joins | Peer-level friction | **PARTIALLY AVAILABLE** — no unblock event |
| Enforcement action mix | Warn / Mute / Ban / Reject | **REQUIRES EVENT TRACKING** (GAP-12) |
| Recidivism after action | Behaviour change post-enforcement | **REQUIRES EVENT TRACKING** (GAP-12) |

**Normalisation matters (INFERENCE)** — raw report counts rise with platform growth. Reports per 1,000 joins is the only version that answers "is it getting worse."

**Why report rate by category is the priority metric (INFERENCE)** — Two of three categories are `Relationships` and `MentalHealth`. Rooms discussing mental health carry duty-of-care obligations a general social product does not. Both inputs already exist — `user_reported` carries `reportedRoomId`, which joins to `Room.Category` — and the metric costs one `GROUP BY`. It is the highest-stakes available analysis in the product and nobody has run it.

## Visualization

- **KPI** — reports per 1,000 joins, with trend arrow.
- **Comparison (bar)** — report rate by `Category`. **INFERENCE** — three bars, and the whole safety question is legible in one glance. This is the highest information-per-pixel item on the dashboard.
- **Trend (line)** — normalised report rate over 12 weeks.
- **Table** — repeat-reported users with report count, categories, and actions taken.

## Drill-Down Path

```
Report rate
   ↓
By category / by room category / by reported user
   ↓
Individual report  →  room context, participants, timing
   ↓
Reported user's history  →  prior reports, actions, subsequent behaviour
```

## Decision Rule

```
Report rate in MentalHealth rooms materially exceeds other categories
  → Category-specific safeguards. Escalate as a product-safety decision,
    not an analytics observation.

Report rate rises while joins are flat
  → Real deterioration, not a growth artefact. Investigate immediately.

Reports concentrate in a small number of reported users
  → Enforcement problem: the ladder is not stopping repeat offenders.

Reports concentrate in a small number of rooms or hosts
  → Room-level or host-level moderation-norm problem, not a platform-wide one.
```

---

# SECTION 6 — RETURN & REPEAT

## Decision Supported
Should Cocorra prioritise retention work, and does it know enough to say?

## Primary Question
**Do users who participate come back?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| Weekly Return Rate | Prior-week WPU who participated again | **AVAILABLE NOW** |
| Rooms per user | Distribution | **AVAILABLE NOW** |
| Return by first-room `Category` | Cohorted | **AVAILABLE NOW** |
| Return by first-room host | Coach-quality signal | **AVAILABLE NOW** |
| Return by spoke / did not speak | Correlational only | **AVAILABLE NOW** |
| Weekly participation cohorts | Cohort grid | **REQUIRES HISTORICAL DATA** |
| True churn | Including deleted accounts | **REQUIRES DATA MODEL CHANGE** (GAP-03) |
| Session-based retention | — | **UNRELIABLE — do not build** |

**Build this from `room_joined`, not `session_started` (RECOMMENDATION)** — **FACT**: `session_started` is cookie-dependent on a Flutter client and the shipped `/Analytics/Retention` endpoint matches activity on *exactly* day N. A room-join-based return metric is server-authoritative, cookie-independent, and measures return to the product's actual value event. It is strictly better on every dimension.

**Two limits that must be displayed, not footnoted (FACT)**
- 180-day event retention caps cohort depth. There is no year-over-year view.
- Hard deletes remove the most-churned users, so **every return rate here is biased upward**. The size of the bias is unknown. **INFERENCE** — this warning belongs on the section itself, because a retention number that looks acceptable may be acceptable only among survivors.

## Visualization

- **Cohort grid** — weekly participation cohorts, W0 through W8, once eight weeks of history exist. **INFERENCE** — a cohort grid before that is mostly empty cells inviting over-interpretation of tiny samples; it should be hidden, not shown sparse.
- **KPI** — weekly return rate.
- **Distribution (histogram)** — rooms per user. **INFERENCE** — this distinguishes "many one-time visitors" from "a small committed core," which are opposite problems with opposite responses, and a mean would blur them into one number that describes neither.
- **Comparison (bar)** — return rate by first-room category and by whether the user spoke, **labelled "correlational — see 07a"** directly on the chart.

## Drill-Down Path

```
Return rate
   ↓
Cohort grid  →  a specific cohort week
   ↓
Segment: category / host / spoke vs listened
   ↓
Individual user participation history
```

## Decision Rule

```
Return rate declines while WPU holds
  → Growth is masking churn. Acquisition is compensating for a leaky product.
    This is the most dangerous pattern on the dashboard because the
    headline number looks fine.

Return differs sharply by first-room host
  → Coach quality affects retention. Supports host coaching and
    first-room routing. Note: correlational.

Rooms-per-user distribution spikes at exactly 1
  → First-experience problem. Investigate what the first room felt like:
    was there a live room to join, did anyone speak, was it in a
    category matching what they expected?
```

---

# SECTION 7 — SOCIAL SURFACES

## Decision Supported
Should Cocorra invest in messaging and the friend graph, or treat them as supporting utilities?

## Primary Question
**Do the social features get used, and do they connect back to the core room loop?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| Messages per active user | Volume | **AVAILABLE NOW** |
| **Conversation reciprocity** | Share of conversations with replies from both sides | **AVAILABLE NOW** |
| Friend requests sent / accepted | Volume and rate | **AVAILABLE NOW** (event-based) |
| Friendship utilisation | Accepted friendships that exchange messages | **AVAILABLE NOW** |
| Room → friendship conversion | Co-attendees who later friend each other | **AVAILABLE NOW** — uncomputed |
| DM origin surface | Room vs friends list | **REQUIRES EVENT TRACKING** (GAP-16) |
| Friend-request origin | How the graph forms | **REQUIRES EVENT TRACKING** (GAP-17) |
| Read latency | — | **REQUIRES DATA MODEL CHANGE** |

**Reciprocity over volume (INFERENCE)** — a high volume of one-directional messages is a *warning* sign, plausibly unwanted contact, not an engagement success. Raw message volume could mask a harassment pattern as healthy usage. Reciprocity is the metric that distinguishes them, and it is available today.

**Acceptance rate caveat (FACT, Finding D)** — `FriendService.SendFriendRequestAsync` mutates the existing row when a rejected relationship is re-attempted, overwriting `Status` and `CreatedAt`. Table-derived acceptance rates therefore undercount rejections. Use the event-derived rate.

## Visualization

- **KPI row** — messages per active user, reciprocity rate, friendship utilisation.
- **Trend (line)** — message volume and friend requests, 12 weeks, one chart.
- **Table** — room → friendship conversion by room, so the room-to-social bridge is visible per room.

**Deliberately small.** **INFERENCE** — `07a` FI-4 and FI-5 conclude these are supporting surfaces whose adoption is structurally capped by upstream constraints (friends-only DMs, exact-ID-search friending). Giving them dashboard real estate proportional to their strategic weight is itself a design decision: an oversized social section would invite investment the evidence does not support.

## Drill-Down Path

```
Social overview
   ↓
Messaging or friend graph
   ↓
Room → friendship conversion by room
   ↓
Individual social pairs and their shared room history
```

## Decision Rule

```
Message volume rises while reciprocity falls
  → Investigate unwanted contact. Cross-check against Section 5 block rate.
    Do not report the volume rise as engagement growth.

Friendship utilisation is low
  → Friending is a low-cost gesture, not a relationship.
    Graph-growth features would produce more of the same.

Room → friendship conversion is near zero
  → Rooms are not producing relationships. Given the friends-only DM design,
    this caps the entire social layer, and the constraint is upstream
    in people-discovery, not in the messaging feature.
```

---

# SECTION 8 — RELIABILITY

## Decision Supported
Should Cocorra invest in stability, media infrastructure, or notification delivery?

## Primary Question
**Does the product actually work for users, and how would we know?**

## Metrics

| Metric | Definition | Availability |
|---|---|:--:|
| `TechnicalProblem` ticket volume | By week, normalised per 1,000 active users | **AVAILABLE NOW** — unexposed (GAP-10) |
| Support first-response time | From `SupportMessage` + `IsFromAdmin` | **AVAILABLE NOW** — uncomputed |
| Support chat resolution time | `ClosedAt − CreatedAt` | **AVAILABLE NOW** |
| Active users with a valid FCM token | Share | **AVAILABLE NOW** (snapshot) |
| Push send success rate | — | **REQUIRES EVENT TRACKING** (GAP-11) |
| Room join failure rate | — | **REQUIRES EVENT TRACKING** (GAP-22) |
| Media quality / connection failures | — | **REQUIRES EVENT TRACKING** (GAP-20) |

**INFERENCE — the honest framing for this section.** With no error tracking anywhere in the stack (errors reach `ILogger` → Docker stdout and are never persisted), `TechnicalProblem` ticket volume is Cocorra's **only** systematic reliability signal. It is lagging, filtered by users' willingness to complain, and biased toward loud failure modes over silent ones — a user whose audio simply never worked may leave without filing anything. It should be labelled on the dashboard as a proxy, not presented as a reliability metric, so nobody mistakes a quiet week for a healthy one.

**FCM token coverage deserves a place despite being a snapshot (INFERENCE)** — commit `dc1c933` fixed reversed FCM delivery. An identical regression today would be invisible. Token coverage is the cheapest available guard against a defect class that has already occurred once in this codebase.

## Visualization

- **KPI** — `TechnicalProblem` tickets per 1,000 active users, **explicitly labelled "proxy — no error tracking exists."**
- **Trend (line)** — ticket volume by type, 12 weeks.
- **KPI** — FCM token coverage.

## Drill-Down Path

```
Reliability overview
   ↓
Ticket type breakdown
   ↓
Individual ticket content
   ↓
That user's recent activity (did they hit a specific room or feature?)
```

## Decision Rule

```
TechnicalProblem tickets rise materially against their recent trend
  → Escalate GAP-20 (LiveKit telemetry) and GAP-22 (error tracking).
    This proxy firing is the trigger to build real instrumentation,
    because the proxy cannot tell you what broke.

FCM token coverage among Active users declines
  → Token invalidation regression. Check against the dc1c933 fix.

Support first-response time rises while ticket volume is flat
  → Staffing problem, not a product problem.
```

---

# SECTION 9 — METRIC TRUST REGISTER

## Decision Supported
Can I rely on the number I am looking at?

## Primary Question
**What is this metric's trust level, and what is it not telling me?**

## Content
Not a chart. A searchable register of every dashboard metric with its full trust metadata, as specified in `08a-metric-trust-framework.md`: business definition, technical definition, formula, population, inclusions, exclusions, time window, timezone, data source, historical reliability, known limitations, trust level.

## Visualization
**Table**, filterable by trust level. Every metric elsewhere on the dashboard links to its register entry, and every metric displays its trust badge inline.

## Why this is a dashboard section and not a wiki page
**INFERENCE** — `07-metric-verification.md` found that three of the twelve shipped metrics are misleading or incorrect, and they look exactly as credible as the nine that are sound. The distinguishing information has to travel with the number, at the point of use, or it will not be consulted. A trust badge beside a KPI is read; a wiki page about metric definitions is not.

## Decision Rule

```
A metric is marked EXPERIMENTAL or UNRELIABLE
  → It may inform investigation. It must not be the sole basis for a decision.

A metric is marked CURRENT SNAPSHOT ONLY
  → Do not compare it to a historical figure. There is no historical figure.
```

---

# SECTION 0 — THE COCORRA DECISION CENTER

> Placed last in this document because it depends on everything above. It appears **first** on the dashboard.

## What This Is Not

Not another page of charts. Not automated decision-making. Not a machine-learning insights engine.

## What This Is

A surface that answers four questions in order:

```
WHAT CHANGED?
        ↓
WHERE?
        ↓
POSSIBLE EXPLANATIONS
        ↓
WHAT SHOULD BE INVESTIGATED?
```

**INFERENCE — the design principle.** The Decision Center never says "adoption dropped because of X." It says "adoption dropped, here is where, here are the explanations consistent with the data, and here is what you would need to look at to distinguish between them." The human decides. Given how much of Cocorra's data is missing or biased (half the decision matrix is NOT POSSIBLE TODAY), a system that asserted causes would be asserting them from evidence that cannot support them — and would be confidently wrong at exactly the moments it mattered most.

## A Prerequisite That Cannot Be Skipped

**FACT** — no baseline exists for any Cocorra metric, and the 180-day event window caps available history.

**RECOMMENDATION** — the Decision Center must not ship before four to six weeks of stable metric history exist. Detection requires knowing what normal looks like. Shipping signal detection without a baseline produces alerts on ordinary variance, and a dashboard that cries wolf in its first month will be ignored permanently. This is a sequencing constraint, not a preference.

---

## Signal Types

Only signal types supported by Cocorra's actual architecture are included.

---

### SIGNAL 1 — North Star Movement

**Signal** — WPU changed materially against its own recent trend.

**Detection Method** — Week-over-week comparison against a rolling baseline of prior weeks, using observed variance rather than a fixed threshold. **REQUIRES HISTORICAL DATA** — a minimum of four weeks before the baseline means anything.

**Supporting Evidence** — `room_joined` events, distinct users, host-excluded. **FACT** — server-emitted, indexed, VERIFIED by the prior audit. The most trustworthy signal available.

**Confidence** — **HIGH** for detecting *that* it moved. **LOW** for attributing *why*.

**Recommended Investigation** — Decompose into the four North Star inputs (supply, activation, conversion, return) and identify which moved. Then follow that section's drill-down.

---

### SIGNAL 2 — Supply Contraction

**Signal** — Distinct Active Hosts declined, or room supply concentrated into fewer hosts.

**Detection Method** — Weekly count of distinct `Room.HostId`, plus a concentration measure (share of rooms from the top 3 hosts). **AVAILABLE NOW.**

**Supporting Evidence** — `Room.HostId`, `Room.CreatedAt`. **FACT** — relational data, fully reliable, no event dependency.

**Confidence** — **HIGH.** This is the highest-confidence signal on the entire Decision Center, because it depends on no events, no cookies, and none of Findings A–E.

**Recommended Investigation** — Identify which hosts stopped. Check whether they were newly recruited (onboarding problem) or long-standing (burnout or dissatisfaction). Check whether their last rooms had unusually low participation. **INFERENCE** — this signal should be treated as more urgent than any user-side signal, because supply loss causes demand loss weeks later, and by then the causal direction will be ambiguous.

---

### SIGNAL 3 — Speaking Conversion Change

**Signal** — The share of participants who activate a mic changed.

**Detection Method** — Weekly ratio of distinct non-host `mic_activated` users to distinct non-host `room_joined` users.

**Supporting Evidence** — Both events server-emitted and reliable.

**Confidence** — **MEDIUM.** **FACT** — host exclusion must be applied correctly or Finding A distorts both numerator and denominator. The ratio is sound once corrected.

**Recommended Investigation** — Segment by `SelectionMode`, `Category`, and host. **INFERENCE — state the limit plainly**: today the investigation stops at *which segment*. It cannot reach *which step* — hand-raise volume, approval latency, and stage capacity pressure are all uninstrumented (GAP-06). This signal will fire and the team will be unable to act on it beyond guessing. That limitation is itself the argument for the GAP-06 events.

---

### SIGNAL 4 — Activation Pipeline Slowdown

**Signal** — Admin review latency increased, or the pending queue is growing.

**Detection Method** — Rolling median and p90 of the gap between `voice_verification_submitted` and `voice_verification_result`. **AVAILABLE NOW** — uncomputed today.

**Supporting Evidence** — Both events server-emitted with `OccurredAtUtc`.

**Confidence** — **HIGH** for latency. **NOT POSSIBLE** for queue depth over time until snapshot history is collected (GAP-05).

**Recommended Investigation** — Check registration volume (is it an input surge or a capacity drop?), day-of-week patterns, and whether bulk operations are being used as backlog catch-up. **INFERENCE** — this signal has direct revenue-side consequence: no acquisition effort can produce more active users than this queue approves.

---

### SIGNAL 5 — Safety Concentration

**Signal** — Reports concentrated in a particular room category, host, or user.

**Detection Method** — Report rate per 1,000 joins, segmented by `Room.Category` (via `user_reported.reportedRoomId`), by host, and by reported user. **AVAILABLE NOW** — uncomputed today.

**Supporting Evidence** — `user_reported` events plus the `Report` table. **FACT** — marked VERIFIED in the prior audit; the highest-quality metric in the shipped system.

**Confidence** — **HIGH.**

**Recommended Investigation** — For a category concentration, review room content norms and consider category-specific safeguards. For a host concentration, review that host's moderation behaviour. For a user concentration, check the enforcement ladder. **INFERENCE** — given the `MentalHealth` category, a category concentration should be escalated as a product-safety decision, not filed as an analytics observation.

---

### SIGNAL 6 — Onboarding Funnel Step Change

**Signal** — A specific onboarding step's conversion changed.

**Detection Method** — Sequential per-user funnel, weekly, compared to a rolling baseline.

**Supporting Evidence** — Six server-emitted onboarding events.

**Confidence** — **MEDIUM.** **FACT** — the data supports it; the shipped `/Analytics/Funnel` endpoint does not compute sequentially (GAP-13), so the Decision Center must compute its own rather than consuming that endpoint.

**Recommended Investigation** — For an `email_confirmed` drop, check email deliverability. For a `voice_verification_submitted` drop, check the recording UX and upload reliability. For an `activation_completed` drop, separate latency from rejection rate — they look identical in the funnel and require opposite responses.

---

### SIGNAL 7 — Room Participation Anomaly

**Signal** — A room drew unusually many or few participants relative to its host's own recent rooms.

**Detection Method** — Per-room participant count compared to that host's rolling median. Comparing against the *host's own* history rather than a platform average controls for the largest source of variation.

**Supporting Evidence** — `room_joined` events with the indexed `RoomId`.

**Confidence** — **MEDIUM.** **INFERENCE** — with a small platform, single-room variance is high and false positives will be common. This signal should trigger a note, not an alert, and should require a sustained pattern across several rooms before escalating.

**Recommended Investigation** — Check scheduling slot (local time), category, whether reminders were set, and whether it coincided with another live room competing for the same audience.

---

### SIGNAL 8 — Return Rate Change

**Signal** — Weekly return rate moved.

**Detection Method** — Share of prior-week WPU who participated again, computed from `room_joined`. **RECOMMENDATION** — never from `session_started`, and never via `/Analytics/Retention` (GAP-04).

**Supporting Evidence** — `room_joined` events across weeks.

**Confidence** — **MEDIUM.** **FACT** — biased upward by hard deletes; capped at 180 days. The *direction* of a change is more trustworthy than its level, since the bias is roughly stable week to week.

**Recommended Investigation** — Segment by first-room category and first-room host. Check whether the prior week had unusual room supply, which mechanically depresses the following week's return base.

---

### SIGNAL 9 — Reliability Proxy Change

**Signal** — `TechnicalProblem` support tickets rose.

**Detection Method** — Weekly ticket count by `SupportTicketType`, normalised per 1,000 active users.

**Supporting Evidence** — `SupportTicket` rows. **AVAILABLE NOW** — no endpoint exposes it.

**Confidence** — **LOW as a reliability measure.** **INFERENCE** — it is a lagging proxy, filtered by users' willingness to complain and biased toward loud failures. A silent audio failure that causes users to leave without complaining produces no signal at all. But it is the only reliability signal the platform has, so it is included with that label attached.

**Recommended Investigation** — Read the ticket content — free text is the actual diagnostic value here, not the count. Cross-reference against recent deployments. **INFERENCE** — if this signal fires repeatedly, the correct response is not a better ticket metric but building real error tracking (GAP-22) and LiveKit telemetry (GAP-20).

---

### SIGNAL 10 — Push Delivery Regression

**Signal** — FCM token coverage among `Active` users dropped.

**Detection Method** — Daily share of `Active` users with a non-null `FcmToken`. **AVAILABLE NOW** as a snapshot; **REQUIRES HISTORICAL DATA** for a trend.

**Supporting Evidence** — `ApplicationUser.FcmToken`.

**Confidence** — **MEDIUM.** A blunt instrument: it detects tokens disappearing, not messages misdelivering.

**Recommended Investigation** — Check against the token-clearing behaviour introduced in `dc1c933` (logout, ban, device exclusivity). **INFERENCE** — this signal exists specifically because that defect class has already occurred once and would currently be undetectable. It is a regression guard, not a product metric.

---

## Signal Summary

| # | Signal | Detection | Confidence | Availability |
|:--:|---|---|:--:|:--:|
| 1 | North Star movement | WPU vs rolling baseline | HIGH (detect) / LOW (attribute) | **AVAILABLE NOW** + history |
| 2 | Supply contraction | Distinct hosts + concentration | **HIGH** | **AVAILABLE NOW** |
| 3 | Speaking conversion change | mic_activated ÷ room_joined | MEDIUM | **AVAILABLE NOW** |
| 4 | Activation slowdown | Review latency p50/p90 | HIGH | **AVAILABLE NOW** |
| 5 | Safety concentration | Report rate by category/host/user | **HIGH** | **AVAILABLE NOW** |
| 6 | Onboarding step change | Sequential funnel | MEDIUM | **AVAILABLE NOW** |
| 7 | Room participation anomaly | Per-host rolling median | MEDIUM | **AVAILABLE NOW** |
| 8 | Return rate change | Room-join-based return | MEDIUM | **AVAILABLE NOW** |
| 9 | Reliability proxy | TechnicalProblem tickets | **LOW** | **AVAILABLE NOW** |
| 10 | Push delivery regression | FCM token coverage | MEDIUM | **AVAILABLE NOW** + history |

**INFERENCE — the notable result.** All ten signals are detectable from data that exists today. The Decision Center needs no new instrumentation; it needs **history** and **queries**. What new events would buy is not detection but *diagnosis* — Signal 3 in particular will fire and leave the team unable to act, which is the sharpest argument for the GAP-06 events.

---

## What The Decision Center Must Never Do

**RECOMMENDATION**, stated as constraints on the implementation:

1. **Never assert a cause.** Present explanations as candidates with the evidence that would distinguish them. See `07a` for why causal claims are unsupportable here.
2. **Never fire on a metric marked UNRELIABLE.** That currently excludes anything derived from `session_started`, `TotalSpokenSeconds`, or the status-backdated growth chart.
3. **Never hide a limitation to make a signal look cleaner.** If Signal 3 cannot reach the step level, say so in the signal itself.
4. **Never invent a threshold.** Until a baseline exists, signals should be descriptive ("changed against recent trend"), not judgemental ("below target").
5. **Never fire more signals than a person will read.** **INFERENCE** — a Decision Center producing ten alerts a week will be ignored by week three. Rank, cap the display, and let the rest be found on request.

---

## Implementation Sequence

**RECOMMENDATION** — build in this order, because each stage depends on the previous:

| Stage | What | Why first |
|:--:|---|---|
| **1** | Sections 1, 2, 3, 5 — using existing data only | No instrumentation needed. Section 2 alone would be the largest single improvement over the current dashboard. |
| **2** | Section 9 (Metric Trust Register) | Before anyone relies on stage 1, they need to know which numbers are trustworthy. |
| **3** | Accumulate 4–6 weeks of history | Prerequisite for anything that detects change. |
| **4** | Section 0 (Decision Center), Signals 2, 4, 5 first | The three HIGH-confidence signals. |
| **5** | Sections 4, 6, 7, 8 | Lower confidence or thinner data; worth having but not first. |
| **6** | GAP-06 events, then re-scope Section 4 | Converts the core-loop funnel from two steps to six and makes Signal 3 actionable. |

**INFERENCE** — the ordering is deliberate: **trust before breadth, and history before detection.** Building the Decision Center first, on metrics whose trust levels are undocumented and against no baseline, would produce a system that generates confident alerts from numbers nobody has verified. That is precisely the failure the previous audit found in the existing dashboard, and repeating it in a more sophisticated form would be worse, not better.
