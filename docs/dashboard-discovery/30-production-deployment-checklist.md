# 30 — Production Deployment Checklist

> **Generated**: 2026-09-02 (Wave 8) | **Target commit**: the tip of `main` after Wave 8
> **Audience**: the person running the deployment. Not a summary — a list to work through with a terminal open.
> **Deployment model**: Docker Compose on a VPS, per `docker-compose.yml`.

---

> **Route shorthand.** Analytics routes are written below both in full
> (`GET /Api/V1/Analytics/System/Health`) and in the shorthand used across the other documents
> (`GET /Analytics/System/Health`). **The real prefix is always `Api/V1/`** — `Router.Root` is
> `"Api"` and `Router.Version` is `"V1"`. Prepend it if you are pasting into curl.

---

## Read these three things first

1. **Migrations are NOT applied automatically.** There is no `Database.Migrate()` anywhere in the solution. Three analytics migrations are unapplied in production. If you deploy without applying them, **the app will start successfully and then fail on every event insert and every analytics query**, because the columns and tables will not exist. §B is not optional.

2. **`ANALYTICS_IP_HASH_SALT` is now required.** Without it `docker compose up` aborts, and if it somehow got past that, `Program.cs` throws at startup. This is intentional — a default or empty salt would produce hashes that look pseudonymised and are not.

3. **The container health check does not check health.** It curls `/`, which returns a static HTML page. **A container can report `healthy` while the database is unreachable and every background service is failing.** Use §D, not `docker ps`.

---

# A. Before deployment

## Source and build

- [ ] `git status --short` is empty — working tree clean
- [ ] `git log --oneline -1` matches the commit you intend to deploy; **write the SHA down**, §F needs it
- [ ] `git diff --check` reports nothing (no conflict markers, no whitespace errors)
- [ ] `dotnet build Cocorra.sln --no-incremental` → **0 errors**. 13 warnings is the expected baseline; investigate anything above that
- [ ] `dotnet test Cocorra.Tests/Cocorra.Tests.csproj` → **269/269 pass, 0 skipped**

> The `Dockerfile` runs `dotnet test` during the build, so a failing test fails the image build. That is a backstop, not a substitute for running it yourself — you want the failure before you have started a deployment.

## Secret configuration

- [ ] `.env` exists next to `docker-compose.yml` **on the server**
- [ ] `.env` is **not** in git: `git check-ignore .env` prints a match
- [ ] `ANALYTICS_IP_HASH_SALT` is set in `.env` to a freshly generated value:

      openssl rand -base64 32

- [ ] **The salt is NOT the value that was previously in `appsettings.json`.** That value is in git history on a hosted remote and must be treated as compromised. Rotating it is safe: nothing in the codebase reads `IpHash`, so no metric changes. See `PRODUCTION-READINESS-REPORT.md` §2 for the full impact analysis.
- [ ] `git grep -n "IpHashSalt" -- '*.json'` shows **only** the `_comment_IpHashSalt` pointer, never a value
- [ ] `firebase-config.json` is present on the server next to `docker-compose.yml` (bind-mounted read-only; without it the Firebase SDK is skipped and push notifications silently do nothing)

## Known-outstanding secret exposure — acknowledge before proceeding

`Cocorra.API/appsettings.json` is still tracked and still contains **nine** live secrets, and
`livekit/livekit.yaml` duplicates two of them. They were deliberately not moved in Wave 8:
each rotation has operational consequences that need coordinating, and the brief's instruction
was to document rather than silently change them.

| Severity | Keys |
|---|---|
| **CRITICAL** | `ConnectionStrings:DefaultConnection` · `JWTSetting:securityKey` · `Minio:SecretKey`+`AccessKey` · `LiveKit:ApiSecret`+`ApiKey` · `livekit.yaml` keys + TURN password |
| **HIGH** | `EmailSettings:SmtpPass` · `SeedAdmin:Password` |
| **MEDIUM** | `LiveKit:IceServers:1:credential` |
| **LOW** | `loadtest.js` hardcoded JWT — **verified expired** (2026-06-12) |

**`JWTSetting:securityKey` is the highest-impact of these: it permits forging a token for any
user, including Admin, with no login attempt and no log entry.**

- [ ] You have read `PRODUCTION-READINESS-REPORT.md` §2 and accepted this for **this** deployment
- [ ] **Repository visibility confirmed.** A public repository makes every key above publicly readable. **If public, rotate before deploying** — §2.4 of the report has the ordered runbook
- [ ] A rotation window is scheduled, or a decision to accept the risk is recorded

### Reduced in Wave 8 — no action needed, listed for completeness

- `cocorra.runasp.net-WebDeploy.publishSettings` (hosting deployment password) — **untracked**; `*.publishSettings` now ignored
- `Cocorra.API/publish/**` — 170 files of build output holding **six duplicate copies of every secret above** — **untracked**; `publish/` now ignored
- `Analytics:IpHashSalt` — **externalised** (rotate per Condition 1 above)

All three remain on disk; none is used by this deployment path.

## Database

- [ ] Connection string points at the intended database
- [ ] **A restore-tested backup exists, taken immediately before this deployment**
- [ ] `dotnet ef migrations list --project Cocorra.DAL --startup-project Cocorra.API` — note which are unapplied
- [ ] `dotnet ef migrations has-pending-model-changes --project Cocorra.DAL --startup-project Cocorra.API` → "No changes have been made to the model since the last migration"

> **If an `ef` command fails with `Analytics:IpHashSalt is not configured` followed by
> `Unable to resolve service for type 'DbContextOptions<AppDbContext>'`, do NOT put the salt
> back into `appsettings.json`.**
>
> That error means `Cocorra.DAL/Data/AppDbContextFactory.cs` is missing or was not found. Without
> a design-time factory, EF Tools build the application host to obtain a `DbContext`, which runs
> `Program.cs`, which correctly throws because the salt is absent by design. The factory exists
> precisely so design-time tooling never touches the salt guard — it needs only a connection
> string. `ProductionReadinessTests.DesignTimeFactory_CreatesAContextWithoutTheSalt` pins this.
>
> To target a specific database from the command line, set the connection string in the
> environment rather than editing a file:
>
>     $env:ConnectionStrings__DefaultConnection = '<connection string>'      # PowerShell
>     export ConnectionStrings__DefaultConnection='<connection string>'      # bash

## Feature flags — verify they are OFF

Stage A requires the pipeline live and no new events emitting.

- [ ] `.env` does **not** set `Analytics__EnableNewEventEmission`
- [ ] `.env` does **not** set `Analytics__EnableHighFrequencyEvents`
- [ ] Both flag lines in `docker-compose.yml` are still commented out
- [ ] `appsettings.json` still has both as `false`

---

# B. Apply migrations

**Do this before starting the new container**, against the backup taken above.

- [ ] Three migrations are expected to apply:

      20260901080448_AddDailyStateSnapshots
      20260901082008_AddAnalyticsPipelineAndReadModels
      20260901120157_AddParticipantLifecycleAndStatusCodes

- [ ] Generate and **read** the SQL before running it:

      dotnet ef migrations script --idempotent \
        --project Cocorra.DAL --startup-project Cocorra.API \
        --output migrate.sql

- [ ] **Review `AddAnalyticsPipelineAndReadModels` specifically.** It adds three columns to `UserEvents`, **backfills `EventId` on every existing row in batches**, and only then applies the `UX_UserEvents_EventId` unique constraint. On a large `UserEvents` table this is the longest-running statement in the deployment. Know the row count first:

      SELECT COUNT(*) FROM UserEvents;

- [ ] Apply during a low-traffic window
- [ ] Verify afterwards:

      -- EventId is added NOT NULL with an all-zeros default and then backfilled with
      -- NEWID(). So test for the ZERO GUID, not for NULL: "WHERE EventId IS NULL"
      -- returns 0 whether or not the backfill ran, and would give false confidence.
      SELECT COUNT(*) FROM UserEvents
        WHERE EventId = '00000000-0000-0000-0000-000000000000';        -- expect 0

      SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_UserEvents_EventId';  -- expect 1
      SELECT TOP 1 * FROM DailyStateSnapshots;                        -- table exists (may be empty)
      SELECT TOP 1 * FROM AggregationCheckpoints;                     -- table exists (may be empty)
      SELECT TOP 1 * FROM DeadLetterEvents;                           -- table exists (expect empty)

- [ ] **`migrate.sql` deleted afterwards** — it may embed schema detail you do not want lying around, and it is not gitignored by name

---

# C. Deploy

- [ ] `docker compose build` succeeds (tests run inside the build)
- [ ] `docker compose up -d`
- [ ] **If it aborts with `ANALYTICS_IP_HASH_SALT is not set`** — that is the guard working. Fix `.env`; do not work around it
- [ ] `docker compose ps` shows the container running
- [ ] `docker compose logs --tail=200 cocorra-api` contains no startup exception

## Startup failure modes and what they mean

| Log line | Cause | Fix |
|---|---|---|
| `Analytics:IpHashSalt is not configured` | Salt not reaching the container | Check `.env` and the `environment:` mapping in `docker-compose.yml` |
| `Analytics:EventChannelCapacity must be greater than zero` | Capacity set to 0 or a non-numeric value | Fix the override |
| `Invalid object name 'DailyStateSnapshots'` | §B skipped | Apply migrations |
| `firebase-config.json not found` | Bind mount missing | **Warning, not fatal.** Push notifications will silently do nothing |

---

# D. Immediately after deployment

**Do not rely on `docker ps` or the container health check.** Neither touches the database or the background services.

- [ ] Application responds: `curl -f http://localhost:5000/` returns the welcome page
- [ ] Swagger loads at `/swagger` — confirms controllers and DI resolved
- [ ] **Authenticate as an Admin and call the one endpoint that proves the analytics stack is alive:**

      GET /Api/V1/Analytics/System/Health

- [ ] It returns `200` with a JSON body — this single call exercises DI, the DbContext, the pipeline counters, the aggregation checkpoint and the snapshot table

## Interpreting the first health response

Immediately after a first deployment, this is **expected and correct**:

| Field | Expected on day one | Meaning |
|---|---|---|
| `pipelineHealthy` | **`false`** | Correct — see the next row |
| `warnings` | contains *"Aggregation has never run: no checkpoint exists."* | The service staggers its first run by 2 minutes and then runs hourly. **Re-check after 15 minutes.** |
| `eventsEnqueued` / `eventsPersisted` | small and rising | Ungated events (`room_joined`, `mic_activated`, `session_started`) still emit with the flags off |
| `eventsDroppedOnEnqueue` | **`0`** | Anything else is R-1 and blocks Stage B |
| `deadLetterBacklog` | **`0`** | — |
| `lastSnapshotDate` | `null` until `SnapshotHourUtc` (00:00 UTC) passes | **The RM-5 clock starts at the first successful run and cannot be backfilled** |
| `countersSinceUtc` | this deployment's start time | Counters are process-local and reset on restart |

- [ ] After 15 minutes: `aggregationLagHours` is a number, `aggregationConsecutiveFailures` is `0`
- [ ] After the first 00:00 UTC: `lastSnapshotDate` is populated and `snapshotGapDates` is empty
- [ ] Core product smoke test — **analytics must never be the reason these fail**:
  - [ ] Log in
  - [ ] Create a room, start it
  - [ ] Join from a second account over SignalR
  - [ ] Raise a hand, get promoted, unmute, mute
  - [ ] Send a chat message
  - [ ] Leave the room

## Run the backfill

Once aggregation has succeeded at least once:

- [ ] `POST /Api/V1/Analytics/System/Backfill?from=<180d ago>&to=<yesterday>` (Admin only)
- [ ] Review the returned `notes` and `datesProcessed`; it is capped at 400 dates per call and returns `resumeFromDate` if throttled out
- [ ] Re-run from `resumeFromDate` until `completed: true`

**RM-3 reconstructs to the platform's first day** — host supply history in one run. **RM-5 backfills nothing**, by design.

- [ ] `GET /Api/V1/Analytics/Platform/Health` now returns `readModelsPopulated: true` and real values
- [ ] `GET /Api/V1/Analytics/Decisions` — **record `weeksOfHistory` and `hasBaseline`.** If `hasBaseline` is already `true`, the Decision Center's gate is met and there is nothing to wait for

---

# E. Stage gates

Full rationale in `28-production-analytics-activation.md`. These are the measurable criteria only.

## Stage A gate — pipeline verification (flags OFF)

**Duration: 7 consecutive days.** Record `GET /Analytics/System/Health` daily, noting `countersSinceUtc` each time.

| # | Criterion | Pass |
|:--:|---|---|
| A1 | Seven consecutive daily readings recorded | ✅ |
| A2 | `eventsDroppedOnEnqueue` is `0`, **or** a steady non-zero with a written explanation | ✅ |
| A3 | `deadLetterBacklog` is `0` on every reading | ✅ |
| A4 | `flushBatchesFailed` is `0` | ✅ |
| A5 | `aggregationLagHours` never exceeded `3` | ✅ |
| A6 | `aggregationConsecutiveFailures` was `0` on every reading | ✅ |
| A7 | `snapshotGapDates` empty for the whole week | ✅ |
| A8 | `unaggregatedEventCount` bounded, not monotonically growing | ✅ |
| A9 | `SELECT COUNT(*) FROM UserEvents` recorded at start and end (this is **R-3**) | ✅ |
| A10 | Daily event volume recorded, and **peak-hour rate estimated** — Stage C needs it to size the channel | ✅ |
| A11 | No product incident attributable to analytics | ✅ |

**FAIL on A2** → do not proceed. Raise `Analytics__EventChannelCapacity`, redeploy, restart Stage A. Adding events to a channel already dropping degrades the events that currently work.

## Stage B gate — core event activation

**Change: exactly one line.** In `.env`:

    Analytics__EnableNewEventEmission=true

Then `docker compose up -d`. **Record the UTC timestamp — this is the AN-017 event clock start.**

Monitor **hourly for 4 hours**, then every 4 hours for 48 hours.

| # | Criterion | Pass |
|:--:|---|---|
| B1 | 7 days elapsed with the flag on | ✅ |
| B2 | `eventsDroppedOnEnqueue` **unchanged from the Stage A baseline** | ✅ |
| B3 | `deadLetterBacklog` still `0` | ✅ |
| B4 | `aggregationLagHours` still under 3 | ✅ |
| B5 | New event types present: `SELECT EventType, COUNT(*) FROM UserEvents WHERE OccurredAtUtc >= DATEADD(day,-7,GETUTCDATE()) GROUP BY EventType` shows `room_went_live`, `stage_promoted` | ✅ |
| B6 | `GET /Analytics/Participation/StageFunnel` shows step 3 (`stage_promoted`) `isMeasured: true` **while step 2 (`hand_raised`) is still `isMeasured: false`** — partial instrumentation reporting honestly | ✅ |
| B7 | No product incident attributable to analytics | ✅ |

**Rollback**: remove the line from `.env`, `docker compose up -d`. Emission stops on restart. Nothing else changes.

## Stage C gate — high-frequency event activation

**Two deploys, deliberately separate**, so a drop-rate spike can be attributed.

**Deploy 1 — capacity only:**

    Analytics__EventChannelCapacity=25000

- [ ] Deployed, restarted, health checked, drop counter still at baseline

**Deploy 2 — the flag:**

    Analytics__EnableHighFrequencyEvents=true

**Record the UTC timestamp — this is the AN-018 event clock start.**

Monitor **hourly for 24 hours**, and specifically **during the platform's peak hour** — take that from Stage A's peak-hours data, converted with `Meta.display.suggestedDisplayOffsetMinutes` (default +180). The raw UTC hour is 2–3 hours off local.

| # | Criterion | Pass |
|:--:|---|---|
| C1 | 7 days elapsed with both flags on | ✅ |
| C2 | `eventsDroppedOnEnqueue` **still at the Stage A baseline**, including across peak hours | ✅ |
| C3 | `deadLetterBacklog` still `0` | ✅ |
| C4 | `aggregationLagHours` still under 3 | ✅ |
| C5 | `GET /Analytics/Participation/StageFunnel` returns **`isFullyInstrumented: true`** | ✅ |
| C6 | Funnel monotonicity holds on production data — each step ≤ the previous | ✅ |
| C7 | `directPromotionsWithoutHandRaise` is **non-null** — both events live | ✅ |
| C8 | `mic_deactivated` present, and `segmentSeconds` values are plausible (not all zero, not absurd) | ✅ |
| C9 | No product incident attributable to analytics | ✅ |

**Rollback**: remove **only** `Analytics__EnableHighFrequencyEvents` from `.env`. **Leave `EnableNewEventEmission` on.** Independent revertibility is the entire reason the increments are separate.

---

# F. Record the baseline

**Fill this in and store it with the deployment record.** Without it, no future reading of a stage-funnel number can be dated.

```
DEPLOYMENT
  Commit SHA                     ____________________
  Deployed at (UTC)              ____________________
  Migrations applied             ____________________
  UserEvents row count before    ____________________

SALT
  Rotated?                       [ ] yes  [ ] no (reason: ______________)

CLOCK 3 — RM-5 SNAPSHOTS  (unrecoverable; starts at first deploy)
  First successful lastSnapshotDate  ____________________

CLOCK 2 — READ MODELS  (satisfied by backfill)
  Backfill completed at (UTC)         ____________________
  weeksOfHistory after backfill       ____________________
  hasBaseline after backfill          [ ] true  [ ] false

CLOCK 4a — AN-017 EVENTS  (Stage B)
  EnableNewEventEmission=true at (UTC)     ____________________

CLOCK 4b — AN-018 EVENTS  (Stage C)
  EventChannelCapacity raised to           ____________________
  EnableHighFrequencyEvents=true at (UTC)  ____________________
  ← this is dataAvailableFromUtc for M-400 step 2

FLAGS AT END OF DEPLOYMENT
  EnableNewEventEmission         [ ] true  [ ] false
  EnableHighFrequencyEvents      [ ] true  [ ] false

STAGE A BASELINE  (the numbers every later stage is judged against)
  Peak-hour event rate           ____________________
  eventsDroppedOnEnqueue         ____________________
  Daily event volume             ____________________
```

**Clock 1 (relational metrics) needs no entry** — it has always been running, and the backfill reconstructs it to day one.

---

# G. Rollback

Every analytics rollback is a configuration change. **No code change, no migration reversal, no data loss.**

| # | Condition | Action | Expected result | Verification |
|:--:|---|---|---|---|
| **R1** | `eventsDroppedOnEnqueue` rises after Stage B | Remove `Analytics__EnableNewEventEmission` from `.env`; `docker compose up -d` | AN-017 events stop. **Pre-existing events unaffected.** Product unaffected | Drop counter stops rising after `countersSinceUtc` resets; `room_joined` still appearing |
| **R2** | Drop counter rises after Stage C | Remove **only** `Analytics__EnableHighFrequencyEvents`. **Leave `EnableNewEventEmission` on** | AN-018 events stop; AN-017 continues | `hand_raised` count stops rising; `stage_promoted` still rising |
| **R3** | Aggregation produces wrong values | Truncate the affected read model, re-run backfill for the range | Rows rebuilt. **No data loss** — read models are fully recomputable from raw events | Compare A-1 against the equivalent live-query endpoint |
| **R4** | Purge contends with ingestion | Lower `Analytics__CleanupBatchSize` | Smaller delete batches | Flush latency recovers |
| **R5** | Dead-letter backlog growing | **No rollback needed.** Inspect `DeadLetterEvents` | Events are **parked, not lost** — that is the table's purpose | `deadLetterBacklog` stops rising once the cause is fixed |
| **R6** | Structured log volume excessive | Unset `Analytics__StructuredLogPath` | Reverts to stdout only | Log volume drops |
| **R7** | Application will not start | Redeploy the previous image tag | Previous version running | `/` responds; `GET /Analytics/System/Health` responds |
| **R8** | Schema rollback needed | `dotnet ef database update <previous-migration>` | **Last resort.** Reverses the `UserEvents` columns and drops the read models | Restore from the §A backup instead if any data was written |

## What does NOT roll back

- [ ] Acknowledged: **`user_status_changed`** is the only durable record of a status transition. There is no `UpdatedAt` and no history table. Any transition that happens while emission is off is **lost permanently**.
- [ ] Acknowledged: **hard deletes** are unrecoverable. AN-013 soft delete is blocked on a data-protection decision, and every deletion until it lands biases M-102, M-501 and M-103 upward, permanently.
- [ ] Acknowledged: **RM-5 snapshots** cannot be backfilled. Days with no snapshot are permanent holes.

## Verified by test, not just by document

`ProductionReadinessTests` asserts the rollback contract mechanically:

| Test | Asserts |
|---|---|
| `DisablingTheFlag_StopsNewEventEmission_ButTheProductStillWorks` | Flag on → event emits. Flag off → no event, **and the hub still notifies the client and saves the participant** |
| `UngatedEvents_KeepEmittingAfterRollback` | `mic_activated` still emits with flags off, so the north star survives a rollback |
| `AFullChannel_DropsTheEventAndCountsIt_WithoutThrowing` | An analytics outage cannot become a product outage |

---

# H. Post-deployment monitoring

There is **no APM, no metrics export and no alerting**. Monitoring is a person calling one endpoint.

| Cadence | Action |
|---|---|
| **Every 4h, first 48h** | `GET /Analytics/System/Health` — check `pipelineHealthy` and `warnings` |
| **Daily, Stages A–C** | Record the full field set from §E into the stage log |
| **Daily** | `snapshotGapDates` — a gap appearing today is permanent |
| **Weekly** | `SELECT COUNT(*) FROM UserEvents` — growth trend, input to AN-045 |
| **Weekly** | `SELECT COUNT(*) FROM DeadLetterEvents` — should stay `0` |
| **After any restart** | `countersSinceUtc` resets; previous counter values no longer comparable |

## What is NOT observable — do not plan around it

| Gap | Consequence |
|---|---|
| **No alerting** | Nobody is told when `pipelineHealthy` goes false. Someone must look |
| **Container health check does not test the database** | A container reports `healthy` with the DB down |
| **Purge outcome is log-only** | `EventCleanupService` logs rows deleted but exposes **no counter**. Retention health is not visible on the health endpoint |
| **No per-service liveness signal** | `EventFlushService` health is inferred from `eventsPersisted` rising; there is no direct "is it running" field |
| **No request latency or CPU metrics** | Analytics' performance impact is not measurable in-app |
| **Counters reset on restart** | A container restart erases the drop-rate history that Stage A exists to establish |
| **Unhandled analytics failures go to stdout** | 10 MB × 3-file rotation, then gone — unless `Analytics__StructuredLogPath` points at a mounted volume |

**RECOMMENDATION** — set `Analytics__StructuredLogPath` to a mounted path before Stage B. It is the difference between diagnosing a flush failure and knowing only that the count went up.

---

# I. Sign-off

- [ ] §A complete, including the acknowledgement of outstanding secret exposure
- [ ] §B migrations applied and verified
- [ ] §D health endpoint returning, product smoke test passed
- [ ] Backfill run to `completed: true`
- [ ] §F baseline record filled in and stored
- [ ] §G "what does not roll back" acknowledgements ticked
- [ ] Stage A start date recorded; **flags confirmed OFF**

```
Deployed by       ____________________
Date (UTC)        ____________________
Commit            ____________________
Stage A begins    ____________________
```
