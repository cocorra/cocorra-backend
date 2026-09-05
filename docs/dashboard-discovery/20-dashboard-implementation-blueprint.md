# 20 — Dashboard Implementation Blueprint

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 9
> **Depends on**: `09-recommended-dashboard.md` (structure), `19-analytics-api-blueprint.md` (endpoints), `14-metric-contracts.md` (metrics), `17-read-models-and-aggregation.md` (sources)
> **Scope**: Documentation only.

---

## Scope boundary

**FACT** — the dashboard is `admin.cocorraapp.com`, a **separate repository** not present in `cocorra-backend` (`00-repository-overview.md`). The mobile client is a separate Flutter repository.

**Therefore this document specifies a contract, not an implementation.** It defines, for each page and widget: the decision it serves, the metric, the endpoint, the underlying source, the trust level, and the drill-down target. It does not prescribe framework, component library, or styling — those belong to the dashboard repository.

**INFERENCE — why the contract must still be written here.** The trust-display requirements are the whole point of the programme. If the backend returns `Meta.trustLevel` and the frontend ignores it, the dashboard reverts to exactly the current failure: wrong and right metrics rendering with identical authority. The UI obligations below are therefore part of the specification, not suggestions.

---

# Page Structure

Nine pages, ordered as in `09-recommended-dashboard.md`. Every page follows the same derivation:

```
PAGE → DECISIONS SUPPORTED → QUESTIONS ANSWERED → METRICS
     → API ENDPOINTS → READ MODELS / QUERIES → DRILL-DOWN
```

---

## PAGE 0 — Decision Center

**Decisions supported** — Where should attention go this week?

**Questions answered** — What changed? Where? What should be investigated?

**Metrics** — No new metrics. Signals derived from A-1, B-1, C-2, E-1, F-3.

**API endpoints** — `GET /Analytics/Platform/Health` (with `compareTo=previous_period`), `GET /Analytics/System/Health`

**Read models** — RM-1, RM-3, RM-5

**Drill-down** — Each signal links directly to the page that diagnoses it.

**Gating requirement (RECOMMENDATION)** — this page must not ship until **4–6 weeks of stable read-model history exist**.

**INFERENCE** — detection requires a baseline. **FACT** — no baseline exists for any Cocorra metric, and raw history is capped at 180 days. Shipping change detection without one produces alerts on ordinary variance; a dashboard that cries wolf in its first month is ignored permanently, and that outcome is harder to reverse than a delayed launch.

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| Signal list (ranked, capped) | Where to look first | Derived deltas | A-1, B-1, C-2, E-1 | RM-1, RM-3 | Inherited per signal | → the relevant page |
| Pipeline health banner | Can I trust today's numbers? | Aggregation lag, dead-letter count | F-3 | `AggregationCheckpoint` | **VERIFIED** | → F-3 detail |
| Supply alert | Is supply contracting? | M-200 trend | B-1 | RM-3 | **VERIFIED** | → Page 2 |

**RECOMMENDATION on the signal list** — cap the displayed signals (three to five) and rank them. **INFERENCE** — a Decision Center producing ten alerts a week will be ignored by week three; the cap is a functional requirement, not a layout preference.

**RECOMMENDATION** — signals must state what changed and what to investigate, never a cause. `07a-feature-investment-framework.md` establishes that no causal claim is currently supportable.

---

## PAGE 1 — Platform Health

**Decisions supported** — Is Cocorra delivering more value than last week? Which input constrained it?

**Questions answered** — How many verified users took part in a live conversation, and what limited that number?

**API endpoints** — `GET /Analytics/Platform/Health?from=&to=&compareTo=previous_period`

**Read models** — RM-1 (weekly grain for M-100), RM-3

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| **KPI — Weekly Participating Users** | Is value delivery growing? | M-100 | A-1 | RM-1 weekly grain | **VERIFIED** | → Page 4 |
| KPI — Speaking Conversion Rate | Is attendance becoming participation? | M-101 | A-1 | RM-1 | **VERIFIED** | → Page 4 |
| KPI — Rooms Gone Live | Is supply adequate? | RM-1 `RoomsWentLive` | A-1 | RM-1 | **VERIFIED** (post-E-09) / **NOT MEASURED** before | → Page 2 |
| KPI — Distinct Active Hosts | Is supply concentrating? | M-200 | A-1 | RM-3 | **VERIFIED** | → Page 2 |
| KPI — New Activations | Are new users arriving? | RM-1 `NewActivations` | A-1 | RM-1 | **VERIFIED** | → Page 3 |
| KPI — Weekly Return Rate | Was it worth repeating? | M-102 | A-1 | RM-1 | **CONDITIONALLY RELIABLE** | → Page 6 |
| Trend — WPU, 12 weeks | Is growth flattening? | M-100 | A-1 | RM-1 weekly | **VERIFIED** | → Page 4 |
| Sparklines — 4 inputs | Which input moved? | M-200, activations, M-101, M-102 | A-1 | RM-1, RM-3 | Mixed | → respective pages |

**Current availability** — **AVAILABLE NOW** for all except `RoomsWentLive`, which **REQUIRES EVENT TRACKING** (E-09) and must render as an explicit "not measured" until then.

**RECOMMENDATION — one chart only on this page.** The 12-week WPU trend is the only place the *shape* matters. Everything else is a current value with a delta, which is a KPI, not a chart.

**Deliberately absent (FACT)** — the user-status pie chart, MBTI distribution, and average age currently returned by `/Analytics/Users/Growth`. **INFERENCE** — none supports a documented decision, and the status breakdown is the UNRELIABLE metric from TRUST-02.

---

## PAGE 2 — Supply Health

**Decisions supported** — Recruit more coaches, or help existing coaches run better rooms? These require different investments and the data distinguishes them.

**Questions answered** — Is supply sufficient, concentrated, and stable? Where are the schedule gaps?

**API endpoints** — B-1, B-2, B-3

**Read models** — RM-3, RM-2

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| KPI — Active Hosts | Is supply contracting? | M-200 | B-1 | RM-3 | **VERIFIED** | → host table |
| KPI — Host Retention | Are coaches staying? | M-201 | B-1 | RM-3 | **VERIFIED** | → host table |
| Trend — hosts + rooms, dual axis | Are fewer hosts carrying more load? | M-200 + rooms created | B-1 | RM-3 | **VERIFIED** | → host table |
| **Distribution — rooms per host** | Is supply concentrated? | M-202 + full distribution | B-1 | RM-3 | **CONDITIONALLY RELIABLE** | → host table |
| Table — host leaderboard | Which coaches to support? | Rooms, median participants, M-203, M-204 | B-2 | RM-3, RM-2 | Mixed | → host detail |
| **Heatmap — schedule coverage** | Where are the gaps? | Rooms live by (day, hour) | B-3 | RM-2 | **CONDITIONALLY RELIABLE** | → rooms in that slot |

**Current availability** — **AVAILABLE NOW**, in full. No new events, no schema change.

**INFERENCE** — this is the highest value-to-effort page in the blueprint and it has no counterpart in the current dashboard.

**RECOMMENDATION on the dual-axis trend** — hosts and rooms must be on one chart. **INFERENCE** — the failure mode neither shows alone is *rooms flat while hosts decline*: fewer people carrying the same load. The headline room count looks healthy while the platform becomes more fragile.

**RECOMMENDATION on the distribution** — show the histogram, not the mean. **INFERENCE** — with a small coach pool, one prolific host makes the average meaningless; the shape is the finding.

**Mandatory UI requirement for the heatmap** — must render in **local time** using `Meta.window.suggestedDisplayOffsetMinutes`. **FACT** — the server computes in UTC; the user base is UTC+2/+3. An unconverted heatmap would send coaches to a slot 2–3 hours off the real peak, which is a worse outcome than showing nothing.

---

## PAGE 3 — Activation Pipeline

**Decisions supported** — Restructure onboarding, invest in review capacity, or leave the gate alone?

**API endpoints** — C-1, C-2, C-3

**Read models** — RM-4, RM-5; live query for latency

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| **Funnel — 6 steps with elapsed time** | Where do prospects abandon? | M-300 | C-1 | RM-4 | **VERIFIED** | → step cohort |
| **Distribution — review latency** | Is the queue a bottleneck? | M-301 (p50/p90/p99) | C-2 | Live query | **VERIFIED** | → slow cases |
| Trend — pending queue depth | Is the backlog growing? | M-303 | C-2 | RM-5 | **VERIFIED** from snapshot start | → queue detail |
| Bar — review outcome mix | Is rejection the constraint? | Active/Rejected/ReRecord split | C-2 | RM-4 | **VERIFIED** | → cases |
| **KPI — Activation → First Room Join** | Is the problem below the gate? | M-302 | C-3 | RM-4 | **VERIFIED** | → Page 4 |
| KPI — ReRecord recovery rate | Is re-record worth keeping? | Derived | C-1 | RM-4 | **VERIFIED** | → cases |

**Current availability** — **AVAILABLE NOW** for all except the queue-depth trend, which **REQUIRES HISTORICAL DATA** (RM-5 must accumulate).

**Mandatory UI requirements**

- **Funnel must show elapsed time per step, not only conversion.** **INFERENCE** — one step is a human review queue. A conversion-only funnel renders an 18-hour wait identically to an instant drop-off, and those need opposite responses.
- **Latency must be a distribution, never a mean.** **FACT** — M-301's contract forbids returning a mean. If most reviews take 20 minutes and 15% take 3 days, the mean describes nobody.
- **Queue depth must appear beside latency.** **INFERENCE** — latency excludes users still waiting, so it understates the problem exactly when the backlog is worst.

---

## PAGE 4 — Room Participation

**Decisions supported** — Redesign the stage flow, change room defaults, or leave the core loop alone?

**API endpoints** — D-1, D-2, D-3

**Read models** — RM-2

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| KPI — Speaking Conversion Rate | Do listeners speak? | M-101 | D-2 | RM-2 | **VERIFIED** | → segments |
| **Funnel — stage journey, gaps visible** | Where does it break? | M-400 | D-1 | RM-2 | **EXPERIMENTAL** → VERIFIED | → room detail |
| Bar — conversion by SelectionMode | Change the default mode? | M-403 | D-2 | RM-2 | **CONDITIONALLY RELIABLE** | → rooms |
| Bar — conversion by Category | Category-specific UX? | M-403 | D-2 | RM-2 | **CONDITIONALLY RELIABLE** | → rooms |
| **Distribution — speakers per room** | Conversation or broadcast? | M-203 | D-2 | RM-2 | **VERIFIED** | → rooms |
| KPI — hand-raise → promotion rate | Is the stage the bottleneck? | M-402 | D-1 | RM-2 | **EXPERIMENTAL** | → rooms |
| Table — non-host speaking minutes | Who contributes most? | M-401 | D-2 | RM-2 | **CONDITIONALLY RELIABLE** | → user detail |
| Table — room detail | Investigate one room | Multiple | D-3 | Live query | Mixed | → underlying events |

**Current availability** — M-101, M-203, M-403 are **AVAILABLE NOW**. M-400, M-401, M-402 **REQUIRE EVENT TRACKING** (E-01…E-05).

**Mandatory UI requirements**

- **The stage funnel must render uninstrumented steps as visible, labelled gaps** — never as zero. **INFERENCE** — this is the single most important UI rule in the blueprint. A zero reads as *"nobody raised their hand"*: a confident, plausible, false conclusion. Showing the gap keeps the instrumentation debt visible to the people who would authorise fixing it.
- **M-401 must display the caveat "unmuted microphone time, not audio"** adjacent to the number, from `Meta.limitations`. **FACT** — no LiveKit telemetry exists; the distinction is not resolvable.
- **M-403 charts must be labelled "correlational."** **INFERENCE** — hosts *choose* the selection mode, so the comparison confounds the mode with the kind of host who picks it.
- **Top Speakers must not appear.** **FACT** — removed from the API entirely (R-8, TRUST-01), so this is enforced server-side rather than left to the UI.

**INFERENCE on the speakers-per-room distribution** — a distribution clustering at zero non-host speakers would be a product-identity finding, not a metric anomaly: it would mean Cocorra operates as a broadcast platform rather than the conversation platform its design intends.

---

## PAGE 5 — Safety & Trust

**Decisions supported** — Proactive moderation, category-specific safeguards, or a different enforcement ladder?

**API endpoints** — E-1, E-2

**Read models** — RM-2, RM-1; live query for repeat offenders

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| KPI — reports per 1,000 joins | Is safety deteriorating? | M-500 | E-1 | RM-1 | **VERIFIED** | → reports |
| **Bar — report rate by room category** | Do MentalHealth rooms need safeguards? | M-501 | E-1 | RM-2 | **VERIFIED** | → rooms in category |
| Trend — normalised report rate | Real change or growth artefact? | M-500 | E-1 | RM-1 | **VERIFIED** | → period detail |
| Bar — report category mix | Which harms dominate? | Category counts | E-1 | `Reports` | **VERIFIED** | → reports |
| Table — repeat-reported users | Is enforcement working? | M-502 | E-1 | Live query | **CONDITIONALLY RELIABLE** | → user history |
| Bar — enforcement action mix | Tune the ladder? | Action distribution | E-2 | RM-2 | **NOT AVAILABLE** until E-17 | → actions |

**Current availability** — **AVAILABLE NOW** except the action mix, which **REQUIRES EVENT TRACKING** (E-17).

**INFERENCE — the report-rate-by-category bar is the highest information-per-pixel widget in the entire blueprint.** Three bars, both inputs already verified, one `GROUP BY`, and it answers Cocorra's highest-stakes safety question. It has never been computed.

**Mandatory UI requirements**

- **Rates must be normalised per 1,000 joins**, never raw counts. **INFERENCE** — raw counts rise with growth; only the normalised version answers "is it getting worse."
- **Absolute counts must appear beside every rate.** With three categories, cells can be small enough that a percentage alone misleads.
- **Access restricted to `Admin`** (per `19-`). Report detail exposes reported-user identities.

---

## PAGE 6 — Return & Repeat

**Decisions supported** — Prioritise retention work, and does Cocorra know enough to say?

**API endpoints** — A-1, plus cohort detail

**Read models** — RM-1

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| KPI — Weekly Return Rate | Are users coming back? | M-102 | A-1 | RM-1 | **CONDITIONALLY RELIABLE** | → cohorts |
| **Cohort grid — 8 weeks** | Is retention improving? | M-102 by cohort | A-1 | RM-1 | **CONDITIONALLY RELIABLE** | → cohort segment |
| Distribution — rooms per user | Many one-timers, or a core? | Derived | A-1 | RM-1 | **VERIFIED** | → users |
| Bar — return by first-room category | Which first experience works? | M-102 segmented | A-1 | RM-1 | **CONDITIONALLY RELIABLE** | → rooms |
| Bar — return by first-room host | Does coach quality matter? | M-102 segmented | B-2 | RM-3 | **CONDITIONALLY RELIABLE** | → host |
| Bar — return by spoke / listened | Does speaking matter? | M-102 segmented | A-1 | RM-1 | **CONDITIONALLY RELIABLE** | → users |

**Current availability** — **AVAILABLE NOW** for the KPI; the cohort grid **REQUIRES HISTORICAL DATA** (8 weeks of RM-1).

**Mandatory UI requirements**

- **Hide the cohort grid until 8 weeks of history exist.** **INFERENCE** — a sparse grid of mostly-empty cells invites over-interpretation of tiny samples. Hidden is better than sparse.
- **The upward-bias warning must be on the page, not in a tooltip.** **FACT** — hard deletes remove the most-churned users, biasing every return rate upward by an unknown margin (TRUST-05). A return rate that looks acceptable may be acceptable only among survivors.
- **Segmented bars must be labelled "correlational."** `07a` establishes that no causal claim is supportable.

**INFERENCE on the rooms-per-user distribution** — a spike at exactly 1 is a first-experience problem and a specific one: it would prompt asking whether there was a live room to join, whether anyone spoke, and whether the category matched expectations. A mean would blur that into a number describing nobody.

---

## PAGE 7 — Social Surfaces

**Decisions supported** — Invest in messaging and the friend graph, or treat them as utilities?

**API endpoints** — F-1

**Read models** — live queries over `Messages`, `FriendRequest`

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| **KPI — message reciprocity** | Is messaging healthy? | M-600 | F-1 | `Messages` | **VERIFIED** | → conversations |
| KPI — messages per active user | Is messaging used? | Derived | F-1 | `Messages` | **VERIFIED** | → users |
| KPI — friendship utilisation | Are friendships real? | Derived | F-1 | `FriendRequest`, `Messages` | **VERIFIED** | → pairs |
| Trend — messages + friend requests | Are social surfaces growing? | Derived | F-1 | Live query | **VERIFIED** | → period |
| Table — room → friendship conversion | Do rooms create relationships? | Derived | F-1 | `RoomParticipant`, `FriendRequest` | **CONDITIONALLY RELIABLE** | → rooms |

**Current availability** — **AVAILABLE NOW**.

**RECOMMENDATION — keep this page deliberately small.** **INFERENCE** — `07a` FI-4 and FI-5 conclude these are supporting surfaces whose adoption is structurally capped upstream (friends-only DMs; friend search requiring a pre-known exact user ID). Giving them real estate proportional to their strategic weight is itself a design decision: an oversized social page would invite investment the evidence does not support.

**Mandatory UI requirement** — **reciprocity must be displayed before volume.** **INFERENCE** — a high volume of one-directional messages is a warning sign, plausibly unwanted contact, not engagement growth. Ordering matters because it shapes the first reading.

---

## PAGE 8 — Reliability

**Decisions supported** — Invest in stability, media infrastructure, or notification delivery?

**API endpoints** — F-2, F-3

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| **KPI — TechnicalProblem tickets per 1,000 users** | Prioritise stability? | M-601 | F-2 | `SupportTickets` | **CONDITIONALLY RELIABLE** | → ticket content |
| Trend — tickets by type | What is breaking? | Derived | F-2 | `SupportTickets` | **VERIFIED** | → tickets |
| KPI — support first-response time | Is support responsive? | Derived | F-2 | `SupportMessage` | **CONDITIONALLY RELIABLE** | → chats |
| KPI — FCM token coverage | Push regression guard | RM-5 | F-2 | RM-5 | **VERIFIED** from snapshot start | → users |
| KPI — push send success rate | Is delivery working? | M-602 | F-2 | RM-1 | **NOT AVAILABLE** until E-16 | → failures |
| **Panel — pipeline health** | Can I trust these numbers? | Lag, dead-letters, drops | F-3 | `AggregationCheckpoint` | **VERIFIED** | → job detail |

**Current availability** — M-601 and support timing **AVAILABLE NOW** (data exists, no endpoint — GAP-10). Token coverage **REQUIRES HISTORICAL DATA**. Push success **REQUIRES EVENT TRACKING**.

**Mandatory UI requirement** — **M-601 must carry the label "proxy — no error tracking exists."** **FACT** — errors reach `ILogger` → Docker stdout and are never persisted. **INFERENCE** — this is a lagging proxy filtered by users' willingness to complain and biased toward loud failures; a silent audio failure that drives users away produces no signal. A quiet week must not read as a healthy one.

**INFERENCE on the pipeline health panel** — this is the widget that makes the entire durability programme observable. Without it, the dead-letter table fills and nobody knows, because there is no structured logging sink, no APM, and no metrics export.

---

## PAGE 9 — Metric Trust Register

**Decisions supported** — Can I rely on the number I am looking at?

**API endpoints** — G-1

### Widgets

| Widget | Decision | Metric | API | Data Source | Trust Level | Drill-down |
|---|---|---|---|---|:--:|---|
| Table — all metric contracts, filterable by trust level | Is this number reliable? | All | G-1 | `IMetricRegistry` | N/A | → metric detail |

**Current availability** — **AVAILABLE NOW** once `IMetricRegistry` exists.

**INFERENCE — why this is a page and not a wiki.** `07-metric-verification.md` found three of twelve shipped metrics misleading or incorrect, and they look exactly as credible as the nine that are sound. The distinguishing information must be reachable from the number itself. Every metric elsewhere must link here.

---

# Dashboard Trust UX

**The core requirement.** The dashboard must never visually imply that an unreliable metric is fully trustworthy. This is the failure that made the current dashboard NOT TRUSTED, and it is a UI failure as much as a computation one.

## Display rules by trust level

| Trust Level | Visual treatment | Interaction |
|---|---|---|
| **VERIFIED** | Normal presentation. Small unobtrusive badge. | Badge links to Page 9. |
| **CONDITIONALLY RELIABLE** | Badge **plus the condition rendered inline**, one line, adjacent to the number. Not a tooltip. | Condition text from `Meta.limitations`. |
| **EXPERIMENTAL** | Badge plus visual de-emphasis (reduced weight or a distinct container). | Explicit "not yet validated" note. |
| **UNRELIABLE** | **Must not be displayed.** | — |

**RECOMMENDATION on UNRELIABLE** — such metrics are removed from the API response entirely (R-8), so the UI cannot render them even by mistake. **INFERENCE** — this is deliberate. A warned-but-visible wrong number still gets screenshotted into a deck without its warning; server-side removal is the only enforcement that survives contact with real users.

**RECOMMENDATION on CONDITIONALLY RELIABLE** — the condition must be **inline, not hover**. **INFERENCE** — a tooltip is not read by someone scanning a dashboard, and it does not survive a screenshot. The condition is what makes the number usable; hiding it behind an interaction defeats the purpose.

## Historical reliability display

| Classification | Display |
|---|---|
| **HISTORICALLY ACCURATE** | Trends shown normally |
| **CURRENT SNAPSHOT ONLY** | No trend line. Explicit "point-in-time" label. Comparison to a remembered earlier value actively discouraged |
| **PARTIALLY RECONSTRUCTABLE** | Trend shown with a visible boundary marker at `dataAvailableFromUtc` |
| **NOT HISTORICALLY RELIABLE** | Not shown as a trend under any circumstance |

## The "not measured" state

**RECOMMENDATION — the most important single UI rule.**

When `Meta.dataAvailableFromUtc` is later than the requested window start, the client must render a **visible, labelled gap** — never a zero, never an interpolation, never a hidden series.

```
Stage Funnel — this week

  Joined            1,247  ████████████████████
  Hand raised          ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Promoted to stage    ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Activated mic        312  █████
```

**INFERENCE** — this is where the previous dashboard's failure mode would most easily reappear. Rendering `0` for an uninstrumented step is not a display shortcut; it is a fabricated finding, and a plausible one. Showing the gap does three things at once: it prevents a wrong conclusion, it tells the reader why the funnel cannot be diagnosed, and it keeps the instrumentation debt visible to the people who could fund closing it.

## Freshness display

**RECOMMENDATION** — a persistent header element showing `Meta.freshness`:

- Healthy — "Updated 12 minutes ago"
- Stale beyond threshold — a visible warning with the last successful aggregation time
- `pipelineHealthy = false` — a banner stating that data may be incomplete

**INFERENCE** — a trust badge on a metric whose pipeline stopped three days ago is worse than no badge, because it certifies stale data as verified. Trust and freshness have to be displayed together or neither means anything.

## Comparison and partial periods

**RECOMMENDATION** — when `Meta.window.isPartialPeriod` is true, label the figure explicitly (e.g. "week to date") and never render it as a completed period in a trend.

**INFERENCE** — without this, the current week always appears as a decline, and a dashboard that shows a drop every Monday trains its readers to ignore drops.

---

# Implementation Sequence

**RECOMMENDATION** — build in this order. Each stage depends on the previous.

| Stage | Pages | Prerequisite | Rationale |
|:--:|---|---|---|
| **1** | Page 9 (Trust Register) | `IMetricRegistry` | **INFERENCE** — before anyone relies on a number, they need to be able to check it. Building the register first also forces every metric to have a contract before it appears anywhere. |
| **2** | Pages 1, 2, 3, 5 | Corrected queries + read models | All **AVAILABLE NOW**. Page 2 alone is the largest single improvement over the current dashboard. |
| **3** | Pages 7, 8 | Support and social endpoints | Live queries over relational data; no new events |
| **4** | — | 4–6 weeks of read-model history | Prerequisite for anything detecting change |
| **5** | Page 6 (cohort grid) | 8 weeks of history | Hidden until the grid is dense enough to read |
| **6** | Page 0 (Decision Center) | Stages 1–4 complete | Requires trust *and* baseline |
| **7** | Page 4 (full stage funnel) | E-01…E-05 deployed | The funnel's middle steps do not exist before this |

**INFERENCE — the ordering principle is trust before breadth, and history before detection.** Building the Decision Center first, on metrics whose trust levels are undocumented and against no baseline, would produce confident alerts from unverified numbers. That is precisely the failure the previous audit found, and reproducing it in a more sophisticated form would be worse than the original, because it would be more persuasive.

---

# Validation — Frontend Contract

| # | Test | Asserts |
|:--:|---|---|
| **1** | Trust badge rendering | Every displayed metric shows a badge matching `Meta.trustLevel` |
| **2** | Inline conditions | CONDITIONALLY RELIABLE metrics render their condition inline, not in a tooltip |
| **3** | **Not-measured state** | A `null` value with `dataAvailableFromUtc` renders a labelled gap, never `0` |
| **4** | Freshness banner | Stale `Meta.freshness` produces a visible warning |
| **5** | Partial period | `isPartialPeriod = true` is labelled and excluded from completed-period trends |
| **6** | Local time | Heatmap and hourly charts apply `suggestedDisplayOffsetMinutes` |
| **7** | No deprecated metrics | Top Speakers, hand-raise count, and `AvgDurationHours` appear nowhere |
| **8** | Correlational labels | M-403 and M-102 segmented charts carry the label |
| **9** | Cohort grid gating | Hidden below 8 weeks of history |
| **10** | Drill-down integrity | Every widget's drill-down resolves to a real endpoint |

**INFERENCE — test 3 is the one that matters most.** It is the single assertion that prevents the new dashboard from manufacturing the same class of confident falsehood as the old one, and it is the easiest to get wrong, because rendering `0` for a missing value is what most charting libraries do by default.

---

# Summary

| Page | Decisions | Availability | Trust profile |
|---|---|---|---|
| 0 — Decision Center | Where to look | Gated on 4–6 weeks history | Inherited |
| 1 — Platform Health | Is value growing? | **AVAILABLE NOW** (1 gap) | Mostly VERIFIED |
| 2 — Supply Health | Recruit or enable coaches? | **AVAILABLE NOW** | VERIFIED |
| 3 — Activation | Restructure onboarding? | **AVAILABLE NOW** (1 gap) | VERIFIED |
| 4 — Room Participation | Redesign the stage flow? | Partly event-gated | Mixed |
| 5 — Safety | Category safeguards? | **AVAILABLE NOW** (1 gap) | VERIFIED |
| 6 — Return & Repeat | Prioritise retention? | Needs history | CONDITIONALLY RELIABLE |
| 7 — Social | Invest in messaging? | **AVAILABLE NOW** | VERIFIED |
| 8 — Reliability | Invest in stability? | **AVAILABLE NOW** (1 gap) | CONDITIONALLY RELIABLE |
| 9 — Trust Register | Can I trust this? | **AVAILABLE NOW** | N/A |

**Two conclusions (INFERENCE).**

**Six of ten pages are buildable today**, with no new events and no schema change, because they rest on data the audit already verified as correct. The dashboard's biggest gap is not instrumentation — it is that nobody has queried what already exists.

**The trust UX is not decoration; it is the deliverable.** The current dashboard's failure was never that some metrics were wrong — every analytics system has wrong metrics. It was that nothing distinguished them. If the backend returns trust metadata and the frontend renders every number identically, this entire programme produces a faster wrong dashboard.
