# 25 — Final Analytics Status Audit

> **Generated**: 2026-09-02 | **Repository state**: `main` @ `c1b4dbe`, working tree clean
> **Method**: every claim below was verified against the code at HEAD, not against the plan documents.
> **Purpose**: establish the true current state before the final hardening phase modifies anything.

---

## How to read this

Each backlog item carries one of five classifications:

| Status | Meaning |
|---|---|
| **COMPLETE** | Implemented, wired into DI/routing where applicable, and covered by a test or a verifiable artefact |
| **PARTIALLY COMPLETE** | Some of the item shipped; a named, specific remainder does not exist |
| **NOT STARTED** | No code, no artefact |
| **BLOCKED** | Cannot proceed without a decision or deliverable owned outside this repository |
| **NOT APPLICABLE** | The item is not backend engineering work (observation, measurement, or another team's deliverable) |

"Evidence" cites the file that proves the classification. Absence of evidence is recorded as absence, not inferred as completion.

---

# Summary

| Status | Count | Items |
|---|:--:|---|
| **COMPLETE** | 26 | AN-002…AN-012, AN-014…AN-026, AN-028…AN-031, AN-033, AN-034, AN-037…AN-040, AN-042 |
| **PARTIALLY COMPLETE** | 2 | AN-036, AN-041 |
| **NOT STARTED** | 3 | AN-027, AN-043, AN-044 |
| **BLOCKED** | 3 | AN-013, AN-035, AN-045 |
| **NOT APPLICABLE** | 2 | AN-001, AN-032 |

**Actionable-now gaps: three.** AN-027 (stage funnel), AN-041 (decide and act on `operation_failed`), and AN-036 (local-time context beyond the supply-health page). Everything else is either done or genuinely gated on something outside this repository.

**One defect was found during the audit and is recorded in §Defects below.**

---

# P0 — Data Trust

## AN-001 — Measure runtime pipeline behaviour

**Status: NOT APPLICABLE (engineering side COMPLETE)**

The item as written is an *observation* task: grep container logs, run two read-only queries, record five numbers in `11-current-state-validation.md` §6. No code was ever in scope.

What *was* built to make the observation possible, and did not exist when the item was written:

| Artefact | File |
|---|---|
| In-process pipeline counters (`events_dropped_on_enqueue`, `flush_batches_failed`, `flush_batches_retried`, `events_dead_lettered`) | `Cocorra.BLL/Services/EventTracking/EventPipelineMetrics.cs` |
| Health assembly over those counters plus checkpoint state | `Cocorra.BLL/Services/Analytics/PipelineHealthService.cs` |
| `GET /Analytics/System/Health` | `Cocorra.API/Controllers/AnalyticsController.cs:315` |

**INFERENCE** — the original item assumed a log grep because no counter existed. It now does, and the counters are strictly better than the grep: they survive log rotation and are queryable. R-1/R-2/R-3 should be read from `GET /Analytics/System/Health` in production rather than from `docker logs`.

**Remaining, and owned by operations, not engineering**: the five measurements have not been *taken*, because the service has not run in production since the counters landed. This is the first item in the Stage A activation checklist (`28-production-analytics-activation.md`).

---

## AN-002 — `EventId`, `SchemaVersion`, `CorrelationId`

**Status: COMPLETE**

| Requirement | Evidence |
|---|---|
| Three columns on `UserEvent` | `Cocorra.DAL/Models/UserEvent.cs` |
| `UX_UserEvents_EventId` unique | `Cocorra.DAL/Data/AppDbContext.cs` |
| Filtered `IX_UserEvents_CorrelationId` | `Cocorra.DAL/Data/AppDbContext.cs` |
| Migration | `20260901082008_AddAnalyticsPipelineAndReadModels` |
| Batched backfill of pre-existing rows | migration body |
| Tests run on a provider that enforces unique indexes | `Cocorra.Tests/PipelineHardeningTests.cs` uses `Microsoft.Data.Sqlite` in-memory |

---

## AN-003 — Harden `EventFlushService`

**Status: COMPLETE**

Failure classification, bounded retry with exponential backoff, per-row duplicate fallback, and dead-lettering are all present in `Cocorra.BLL/Services/EventTracking/EventFlushService.cs`. `DeadLetterEvents` is a real table (`Cocorra.DAL/Models/Analytics/DeadLetterEvent.cs`).

The highest-risk detail called out in the backlog — that `AddRange` + one `SaveChangesAsync` turns a single duplicate into a 100-row loss — is handled by the per-row fallback and covered in `Cocorra.Tests/PipelineHardeningTests.cs`.

---

## AN-004 — Batched retention purge

**Status: COMPLETE**

`EventCleanupService` deletes in configurable batches (`EventTrackingOptions.CleanupBatchSize`, default 5,000) with `RawEventRetentionDays` configurable (default 180).

---

## AN-005 — Host exclusion; remove Top Speakers

**Status: COMPLETE**

Host exclusion is applied at the query layer. `TopSpeakers` and `UsersWhoRaisedHand` are **removed from the DTO entirely**, not zeroed — `Cocorra.DAL/DTOS/AnalyticsDto/ParticipationStatsDto.cs` carries an explicit comment explaining that returning `0` would be a false statement rather than an admission of not measuring. Covered by `QueryCorrectionsTests`.

---

## AN-006 — Replace the retention metric

**Status: COMPLETE** — `AnalyticsRepository.Corrections.cs:22` (`GetWeeklyReturnRateAsync`), route `GET /Analytics/Return/Weekly`. The legacy `/Analytics/Retention` route remains live and is graded UNRELIABLE in the registry (`M-102-LEGACY`).

---

## AN-007 — Sequential funnel

**Status: COMPLETE** — `AnalyticsRepository.Corrections.cs:98`. Monotonicity is structural (`qualified` only shrinks) with a defensive assertion that throws rather than returning a funnel that widens downward.

---

## AN-008 — Split growth; reconstruct status history

**Status: COMPLETE** — `AnalyticsRepository.UserGrowth.cs`. Registrations and reconstructed status are separate metrics with different trust levels.

---

## AN-009 — `StateSnapshotService`

**Status: COMPLETE**

`Cocorra.BLL/Services/Analytics/StateSnapshotService.cs`, registered both as a singleton and a hosted service (`Program.cs:215-216`), table `DailyStateSnapshots` (migration `20260901080448`), gap reporting in `SnapshotGapReport.cs`, tests in `Cocorra.Tests/DailyStateSnapshotTests.cs`.

**Note** — the source file carries no `AN-009` marker, which is why a marker-based grep under-reports it. The implementation is complete.

---

## AN-010 — `activation_completed` deduplication

**Status: COMPLETE** — `Cocorra.BLL/Services/AdminService/AdminService.cs:154-163`. A deterministic `eventKey` of `activation_completed:{userId}` means idempotency is enforced by the `UX_UserEvents_EventId` database constraint rather than by a read-then-write race.

---

## AN-011 — Emit `user_status_changed`

**Status: COMPLETE** — `AdminService.cs`, with `adminId` and `isBulk` threaded through `IAdminService` so the transition is attributable. This event is the only durable record of a status transition; `EventTypes.cs:10-15` documents that reverting its emission loses transitions permanently.

---

## AN-012 — `IMetricRegistry` and trust metadata

**Status: COMPLETE**

24 contracts in `MetricRegistry.cs`; `Response<T>.Meta` populated by `AnalyticsService.BuildMeta`; a composite response inherits its weakest component's trust level; `MetricRegistryContractTests` fails the build if a declared metric key has no contract or a contract is missing a mandatory field.

**Carried into Phase 1** — the registry's *identifiers* have drifted from `14-metric-contracts.md`. The mechanism is complete; the numbering is not reconciled. See `26-metric-registry-reconciliation.md`.

---

## AN-013 — Soft delete for `ApplicationUser`

**Status: BLOCKED**

`Cocorra.DAL/Models/ApplicationUser.cs` has no `IsDeleted` and no `DeletedAt`. (`IsDeleted` appears only in `GenericRepositoryAsync`, on a different entity path.)

**Blocked by B-3, a data-protection decision, not by engineering.** The backlog and the master plan both flag this as non-engineering and both say to raise it on day one. It has not been answered.

Three shipped metrics carry an explicit limitation naming this item — `M-102`, `M-501` (registrations), `M-103` — all worded "biasing the rate upward until AN-013 lands". Those limitations are correct and must stay until the decision arrives.

**Cost of delay is irreversible**: every hard delete between now and the decision is a row that cannot be recovered for any historical metric.

---

# P1 — Core Analytics Infrastructure

## AN-014 — Read model tables · AN-015 — `AnalyticsAggregationService` · AN-016 — Backfill

**Status: COMPLETE (all three)**

| Item | Evidence |
|---|---|
| AN-014 | `DailyPlatformMetrics`, `DailyRoomMetrics`, `DailyHostMetrics`, `DailyFunnelMetrics`, `DailyStateSnapshot`, `AggregationCheckpoint` |
| AN-015 | `AnalyticsAggregationService.cs` + `.ReadModels.cs`; watermark on `UserEvent.Id` with a 120-second safety lag so identity values assigned before commit are not stepped over |
| AN-016 | `AnalyticsBackfillService.cs`; `POST /Analytics/System/Backfill`; shares the rollup code path with live aggregation so backfilled and live rows are byte-identical |

---

## AN-017 · AN-018 · AN-019 — Core-loop events

**Status: COMPLETE (shipped), DORMANT (not enabled)**

All ten events emit from real sites:

| Event | Site |
|---|---|
| `room_went_live` | `RoomService` — both start paths |
| `stage_promoted` | `RoomHub.ApproveToStage:506` — tracked against the **promoted participant** |
| `stage_demoted` | `RoomHub:582` |
| `participant_kicked` | `RoomHub` |
| `speaker_time_extended` | `RoomHub` |
| `speaker_time_exhausted` | `RoomHub:626` — before the throw |
| `hand_raised` | `RoomHub.RaiseHand:424` |
| `hand_lowered` | `RoomHub:458` (self) and `:520` (approved) |
| `mic_deactivated` | five close sites across `RoomHub` and `RoomService` |
| `room_joined` v2 | `RoomHub:282` with `isHost`, `isRejoin`, `entrySource` |

**Both flags default to `false`** (`EventTrackingOptions.EnableNewEventEmission`, `EnableHighFrequencyEvents`) and neither appears in `appsettings.json`. This is the intended sequencing — the flags gate the only changes that add load to a bounded channel, and AN-001 has not been measured — but it means every one of these events is currently emitting nowhere in production. Addressed in `28-production-analytics-activation.md`.

---

## AN-020 · AN-021 · AN-022 · AN-023 · AN-024 · AN-025

**Status: COMPLETE (all six)**

| Item | Route |
|---|---|
| AN-020 Supply Health | `GET /Analytics/Supply/Health` |
| AN-021 Report rate by category | `GET /Analytics/Safety/ReportRate` (Admin only) |
| AN-022 Review latency | `GET /Analytics/Review/Latency` — percentiles only, no mean |
| AN-023 Support analytics | `GET /Analytics/Support` |
| AN-024 Push delivery events | `push_send_attempted` / `push_send_result` in `PushNotificationService` |
| AN-025 Job health | `GET /Analytics/System/Health` |

---

# P2 — Decision Analytics

## AN-026 — Reminder events

**Status: COMPLETE** — `room_reminder_toggled` emitted from `RoomService`. The `RoomReminder` row is deleted on un-toggle, so the event is the only surviving record that a reminder was withdrawn.

---

## AN-027 — Stage funnel endpoint

**Status: NOT STARTED — and it is the one gap blocked by nothing**

| Check | Result |
|---|---|
| Route | **absent** from `Router.AnalyticsRouting` |
| Controller action | **absent** |
| Repository method | **absent** |
| DTO | **absent** |
| `M-400` in registry | **absent** |
| Feeding events (`hand_raised`, `stage_promoted`) | **present and emitting** (AN-018/AN-017) |
| Dependency AN-015 | **complete** |

Both stated dependencies are satisfied. This is implemented in Phase 1.

---

## AN-028 — Room participation endpoint

**Status: COMPLETE (by in-place replacement rather than a new route)**

The backlog anticipated a new endpoint superseding `/Analytics/Participation`. What shipped instead was a corrected `/Analytics/Participation` with the deprecated fields removed at the DTO level and `UsersWhoSpoke` derived from `mic_activated` rather than from `TotalSpokenSeconds`.

**This is the better outcome**: a parallel route would have left the misleading original serving traffic during cutover. Recorded here because the *artefact* differs from the plan, and a reader comparing the two would otherwise score this as missing.

---

## AN-029 · AN-030 · AN-031 · AN-033 · AN-034 · AN-037 · AN-038

**Status: COMPLETE (all seven)**

| Item | Evidence |
|---|---|
| AN-029 Social endpoints | `GET /Analytics/Social` — reciprocity reported before volume |
| AN-030 Social origin properties | `ChatService`, `FriendService`; `isFirstMessageToRecipient` checked before insert |
| AN-031 `LeftAt` + preserve `JoinedAt` | `RoomParticipant.cs`, migration `20260901120157` |
| AN-033 Status enums + resolution timestamps | `Cocorra.DAL/Enums/ReportStatus.cs`, `StatusCode` + resolution timestamp columns |
| AN-034 Moderation action event | `moderation_action_taken` in `SupportService` |
| AN-037 MBTI dichotomies | `GET /Analytics/Mbti/Dichotomies` — four dichotomies, not sixteen types |
| AN-038 Cohort grid | `GET /Analytics/Return/CohortGrid` with `hasSufficientHistory` |

---

## AN-032 — Group-chat existence check

**Status: NOT APPLICABLE (measurement only; requires production data)**

The backlog explicitly scopes this as "measurement only — establish whether the behaviour is material before building". It is a production query, not code. It cannot be executed from the repository.

Recorded for the mobile team and operations in `docs/mobile/COCORRA-ANALYTICS-MOBILE-INTEGRATION-GUIDE.md` §5 and in the activation runbook.

---

## AN-035 — Session signal replacement

**Status: BLOCKED (Flutter client work + a decision that needs parallel evidence)**

`session_started` remains on the client allowlist (`EventsController.cs`). The backlog's own instruction is to "run parallel, decide on evidence" — the evidence is R-4 (distinct `SessionId` per user per day vs distinct active users per day), which is a production measurement that has not been taken.

The backend has already routed around the problem: `M-102` (Weekly Return Rate) deliberately uses server-emitted `room_joined` instead of `session_started`, and its contract says so. The legacy cookie-based metric is retained only as `M-102-LEGACY`, graded UNRELIABLE.

**Nothing in the served metric set depends on `session_started` today.** That is what makes this safe to leave blocked.

---

## AN-036 — Local-time display context

**Status: PARTIALLY COMPLETE**

| Surface | State |
|---|---|
| Supply Health schedule heatmap | **Done** — `SupplyHealthDto.SuggestedDisplayOffsetMinutes` (default 180) |
| `GET /Analytics/PeakHours` | **Missing** — returns bare UTC hours, and `M-506`'s contract states the limitation without offering the offset |
| `Meta.window.suggestedDisplayOffsetMinutes` as a general envelope field | **Missing** — the blueprint's frontend contract test 6 expects it |

The gap is narrow and real: the one hourly chart outside supply health has no offset to apply. Addressed in Phase 1.

---

# P3 — Advanced Intelligence

## AN-039 — Decision Center

**Status: COMPLETE (built), GATED (must not be relied on yet)**

`DecisionCenterService.cs`, `GET /Analytics/Decisions`. The hard gate remains: the backlog's own recommendation is that change detection without a baseline produces alerts on ordinary variance. No baseline exists. The endpoint is built and correct; the *dashboard page* must render a "collecting baseline" state until 4–6 weeks of read-model history accumulate.

---

## AN-040 — LiveKit webhook ingestion

**Status: COMPLETE** — `Cocorra.API/Controllers/LiveKitWebhookController.cs`, route `POST /api/Webhooks/LiveKit`, emitting `media_session_event`.

---

## AN-041 — Failure-path events

**Status: PARTIALLY COMPLETE — a declared event with zero emission sites**

`EventTypes.OperationFailed` is declared at `EventTypes.cs:135` with a full doc comment. `git grep "EventTypes.OperationFailed"` returns **nothing outside its own declaration**. It is the only event constant in the file that nobody fires.

This is the worst of the three possible states: the constant's presence implies coverage that does not exist, and a future reader querying `operation_failed` gets an empty set indistinguishable from "no failures occurred". Resolved in `27-operation-failure-decision.md`.

---

## AN-042 — Structured logging sink

**Status: COMPLETE** — `StructuredFileLogger.cs`, registered conditionally at `Program.cs:204` only when `Analytics:StructuredLogPath` is set, so the default behaviour is unchanged.

---

## AN-043 — Experiment capability · AN-044 — Acquisition attribution

**Status: NOT STARTED (both)**

No flags, buckets, experiment tables, or acquisition-source field exist. Neither is started and neither should be, on the plan's own reasoning:

- **AN-043** — the backlog concludes Cocorra is "unlikely to have volume for well-powered A/B tests on secondary features soon" with a manual approval gate throttling intake, and recommends exploiting the approval-latency natural experiment first. Building A/B infrastructure now would be premature.
- **AN-044** — requires a product decision about what acquisition sources exist and how they are captured, plus mobile work to pass them. Not backend-actionable in isolation.

These are **deliberately deferred**, which is different from overlooked. Recorded as such rather than quietly dropped.

---

## AN-045 — `UserEvents` partitioning

**Status: BLOCKED (on AN-001, by design)**

The backlog states "deferred pending R-3" — the raw event volume measurement. Partitioning a table whose size has never been measured would be optimising against a guess. R-3 requires production access.

---

# Defects found during this audit

## D-6 — Clock-dependent aggregation test (FIXED)

`Cocorra.Tests/AnalyticsAggregationTests.cs` seeded events at `today.AddHours(4)` and `today.AddHours(5)` and asserted the aggregation cycle processed two of them.

`AnalyticsAggregationService.PerformAggregationCycleAsync` deliberately holds back the most recent `AggregationSafetyLagSeconds` (120s) of inserts, because identity values are assigned before commit and advancing the watermark to `MAX(Id)` would silently skip a row that commits late.

Before roughly 05:02 UTC the seeded events sat inside that lag window, so the service correctly returned `0` and the test failed. **The service was right and the test was wrong.** The test failed every morning and passed every afternoon.

**Fix** — the fixture is now anchored behind the safety lag rather than to a fixed hour of the day. `AnalyticsAggregationTests.cs:65`.

**INFERENCE** — worth recording rather than fixing silently. A test that fails on a schedule is usually read as flaky infrastructure and muted, and the assertion it was making — that the watermark advances and the rollup is idempotent — is one of the more important ones in the suite.

---

# What the audit did not find

Stated plainly, because a clean bill of health is only useful if the checks were real:

- **No orphaned routes** — every constant in `Router.AnalyticsRouting` resolves to a controller action.
- **No unregistered services** — every analytics service resolved by a controller is registered in `Program.cs`.
- **No metric served without a contract** — enforced by `MetricRegistryContractTests`, which fails the build.
- **No event constant emitted from a site that contradicts its documented convention** — `stage_promoted` in particular is tracked against the promoted participant, which is the opposite of the older `room_join_approved` precedent and is the one the stage funnel requires.

---

# Phase 1 work list

Derived from this audit, in the order it will be executed:

| # | Item | Why it is actionable |
|:--:|---|---|
| 1 | **AN-027** stage funnel | Both dependencies satisfied; nothing external required |
| 2 | **Metric registry reconciliation** | Pure code + documentation; no external input |
| 3 | **AN-041** decide and act | A decision this repository can make and justify |
| 4 | **AN-036** finish local-time context | One DTO field and one envelope field |
| 5 | **Configuration explicitness** | Making dormant flags discoverable is not the same as enabling them |

Everything else in this document is COMPLETE, BLOCKED on a named external owner, or NOT APPLICABLE.


---

# ADDENDUM — what Phase 1 found that this audit did not

> Appended 2026-09-02, after Phase 1 completed. **This audit's own conclusions are left unedited above**, because a status document that quietly absorbs later findings stops being a record of what was known when.

This audit concluded "actionable-now gaps: three" (AN-027, AN-041, AN-036). That count was **wrong — it was too low.** Four further actionable gaps surfaced during Phase 1, all of them invisible to the method used here.

| Gap | Why this audit missed it |
|---|---|
| **A-1 `/Analytics/Platform/Health` never built** | The audit checked the **AN-item backlog** (`23-`). A-1 lives in the **API blueprint** (`19-`) and has no AN number, so a backlog-driven sweep could not see it. Consequence: the declared north star M-100 was reachable only through the baseline-gated Decision Center — **unreadable on any page anyone could trust.** |
| **M-200 described a different figure than its endpoint returned** | The contract was complete, well-formed and internally consistent. Nothing in a contract-completeness check can catch a correct contract about the wrong thing. |
| **`/Analytics/Funnel` declared M-507**, the *corrected* sequential contract, while computing the defective independent-step version | Same blind spot. The trust envelope was certifying D-5 as fixed on the one route that still has it. |
| **`AvgDurationHours` still served** | The audit checked AN-items. This was a **Definition-of-Done** line item and a frontend-contract test, neither of which is an AN number. |

## The method flaw, stated plainly

**This audit verified that every backlog item had an artefact. It did not verify that every artefact was attached to the right thing.**

Three of the four misses share one shape: a complete, correct-looking contract or DTO pointing at something other than what it claimed. No completeness check finds those. The check that does is a comparison — *does this metric's technical definition describe a figure actually present in this endpoint's payload?* — and it has to be run deliberately, across every `BuildMeta` call site.

**Final tally after Phase 1: 7 actionable gaps found, 7 fixed, 0 remaining.** Full detail in `FINAL-ANALYTICS-IMPLEMENTATION-REPORT.md` §4 (Wave 7) and `26-metric-registry-reconciliation.md` §6.
