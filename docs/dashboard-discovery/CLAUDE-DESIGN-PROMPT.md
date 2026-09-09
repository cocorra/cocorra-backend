# Claude Design Prompt — Cocorra Decision Intelligence Dashboard

> **How to use this file**: copy everything below the divider into Claude Design, and attach the official Cocorra logo. The prompt is self-contained — it assumes no access to this repository.

---

I need you to design the **Cocorra Decision Intelligence Dashboard**: a complete, implementation-ready analytics product. I will attach the official Cocorra logo.

---

## PART 1 — Brand identity

**I am attaching the official Cocorra logo. Before designing anything:**

1. **Inspect the logo carefully.** Read its shapes, weight, geometry, spacing and character — not just its colours.
2. **Extract the visual brand language.** Is it geometric or organic? Warm or cool? Dense or airy? Serious or approachable? What does the logo's construction imply about type weight, corner radius and spacing rhythm?
3. **Derive a professional colour system from it.** Use the logo as *inspiration and anchor*, not as a paint bucket — do not sample its colours onto every surface. A dashboard read for hours needs a calm, mostly-neutral field with brand colour used sparingly and meaningfully.
4. **Create semantic design tokens**, not raw hex values scattered through the design.

**Required tokens:**

```
primary            secondary          accent
background         surface            surface-elevated
text-primary       text-secondary     border
success            warning            error            info
```

**Accessibility is a requirement, not a nice-to-have.** WCAG AA minimum for all text — this product is read for long sessions by people making consequential decisions. Ensure every state is distinguishable **without relying on colour alone**; a colour-blind reader must be able to tell a verified metric from an experimental one.

**The result must feel:** premium · modern · data-driven · professional · calm · high-trust.

**Do not** use excessive gradients, rainbow-coloured cards, or decorative colour. In this product **colour carries meaning** — trust state, direction of change, severity. Colour spent on decoration is colour that can no longer carry information.

---

## PART 2 — What this product is, and the failure it exists to correct

**Cocorra** is a voice-room platform. Verified users join live audio rooms hosted by coaches, raise a hand to be promoted to a stage, and speak. Rooms belong to one of three categories, two of which — **Relationships** and **MentalHealth** — carry duty-of-care obligations a general social product does not.

The value loop is: **a listener joins a room and becomes a speaker.**

### This is NOT a generic admin dashboard

It is a **Decision Intelligence Dashboard**. It exists to answer four questions:

```
WHAT CHANGED?
WHERE?
WHY MIGHT IT HAVE CHANGED?
WHAT SHOULD WE INVESTIGATE NEXT?
```

**Not** to display many numbers. Every widget must narrow a decision. If it does not, it should not be on the page.

### The failure this design must not repeat

The previous Cocorra dashboard showed twelve metrics. **One was verified. Three were actively misleading. All twelve rendered identically.**

The sharpest case: a room host's microphone opens automatically when the room starts, so a silent host accrued the room's full 2–3 hours as "speaking time". The same coach therefore appeared as **the platform's #1 speaker and as a silent listener, in two panels of the same dashboard** — both looking equally credible.

**The failure was not that some numbers were wrong. Every analytics system has wrong numbers. The failure was that nothing told them apart — and that is a UI failure as much as a data one.**

The backend has been rebuilt to attach a trust contract to every number. **Your design is what makes that visible. If the UI renders a caveated number the same way it renders a verified one, the whole programme has produced a faster wrong dashboard.**

---

## PART 3 — The trust system (the core design mechanic)

Every metric arrives with a trust level. **Four states, four visually distinct treatments:**

| State | Treatment |
|---|---|
| **VERIFIED** | Normal presentation. Small, unobtrusive badge |
| **CONDITIONALLY RELIABLE** | Badge **plus the condition rendered inline** — one line of text adjacent to the number |
| **EXPERIMENTAL** | Badge plus visual de-emphasis (reduced weight, or a distinct container) and an explicit "not yet validated" note |
| **UNRELIABLE** | **Never displayed at all** |

Trust must be **visible but not overwhelming.** A dashboard where every number shouts about its own reliability is unreadable. Badges should be quiet, consistent and always in the same relative position, so a reader learns to check them without being shouted at.

### THE MOST IMPORTANT RULE IN THIS BRIEF

**A metric with no measurement renders as a visible, labelled gap. Never as zero.**

```
Stage Funnel — this week

  Joined room            1,247  ████████████████████
  Raised hand               ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Promoted to stage         ??  ░░░░░░░░░░░░░░░░░░░░  not measured
  Activated microphone     312  █████                 observed only
```

`0` and "not measured" look identical on a chart and they are **opposite claims**:

- `0` says **nobody raised their hand** — a confident, plausible, and false conclusion
- "not measured" says **we did not instrument this yet**

**Most charting libraries coerce missing values to zero by default. That default fabricates findings.** Design the gap treatment explicitly — for line charts, bar charts, funnels, KPI cards and tables.

Showing the gap does three things at once: it prevents a wrong conclusion, it tells the reader why the chart cannot be diagnosed, and it keeps the instrumentation debt visible to the people who could fund closing it.

### Two more rules that are functional, not aesthetic

**Conditions are inline, never on hover.** A tooltip is not read by someone scanning a dashboard, and **it does not survive a screenshot**. The condition is what makes a caveated number usable.

**Freshness and trust are shown together, or neither means anything.** A persistent header element shows pipeline freshness ("Updated 12 minutes ago" / a staleness warning / a "data may be incomplete" banner). A trust badge over three-day-old data **certifies stale data as verified**, which is worse than no badge.

---

## PART 4 — Information architecture

**Ten pages. Do not invent pages or metrics.** Each is backed by a real API.

| # | Page | The decision it supports | Data state |
|:--:|---|---|---|
| **0** | **Decision Center** | Where should attention go this week? | 🟡 **Collecting baseline** — the server reports when it ends |
| **1** | **Platform Health** | Is Cocorra delivering more value, and which input constrained it? | 🟢 Live |
| **2** | **Supply Health** | Recruit more coaches, or help existing coaches run better rooms? | 🟢 Live |
| **3** | **Activation Pipeline** | Restructure onboarding, invest in review capacity, or leave the gate alone? | 🟢 Live |
| **4** | **Room Participation** | Redesign the stage flow, change room defaults, or leave the core loop alone? | 🟠 **Partly not measured** |
| **5** | **Safety & Trust** | Proactive moderation, category safeguards, or a different enforcement ladder? | 🟢 Live · **Admin only** |
| **6** | **Return & Repeat** | Prioritise retention work — and does Cocorra know enough to say? | 🟠 Cohort grid needs 8 weeks |
| **7** | **Social Surfaces** | Invest in messaging and the friend graph, or treat them as utilities? | 🟢 Live · keep small |
| **8** | **Reliability** | Invest in stability, media infrastructure, or notification delivery? | 🟢 Live |
| **9** | **Metric Trust Register** | Can I rely on the number I am looking at? | 🟢 Live |

**Design every page in the state it will actually ship in.** Pages 0, 4 and 6 must be designed in their gated states as well as their populated states — the gated state is what users will see first, for weeks.

---

## PART 5 — Page requirements

### Page 1 — Platform Health (the landing view)

Six KPI cards, each with a previous-period delta and a drill-down:

1. **Weekly Participating Users** — *the north star* — VERIFIED
2. Speaking Conversion Rate — VERIFIED
3. Distinct Active Hosts — VERIFIED
4. Rooms Gone Live — VERIFIED
5. New Activations — VERIFIED
6. New Registrations — VERIFIED

Plus **one chart only**: a 12-week trend of the north star. Everything else is a current value with a delta, which is a KPI, not a chart.

- **Make the north star visually dominant.** Not one of six equal tiles — the hierarchy should make it unmistakable and the other five read as its inputs.
- **A bare headline number is close to meaningless.** The period comparison is not decoration. If no comparison is available, say so rather than showing a lone figure.
- **Growth from zero has no percentage.** When the previous value is zero, show the absolute change only — never "+100%", never "∞".

### Page 2 — Supply Health (highest value-to-effort page)

KPI: Active Hosts · KPI: Host Second-Room Rate · **dual-axis trend of hosts + rooms** · **histogram of rooms per host** · host leaderboard table · **schedule heatmap by day × hour**.

- **Hosts and rooms must share one chart with a dual axis.** The failure mode neither reveals alone is *rooms flat while hosts decline* — fewer people carrying the same load. The room count looks healthy while the platform becomes more fragile.
- **Show the histogram, not the mean.** With a small coach pool one prolific host makes the average meaningless. **The shape is the finding.**
- **The heatmap must render in local time**, using a display offset the server supplies (+180 minutes / UTC+3 by default). The server computes in UTC; the audience is UTC+2/+3. An unconverted heatmap points a coach at a slot 2–3 hours off the real peak — **a confident wrong answer, worse than showing nothing.**

### Page 3 — Activation Pipeline

Sequential funnel with elapsed time per step · **review-latency distribution (p50/p90/p99)** · pending-queue-depth trend · review outcome mix.

- **Never show a mean review latency.** The API does not return one, by contract. If most reviews take 20 minutes and 15% take three days, the mean describes nobody and hides the users being harmed. **Design for percentiles.**
- **Queue depth must sit beside latency.** Pending submissions are excluded from latency, so a growing backlog does not move that figure.

### Page 4 — Room Participation (where the gap treatment is decided)

The stage funnel has four steps: **joined room → raised hand → promoted to stage → activated microphone.** Two middle steps are **not yet instrumented in production**.

Design the funnel for **four distinct per-step states**:

| State | Render |
|---|---|
| Measured | Normal bar with a count |
| Not instrumented | **Labelled gap** with the server's reason text |
| Instrumented, but an upstream step is missing | **Gap for the funnel bar**, with the raw observed figure shown separately and labelled "observed only" |
| Instrumentation started mid-window | Boundary marker at the start-of-data point |

Also surface **"direct promotions without a hand raise"** — participants a host invited to the stage directly. A strict sequential funnel drops them at step 2, which without this reads as promotions vanishing.

This page is graded **EXPERIMENTAL**. It must look like a page that is not yet load-bearing.

### Page 5 — Safety & Trust (Admin only)

**Report rate by room category, per 1,000 room joins** · report category mix · most-reported-users table.

- **Rates normalised per 1,000 joins, never raw counts.** Raw counts rise with growth; only the normalised form answers "is it getting worse".
- **Absolute counts must appear beside every rate.** With three categories, cells can be small enough that a percentage alone misleads.
- **A category with no joins shows a gap, not a zero rate.**
- The metric measures **reports filed, not incidents occurred** — under-reporting in a sensitive category would read as safety. Put that caveat on the page.
- **Admin only.** Design the Coach experience as this page being absent, not as an empty state.

This page's category bar chart is the **highest information-per-pixel widget in the product**: three bars answering the platform's highest-stakes safety question.

### Page 6 — Return & Repeat

Weekly return rate KPI · **8-week cohort retention grid**.

- **Hide the cohort grid until 8 weeks of history exist.** A sparse grid of mostly-empty cells invites over-interpretation of tiny samples. **Hidden beats sparse.** Design the hidden state.
- **Recent cohorts have shorter rows by construction. A short row is missing data, not a retention collapse.** The visual must not permit that misread.
- **An upward-bias warning goes on the page, not in a tooltip.** Deleted users are removed from the data, biasing every return rate upward by an unknown margin. A rate that looks acceptable may be acceptable only among survivors.

### Page 7 — Social Surfaces

**Keep this page deliberately small.** These are supporting surfaces whose adoption is capped upstream by product structure. Giving them real estate proportional to their strategic weight is itself a design decision — an oversized social page would invite investment the evidence does not support.

**Mandatory ordering: reciprocity before volume.** A high volume of one-directional messages is a warning sign — plausibly unwanted contact — not engagement growth. Ordering shapes the first reading.

### Page 8 — Reliability

Support tickets per 1,000 users · tickets by type · first-response time · **pipeline health panel** (event drop count, dead-letter backlog, aggregation lag, snapshot gaps).

- The support metric **must carry the label "proxy — no error tracking exists."** It counts problems users bothered to report, not failures that occurred. **A silent failure that drives users away produces no signal here, so a quiet week must not read as a healthy one.**
- **Snapshot gaps are permanent holes**, not pending work. Present them that way.

### Page 9 — Metric Trust Register

A filterable table of all 26 metric contracts: ID, name, trust level, business purpose, technical definition, formula, exclusions, limitations, validation method.

**Why this is a page and not a wiki**: three of twelve previous metrics were misleading and **looked exactly as credible as the nine that were sound**. The distinguishing information must be reachable from the number itself. **Every trust badge in the product links here.**

### Page 0 — Decision Center (design both states)

**Do not design live change detection as the launch state.** The backend reports whether a baseline exists yet, and on day one it will not. Detection without one alerts on ordinary variance, and **a dashboard that cries wolf in its first month is ignored permanently — harder to recover from than a delayed launch.**

Design the **"Collecting baseline"** state: progress toward the gate, and what *is* available meanwhile.

Then design the populated state. Signal card anatomy:

```
WHAT CHANGED        Speaking conversion fell from 27% to 19%
WHERE               Room Participation · MentalHealth category
WHY IT MIGHT HAVE   Stage promotions down 40%; hand raises flat
CONFIDENCE          Medium — 3 weeks of baseline
INVESTIGATE         → Stage Funnel   → Room Participation
```

- **Cap signals at 3–5, ranked.** A Decision Center producing ten alerts a week is ignored by week three. **The cap is a functional requirement, not a layout preference.**
- **Signals state what changed and what to investigate — never a cause.** No causal claim is supportable from this data. Present confidence and evidence, never certainty.
- A **pipeline health banner** at the top answers the prior question: *can I trust today's numbers at all?*

---

## PART 6 — Drill-down model

```
Executive Overview  →  Area  →  Metric  →  Segment  →  Underlying evidence
   (Page 1)          (2–8)    (detail)   (category,      (rows, events,
                                          host, room,      the contract)
                                          cohort)
```

Design this as a coherent navigation pattern, not ten unrelated links.

**Every level carries its trust state down with it.** A drill-down that loses the caveat is how a caveated number becomes an uncaveated screenshot in someone's deck.

---

## PART 7 — Time range controls

`Today` · `7 days` · `30 days` · `90 days` · `Custom range` · `Comparison: previous period`

- **7 days is the default on Page 1** — the north star is defined over a rolling 7-day window.
- **Not every page offers every range.** The cohort grid is inherently 8-weekly; the Decision Center is weekly; the trust register has no range. Offering 90 days on a metric with 14 days of history invites a wrong reading.
- When the range includes today, **label the period as incomplete** ("week to date") and do not plot it as a completed point. Otherwise the current week always looks like a decline, and a dashboard that shows a drop every Monday trains its readers to ignore drops.

---

## PART 8 — Empty, gap and baseline states

**These are the most important states in this product.** Design each **distinctly** — they are different claims and must not look alike:

| State | Meaning |
|---|---|
| **No data** | Genuinely nothing happened in the window. A real zero — **this one is a finding** |
| **Not measured** | The event is not instrumented. **A labelled gap. Never a zero** |
| **Insufficient data** | Present but too thin to read — hide the visualisation, explain the bar |
| **Collecting baseline** | The server reports progress toward the gate; show that progress and what is available meanwhile |
| **Feature disabled** | Instrumentation exists but is switched off |
| **Experimental** | Computed, not yet validated |
| **Data delayed** | The pipeline is stale — banner plus per-metric staleness |
| **Loading** | — |
| **Error** | Request failed. **Never render as a zero-valued chart** |

**The distinction between "no data" and "not measured" is the whole design.** Getting it wrong reproduces the exact failure this dashboard exists to correct.

---

## PART 9 — Component system

Design a coherent system for:

```
Navigation                     Page header + decision statement
Freshness indicator            Date range picker
Comparison selector            KPI cards (with delta, trust badge, inline condition)
Trend / line charts            Dual-axis trend
Bar charts                     Funnel visualisation (with four per-step states)
Cohort / retention grid        Schedule heatmap (day × hour)
Distribution / histogram       Data tables with drill-in rows
Insight cards                  Alert / banner cards
Trust badges (4 states)        Inline condition lines
Status indicators              Gap / not-measured treatments
Tooltips (supplementary only)  Drill-down panels
Filters                        Empty / loading / error states
```

**Do not use every chart type because it exists. The visualisation must match the analytical question.**

- Use a **histogram** where a mean would mislead (rooms per host, rooms per user)
- Use a **dual axis** only where the divergence between two series *is* the finding
- Use a **heatmap** only for genuine two-dimensional density (day × hour)
- **No pie charts.** The removed user-status pie is this product's cautionary example
- **No gauges, no radial progress, no 3D.** None carries more information than a number would
- **Tooltips are supplementary only** — never for anything required to read the number correctly

---

## PART 10 — Responsive behaviour

**Primary experience: desktop analytics dashboard.** That is where these decisions get made.

| Breakpoint | Behaviour |
|---|---|
| **Desktop** | Full multi-column layouts, complete tables, all charts |
| **Tablet** | Single-column stacking; charts keep full width; tables scroll inside their own container |
| **Mobile** | **Decision summary → critical insights → key metrics → simple drill-down** |

**Do not squeeze desktop tables into mobile screens.** A six-column host leaderboard is not a mobile experience; the mobile answer is "here is the signal, here is the one number, open desktop to investigate".

**The page body must never scroll horizontally.** Wide content scrolls inside its own container.

---

## PART 11 — Quality bar

### Explicitly prohibited

```
Generic SaaS dashboard clones
Random gradient backgrounds
Rainbow KPI cards
Excessive glassmorphism
Decorative charts without analytical meaning
Tiny unreadable text
Overcrowded screens
Metrics without context (no comparison, no trust state, no definition)
Fake data that looks like real production data
```

### On mock data

Use realistic placeholder data, **clearly presented as mock/demo data**.

**Include the awkward cases deliberately** — they are where this design is actually decided:

- A KPI that is not measured
- A funnel with two unmeasured middle steps
- A cohort grid with insufficient history
- A stale-pipeline banner
- A partial current period
- A growth-from-zero delta with no percentage
- A category with a `null` rate rather than a zero

**A design that only shows the happy path has not been designed for this product.**

---

## PART 12 — Deliverables

1. **Design system** — tokens derived from the logo, type scale, spacing, elevation, the four-state trust palette
2. **All ten pages**, desktop
3. **Page 0 Decision Center** in both its baseline-collecting and populated states
4. **Page 4** stage funnel showing all four per-step states
5. **Drill-down flow** — overview → area → metric → segment → evidence
6. **Trust state treatments** — all four, shown in context on a KPI, a chart and a table
7. **Empty / gap / baseline states** — every state from Part 8
8. **Component library** — every component in Part 9
9. **Responsive guidance** — tablet and mobile behaviour per Part 10

**The design must be implementation-ready**: a developer should be able to build from it without asking what a state looks like.

---

## PART 13 — The ten non-negotiables

If the design gets everything else right and misses these, it has failed:

1. **A missing measurement renders as a labelled gap, never `0`.**
2. **Conditionally reliable conditions render inline, not on hover.**
3. **Unreliable metrics never appear.**
4. **Freshness is always visible.**
5. **Hour-of-day charts apply the server's display offset, not the viewer's device timezone.**
6. **Report rates are normalised per 1,000 joins, with absolute counts beside them.**
7. **Decision Center signals state what changed and what to investigate — never a cause.**
8. **Decision Center signals are capped at 3–5 and ranked.**
9. **The cohort grid is hidden below 8 weeks of history.**
10. **Every trust badge links to the Metric Trust Register.**

---

## One last thing

The team that built the backend for this dashboard found that the original failure was never that some metrics were wrong. Every analytics system has wrong metrics.

**The failure was that nothing distinguished them.**

Your design is the part that distinguishes them. A beautiful dashboard that renders a caveated number identically to a verified one would be a more persuasive version of the problem we just spent a programme fixing.

Design for the reader who is about to make a decision and needs to know whether they can.
