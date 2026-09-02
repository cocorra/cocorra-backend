# Cocorra Decision Intelligence Dashboard — Design Brief

> **Generated**: 2026-09-02
> **Audience**: whoever designs and builds the dashboard UI (`admin.cocorraapp.com`, separate repository)
> **Sources of truth**: `09-recommended-dashboard.md` (structure) · `20-dashboard-implementation-blueprint.md` (widgets, trust UX) · `29-final-metric-trust-register.md` (current trust grades)
> **Companion**: `CLAUDE-DESIGN-PROMPT.md` — a standalone prompt derived from this brief

---

# 1. What this product is

**Cocorra** is a voice-room platform. Verified users join live audio rooms hosted by coaches, raise a hand to be promoted to a stage, and speak. Rooms belong to one of three categories, two of which — **Relationships** and **MentalHealth** — carry duty-of-care obligations a general social product does not.

The value loop is: **a listener joins a room and becomes a speaker.** Everything the dashboard measures is upstream or downstream of that.

## What this dashboard is

**Not an admin dashboard. Not a metrics wall.**

It is a **Decision Intelligence Dashboard**. Its purpose is to answer four questions:

```
WHAT CHANGED?
WHERE?
WHY MIGHT IT HAVE CHANGED?
WHAT SHOULD WE INVESTIGATE NEXT?
```

Every page derives from a decision someone actually has to make. If a widget does not narrow a decision, it does not belong on the page — the blueprint deliberately removes the user-status pie chart, the MBTI distribution and the average-age figure from the landing view for exactly that reason.

## The failure this design must not repeat

Cocorra's previous dashboard showed twelve metrics. **One was verified. Three were actively misleading. All twelve rendered identically.**

The sharpest case: a silent host accrued a room's full 2–3 hours as speaking time, so the same coach appeared as **the platform's #1 speaker and as a silent listener, in two panels of the same dashboard**, both looking equally credible.

**The failure was not that some numbers were wrong. It was that nothing told them apart — and that is a UI failure as much as a computation one.** The backend now attaches a trust contract to every number. If the UI renders them all the same way, this programme has produced a *faster wrong dashboard*.

---

# 2. Information architecture

Ten pages. Order and content are fixed by `09-` and `20-`. **Do not invent pages or metrics.**

| # | Page | Decision supported | Data state |
|:--:|---|---|---|
| **0** | **Decision Center** | Where should attention go this week? | 🟡 **Collecting baseline** — 4–6 weeks required |
| **1** | **Platform Health** | Is Cocorra delivering more value, and which input constrained it? | 🟢 Available |
| **2** | **Supply Health** | Recruit more coaches, or help existing coaches run better rooms? | 🟢 Available — **highest value-to-effort page** |
| **3** | **Activation Pipeline** | Restructure onboarding, invest in review capacity, or leave the gate alone? | 🟢 Available (1 trend needs history) |
| **4** | **Room Participation** | Redesign the stage flow, change room defaults, or leave the core loop alone? | 🟠 **Partly gated** — stage funnel is EXPERIMENTAL, two steps not measured |
| **5** | **Safety & Trust** | Proactive moderation, category safeguards, or a different enforcement ladder? | 🟢 Available |
| **6** | **Return & Repeat** | Prioritise retention work — and does Cocorra know enough to say? | 🟠 KPI available; **cohort grid needs 8 weeks** |
| **7** | **Social Surfaces** | Invest in messaging and the friend graph, or treat them as utilities? | 🟢 Available — **keep deliberately small** |
| **8** | **Reliability** | Invest in stability, media infrastructure, or notification delivery? | 🟢 Available (1 KPI event-gated) |
| **9** | **Metric Trust Register** | Can I rely on the number I am looking at? | 🟢 Available |

## Build order

**Page 9 first.** Before anyone relies on a number they must be able to check it. Then 1, 2, 3, 5 (all available). Then 7, 8. Then 6's cohort grid at 8 weeks, 4's full funnel after event activation, and 0 last.

**Page 2 alone is the largest single improvement over the current dashboard** — it has no counterpart there, needs no new events, and answers the platform's most consequential unwatched question.

---

# 3. The trust system — the design's core mechanic

Every metric arrives with a trust level in `Meta`. **Four states get four visually distinct treatments.**

| Level | Treatment | Interaction |
|---|---|---|
| **VERIFIED** | Normal presentation. Small, unobtrusive badge | Badge links to Page 9 |
| **CONDITIONALLY RELIABLE** | Badge **plus the condition rendered inline** — one line, adjacent to the number | Condition text comes from `Meta.metrics[].limitations` |
| **EXPERIMENTAL** | Badge plus visual de-emphasis (reduced weight, or a distinct container). Explicit "not yet validated" note | — |
| **UNRELIABLE** | **Never displayed.** Removed from the API response entirely | — |

## Three rules that are functional requirements, not preferences

### Rule 1 — `null` renders as a labelled gap. Never `0`.

**The single most important rule in this brief.**

When a metric returns `isMeasured: false` or `value: null`, the UI must render a **visible, labelled gap** using the server-supplied `notMeasuredReason`. Never a zero, never an interpolation, never a hidden series.

```
Stage Funnel — this week

  Joined room            1,247  ████████████████████
  Raised hand               ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Promoted to stage         ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Activated microphone     312  █████                 observed only
```

`0` and `null` look identical on a chart and they are **opposite claims**. `0` says *nobody raised their hand* — a confident, plausible, false conclusion. `null` says *we did not measure it*.

**Most charting libraries coerce `null` to `0` by default. That default is a fabricated finding.** Showing the gap does three things at once: prevents a wrong conclusion, tells the reader why the funnel cannot be diagnosed, and keeps the instrumentation debt visible to the people who could fund closing it.

### Rule 2 — Conditions are inline, not on hover.

A tooltip is not read by someone scanning a dashboard, and **it does not survive a screenshot**. The condition is what makes a CONDITIONALLY RELIABLE number usable; hiding it behind an interaction defeats the purpose.

### Rule 3 — Freshness and trust are displayed together, or neither means anything.

A persistent header element showing pipeline freshness:

| State | Display |
|---|---|
| Healthy | "Updated 12 minutes ago" |
| Stale beyond threshold | Visible warning + last successful aggregation time |
| `pipelineHealthy: false` | **Banner: data may be incomplete** |

A trust badge on a metric whose pipeline stopped three days ago is **worse than no badge**, because it certifies stale data as verified.

## Historical reliability — a separate axis from trust

| Classification | Display |
|---|---|
| **HISTORICALLY ACCURATE** | Trends shown normally |
| **CURRENT SNAPSHOT ONLY** | **No trend line.** Explicit "point-in-time" label |
| **PARTIALLY RECONSTRUCTABLE** | Trend with a **visible boundary marker** at `dataAvailableFromUtc` |
| **NOT HISTORICALLY RELIABLE** | **Never shown as a trend, under any circumstance** |

## Partial periods

When the requested window includes today, `isPartialPeriod` is true. Label it ("week to date") and **never plot it as a completed period**. Without this the current week always looks like a decline, and a dashboard that shows a drop every Monday trains its readers to ignore drops.

---

# 4. Page-by-page

## Page 0 — Decision Center

**State: 🟡 must ship in a "Collecting baseline" state.**

**Do not ship live change detection.** No baseline exists for any Cocorra metric. Detection without one produces alerts on ordinary variance, and **a dashboard that cries wolf in its first month is ignored permanently — harder to reverse than a delayed launch.**

Design both states: the baseline-collecting state *and* the populated state.

**Signal card anatomy:**

```
WHAT CHANGED        Speaking conversion fell from 27% to 19%
WHERE               Room Participation · MentalHealth category
WHY IT MIGHT HAVE   Stage promotions down 40%; hand raises flat
CONFIDENCE          Medium — 3 weeks of baseline
INVESTIGATE         → Stage Funnel · → Room Participation
```

**Design constraints:**

- **Cap displayed signals at 3–5, ranked.** A Decision Center producing ten alerts a week is ignored by week three. **The cap is a functional requirement.**
- Signals state **what changed and what to investigate — never a cause.** No causal claim is currently supportable from Cocorra's data.
- Every signal links to the page that diagnoses it.
- A **pipeline health banner** at the top: *can I trust today's numbers at all?*

**Confidence must be shown as confidence, never as certainty.** These are automated observations, not findings.

---

## Page 1 — Platform Health

**Endpoint**: `GET /Analytics/Platform/Health?from=&to=&compareTo=previous_period`

**Six KPI cards, each with a previous-period delta and a drill-down:**

| KPI | Metric | Trust | Drill-down |
|---|---|---|---|
| **Weekly Participating Users** ← *the north star* | M-100 | VERIFIED | → Page 4 |
| Speaking Conversion Rate | M-101 | VERIFIED | → Page 4 |
| Distinct Active Hosts | M-200 | VERIFIED | → Page 2 |
| Rooms Gone Live | M-205 | VERIFIED | → Page 2 |
| New Activations | M-508 | VERIFIED | → Page 3 |
| New Registrations | M-501 | VERIFIED | → Page 3 |

**Plus: one chart only.** A 12-week WPU trend. Everything else is a current value with a delta — which is a KPI, not a chart.

**Design constraints:**

- **The north star must be visually dominant.** Not one of six equal tiles — the hierarchy should make WPU unmistakable and the other five read as its inputs.
- **A bare headline number is close to meaningless.** The comparison is not decoration; if `hasComparison` is false, say so rather than showing a lone figure.
- **Growth from zero has no percentage.** When `deltaPercent` is `null` but `deltaAbsolute` is present, show the absolute only. Never "+100%", never "∞".
- Weekly Return Rate belongs on Page 6, not here — it is CONDITIONALLY RELIABLE and needs its inline caveat.

---

## Page 2 — Supply Health

**Endpoint**: `GET /Analytics/Supply/Health` · **Available now, in full. No new events, no schema change.**

| Widget | Metric | Trust |
|---|---|---|
| KPI — Active Hosts | M-200 | VERIFIED |
| KPI — Host Second-Room Rate | M-201 | VERIFIED |
| **Trend — hosts + rooms, dual axis** | M-200 + rooms | VERIFIED |
| **Distribution — rooms per host (histogram)** | M-202 | VERIFIED |
| Table — host leaderboard | Multiple | Mixed |
| **Heatmap — schedule coverage by (day, hour)** | Rooms live | COND. RELIABLE |

**Design constraints:**

- **Hosts and rooms must share one chart with a dual axis.** The failure mode neither shows alone is *rooms flat while hosts decline* — fewer people carrying the same load. The headline room count looks healthy while the platform becomes more fragile.
- **Show the histogram, not the mean.** With a small coach pool, one prolific host makes the average meaningless. **The shape is the finding.**
- **The heatmap must render in local time** using `suggestedDisplayOffsetMinutes` (default +180). The server computes in UTC; the audience is UTC+2/+3. An unconverted heatmap sends a coach to a slot 2–3 hours off the real peak — **worse than showing nothing.**
- M-201 **rises as later data arrives** (a host's second room may fall after the window). Label it as not-final.

---

## Page 3 — Activation Pipeline

| Widget | Metric | Trust |
|---|---|---|
| **Funnel — sequential steps with elapsed time** | M-507 | VERIFIED |
| **Distribution — review latency (p50/p90/p99)** | M-302 | COND. RELIABLE |
| Trend — pending queue depth | M-303 | VERIFIED from snapshot start |
| Bar — review outcome mix | Derived | VERIFIED |
| KPI — voice verification drop-off | M-508 | VERIFIED |

**Design constraints:**

- **Never display a mean review latency. The API does not return one, by contract.** If most reviews take 20 minutes and 15% take three days, the mean describes nobody and hides the users being harmed. Design for percentiles.
- **Queue depth must sit beside latency.** Pending submissions are excluded from latency, so a growing backlog does not move that figure. Reading either alone is misleading.
- Funnel steps carry median and p90 elapsed time between them — **the wait is the finding**, not just the drop.
- The queue-depth trend has **no history before the snapshot job's first run**. Show the boundary.

---

## Page 4 — Room Participation

**State: 🟠 the stage funnel is EXPERIMENTAL and two of its four steps are not measured.**

**Endpoint**: `GET /Analytics/Participation/StageFunnel`

| Widget | Metric | Trust |
|---|---|---|
| KPI — Speaking Conversion Rate | M-101 | VERIFIED |
| **Funnel — stage journey, gaps visible** | M-400 | **EXPERIMENTAL** |
| Table — room detail | Multiple | Mixed |

**This page is where Rule 1 gets exercised hardest.** Design the funnel for **four distinct per-step states**:

| State | Server signal | Render |
|---|---|---|
| Measured | `isMeasured: true`, `count: n` | Normal bar |
| Not instrumented | `isMeasured: false` | **Labelled gap** + `notMeasuredReason` |
| Instrumented but sequence broken upstream | `isMeasured: true`, `count: null`, `observedParticipations: n` | **Gap for the funnel bar, with the observed figure shown separately and labelled "observed only"** |
| Partially instrumented in-window | `isPartiallyMeasured: true` | Boundary marker at `measurementStartedUtc` |

**Also surface `directPromotionsWithoutHandRaise`** — participants the host promoted directly. A strict sequential funnel drops them at step 2, which without this reads as promotions vanishing.

**Deliberately absent, enforced server-side:** Top Speakers. It ranked hosts by room length. It cannot appear because the API does not return it.

---

## Page 5 — Safety & Trust · Admin only

| Widget | Metric | Trust |
|---|---|---|
| **Bar — report rate by room category** | M-301 | VERIFIED |
| Bar — report category mix | Derived | VERIFIED |
| Table — most-reported users | M-300 | VERIFIED |

**The report-rate-by-category bar is the highest information-per-pixel widget in the entire blueprint.** Three bars, both inputs verified, one `GROUP BY`, and it answers Cocorra's highest-stakes safety question. It had never been computed.

**Design constraints:**

- **Rates normalised per 1,000 joins, never raw counts.** Raw counts rise with growth; only the normalised version answers "is it getting worse".
- **Absolute counts must appear beside every rate.** With three categories, cells can be small enough that a percentage alone misleads.
- **A category with no joins returns `null`, not a zero rate.** Render the gap.
- **Admin only** — report detail exposes reported-user identities. Design the Coach experience as the page being absent, not as an empty state.
- The metric measures **reports filed, not incidents occurred**. Under-reporting in a sensitive category would read as safety. This caveat belongs on the page.

---

## Page 6 — Return & Repeat

| Widget | Metric | Trust |
|---|---|---|
| KPI — Weekly Return Rate | M-102 | VERIFIED |
| **Cohort grid — 8 weeks** | M-103 | COND. RELIABLE |

**Design constraints:**

- **Hide the cohort grid until 8 weeks of history exist** (`hasSufficientHistory`). A sparse grid of mostly-empty cells invites over-interpretation of tiny samples. **Hidden beats sparse.**
- **Recent cohorts have shorter rows by construction. A short row is missing data, not a retention collapse.** The visual must not let that misread happen.
- **The upward-bias warning goes on the page, not in a tooltip.** Hard deletes remove the most-churned users, biasing every return rate upward by an unknown margin. A rate that looks acceptable may be acceptable only among survivors.
- The final week in any window has no later week yet, so `isComplete: false`. Do not chart it.

---

## Page 7 — Social Surfaces

**Keep this page deliberately small.** These are supporting surfaces whose adoption is structurally capped upstream (friends-only DMs; friend search requiring a pre-known exact user ID). **Giving them real estate proportional to their strategic weight is itself a design decision** — an oversized social page would invite investment the evidence does not support.

**Mandatory ordering: reciprocity before volume.** A high volume of one-directional messages is a warning sign — plausibly unwanted contact — not engagement growth. Ordering shapes the first reading.

Also surface `maxRequestsBySingleSender` — spam-wave detection.

---

## Page 8 — Reliability

| Widget | Metric | Trust |
|---|---|---|
| KPI — support tickets per 1,000 users | M-601 | COND. RELIABLE |
| Trend — tickets by type | Derived | VERIFIED |
| KPI — support first-response time | Derived | COND. RELIABLE |
| **Panel — pipeline health** | Derived | VERIFIED |

**Design constraints:**

- **M-601 must carry the label "proxy — no error tracking exists."** Errors reach container stdout and are never persisted. This is a lagging proxy filtered by users' willingness to complain and biased toward loud failures. **A silent audio failure that drives users away produces no signal here, so a quiet week must not read as a healthy one.**
- The **pipeline health panel** makes the entire durability programme observable: drop count, dead-letter backlog, aggregation lag, snapshot gaps. Without it the dead-letter table fills and nobody knows — there is no APM and no metrics export.
- Snapshot gaps are **permanent holes**. Present them as such, not as pending.

---

## Page 9 — Metric Trust Register

**Endpoint**: `GET /Analytics/Metrics/Registry` — all 26 contracts.

A filterable table: metric ID, name, trust level, business purpose, technical definition, formula, exclusions, limitations, validation method.

**Why this is a page and not a wiki**: three of twelve shipped metrics were misleading, and **they looked exactly as credible as the nine that were sound**. The distinguishing information must be reachable from the number itself. **Every trust badge everywhere in the product links here.**

---

# 5. Drill-down model

```
Executive Overview  (Page 1)
      ↓  which input moved?
Area                (Pages 2–8)
      ↓  which metric?
Metric detail       (segments, trend, contract)
      ↓  which segment?
Segment             (category, host, room, cohort)
      ↓  show me the evidence
Underlying evidence (rows, events, the contract on Page 9)
```

Every widget declares its own drill-down target. **A-1 returns `drillDownEndpoint` per metric** — use it rather than hardcoding routes in the client, so a drill-down cannot point at a route that no longer exists.

**Every level must carry its trust state down with it.** A drill-down that loses the caveat is how a caveated number becomes an uncaveated screenshot.

---

# 6. Time range controls

| Control | Notes |
|---|---|
| Today · 7 days · 30 days · 90 days · Custom | **7 days is the default on Page 1** — M-100 is defined over a rolling 7-day window |
| Comparison: previous period | `compareTo=previous_period`. Equal-length adjacent window |

**Not every page should offer every range.** The cohort grid is inherently 8-weekly; the Decision Center is inherently weekly; the trust register has no range at all. Offering a 90-day range on a metric with 14 days of history invites a wrong reading.

**All requests are UTC.** The client converts for display only, using the server's offset — **never the device's**. The server offset represents the *audience's* timezone; the device offset represents *one viewer's*. Two admins in different countries must not disagree about when Cocorra's peak is.

---

# 7. Empty, gap and baseline states

**These are the most important states in the product, not an afterthought.** Design each distinctly — they are different claims and must not look alike.

| State | Meaning | Signal | Design |
|---|---|---|---|
| **No data** | Genuinely nothing in the window | `count: 0`, `isMeasured: true` | Real zero. A neutral "no activity" — this one *is* a finding |
| **Not measured** | The event is not instrumented | `isMeasured: false` + reason | **Labelled gap.** Never zero. Show `notMeasuredReason` |
| **Insufficient data** | Present but too thin to read | `hasSufficientHistory: false` | Hide the visualisation, explain the bar |
| **Collecting baseline** | Needs 4–6 weeks | Decision Center | Progress toward the gate, and what is available meanwhile |
| **Feature disabled** | Flag off | `isMeasured: false` | Gap + "instrumentation not yet enabled" |
| **Experimental** | Not yet validated | `trustLevel: Experimental` | De-emphasised + "not yet validated" |
| **Data delayed** | Pipeline stale | `pipelineHealthy: false` | Banner, plus per-metric staleness |
| **Read models empty** | Aggregation has not run | `readModelsPopulated: false` | **Distinct from "no data".** One is a pipeline state, the other a finding |
| **Error** | Request failed | HTTP 4xx/5xx | Never a zero-valued chart |

**The distinction between "no data" and "not measured" is the whole design.** Getting it wrong reproduces the exact failure this dashboard exists to correct.

---

# 8. Component inventory

Build a coherent system for these. **Do not use a chart type because it exists — the visualisation must match the analytical question.**

| Component | Where | Why this form |
|---|---|---|
| Navigation (10 pages) | Global | — |
| Page header + decision statement | Every page | The page states the decision it supports |
| Freshness indicator | Global, persistent | Trust is meaningless without it |
| Date range + comparison selector | Most pages | Contextual, not universal |
| **KPI card** with delta, trust badge, inline condition, drill-down | Pages 1, 2, 3, 6, 8 | The workhorse |
| **Trend line** | Page 1 (12-week WPU), Page 2 | Only where the *shape* matters |
| **Dual-axis trend** | Page 2 | Hosts vs rooms — the divergence is the finding |
| **Funnel** with four per-step states | Pages 3, 4 | Sequential progression |
| **Cohort grid / heatmap** | Page 6 | Retention across cohorts |
| **Schedule heatmap** (day × hour, local time) | Page 2 | Coverage gaps |
| **Histogram** | Pages 2, 4, 6 | Where a mean would mislead |
| **Bar chart** | Pages 5, 8 | Category comparison |
| **Table** with drill-in rows | Pages 2, 5, 9 | Per-entity investigation |
| **Insight card** (what/where/why/confidence/investigate) | Page 0 | The Decision Center's unit |
| **Alert / banner** | Global | Pipeline health |
| **Trust badge** (4 states) | Everywhere a number appears | The core mechanic |
| **Inline condition line** | Every COND. RELIABLE metric | Must not be a tooltip |
| **Gap / not-measured treatment** | Charts, funnels, KPIs | **The most important component** |
| Tooltip | Supplementary only | **Never for anything required to read the number correctly** |
| Drill-down panel | Global | — |
| Filters | Pages 2, 5, 9 | — |
| Empty / loading / error states | Every widget | Per §7 |

**Deliberately not in the inventory:** pie charts (the removed user-status pie is the cautionary example), gauges, radial progress, 3D anything, and any chart whose form carries less information than a number would.

---

# 9. Responsive behaviour

**Primary experience: desktop analytics dashboard.** That is where these decisions get made.

| Breakpoint | Behaviour |
|---|---|
| **Desktop** (primary) | Full multi-column layouts, complete tables, all charts |
| **Tablet** | Single-column stacking; charts keep full width; tables scroll horizontally in their own container |
| **Mobile** | **Decision summary → critical insights → key metrics → simple drill-down.** Tables become cards or are replaced by a link to desktop |

**Do not squeeze desktop tables into mobile screens.** A host leaderboard with six columns is not a mobile experience; the mobile answer is "here is the signal, here is the one number, open desktop to investigate".

**Never let the page body scroll horizontally.** Wide content scrolls inside its own container.

---

# 10. Visual direction

**The result must feel:** premium · modern · data-driven · professional · calm · high-trust.

**Brand**: the Cocorra logo will be supplied. Derive a semantic token system from it — extract the visual language rather than sampling colours literally into every surface.

Required tokens: `primary` · `secondary` · `accent` · `background` · `surface` · `surface-elevated` · `text-primary` · `text-secondary` · `border` · `success` · `warning` · `error` · `info`.

Plus a trust palette: four states, distinguishable **without relying on colour alone** — a colour-blind reader must still tell VERIFIED from EXPERIMENTAL.

## Explicitly prohibited

- Generic SaaS dashboard clones
- Random gradient backgrounds
- Rainbow KPI cards — colour must carry meaning, and a KPI's colour should reflect its trust or its direction, never decoration
- Excessive glassmorphism
- Decorative charts without an analytical purpose
- Tiny unreadable text
- Overcrowded screens
- **Metrics without context** — a number with no comparison, no trust state and no definition is the old dashboard
- **Fake data that looks like real production data**

## On mock data

Use realistic placeholder data, **clearly labelled as demo data**. Include the awkward cases deliberately, because they are where the design is decided:

- A metric with `isMeasured: false`
- A funnel with two unmeasured middle steps
- A cohort grid with insufficient history
- A stale pipeline banner
- A partial current period
- A growth-from-zero delta with no percentage

**A design that only shows the happy path has not been designed for this product.**

---

# 11. Non-negotiables

If the design gets everything else right and misses these, it has failed:

1. **`null` renders as a labelled gap, never `0`.** Uninstrumented and empty are opposite claims.
2. **CONDITIONALLY RELIABLE conditions render inline, not on hover.**
3. **UNRELIABLE metrics never appear.** The API removes them; the UI must not reintroduce them from cache or a hardcoded fallback.
4. **Freshness is always visible.** A trust badge over stale data certifies a lie.
5. **Hour-of-day charts apply the server's display offset.**
6. **Report rates are normalised per 1,000 joins**, with absolute counts beside them.
7. **Decision Center signals state what changed and what to investigate — never a cause.** No causal claim is supportable from this data.
8. **Decision Center signals are capped at 3–5 and ranked.**
9. **The cohort grid is hidden below 8 weeks.**
10. **Every trust badge links to Page 9.**

---

# 12. Reference index

| Document | Use it for |
|---|---|
| `09-recommended-dashboard.md` | Page structure, decisions, drill-down paths |
| `20-dashboard-implementation-blueprint.md` | Widget tables, trust UX, frontend contract tests |
| `29-final-metric-trust-register.md` | **Current trust grades — authoritative** |
| `26-metric-registry-reconciliation.md` | **Metric ID translation. Read before wiring any widget** |
| `28-production-analytics-activation.md` | Which data is live when |
| `FINAL-ANALYTICS-IMPLEMENTATION-REPORT.md` | What exists, what does not, and why |
| `GET /Analytics/Metrics/Registry` | The live contracts |

> **⚠ `20-dashboard-implementation-blueprint.md` uses the PLANNING metric IDs, and six of them mean something different in the shipped system.** Its widget tables say things like "reports per 1,000 joins → M-500", but `M-500` is Platform Summary. **Translate every ID through `26-` §4 before implementing a widget.** This brief and the Claude Design prompt therefore reference metrics **by name and endpoint**, never by the blueprint's ID.
