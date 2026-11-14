# 30 — Production Deployment Checklist

> **Generated**: 2026-09-02 (Wave 8) | **Deploy flow updated**: 2026-11-14 (GHCR image + `deploy/deploy.sh`)
> **Audience**: the person running the deployment. Not a summary — a list to work through with a terminal open.
> **Deployment model**: a merge to `prod` runs `.github/workflows/deploy.yml`: tests → image pushed to
> GHCR tagged with the commit SHA → `deploy/deploy.sh` on the VPS (backup, pull, start, health check,
> automatic rollback). The server runs `deploy/docker-compose.prod.yml`, uploaded as
> `/opt/cocorra/api/docker-compose.yml`. `docker-compose.dev.yml` is for local use only.

---

> **Route shorthand.** Analytics routes are written below both in full
> (`GET /Api/V1/Analytics/System/Health`) and in the shorthand used across the other documents
> (`GET /Analytics/System/Health`). **The real prefix is always `Api/V1/`** — `Router.Root` is
> `"Api"` and `Router.Version` is `"V1"`. Prepend it if you are pasting into curl.

---

## Read these three things first

1. **Migrations ARE applied automatically, at startup** (`Database.MigrateAsync()` in `Program.cs`), and **a rollback does not undo them.** If the new release fails its health check, `deploy.sh` restarts the previous image against the already-migrated schema. Every migration must therefore be backward-compatible with the previous release — see [`docs/MIGRATIONS.md`](../MIGRATIONS.md) and §B.

2. **Required secrets are enforced twice.** `ANALYTICS_IP_HASH_SALT`, `OTP_HMAC_KEY`, `JWT_SECURITY_KEY`, `SEED_ADMIN_PASSWORD` and `DB_CONNECTION_STRING` use `${VAR:?}` in the compose file, so `docker compose up` aborts without them, and `Program.cs` throws at startup on a missing or placeholder value. This is intentional — never work around it.

3. **The container health check is real.** It probes `GET /health`, which includes a database connectivity check; `deploy.sh` waits for it and rolls back if it fails. It does **not** cover the analytics background services — §D still applies.

---

# A. Before deployment

## Source and build

- [ ] The commit to deploy is on `prod` (or is the head of the PR into `prod`); **write the SHA down**, §F needs it
- [ ] `.github/workflows/ci.yml` is green for it: restore, Release build and the full test suite. The deploy workflow re-runs the same tests and will not build an image if they fail
- [ ] Any migration in the release follows [`docs/MIGRATIONS.md`](../MIGRATIONS.md) (see §B)

## Server prerequisites (once per server, re-check before a first deploy)

- [ ] `/opt/cocorra/api/.env` exists, filled in from `.env.example` — every `CHANGE_ME` replaced. `API_IMAGE` / `IMAGE_TAG` are written by `deploy.sh`; do not add them by hand
- [ ] `ANALYTICS_IP_HASH_SALT` is a freshly generated value (`openssl rand -base64 32`), **not** the value once committed to `appsettings.json` — that is in git history and compromised. Rotating it is safe: nothing reads `IpHash`. See `PRODUCTION-READINESS-REPORT.md` §2
- [ ] `/opt/cocorra/api/firebase-config.json` exists with mode **644**: the container runs as UID 1654 and reads it through the world-read bit. `deploy.sh` refuses to deploy otherwise
- [ ] `/opt/cocorra/infra/backup.sh` is the **current** `deploy/backup.sh` from the repository, mode 755. `deploy.sh` refuses to deploy if it is missing, not executable, or does not print a final `BACKUP_DB=<path>` line
- [ ] The `cocorra-network` Docker network exists, and the `cocorra_db` (SQL Server) and MinIO containers are on it
- [ ] GitHub: the `production` environment is restricted to the `prod` branch, and holds `SSH_PRIVATE_KEY`, `SSH_KNOWN_HOSTS`, `SSH_USER`, `SSH_HOST`

## Secret rotation — acknowledge before proceeding

Tracked configuration no longer holds secrets: the connection string, JWT key, seed admin
password, LiveKit key/secret and TURN credentials were removed from `appsettings.json` and
`livekit/livekit.yaml` (now `livekit.example.yaml`, placeholders only), and production reads
them from `.env`. **The old values remain in git history**, so they are compromised until rotated.

**`JWTSetting:securityKey` is the highest-impact: the old value permits forging a token for any
user, including Admin, with no login attempt and no log entry.**

- [ ] Every value in `.env` is a **new** value, not one that was ever committed: DB password, JWT key (signs every user out), seed admin password, LiveKit key/secret, MinIO keys, Resend key
- [ ] Or: a decision to accept the risk for this deployment is recorded, with a rotation date

## Database

- [ ] `DB_CONNECTION_STRING` points at the intended database
- [ ] **The backup is automatic:** `deploy.sh` runs `backup.sh` before every deploy and prints the file. Make sure a backup from this script has been **restore-tested** at least once (the restore steps `deploy.sh` prints on a failed deploy are the procedure)
- [ ] `dotnet ef migrations list --project Cocorra.DAL --startup-project Cocorra.API` — note which are unapplied (locally this uses the connection string in `appsettings.Development.json`; set `ConnectionStrings__DefaultConnection` to target another database)
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

### Running the API locally

`dotnet run` and `dotnet watch run` need the salt too — and the OTP key, JWT key and seed admin
password, which have the same startup guards — and `.env` is not how they get them: it is read
by docker-compose only. Use user-secrets, which stores the values **outside the repository**:

    dotnet user-secrets set "Analytics:IpHashSalt"   "$(openssl rand -base64 32)" --project Cocorra.API
    dotnet user-secrets set "Otp:HmacKey"            "$(openssl rand -base64 32)" --project Cocorra.API
    dotnet user-secrets set "JWTSetting:securityKey" "$(openssl rand -base64 48)" --project Cocorra.API
    dotnet user-secrets set "SeedAdmin:Password"     "<a strong password>"        --project Cocorra.API

It loads automatically because `launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development`.

**User secrets load only in Development.** Running locally with `--no-launch-profile`, or with
`ASPNETCORE_ENVIRONMENT=Production`, will still fail the guard — correctly, because a production
environment must take the salt from its own environment rather than from a developer's machine.

**Do not put the salt in `launchSettings.json`.** It is tracked, it has an
`environmentVariables` block, and it is the most tempting place to stash the value once
`dotnet watch run` starts failing. `ProductionReadinessTests.LaunchSettings_DoNotContainASalt`
fails the build if anyone does.

## Feature flags — verify they are OFF

Stage A requires the pipeline live and no new events emitting.

- [ ] `.env` does **not** set `Analytics__EnableNewEventEmission`
- [ ] `.env` does **not** set `Analytics__EnableHighFrequencyEvents`
- [ ] `deploy/docker-compose.prod.yml` sets neither flag (`ProductionReadinessTests` asserts this for both compose files)
- [ ] `appsettings.json` still has both as `false`

---

# B. Migrations

Pending migrations are applied **by the new container at startup**, before it reports healthy.
There is no separate migration step in the normal flow — but you still review them first.

- [ ] Every migration in this release is **backward-compatible with the previous release**
      (expand now, contract later — [`docs/MIGRATIONS.md`](../MIGRATIONS.md)). A failed deploy
      rolls back the image, **not** the schema
- [ ] **Long migrations must not run at startup.** The deploy gives the container about two
      minutes (60s health-check start period, then three failed 15s probes, inside `deploy.sh`'s
      150s wait) and Kestrel does not answer `/health` until migrations finish. A migration or
      backfill that may take longer — e.g. a batched `UPDATE` over `UserEvents` — must be applied
      by hand **before** merging to `prod`, using the idempotent script below; the container then
      finds nothing pending
- [ ] Generate and **read** the SQL:

      dotnet ef migrations script --idempotent \
        --project Cocorra.DAL --startup-project Cocorra.API \
        --output migrate.sql

- [ ] Know the row counts of any table a migration rewrites. **Example from Wave 8 —
      `AddAnalyticsPipelineAndReadModels`** — was exactly the kind of migration to apply by hand
      on a large database: it adds three columns to `UserEvents`, **backfills `EventId` on every existing row in batches**, and only then applies the `UX_UserEvents_EventId` unique constraint. On a large `UserEvents` table this is the longest-running statement in the deployment. Know the row count first:

      SELECT COUNT(*) FROM UserEvents;

- [ ] If applying by hand: take a backup first (`/opt/cocorra/infra/backup.sh`), apply during a low-traffic window, then merge to `prod`
- [ ] Verify afterwards (Wave 8 example):

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

**Do not run `docker compose up` by hand on the server.** A deploy is a merge to `prod`.

- [ ] Merge the PR into `prod` (or re-run "Deploy API" with *Run workflow* on `prod`; any other
      branch builds an image but the deploy job is skipped)
- [ ] Watch the run in GitHub Actions. The jobs are:
  1. **test** — restore, Release build, full test suite. A failure stops everything; nothing is built
  2. **build** — builds the image and pushes `ghcr.io/<owner>/<repo>:<commit sha>`
  3. **deploy** (environment `production`) — uploads `deploy/docker-compose.prod.yml` to
     `/opt/cocorra/api/docker-compose.yml` and runs `deploy/deploy.sh` on the server over SSH
- [ ] `deploy.sh` does, in order, and the run log shows each step:
  1. Pre-flight: `.env`, `firebase-config.json` (world-readable) and `backup.sh` present — otherwise it stops with the fix, **before changing anything**
  2. Database backup via `backup.sh`; the log line `database backup: /opt/cocorra/backups/db-<ts>.bak.gz` names it — **note it**
  3. Pull the new image (a failed pull changes nothing)
  4. Replace the container (**the API is down from here until the new container is healthy**, typically under a minute; longer if migrations run)
  5. Wait up to 150s for `/health`; stop early on `unhealthy` or a crash loop
  6. Healthy → record the tag in `.env`, prune old images of this repository (newest 3 kept). **Deploy OK**
  7. Not healthy → roll back to the previous tag and wait for it to be healthy, then print the database restore steps (see §G). The run fails either way
- [ ] The run ends with `==> Deploy OK: <sha>`

## Failure modes and what they mean

| Where | Message | Cause | Fix |
|---|---|---|---|
| deploy.sh pre-flight | `firebase-config.json is not world-readable` | Mode 600/640 | `chmod 644 /opt/cocorra/api/firebase-config.json` |
| deploy.sh pre-flight | `/opt/cocorra/infra/backup.sh is missing` / `not executable` | Script not installed | Copy `deploy/backup.sh` there, `chmod 755` |
| deploy.sh backup | `did not report an existing backup file` | Outdated `backup.sh` (no `BACKUP_DB=` line) | Install the current `deploy/backup.sh` |
| `docker compose` | `required variable ... is missing a value` | A required `.env` value is unset | Fix `.env`; do not work around it |
| container log | `Analytics:IpHashSalt is not configured` (or `Otp:HmacKey`, `JWTSetting:securityKey`, `SeedAdmin:Password`) | Value missing or still a placeholder | Fix `.env` |
| container log | `Analytics:EventChannelCapacity must be greater than zero` | Capacity set to 0 or a non-numeric value | Fix the override |
| container log | An exception during `Applying database migrations...` | A migration failed | The previous version is restored automatically; fix the migration (§B) |
| container log | `UnauthorizedAccessException` reading `firebase-config.json` | File not readable by UID 1654 | `chmod 644` (pre-flight normally catches this) |
| container log | `firebase-config.json not found` | Bind mount missing | **Warning, not fatal.** Push notifications silently do nothing |

---

# D. Immediately after deployment

**The container health check proves the API is up and can reach the database — nothing more.** It does not touch the analytics background services.

- [ ] On the server: `curl -fsS http://localhost:5000/health` returns `Healthy`
- [ ] Through the proxy: `curl -fsS https://api.cocorraapp.com/health` returns `Healthy` (TLS and routing work)
- [ ] (Swagger is not served in Production — that is intended)
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
| `lastSnapshotDate` | `null` until the first capture, about 2 minutes after startup; then daily at 00:15 Cairo time | **The RM-5 clock starts at the first successful run and cannot be backfilled** |
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

Every analytics rollback (R1–R6) is a configuration change. **No code change, no migration reversal, no data loss.** Analytics flags live in `/opt/cocorra/api/.env`; after editing it, restart with `cd /opt/cocorra/api && docker compose up -d` (`.env` already names the running image).

A failed **deploy** rolls itself back (R7). The database is never rolled back automatically (R8).

| # | Condition | Action | Expected result | Verification |
|:--:|---|---|---|---|
| **R1** | `eventsDroppedOnEnqueue` rises after Stage B | Remove `Analytics__EnableNewEventEmission` from `.env`; `docker compose up -d` | AN-017 events stop. **Pre-existing events unaffected.** Product unaffected | Drop counter stops rising after `countersSinceUtc` resets; `room_joined` still appearing |
| **R2** | Drop counter rises after Stage C | Remove **only** `Analytics__EnableHighFrequencyEvents`. **Leave `EnableNewEventEmission` on** | AN-018 events stop; AN-017 continues | `hand_raised` count stops rising; `stage_promoted` still rising |
| **R3** | Aggregation produces wrong values | Truncate the affected read model, re-run backfill for the range | Rows rebuilt. **No data loss** — read models are fully recomputable from raw events | Compare A-1 against the equivalent live-query endpoint |
| **R4** | Purge contends with ingestion | Lower `Analytics__CleanupBatchSize` | Smaller delete batches | Flush latency recovers |
| **R5** | Dead-letter backlog growing | **No rollback needed.** Inspect `DeadLetterEvents` | Events are **parked, not lost** — that is the table's purpose | `deadLetterBacklog` stops rising once the cause is fixed |
| **R6** | Structured log volume excessive | Unset `Analytics__StructuredLogPath` | Reverts to stdout only | Log volume drops |
| **R7** | New release will not start / fails `/health` | **Automatic:** `deploy.sh` restarts the previous tag. To roll back a release that passed its health check but misbehaves, revert the commit on `prod` (a normal deploy), or on the server: `cd /opt/cocorra/api && API_IMAGE=<image> IMAGE_TAG=<previous sha> docker compose up -d` (the newest 3 tags stay on the host; older ones are pulled from GHCR) | Previous version running | `/health` returns `Healthy`; `GET /Analytics/System/Health` responds |
| **R8** | Schema change broke the previous release | Restore the pre-deploy backup with the exact steps `deploy.sh` printed in the failed run (stop `cocorra-api`, `gunzip`, `docker cp` into `cocorra_db`, `RESTORE DATABASE ... WITH REPLACE`, start the previous version) | **Last resort: discards every write since the backup.** Avoided entirely by following `docs/MIGRATIONS.md` | Previous version healthy; spot-check recent data |

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
| **Container health check covers the database only** | `/health` fails when the DB is unreachable, but not when an analytics background service is stuck |
| **Purge outcome is log-only** | `EventCleanupService` logs rows deleted but exposes **no counter**. Retention health is not visible on the health endpoint |
| **No per-service liveness signal** | `EventFlushService` health is inferred from `eventsPersisted` rising; there is no direct "is it running" field |
| **No request latency or CPU metrics** | Analytics' performance impact is not measurable in-app |
| **Counters reset on restart** | A container restart erases the drop-rate history that Stage A exists to establish |
| **Unhandled analytics failures go to stdout** | 10 MB × 3-file rotation, then gone — unless `Analytics__StructuredLogPath` points at a mounted volume |

**RECOMMENDATION** — set `Analytics__StructuredLogPath` to a mounted path before Stage B. It is the difference between diagnosing a flush failure and knowing only that the count went up.

---

# I. Sign-off

- [ ] §A complete, including server prerequisites and the secret-rotation acknowledgement
- [ ] §B migrations reviewed: backward-compatible, and any long one applied by hand before the merge
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
