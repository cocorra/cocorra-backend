# 28 — Production Analytics Activation

> **Generated**: 2026-09-02
> **Audience**: whoever deploys and operates the Cocorra API.
> **Precondition**: nothing in this document has been executed. The analytics pipeline has never run in production with the AN-017/AN-018 events enabled.

---

# 0. Read this first

**Two feature flags are shipped and both default to `false`.** Every core-loop event added by AN-017 and AN-018 — ten event types, across `RoomHub` and `RoomService` — is currently emitting nowhere in production.

That is the intended sequencing, not an oversight. But it has a consequence worth stating plainly before the process below:

> **Every day the flags stay off is a day the stage funnel (M-400) has no data**, and `hand_raised` / `stage_promoted` **cannot be backfilled** — they were never captured, so history not collected today is permanently unavailable.
>
> Note that this does **not** apply to the Decision Center, contrary to earlier revisions of this document: it reads `DailyPlatformMetrics`, which the backfill can populate retroactively. See §5.

The counter-pressure is equally real: turning both on at once, without a measured baseline, risks saturating a bounded channel that the *currently working* events share. That is risk R-1, and the drop is only visible because AN-003 made it countable.

**The resolution is the staged process below, not a judgement call at deploy time.**

---

# 1. Configuration reference

All settings bind to the `Analytics` section (`EventTrackingOptions`, `SectionName = "Analytics"`). Defaults are now written explicitly into `Cocorra.API/appsettings.json` so they are discoverable rather than buried in a C# initialiser.

## Flags

| Key | Default | Effect |
|---|:--:|---|
| `EnableNewEventEmission` | `false` | AN-017 low-frequency increment: `room_went_live`, `stage_promoted`, `stage_demoted`, `participant_kicked`, `speaker_time_extended`, `speaker_time_exhausted`, and AN-041 `operation_failed` |
| `EnableHighFrequencyEvents` | `false` | AN-018 high-frequency increment: `hand_raised`, `hand_lowered`, `mic_deactivated` |

**The flags are conjunctive, enforced in code.** `EventTracker` computes:

```csharp
HighFrequencyEventsEnabled = NewEventEmissionEnabled && <the high-frequency flag>
```

Setting `EnableHighFrequencyEvents: true` while `EnableNewEventEmission` is `false` does **nothing**. This is deliberate: deploying the high-volume increment before the low-frequency one has proven stable would remove any ability to attribute a drop-rate spike to one increment or the other. Do not attempt to work around it.

## Pipeline

| Key | Default | Notes |
|---|:--:|---|
| `IpHashSalt` | *(none)* | **Required.** `Program.cs` throws at startup if missing — no insecure fallback. Keep in an env var or secret store, never in a committed file. |
| `EventChannelCapacity` | `10000` | Read at registration time, not via `IOptions`. Must be > 0 or startup throws. **The lever to pull before Stage C.** |
| `EventFlushBatchSize` | `100` | |
| `EventFlushMaxRetries` | `3` | Bounded retry before dead-lettering |
| `EventFlushInitialBackoffMs` | `200` | Exponential from here |

## Retention and aggregation

| Key | Default | Notes |
|---|:--:|---|
| `RawEventRetentionDays` | `180` | |
| `CleanupBatchSize` | `5000` | Lower this if the purge contends with ingestion |
| `AggregationIntervalMinutes` | `60` | |
| `AggregationBatchSize` | `50000` | |
| `AggregationTrailingDays` | `45` | Funnel-cohort recomputation window |
| `SnapshotHourUtc` | `0` | When `StateSnapshotService` captures state |

## Display and logging

| Key | Default | Notes |
|---|:--:|---|
| `DisplayTimeZoneOffsetMinutes` | `180` | AN-036. A **display hint only** — surfaced as `Meta.display.suggestedDisplayOffsetMinutes`. Changing it never changes what is stored or how anything is bucketed. |
| `StructuredLogPath` | *(unset)* | AN-042. Unset keeps current stdout-only behaviour. Set to a path **on a mounted volume** to also write newline-delimited JSON that survives a container restart. |
| `StructuredLogMinimumLevel` | `Warning` | |

## Environment overrides

Standard ASP.NET Core precedence applies: environment variables override `appsettings.{Environment}.json`, which overrides `appsettings.json`. Use the double-underscore form:

```bash
Analytics__EnableNewEventEmission=true
Analytics__EventChannelCapacity=25000
Analytics__IpHashSalt=<secret>
```

**RECOMMENDATION** — flip the flags by **environment variable, not by editing `appsettings.json`**. A flag set in the committed file is a flag that arrives with the next deploy of any branch; a flag set in the environment can be reverted in seconds without a build, which is what makes the rollback in §7 credible.

**`IpHashSalt` is no longer in any committed file.** It was externalised in Wave 8: `appsettings.json` carries only a pointer, `docker-compose.yml` reads `Analytics__IpHashSalt` from a gitignored `.env`, and `docker compose up` aborts with an explanatory message if the variable is unset.

**The old value remains in git history and must be treated as compromised.** Moving a secret does not un-publish it. Rotate on first deploy; the impact analysis is in `PRODUCTION-READINESS-REPORT.md` §2.

---

# 2. Stage A — Pipeline verification

**Goal: establish the baseline that AN-001 asked for, and that no subsequent stage can be judged without.**

Duration: **one full week** of normal traffic with flags off. Do not shorten this — the point is to know what "normal" looks like before changing anything.

## The single instrument

```
GET /Analytics/System/Health        (Admin only)
```

This replaces the log-grep AN-001 originally specified. The counters survive log rotation and are queryable; `docker logs` was only proposed because no counter existed at the time.

## What to record, daily

| Field | Healthy | Investigate |
|---|---|---|
| `pipelineHealthy` | `true` | Any `false` — read `warnings` |
| `eventsEnqueued`, `eventsPersisted` | Persisted tracks enqueued | A widening gap |
| **`eventsDroppedOnEnqueue`** | **`0`** | **Any sustained non-zero. This is R-1.** |
| `flushBatchesRetried` | Occasional | Continuous — the database is struggling |
| `flushBatchesFailed` | `0` | Any |
| `eventsDeadLettered` | `0` | Any |
| `duplicateEventsDiscarded` | Small, non-zero is fine | This is the idempotency guarantee working, not a fault |
| `deadLetterBacklog` | `0` | Any — rows are waiting for a human |
| `aggregationLagHours` | `< 3` | `> 3` sets `pipelineHealthy = false` |
| `aggregationConsecutiveFailures` | `0` | Any |
| `unaggregatedEventCount` | Stable, bounded | Growing monotonically — aggregation is not keeping up |
| `lastSnapshotDate` | Yesterday or today | Older |
| `snapshotGapDates` | Empty | **Any gap is permanent** — state counts cannot be backfilled |

**Counters are process-local and reset on container restart** — `countersSinceUtc` says when the window began. Record that field alongside the numbers or the numbers are meaningless.

## Database growth

Two direct queries, once at the start and once at the end of the week:

```sql
SELECT COUNT(*) FROM UserEvents;
SELECT CAST(OccurredAtUtc AS DATE) AS d, COUNT(*)
FROM UserEvents
WHERE OccurredAtUtc >= DATEADD(day, -30, GETUTCDATE())
GROUP BY CAST(OccurredAtUtc AS DATE) ORDER BY d;
```

This is **R-3**, and it is the input to the AN-045 partitioning decision, which is deferred precisely because this number has never been measured.

## Exit gate — all must hold

- [ ] Seven consecutive days recorded, with `countersSinceUtc` noted for each
- [ ] `eventsDroppedOnEnqueue` is **0**, or a known steady-state value with a stated cause
- [ ] `deadLetterBacklog` is **0**
- [ ] `aggregationLagHours` never exceeded 3
- [ ] `snapshotGapDates` empty for the whole week
- [ ] Daily event volume and total row count recorded
- [ ] **Peak-hour event rate** estimated — needed to size the channel in Stage C

**If the drop counter is already non-zero with the flags off, stop.** Adding events to a channel that is already saturating will degrade the events that currently work, and it does so silently apart from this counter. Raise `EventChannelCapacity` and repeat Stage A.

---

# 3. Stage B — Core event activation

**Goal: turn on the low-frequency increment alone.**

## The change

```bash
Analytics__EnableNewEventEmission=true
```

Nothing else. `EnableHighFrequencyEvents` stays `false`.

## What begins emitting

| Event | Site | Volume shape |
|---|---|---|
| `room_went_live` | `RoomService`, both start paths | Once per room |
| `stage_promoted` | `RoomHub.ApproveToStage` | Per promotion — host action |
| `stage_demoted` | `RoomHub.MoveToAudience` | Per demotion |
| `participant_kicked` | `RoomHub.KickUser` | Rare |
| `speaker_time_extended` | `RoomHub.GrantExtraTime` | Rare |
| `speaker_time_exhausted` | `RoomHub.ToggleMic` | Per exhausted speaker |
| `operation_failed` | `RoomHub` × 5 sites (AN-041) | Per refused join/promotion |

**All are bounded by host actions or refusals**, not by participant interaction. That is the definition of the low-frequency increment.

## Monitoring — first 48 hours

Check `GET /Analytics/System/Health` **hourly for the first 4 hours**, then every 4 hours.

| Signal | Action |
|---|---|
| `eventsDroppedOnEnqueue` moves off 0 | **Roll back immediately** (§7). Raise `EventChannelCapacity`, repeat. |
| `flushBatchesFailed` or `eventsDeadLettered` rises | Roll back; inspect `DeadLetterEvents` |
| `unaggregatedEventCount` grows monotonically for 6h | Aggregation cannot keep up; raise `AggregationBatchSize` |
| `aggregationLagHours > 3` | Investigate before proceeding |

## Exit gate

- [ ] **Seven days** at `EnableNewEventEmission=true`
- [ ] Drop counter unchanged from the Stage A baseline
- [ ] Dead-letter backlog still 0
- [ ] Aggregation lag still under 3h
- [ ] Events visible: `SELECT EventType, COUNT(*) FROM UserEvents WHERE OccurredAtUtc >= DATEADD(day,-7,GETUTCDATE()) GROUP BY EventType` shows the new types
- [ ] **`GET /Analytics/Participation/StageFunnel` shows step 3 (`stage_promoted`) as `isMeasured: true`** while step 2 (`hand_raised`) remains `isMeasured: false` — this is the partial-instrumentation state working correctly, and confirms the funnel is honest about what it does and does not know

---

# 4. Stage C — High-frequency event activation

**Goal: complete core-loop instrumentation.**

## Why this is separate

| Event | Volume driver |
|---|---|
| `hand_raised` | Every raise, **including repeats** — raise → lower → raise is deliberately two events, because two asks for the stage is real demand |
| `hand_lowered` | Every lower, from two paths |
| `mic_deactivated` | **Every mic close, from five sites** — self-mute, demotion, kick, room end, disconnect |

These scale with engagement and land hardest on the busiest rooms — exactly where the channel is already most loaded. `mic_deactivated` in particular fires on every mute in an active conversation.

## Before flipping: size the channel

Using the Stage A peak-hour rate, estimate the multiplier. A conservative starting point:

```bash
Analytics__EventChannelCapacity=25000     # from 10000
Analytics__EnableHighFrequencyEvents=true
```

**Raise the capacity in a separate, earlier deploy than the flag.** The capacity is read at registration time and requires a restart; doing both at once means a drop spike cannot be attributed to either.

## Monitoring — first 24 hours

**Hourly, without exception**, and specifically **during the platform's peak hour** (from Stage A's peak-hours data, converted with `Meta.display.suggestedDisplayOffsetMinutes` — the raw UTC hour is 2–3 hours off local).

Any movement in `eventsDroppedOnEnqueue` → roll back this flag only. `EnableNewEventEmission` stays on; that is the entire reason the increments are separate.

## Exit gate

- [ ] Seven days at both flags true
- [ ] Drop counter still at baseline
- [ ] **`GET /Analytics/Participation/StageFunnel` returns `isFullyInstrumented: true`** with all four steps carrying counts
- [ ] Funnel monotonicity holds in production data (each step ≤ the previous)
- [ ] `directPromotionsWithoutHandRaise` is non-null — both events are live

---

# 5. Stage D — Baseline collection

## There is no single baseline clock — there are four

> **CORRECTED 2026-09-02 (Wave 8).** Earlier revisions of this document, and
> `FINAL-ANALYTICS-IMPLEMENTATION-REPORT.md`, stated that "the baseline clock starts when
> `EnableHighFrequencyEvents` goes true". **That is wrong for three of the four families**, and
> it was wrong in the direction that costs the most time: it implied a 4–6 week wait for the
> Decision Center that the backfill already satisfies.
>
> The error came from the planning documents, which assumed read models could only accumulate
> forward. They can be **backfilled**, and `AnalyticsBackfillService` runs the same rollup code
> path as live aggregation, so a backfilled week is indistinguishable from a lived one.

Four independent clocks. **Different metric families start at different times, and pretending
one timestamp covers them all is what produced the error above.**

### Clock 1 — Relational metrics: ALREADY RUNNING, since day one

| | |
|---|---|
| **Starts** | Cocorra's first room. Nothing to wait for. |
| **Source** | `Rooms`, `RoomParticipants`, `AspNetUsers` — relational, never purged |
| **Backfillable** | **Fully**, to the platform's first day |
| **Metrics** | M-200, M-201, M-202, M-205, M-300, M-301, M-501, M-503, M-504 |
| **Action** | Run the backfill. No waiting. |

### Clock 2 — Read-model metrics: SATISFIED BY BACKFILL

| | |
|---|---|
| **Starts** | First successful aggregation — **but retroactively extended by backfill** |
| **Source** | RM-1 `DailyPlatformMetrics`, RM-3 `DailyHostMetrics` |
| **Backfillable** | **RM-3 to day one. RM-1 to day one for every field except `VoiceVerificationsSubmitted` and `VoiceVerificationsApproved`, which are event-derived and therefore bounded by the 180-day raw window.** RM-1's participation fields come from `RoomParticipants.JoinedAt`, not from `room_joined` events. |
| **Metrics** | M-100, M-101, and **the Decision Center** |
| **Action** | Backfill, then **read `weeksOfHistory` from `GET /Analytics/Decisions`.** |

**The Decision Center is not gated on elapsed time.** `DecisionCenterService.RequiredBaselineWeeks = 4`, counted as **complete weeks present in `DailyPlatformMetrics`**. A successful backfill over the last quarter satisfies that immediately. The endpoint returns `weeksOfHistory`, `requiredBaselineWeeks` and `hasBaseline` — **read those rather than counting days on a calendar.**

### Clock 3 — Snapshot metrics: STARTS AT FIRST DEPLOY, and CANNOT be recovered

| | |
|---|---|
| **Starts** | The first successful `StateSnapshotService` run — **Stage A, deploy day.** Not Stage B, not Stage C. |
| **Source** | RM-5 `DailyStateSnapshots` |
| **Backfillable** | **NO. Structurally impossible.** No event records how many users were `Pending` yesterday. |
| **Metrics** | M-303 queue-depth **trend**, FCM token-coverage trend |
| **Action** | **Deploy sooner rather than later.** Every day before the first deploy is a permanent hole. |

**This is the only clock where delay destroys evidence**, and it is the one the earlier text obscured by pointing at Stage C. `snapshotGapDates` on `GET /Analytics/System/Health` lists holes already accumulated.

### Clock 4 — New-event metrics: STARTS AT STAGE B / STAGE C

| | Stage B events | Stage C events |
|---|---|---|
| **Starts** | `EnableNewEventEmission=true` | `EnableHighFrequencyEvents=true` |
| **Events** | `room_went_live`, `stage_promoted`, `stage_demoted`, `participant_kicked`, `speaker_time_extended`, `speaker_time_exhausted`, `operation_failed`, `room_reminder_toggled`, `moderation_action_taken` | `hand_raised`, `hand_lowered`, `mic_deactivated` |
| **Backfillable** | **NO — never captured** | **NO — never captured** |
| **Metrics** | M-400 step 3 | **M-400 step 2**; reserved M-401, M-402 |

**Record both timestamps.** They are the `dataAvailableFromUtc` for anything resting on those events, and they are the only clocks that genuinely cannot be compressed.

### The one-line answer

> **Q: When does the baseline clock start?**
>
> **A: There is no single clock.** Relational and read-model metrics have their baseline
> *already*, once the backfill runs — including the Decision Center. Snapshot metrics start
> the day the container first runs and can never be recovered. Only the new core-loop events
> start at Stage B and Stage C, and only those require waiting.

### Metric maturity is a separate gate from baseline

`M-400` is graded **EXPERIMENTAL** and promotes to VERIFIED after **4 weeks of stable emission**
following Stage C. That is a *validation* period — confirming the events behave as designed in
production — not baseline accumulation. It is genuine and it cannot be compressed.

## Analysable immediately (no baseline needed)

These rest on relational data that was never purged and needs no new events:

| Available now | Endpoint |
|---|---|
| Supply health — active hosts, host retention, concentration, schedule | `/Analytics/Supply/Health` |
| Report rate by room category | `/Analytics/Safety/ReportRate` |
| Review latency percentiles + queue depth | `/Analytics/Review/Latency` |
| Support volume and response times | `/Analytics/Support` |
| Sequential activation funnel | `/Analytics/Activation/Funnel` |
| Weekly return rate | `/Analytics/Return/Weekly` |
| Social graph | `/Analytics/Social` |
| MBTI dichotomies | `/Analytics/Mbti/Dichotomies` |
| Metric trust register | `/Analytics/Metrics/Registry` |
| Pipeline health | `/Analytics/System/Health` |

**These are the majority of the dashboard, and they are ready before the flags are touched.** Build them first.

## Must wait

| Waiting on | What | Why |
|---|---|---|
| Stage C + 4 weeks | **M-400 stage funnel** graded EXPERIMENTAL → VERIFIED | The contract says promotion requires stable emission, not elapsed time alone |
| **Backfill, not elapsed time** | **Decision Center** (`/Analytics/Decisions`) | Needs 4 complete weeks present in RM-1, which the backfill supplies. **Read `hasBaseline` from the endpoint; do not count calendar days.** Until it reports true, render "Collecting baseline" — change detection against no baseline alerts on ordinary variance, and a dashboard that cries wolf in its first month is ignored permanently. |
| RM-5 accumulation | Pending-queue and FCM-coverage trends | Snapshots cannot be backfilled |
| **8 weeks of room_joined history** | **Cohort grid** (`/Analytics/Return/CohortGrid`) | Reads raw `UserEvents`, **not RM-1**, so its gate depends on how long `room_joined` has been emitting, bounded above by the 180-day retention window. Find the real start with `SELECT MIN(OccurredAtUtc) FROM UserEvents WHERE EventType = 'room_joined'`. Check `hasSufficientHistory` before rendering; hidden beats sparse. |
| A production query | **AN-032** group-chat materiality; **AN-035** session-signal evidence (R-4); **AN-045** partitioning (R-3) | All are measurements, not code |

---

# 6. Backfill

Once Stage B is stable, replay the read models over history:

```
POST /Analytics/System/Backfill?from=&to=&force=      (Admin only)
```

Shares the rollup code path with live aggregation, so backfilled and live rows are byte-identical by construction rather than by assertion.

| Read model | Backfill |
|---|---|
| `DailyHostMetrics` (RM-3) | **Full history** — derives from `Rooms`, relational, never purged. Cocorra's leading indicator can be reconstructed from day one. |
| `DailyPlatformMetrics` (RM-1) | 180 days — bounded by raw retention |
| `DailyRoomMetrics` (RM-2) | Partial — joins/speakers/reports yes; hand raises, promotions and speaking seconds **no**, never captured |
| `DailyFunnelMetrics` (RM-4) | Partial — onboarding funnel yes; stage funnel **no** |
| `DailyStateSnapshots` (RM-5) | **None.** Pure state, structurally unrecoverable. |

**Never fabricate historical events.** Metrics with no historical source return `dataAvailableFromUtc` and must render as visible gaps.

---

# 7. Rollback

Every stage rolls back by configuration. No code change, no migration reversal.

| Failure | Rollback | Data impact |
|---|---|---|
| Drop counter rises after Stage B | `Analytics__EnableNewEventEmission=false`, restart | AN-017 events missing for the window; **no loss to pre-existing events** |
| Drop counter rises after Stage C | `Analytics__EnableHighFrequencyEvents=false`, restart. **Leave `EnableNewEventEmission` on.** | AN-018 events missing for the window only |
| Aggregation produces wrong values | Truncate the affected read model and re-run backfill | **None** — INV-5 guarantees recomputability from raw events |
| Purge contends with ingestion | Lower `CleanupBatchSize` | None |
| Dead-letter backlog growing | Investigate `DeadLetterEvents`; events are **not lost**, they are parked | None — that is the point of the table |
| Structured log volume | Unset `StructuredLogPath` | Reverts to stdout-only |

**What does NOT roll back cleanly**, stated because both were flagged as high-risk and neither is reversible by a flag:

- **`user_status_changed`** — the only durable record of a status transition. There is no `UpdatedAt` and no history table. Reverting its emission loses every transition in the window **permanently**.
- **Soft delete (AN-013)** — not implemented, blocked on a data-protection decision. Every hard delete until that decision lands is unrecoverable for all historical metrics. Three shipped metrics (M-102, M-501, M-103) carry an upward-bias limitation because of it.

---

# 8. Readiness

| Requirement | State |
|---|---|
| Flags shipped, defaulted off, documented | ✅ |
| Flag semantics enforced (conjunctive) | ✅ in `EventTracker` |
| Defaults explicit in `appsettings.json` | ✅ this phase |
| Rollback by configuration alone | ✅ |
| Monitoring endpoint | ✅ `GET /Analytics/System/Health` |
| Health thresholds encoded, not just documented | ✅ `PipelineHealthService` sets `pipelineHealthy=false` on stale aggregation, dead letters, drops, or snapshot gaps |
| Backfill shares the live code path | ✅ |
| **Stage A baseline recorded** | ❌ **NOT DONE — this is the gate** |
| `IpHashSalt` moved out of the committed file | ❌ **recommended before Stage B** |

**Verdict: READY TO BEGIN STAGE A.** Not ready to enable either flag, because that requires Stage A's output and Stage A has not run.

---

# 9. Fastest defensible path

```
Week 0    Salt externalised (done, Wave 8). Deploy. Begin Stage A.
          ← RM-5 SNAPSHOT CLOCK STARTS. Unrecoverable, so this is the one
            date worth pulling forward.
Week 1    Stage A exit gate. Run the backfill.
          ← Decision Center baseline likely SATISFIED here, not in week 9.
            Verify with hasBaseline on GET /Analytics/Decisions.
          → Build dashboard pages 1,2,3,5,7,8,9 (all available now)
Week 2    Stage B: EnableNewEventEmission=true     ← AN-017 EVENT CLOCK STARTS
Week 3    Stage B exit gate. Raise EventChannelCapacity, deploy, verify.
Week 4    Stage C: EnableHighFrequencyEvents=true  ← AN-018 EVENT CLOCK STARTS
Week 5    Stage C exit gate. Stage funnel isFullyInstrumented=true.
Week 8    M-400 EXPERIMENTAL → VERIFIED (4 weeks of stable emission).
          Cohort grid gate depends on room_joined history, not on this
          schedule — query MIN(OccurredAtUtc) for its real date.
```

**INFERENCE — the ordering matters more than the duration.** Seven of ten dashboard pages need none of this and can be built during weeks 0–1. The three that wait are waiting on time, which no amount of effort compresses. Building the available pages first means the dashboard delivers value in week 1 rather than week 13, and it puts the trust register in front of users before any number they might act on.
