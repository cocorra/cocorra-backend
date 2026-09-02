# Cocorra — Production Readiness Report

> **Generated**: 2026-09-02 (Wave 8 — Production Readiness & Security Closure)
> **Scope assessed**: deployability of the Cocorra API with the analytics platform, and the security posture of its configuration.
> **Companion**: `30-production-deployment-checklist.md` — the executable version of this report.
> **No secret value appears anywhere in this document.** Findings are named and sized only.

---

# 1. Executive Decision

## ⚠ READY WITH CONDITIONS

The analytics platform is deployable. The build is clean, the tests pass, the activation sequence is staged and gated, every rollback is a configuration change, and the pipeline is observable through a single endpoint.

**It is not unconditionally ready, for one reason: `Cocorra.API/appsettings.json` is still tracked and still contains nine live production secrets, all of which are in git history on a hosted remote.**

That exposure is **pre-existing and unchanged by this wave**. It was deliberately not remediated here, because rotating a JWT signing key invalidates every active session and rotating a database credential needs a maintenance window — doing either silently would have been worse than reporting it. The one secret in this wave's stated scope, `Analytics:IpHashSalt`, is fixed.

## The five conditions

| # | Condition | Why it is a condition and not a blocker |
|:--:|---|---|
| **1** | **Generate a NEW `ANALYTICS_IP_HASH_SALT`. Do not reuse the historical value.** | The old value is in git history. Moving a secret does not un-publish it. Rotation is functionally free — see §2. |
| **2** | **Apply the three unapplied migrations before starting the container.** | There is no `Database.Migrate()` in the solution. Without this the app starts and then fails on every event insert. |
| **3** | **Confirm repository visibility.** If public, treat all nine remaining secrets as compromised and rotate before deploying. | This single fact decides whether §2 is a housekeeping item or an incident. It cannot be determined from inside the repository. |
| **4** | **Deploy with both event flags OFF.** | Stage A requires the pipeline live and no new events emitting. Enforced by defaults and asserted by test, but confirm it. |
| **5** | **Fill in the §F baseline record in the checklist.** | The RM-5 snapshot clock starts at first deploy and cannot be reconstructed. An unrecorded start date makes every later reading undateable. |

**None of the five requires a code change.** All five are deployment actions.

---

# 2. Security Status

## 2.1 `Analytics:IpHashSalt` — impact analysis before action

The brief required an impact analysis before touching the salt. Here it is, and its conclusion changed the recommendation.

### What the salt protects

`EventTracker.HashIpAddress` computes `SHA256(salt + ipAddress)` and stores the 64-character hex result in `UserEvent.IpHash`.

**The salt is the only thing making that irreversible.** The IPv4 space is 2³² addresses. Anyone holding the salt can hash every possible address and build a complete reverse lookup in minutes on a laptop. **A committed salt turns a hashed column into a plaintext IP log for everyone with repository access.**

### What rotation would break — the finding that matters

| Consumer | Reads `IpHash`? |
|---|---|
| Any repository query | **No** |
| Any aggregation service | **No** |
| Any metric in the registry (all 27) | **No** |
| Deduplication | **No** — idempotency uses `EventId`, not `IpHash` |
| Abuse or security detection | **No** — no such feature exists |
| Any index | **No** — `UserEvent.IpHash` is unindexed |

**`IpHash` is write-only.** Verified by `git grep "IpHash"`: the only non-test references are the write in `EventTracker`, the model property, the config key, and the startup guard.

### Conclusion

| Question | Answer |
|---|---|
| Does rotation affect existing analytics records? | **No.** They keep their old hashes; nothing reads them. |
| Does it affect IP pseudonymisation continuity? | **Yes, in principle** — hashes either side of the rotation are uncorrelatable. |
| Does that matter today? | **No.** Nothing correlates them. It forecloses a *future* analysis that spans the boundary, and no such analysis is planned or specified. |
| Does it affect user/session correlation? | **No.** That uses `SessionId`. |
| Does it affect deduplication? | **No.** That uses `EventId` + a unique constraint. |
| Does it affect abuse detection? | **No.** None exists. |
| Does it affect historical analytics? | **No.** |
| Any other persisted hashes? | **No.** `IpHash` is the only salted hash in the schema. |

> **Therefore: rotate.** The cost is a theoretical future join nobody has asked for. The benefit is that the pseudonymisation actually pseudonymises. This is close to the cheapest security remediation available in the repository, and the analysis is the reason it can be recommended confidently rather than cautiously.

### What was changed

| Change | Detail |
|---|---|
| Removed from tracked config | `appsettings.json` now carries only a `_comment_IpHashSalt` pointer, no value |
| Delivery mechanism | `docker-compose.yml` maps `Analytics__IpHashSalt` from `${ANALYTICS_IP_HASH_SALT}`, read from a **gitignored** `.env` |
| Deploy-time guard | The `${VAR:?message}` form aborts `docker compose up` with instructions if the variable is unset |
| Runtime guard | `Program.cs` still throws at startup. **Two independent guards, no fallback** |
| Developer path | `.env.example` is tracked, holds a `CHANGE_ME` placeholder, and documents `openssl rand -base64 32` |
| Verified by test | 8 assertions in `ProductionReadinessTests` — see §3 |

**Mechanism chosen to fit the existing deployment, not to add infrastructure.** `docker-compose.yml` already documented the `ConnectionStrings__DefaultConnection` env-var override pattern; this follows it. No secrets manager, no vault, no new dependency. `dotnet user-secrets` was considered and rejected: it would be a second mechanism for the same job, and the project has no `UserSecretsId`.

### Status

| | |
|---|---|
| **Going forward** | ✅ **SECURED** |
| **The historical value** | ❌ **COMPROMISED — present in git history on a hosted remote** |

**Rewriting git history was not attempted.** On a shared remote it breaks every clone, and it does not help: the value must be assumed captured. Rotation is the remediation; history rewriting is theatre.

---

## 2.2 Repository secret scan — **FINDINGS**

Scanned every tracked file by key-name heuristic **and** by value shape, so credentials with non-obvious key names were caught. `EmailSettings:SmtpPass` was missed by the first pass and found by the second.

### Still tracked — NOT changed in this wave

| # | Location | Key | Severity | Impact if exposed |
|:--:|---|---|:--:|---|
| 1 | `appsettings.json` | `ConnectionStrings:DefaultConnection` | **CRITICAL** | Direct read/write access to the production database, including every user record and voice-verification state |
| 2 | `appsettings.json` | `JWTSetting:securityKey` | **CRITICAL** | **Forge a token for any user, including Admin.** No credential needed, no login attempt logged. The single highest-impact item in this list |
| 3 | `appsettings.json` | `Minio:SecretKey` (+ `AccessKey`) | **CRITICAL** | Object storage holding **profile images and voice recordings**. Cocorra runs `MentalHealth` and `Relationships` rooms; these are the most sensitive assets the platform holds |
| 4 | `appsettings.json` | `LiveKit:ApiSecret` (+ `ApiKey`) | **CRITICAL** | Mint LiveKit tokens — join or record any live room, including private ones |
| 5 | `livekit/livekit.yaml` | `keys:` entry + `turn.users[].password` | **CRITICAL** | **Duplicates #4** and adds the TURN password. Same credentials, second location |
| 6 | `appsettings.json` | `EmailSettings:SmtpPass` | **HIGH** | Send mail as Cocorra — password-reset phishing from the genuine domain |
| 7 | `appsettings.json` | `SeedAdmin:Password` | **HIGH** | Admin login, **if the seeded account still exists with this password** |
| 8 | `appsettings.json` | `LiveKit:IceServers:1:credential` | **MEDIUM** | TURN relay abuse / bandwidth theft |
| 9 | `loadtest.js` | Hardcoded JWT | **LOW** | **Verified expired** (`exp` 2026-06-12). Still leaks a real user's email, id and role claims, and demonstrates that tokens were minted with the key in #2 |

### Untracked in this wave

| # | Location | Severity | Action taken |
|:--:|---|:--:|---|
| 10 | `cocorra.runasp.net-WebDeploy.publishSettings` — `userPWD`, 12 chars | **CRITICAL** | **Untracked.** A WebDeploy password grants arbitrary code deployment to the hosting environment. A credential file has no legitimate reason to be versioned, and this one is not used by the docker-compose deployment path at all. File left on disk; `*.publishSettings` added to `.gitignore` |
| 11 | `Cocorra.API/publish/**` — 170 files, nested six deep | **HIGH** | **Untracked.** Committed build output containing **six duplicate copies of `appsettings.json`**, each with every secret above. Multiplied the exposure surface sixfold and was not covered by any ignore rule. `publish/` added to `.gitignore` |

### False positives — checked and dismissed

| Location | Why it is not a secret |
|---|---|
| `docker-compose.yml` "Password" | A commented `YOUR_PASSWORD` placeholder in the example connection string |
| `index.html`, `login.html` | Manual API test harnesses. Every hit is an `<input>` element id or a JavaScript local (`token`, `loginPassword`) |

### The `.gitignore` finding

`.gitignore` already contained:

```
**/appsettings.json
**/appsettings.Production.json
**/appsettings.Development.json
```

**These rules have never protected the files they name.** Both `appsettings.json` and `appsettings.Development.json` were committed *before* the rules were added, and **`.gitignore` has no effect on an already-tracked file.** Every edit to them has been versioned the whole time.

This is worse than having no rule, because it reads as protection. The rules are retained — they do stop *new* copies being added — with a comment stating plainly what they do and do not do.

`.gitignore` also contained a **UTF-16 fragment appended without a newline**, producing the literal pattern `Thumbs.dbi\0m\0p\0l\0e...`. The `Thumbs.db` rule was silently broken, and the file contained NUL bytes. Rewritten cleanly; a test now asserts the file contains no NUL byte.

---

## 2.3 Outstanding risks

| Risk | Severity | Owner | Mitigation |
|---|:--:|---|---|
| **Nine live secrets in tracked config and git history** | **CRITICAL if the repository is public**, HIGH if private | Repository owner | Rotate on the schedule in §2.4. **Confirm visibility first** |
| Historical IP-hash salt is compromised | HIGH → resolved by Condition 1 | Deployer | Rotate on first deploy |
| No secrets manager | MEDIUM | Owner | `.env` + gitignore is proportionate for a single-VPS docker-compose deployment. Revisit if the deployment fans out |
| `.env` lives unencrypted on the VPS | LOW | Owner | Inherent to the mechanism. Restrict file mode to `600` and the containing directory to the deploy user |

## 2.4 Rotation runbook — not executed, deliberately

Ordered by blast radius so the least disruptive comes first. **Each needs its own decision; none was performed in this wave.**

| Order | Secret | Consequence of rotating | Notes |
|:--:|---|---|---|
| 1 | `ANALYTICS_IP_HASH_SALT` | **None** | Condition 1. Do it now |
| 2 | WebDeploy `userPWD` | None — the docker-compose path does not use it | Rotate in the hosting control panel |
| 3 | `EmailSettings:SmtpPass` | Outbound mail fails until updated | Single config value |
| 4 | `Minio` key pair | Uploads and reads fail until updated | Coordinate; user-facing |
| 5 | `LiveKit` key pair + TURN password | **Live rooms break.** Must be changed in `appsettings.json` **and** `livekit/livekit.yaml` together | Maintenance window. Both locations or neither |
| 6 | `SeedAdmin:Password` | None if the account is unused; otherwise re-seed | Verify whether the account still exists |
| 7 | `ConnectionStrings:DefaultConnection` | Total outage until updated | Maintenance window |
| 8 | `JWTSetting:securityKey` | **Every active session is invalidated. Every user is logged out.** | Highest impact, highest value. Schedule deliberately, communicate first |

---

# 3. Build & Test Status

| | Result |
|---|---|
| **Build** | ✅ **PASS** — `dotnet build Cocorra.sln --no-incremental`, **0 errors** |
| **Warnings** | **13** |
| **Tests** | ✅ **268 / 268 PASS** |
| **Failed** | **0** |
| **Skipped** | **0** |
| **Migrations** | No pending model changes — `has-pending-model-changes` reports none |

## Warnings — pre-existing vs introduced

| Classification | Count | Detail |
|---|:--:|---|
| **Pre-existing** | **13** | 4 × `CS8981` (lowercase migration class names `deploy`, `analytices`); 6 × `CS8602`/`CS8604` nullable dereference in `EmailService`, `ChatService`, `MessageRepository`, `SupportService`, `AnalyticsRepository`, `Program.cs`; 1 × `CS0618` obsolete `GoogleCredential.FromFile`; 2 × related designer warnings |
| **Introduced by Wave 8** | **0** | |
| **Resolved by Wave 8** | **0** | Out of scope — this is a readiness wave, and touching them would obscure the security diff |

**No warning is in code this wave added.** They remain listed as known limitations, not as clean.

## Tests added in Wave 8: +14 (254 → 268)

`ProductionReadinessTests` — security posture and the rollback contract:

| Test | Asserts |
|---|---|
| `TrackedAppSettings_DoNotContainAnIpHashSaltValue` | **Regression guard.** The key carries no value in either tracked appsettings file. The explanatory comment is permitted; a value is not |
| `EnvExampleTemplate_ExistsAndHoldsOnlyAPlaceholder` | The tracked template never acquires a real value |
| `GitIgnore_ExcludesEnvFilesPublishOutputAndPublishProfiles` | `.env` ignored, `!.env.example` negation intact, `publish/` and `*.publishSettings` covered, **and no NUL bytes** |
| `DockerCompose_SourcesTheSaltFromTheEnvironment_AndDoesNotEnableEventFlags` | The salt comes from the environment, **and neither flag is enabled from an uncommented line** |
| `SaltBindsFromTheDoubleUnderscoreEnvironmentVariableForm` | `Analytics__IpHashSalt` → `Analytics:IpHashSalt`. Proves the production delivery path reaches the key the code reads |
| `EnvironmentConfiguration_OverridesFileConfiguration` | Precedence order — the whole externalisation rests on it |
| `MissingSalt_IsRejectedByTheStartupGuardCondition` (×3) | `null`, `""`, `"   "` all rejected. Fail-fast is intentional |
| `WithoutASalt_TheTrackerStoresNoIpHash_RatherThanAnUnsaltedOne` | **Defence in depth.** No salt → no hash, never an unsalted one |
| `WithASalt_TheTrackerStoresAHashThatFitsTheColumn` | 64 chars, exactly `MaxLength(64)`, and the salt does not leak into the output |
| `DisablingTheFlag_StopsNewEventEmission_ButTheProductStillWorks` | **The rollback contract.** Flag off → no event, **and the hub still notifies the client and saves the participant** |
| `UngatedEvents_KeepEmittingAfterRollback` | `mic_activated` still emits with flags off — the north star survives a rollback |
| `AFullChannel_DropsTheEventAndCountsIt_WithoutThrowing` | An analytics outage cannot become a product outage, and the drop is counted |

---

# 4. Analytics Activation Status

| Stage | Status | Gate |
|---|---|---|
| **Stage A** — pipeline verification | ✅ **READY** | Nothing outstanding. Deploy with flags off, record `GET /Api/V1/Analytics/System/Health` daily for 7 days. 11 measurable criteria in checklist §E |
| **Stage B** — core event activation | ✅ **READY**, gated on Stage A | One line in `.env`. 7 criteria |
| **Stage C** — high-frequency activation | ✅ **READY**, gated on Stage B | Two separate deploys (capacity, then flag). 9 criteria |
| **Baseline lifecycle** | ✅ **CLEAR** — was ambiguous, corrected in this wave | See below |

## The baseline ambiguity, and its correction

Earlier documentation stated *"the baseline clock starts when `EnableHighFrequencyEvents` goes true"* and *"the Decision Center needs 4–6 weeks"*.

**Both were wrong.** `DecisionCenterService` reads `DailyPlatformMetrics` and requires `RequiredBaselineWeeks = 4` **complete weeks present in that table** — and `AnalyticsBackfillService` populates it retroactively, using the same rollup code path as live aggregation. RM-1's participation fields derive from `RoomParticipants`, not from `room_joined` events, so they reconstruct to the platform's first day.

**The error came from the planning documents, which assumed read models could only accumulate forward.** It was wrong in the expensive direction: it implied a month-long wait that the backfill already satisfies.

### There are four clocks, not one

| Clock | Starts | Backfillable | Gates |
|---|---|:--:|---|
| **1 — Relational** | Cocorra's first room | ✅ Fully | M-200/201/202/205, M-300/301, M-501, M-503/504 |
| **2 — Read models** | First aggregation, **extended retroactively by backfill** | ✅ RM-3 to day one; RM-1 to day one except two event-derived fields | M-100, M-101, **the Decision Center** |
| **3 — Snapshots (RM-5)** | **First deploy, Stage A** | ❌ **Structurally impossible** | M-303 trend, FCM coverage trend |
| **4 — New events** | **Stage B** (AN-017) / **Stage C** (AN-018) | ❌ Never captured | M-400 steps 2–3 |

**Clock 3 is the only one where delay destroys evidence**, and it starts at first deploy — which is the argument for deploying sooner, and the reason it was worth correcting the earlier text that pointed at Stage C.

**One unambiguous answer**, now stated in `28-` §5, `30-` §F, and re-checked at runtime: read `hasBaseline` from `GET /Api/V1/Analytics/Decisions`. Do not count calendar days.

### Documentation consistency

| Document | Action |
|---|---|
| `28-production-analytics-activation.md` | **Corrected in place**, with the correction recorded |
| `FINAL-ANALYTICS-IMPLEMENTATION-REPORT.md` | 11 statements corrected |
| `29-final-metric-trust-register.md` | M-100 availability note corrected |
| `25-final-analytics-status-audit.md` | Addendum 2 appended — body left intact, it is a point-in-time snapshot |
| `COCORRA-DASHBOARD-DESIGN-BRIEF.md`, `CLAUDE-DESIGN-PROMPT.md` | Corrected — a designer must not be told a wrong fact |
| `20-dashboard-implementation-blueprint.md` | Gating requirement marked **SUPERSEDED IN PART** — it is what a dashboard engineer builds from |
| `ANALYTICS-IMPLEMENTATION-MASTER-PLAN.md` | B-4 blocker marked partly superseded |
| `09-`, `10-`, `21-`, `23-`, `24-` | **Deliberately not rewritten.** Planning artefacts recording what was believed at the time. They retain the 4–6 week assumption; the operational documents above are authoritative. Recorded here so the residual is a known state rather than a contradiction |

---

# 5. Production Configuration

**Names only. No values.**

## Required — deployment fails without these

| Name | Required | Environment | Source | Default behaviour if missing |
|---|:--:|---|---|---|
| `ANALYTICS_IP_HASH_SALT` | **Yes** | Production | `.env` on the server | `docker compose up` **aborts** with instructions; `Program.cs` would also throw. **No fallback** |
| `ConnectionStrings__DefaultConnection` | Yes | All | `appsettings.json` *(⚠ tracked)* or env override | Startup fails on first DB access |
| `JWTSetting__securityKey` | Yes | All | `appsettings.json` *(⚠ tracked)* | Authentication fails |
| `ASPNETCORE_ENVIRONMENT` | Yes | All | `docker-compose.yml` → `Production` | Defaults to `Production` in the container image |
| `ASPNETCORE_URLS` | Yes | All | `docker-compose.yml` → `http://+:8080` | Framework default |

## Analytics — all optional, all defaulted

| Name | Required | Source | Default |
|---|:--:|---|---|
| `Analytics__EnableNewEventEmission` | No | env (Stage B) | **`false`** |
| `Analytics__EnableHighFrequencyEvents` | No | env (Stage C) | **`false`** — and inert unless the above is also true |
| `Analytics__EventChannelCapacity` | No | env (raise before Stage C) | `10000`; **must be > 0 or startup throws** |
| `Analytics__EventFlushBatchSize` | No | `appsettings.json` | `100` |
| `Analytics__EventFlushMaxRetries` | No | `appsettings.json` | `3` |
| `Analytics__EventFlushInitialBackoffMs` | No | `appsettings.json` | `200` |
| `Analytics__RawEventRetentionDays` | No | `appsettings.json` | `180` |
| `Analytics__CleanupBatchSize` | No | `appsettings.json` | `5000` |
| `Analytics__AggregationIntervalMinutes` | No | `appsettings.json` | `60` |
| `Analytics__AggregationBatchSize` | No | `appsettings.json` | `50000` |
| `Analytics__AggregationTrailingDays` | No | `appsettings.json` | `45` |
| `Analytics__SnapshotHourUtc` | No | `appsettings.json` | `0` |
| `Analytics__DisplayTimeZoneOffsetMinutes` | No | `appsettings.json` | `180` — display hint only, never affects storage or bucketing |
| `Analytics__StructuredLogPath` | No | env | **unset** → stdout only. **Recommended before Stage B**, pointed at a mounted volume |
| `Analytics__StructuredLogMinimumLevel` | No | `appsettings.json` | `Warning` |

## Other — currently from tracked config

| Name | Required | Note |
|---|:--:|---|
| `EmailSettings__*` (5 keys) | Yes for mail | ⚠ `SmtpPass` tracked |
| `LiveKit__*` (+ `IceServers`) | Yes for voice | ⚠ tracked, **and duplicated in `livekit/livekit.yaml`** |
| `Minio__*` (4 keys) | Yes for uploads | ⚠ `SecretKey` tracked |
| `SeedAdmin__*` | Seeding only | ⚠ `Password` tracked |
| `Cors__*`, `AllowedHosts`, `Logging__*` | No | Not secrets |
| `firebase-config.json` | No | **File, bind-mounted read-only.** Absent → SDK skipped and **push notifications silently do nothing** |

---

# 6. Observability

## ✅ Available — `GET /Api/V1/Analytics/System/Health` (Admin)

One endpoint, one call, exercises DI + DbContext + counters + checkpoint + snapshots.

| Area | Fields |
|---|---|
| **Ingestion** | `eventsEnqueued`, `eventsPersisted`, **`eventsDroppedOnEnqueue`**, `flushBatchesRetried`, `flushBatchesFailed`, `eventsDeadLettered`, `duplicateEventsDiscarded`, `countersSinceUtc` |
| **Dead letters** | `deadLetterBacklog` |
| **Aggregation** | `lastAggregationSuccessUtc`, `aggregationLagHours`, `aggregationConsecutiveFailures`, **`aggregationWatermarkEventId`**, `unaggregatedEventCount` |
| **Snapshots** | `lastSnapshotDate`, **`snapshotGapDates`** |
| **Verdict** | `pipelineHealthy` (false on stale aggregation, dead letters, drops, or snapshot gaps) + human-readable `warnings[]` |

**The thresholds are encoded, not merely documented** — `PipelineHealthService` decides `pipelineHealthy`, so an operator does not have to remember what "too stale" means.

## ⚠ Partially available

| Area | What exists | What does not |
|---|---|---|
| **Storage growth** | Direct SQL (`SELECT COUNT(*) FROM UserEvents`) | Not on the health endpoint; nothing trends it |
| **Background service liveness** | Inferred: aggregation from `aggregationLagHours`, snapshots from `lastSnapshotDate`, flush from `eventsPersisted` rising | **No direct per-service "is it running" field.** `EventCleanupService` has no signal at all |
| **Analytics errors** | `ILogger` → container stdout, 10 MB × 3-file rotation. Durable if `Analytics__StructuredLogPath` is set to a mounted path | Not queryable, not aggregated, no alerting |
| **Read-model growth** | Direct SQL | Not surfaced |

## ❌ NOT CURRENTLY OBSERVABLE

Stated plainly rather than implied. **Do not build an operational plan that assumes any of these.**

| Gap | Consequence |
|---|---|
| **No alerting of any kind** | Nobody is told when `pipelineHealthy` goes false. **A person must call the endpoint.** This is the largest gap |
| **The container health check does not check health** | It curls `/`, a static HTML redirect page. **A container reports `healthy` with the database unreachable and every background service failing** |
| **No APM, no metrics export, no tracing** | Analytics' effect on request latency and CPU is unmeasurable in-app |
| **Retention/purge outcome** | `EventCleanupService` logs rows deleted but exposes **no counter**. Purge health is invisible on the endpoint |
| **Database pressure** | No connection-pool, lock or IO visibility from the application |
| **Counters reset on restart** | A restart erases the drop-rate history Stage A exists to establish. `countersSinceUtc` bounds the window — **record it or the numbers are meaningless** |
| **Unhandled analytics failures, by default** | Rotate out of the container logs and are gone, unless the structured sink is configured |

**Verdict: PARTIAL.** Sufficient to run the staged activation, because the one number that gates every stage — `eventsDroppedOnEnqueue` — is reliably observable. Insufficient for unattended operation, because nothing raises an alarm.

---

# 7. Rollback Readiness

## ✅ READY

| Layer | Mechanism | Data impact | Verification |
|---|---|---|---|
| **Feature flags** | Remove the line from `.env`, `docker compose up -d` | New events missing for the window. **Nothing else** | `hand_raised` / `stage_promoted` counts stop rising; ungated events continue |
| **High-frequency only** | Remove **only** `Analytics__EnableHighFrequencyEvents` | AN-018 events only. AN-017 continues | The entire reason the increments are separate flags |
| **Aggregation** | Truncate the read model, re-run backfill | **None** — read models are fully recomputable from raw events | Compare A-1 against the equivalent live-query endpoint |
| **Purge pressure** | Lower `Analytics__CleanupBatchSize` | None | Flush latency recovers |
| **Dead letters** | **No rollback needed** — events are parked, not lost | None | `deadLetterBacklog` stops rising |
| **Application** | Redeploy the previous image tag | None | `/` responds; health endpoint responds |
| **Schema** | `dotnet ef database update <previous>` | **Last resort.** Prefer restoring the pre-deployment backup | Verify column and table removal |

**Every analytics rollback is a configuration change.** No code change, no migration reversal, no data loss.

## Verified by test, not only asserted

`DisablingTheFlag_StopsNewEventEmission_ButTheProductStillWorks` proves both halves: analytics goes silent **and** the hub still notifies the client and persists the participant. `AFullChannel_DropsTheEventAndCountsIt_WithoutThrowing` proves an analytics outage cannot become a product outage.

## ❌ What does NOT roll back

| Item | Why |
|---|---|
| **`user_status_changed`** | The only durable record of a status transition. No `UpdatedAt`, no history table. Transitions occurring while emission is off are **lost permanently** |
| **Hard deletes** | AN-013 soft delete is blocked on a data-protection decision. Every deletion until then biases M-102, M-501 and M-103 **upward**, permanently |
| **RM-5 snapshots** | Cannot be backfilled. A day without a snapshot is a permanent hole |
| **The historical IP-hash salt** | Already published. Rotation is forward-only |

---

# 8. Known Risks

## 🔴 Deployment blockers — must be resolved before deploying

| # | Blocker | Resolution |
|:--:|---|---|
| **1** | **Three unapplied migrations.** No `Database.Migrate()` exists. The app will start and then fail on every event insert and analytics query | Apply per checklist §B. **Review `AddAnalyticsPipelineAndReadModels` first** — it backfills `EventId` across the whole table before applying a unique constraint, and is the longest statement in the deployment |
| **2** | **`ANALYTICS_IP_HASH_SALT` not yet set on the server** | Generate a **new** value into `.env`. `docker compose up` aborts without it |

Both are deployment actions with a documented procedure. Neither needs code.

## 🟠 Operational risks — accept and monitor

| Risk | Severity | Mitigation |
|---|:--:|---|
| **Nine live secrets in tracked config and git history** | CRITICAL if public / HIGH if private | Confirm visibility (Condition 3); rotate per §2.4 |
| **No alerting** | HIGH | Manual health checks per checklist §H. The single largest operational gap |
| **Container health check does not test the DB** | MEDIUM | Never use `docker ps` as a health signal. Use the analytics health endpoint |
| **Counters reset on restart** | MEDIUM | Record `countersSinceUtc` with every reading |
| Channel drop under sustained overload | MEDIUM | Countable and gated by Stage A; raise capacity before Stage C |
| Single-instance assumptions (channel, `IMemoryCache`, `SemaphoreSlim`) | MEDIUM | Correct for one container. **Revisit before adding a second** |
| `firebase-config.json` absent → push silently dead | MEDIUM | Verify the bind mount; it is a warning, not a startup failure |
| Backfill contends with live traffic | LOW | 250 ms inter-date delay, 400-date cap, resumable |

## 🟡 Known limitations — by design or accepted

- **M-400 is EXPERIMENTAL** and two of its four steps are unmeasured until Stage C. It reports `isMeasured: false`, correctly.
- **M-102-LEGACY and M-507-LEGACY are UNRELIABLE** and must not be displayed. Retained until dashboard cutover.
- **`Meta` lacks `window.isPartialPeriod` and `freshness`.** Partial-period is available on A-1 as a top-level field; freshness via the health endpoint.
- **No general error tracking (GAP-22).** AN-041 was deliberately narrowed to core-loop refusals; unbounded error tracking needs an APM.
- **13 pre-existing build warnings**, untouched.
- **`AvgDurationHours`, Top Speakers, hand-raise count, projected status breakdown** — removed from the API, permanently unrecoverable.
- **Seven planning documents retain the superseded 4–6 week baseline assumption** — deliberately not rewritten; §4 lists them.

## 🔵 Blocked on decisions outside engineering

| Item | Owner | Cost of delay |
|---|---|---|
| **AN-013 soft delete** | **Product / Legal** | **IRREVERSIBLE AND RUNNING.** Every hard delete permanently biases three shipped metrics upward. The only blocked item where waiting destroys evidence daily |
| Repository visibility + secret rotation | Repository owner | Compounds while the repository is reachable |
| AN-035 session signal | Mobile | No served metric depends on it, but Cocorra has **no session or app-open metric at all** |
| AN-032 in-room chat materiality | Product / Mobile | Active-vs-Passive may be mis-named |
| AN-044 acquisition attribution | Product | Attribution impossible for the intervening period |

## ⚪ Future work

AN-043 experiments (volume-gated) · AN-045 partitioning (needs R-3 from Stage A) · general error tracking / APM · horizontal scaling · alerting on `pipelineHealthy` · M-203, M-204, M-403 and activation→first-join (**all need no new events and no schema change** — the cheapest remaining analytics work)

---

# 9. Final Go / No-Go Decision

## 🟢 GO WITH CONDITIONS

### Why GO

Every technical prerequisite is met and verified rather than asserted:

- **Build clean, 268/268 tests pass**, zero new warnings
- **The wave's stated security objective is closed.** The salt is externalised behind two independent guards, with an impact analysis showing rotation is functionally free, and 8 regression tests
- **The activation lifecycle is unambiguous**, and a genuine error in it was found and corrected — the earlier model would have imposed a month-long wait the backfill already satisfies
- **Every rollback is a configuration change**, and the rollback contract is proven by test: analytics goes silent, the product does not
- **The pipeline is observable** through one endpoint whose thresholds are encoded in code
- **Both event flags default to false**, enforced conjunctively and asserted by test. **Nothing was enabled automatically**
- **Two secret exposures were reduced** — a WebDeploy password and 170 files of build output holding six duplicate copies of every secret

### Why WITH CONDITIONS and not unconditional

**Nine live production secrets remain in tracked configuration and in git history**, including a JWT signing key that permits forging an Admin token and object-storage credentials for voice recordings from `MentalHealth` rooms.

That was **out of this wave's scope by instruction** — *"do not silently change unrelated secrets"* — and each rotation carries operational consequences requiring coordination. But it is real, and calling this deployment unconditionally ready would misrepresent it.

**The deciding fact is one nobody inside the repository can determine: whether `github.com/cocorra/cocorra-backend` is public.** If it is, §2.2 is an incident and rotation precedes deployment. If it is private, it is serious housekeeping with a scheduled window. **Confirm before deploying.**

### Why not NO-GO

The remaining exposure is **pre-existing and unchanged**. Cocorra is already running with these secrets in tracked configuration; deploying this commit does not worsen it, and it reduces it. Blocking on a condition the deployment does not create — while the RM-5 snapshot clock, which cannot be reconstructed, does not start until deployment — would trade a fixable risk for an unfixable data gap.

### The conditions, restated

```
1. Generate a NEW ANALYTICS_IP_HASH_SALT. Do not reuse the historical value.
2. Apply the three migrations before starting the container.
3. Confirm repository visibility. If public, rotate all nine secrets first.
4. Deploy with both event flags OFF.
5. Record the §F baseline — the RM-5 clock starts at first deploy and cannot be recovered.
```

### Against the final rule

| Requirement | Status |
|---|---|
| Secure configuration | ⚠ **Partial** — the in-scope secret is secured; nine remain, documented with severities and a rotation runbook |
| Correct deployment configuration | ✅ Verified — compose YAML parses, env binding tested, defaults tested |
| Clear activation lifecycle | ✅ Four clocks documented; a real ambiguity found and corrected |
| Observable pipeline | ⚠ **Partial** — one good endpoint, **no alerting**. Gaps listed rather than implied |
| Safe rollback | ✅ Configuration-only, proven by test |
| Validated build | ✅ 0 errors, 13 pre-existing warnings |
| Validated tests | ✅ 268/268 |

**Two of seven are partial, and both are stated as partial rather than rounded up.** That is why the decision is GO **WITH CONDITIONS** and not GO.

---

## Immediate next action

**Confirm whether `github.com/cocorra/cocorra-backend` is public.**

Everything else in this report is a procedure with a written answer. That one fact changes whether the next step is "run the deployment checklist" or "rotate nine credentials first", and it takes one click.
