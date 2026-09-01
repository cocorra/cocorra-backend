# 22 — Testing & Validation Strategy

> **Generated**: 2026-09-01 | **Phase**: Implementation Blueprint, Phase 11
> **Depends on**: `11-current-state-validation.md` (test stack), `14-metric-contracts.md` (validation methods), `16-`, `17-`, `18-`
> **Scope**: Documentation only. No tests were written.

---

# Existing Test Infrastructure

**FACT** — `Cocorra.Tests` (`Cocorra.Tests.csproj`) targets `net10.0` and uses:

| Package | Version |
|---|---|
| xUnit | 2.9.3 |
| Moq | 4.20.72 |
| Microsoft.EntityFrameworkCore.InMemory | 10.0.2 |
| coverlet.collector | 6.0.4 |

**FACT** — 20 test files, two directly relevant:

- **`EventTrackingSmokeTests.cs`** — already covers `EventTracker.Track` → channel → `EventFlushService` → DB → `AnalyticsRepository`. It builds the tracker with a **null `HttpContext`** and documents why: *"No HttpContext (as when firing from a SignalR hub) → enrichment is skipped."*
- **`AnalyticsControllerTests.cs`** — controller tests with a mocked `IAnalyticsService`, including `BadRequest` validation on `limit`.

**RECOMMENDATION — extend this file, do not build a new harness.** **INFERENCE** — `EventTrackingSmokeTests` is already a working end-to-end pipeline test with the correct helpers (`BuildTracker`, `NewQueue`, `DrainOne`). Every event and idempotency test below is a variation on a pattern that already exists in-repo, which removes the main cost of a testing programme: establishing conventions.

---

# The Provider Problem

> The most important constraint in this document. Getting it wrong makes the entire idempotency guarantee untested while appearing tested.

**FACT** — `Microsoft.EntityFrameworkCore.InMemory` does **not** enforce:

- unique indexes or unique constraints
- referential integrity or `DeleteBehavior` (`SetNull`, `Cascade`)
- relational data types, precision, or length limits
- transactions

**INFERENCE — the consequence is specific and severe.** The whole idempotency design rests on `UNIQUE(EventId)` (`16-`). A test asserting "a duplicate `EventId` is rejected" written against `EFCore.InMemory` **passes whether or not the constraint exists**. It would report success for a broken implementation, and it would do so silently.

## Required provider matrix

| Test category | Provider | Justification |
|---|---|---|
| Formula / pure computation | **In-memory or none** | No constraint dependency |
| Event production | **In-memory** | Assertions are on the channel, not the database |
| **Idempotency / unique constraint** | **SQLite in-memory or SQL Server** | In-memory does not enforce uniqueness |
| **Referential (`SetNull` on user delete)** | **SQL Server** | SQLite's FK semantics differ from SQL Server's |
| Aggregation correctness | **SQLite in-memory** | Needs real SQL semantics for grouping and upsert |
| Migration / schema | **SQL Server** | Only real SQL Server validates the migration |
| Retention batch delete | **SQL Server** | `ExecuteDeleteAsync` batching behaviour is provider-specific |

**RECOMMENDATION** — add `Microsoft.EntityFrameworkCore.Sqlite` to `Cocorra.Tests.csproj` and use SQLite in-memory for the constraint-dependent tests. Keep `EFCore.InMemory` for everything else; it is faster and adequate where no constraint is involved.

**RECOMMENDATION — a guard against silent regression.** Add a test asserting that the SQLite-backed context **does** reject a duplicate `EventId`. **INFERENCE** — this is a test of the test infrastructure. If someone later switches those tests back to `EFCore.InMemory` for speed, this assertion fails and explains why, rather than the suite quietly becoming vacuous.

---

# 1. Metric Unit Tests

Formula correctness in isolation, no database. One test class per metric contract from `14-`.

## Pattern

```
Arrange:  a fixture set of events/rows with known values
Act:      invoke the metric computation
Assert:   the exact expected value, not a range
```

## Priority tests — the ones that catch the P0 defects

| # | Metric | Assertion | Catches |
|:--:|---|---|---|
| **1** | M-100 WPU | A user joining the same room 5 times counts **once** | Reconnect inflation |
| **2** | M-100 WPU | A host joining **their own** room is excluded; the same user joining **another** room is included | TRUST-01 |
| **3** | M-101 Conversion | 1 host + 3 joiners, 2 unmute → **66.7%** (not 50%, not 75%) | Host in numerator *or* denominator |
| **4** | M-101 / M-401 | **No `UserId` appears in both the speaker set and the passive set** for the same window | **TRUST-01 — the executable statement of the contradiction** |
| **5** | M-102 Return | A user active on days 2 and 5 **counts** as returned | TRUST-03 exact-day bug |
| **6** | M-300 Funnel | Each step's count **≤** the previous step's, for any input | **TRUST-06 — impossible under the current implementation** |
| **7** | M-300 Funnel | A user with `activation_completed` **before** `email_confirmed` does not count at the later step | Ordering not enforced |
| **8** | M-301 Latency | The response contains **no mean** | M-301 contract requirement |
| **9** | M-301 Latency | Known gaps produce exact p50/p90/p99 | Percentile computation |
| **10** | M-203 Speakers/room | A host-only room reports **0**, not 1 | TRUST-01 |
| **11** | M-203 Speakers/room | A zero-speaker room **appears** with 0 via left join | Silent omission of the most informative case |
| **12** | M-401 Speaking minutes | Host segments excluded; unmute→mute over 90s → **1.5 minutes** | TRUST-01, segment arithmetic |
| **13** | M-501 Report rate | Reports with no `reportedRoomId` are **excluded**, not bucketed into `Others` | R-5 population mixing |
| **14** | M-600 Reciprocity | (A→B) and (B→A) are **one** pair, not two | Order-dependent pair keying |
| **15** | M-202 Concentration | Total host count is **present** in the result | CONDITIONALLY RELIABLE precondition |
| **16** | M-204 Audience return | Hosts with fewer than 2 rooms are **excluded**, not reported as 0% | Ranking inversion |

**INFERENCE — tests 4 and 6 are the two that matter most.** Test 6 is *impossible to satisfy* under the current funnel implementation, making it a precise, unambiguous acceptance criterion rather than a judgement call. Test 4 is the direct executable form of the TRUST-01 contradiction: it fails today and must pass afterwards, which is exactly what a regression test for a data-trust defect should look like.

---

# 2. Event Production Tests

That each emit site produces the right event with the right payload, at the right time.

## Pattern (extends `EventTrackingSmokeTests`)

```
Arrange:  bounded channel + EventTracker with null HttpContext (hub simulation)
Act:      invoke the hub or service method
Assert:   drain the channel; verify EventType, UserId, RoomId promotion, and properties
```

## Per-event tests

| # | Event | Assertion |
|:--:|---|---|
| **17** | `hand_raised` | Exactly one event; `roomId` promoted to the indexed column; `stageCapacity` and `currentStageOccupancy` present |
| **18** | `hand_raised` / `hand_lowered` | raise → lower → raise produces **2** raises and **1** lower, in timestamp order |
| **19** | `hand_lowered` | `wasApproved = true` when lowered by promotion; `false` when withdrawn |
| **20** | `stage_promoted` | **`UserId` is the promoted participant, not the host**; `byHostId` carries the host |
| **21** | `mic_deactivated` | Emitted from **all three** close sites: `ToggleMic`, `LeaveRoomCleanupAsync`, `EndRoomAsync` |
| **22** | `mic_deactivated` | `wasInitialHostMic = true` only for the host's auto-opened mic |
| **23** | `speaker_time_exhausted` | Emitted **before** the `HubException` is thrown |
| **24** | `room_went_live` | Emitted from both `StartScheduledRoomAsync` and live `CreateRoomAsync`; `minutesLateVsSchedule` correct for a late start |
| **25** | `user_status_changed` | `changedByAdminId` populated from the threaded `adminId`; `isBulkOperation` correct in both paths |
| **26** | `room_joined` | `isHost` and `isRejoin` correct; `entrySource` defaults to `"direct"` when absent |
| **27** | **All events** | Emitted **after** the domain write succeeds — a failed save produces **no** event (INV-2) |
| **28** | **All events** | `Track` never throws, even with a full channel (INV-1) |

**INFERENCE on test 20** — this is the subtle one. `15-` sets `stage_promoted.UserId` to the *promoted participant* so the M-400 funnel can chain on a single `UserId`. **FACT** — the existing `room_join_approved` uses the opposite convention (tracked against the host, `RoomService.cs:311`). Without an explicit test, an implementer following the existing precedent would break the funnel in a way no metric test would catch, because the event would still exist with plausible data.

**INFERENCE on test 27** — the most valuable structural test in this section. It encodes INV-2, and the failure it prevents is a false positive: an event recording an action that was rolled back. A missing event undercounts; a phantom event asserts something that never happened, and no downstream consumer can detect it.

---

# 3. Idempotency Tests

**Provider: SQLite in-memory or SQL Server. Not `EFCore.InMemory`.**

| # | Scenario | Assertion |
|:--:|---|---|
| **29** | Duplicate `EventId` insert | Second insert rejected; first row survives |
| **30** | Batch replay after simulated transient failure | Row count **unchanged** after replaying an identical batch |
| **31** | **Mixed batch: 100 events, 1 duplicate** | **99 rows persist, 1 discarded, batch NOT failed** |
| **32** | Deterministic key stability | The same `eventKey` yields the same `EventId` across processes and restarts |
| **33** | `activation_completed` concurrency | Two parallel activations of the same user produce **exactly one** persisted event |
| **34** | `room_went_live` | Two start attempts for the same room produce **one** event |
| **35** | Aggregation re-run | Running the aggregator twice over the same range produces **byte-identical** rows |
| **36** | Snapshot re-run | Two `StateSnapshotService` runs on the same date produce **one** row |

**INFERENCE — test 31 is the one most likely to be skipped and most damaging to omit.** `AddRange` + one `SaveChangesAsync` means a single duplicate key fails the **entire** batch. Adding `UNIQUE(EventId)` without the per-row fallback would create a new 99-event-wide loss path — strictly worse than the problem the constraint was added to solve. This test is the only thing standing between a durability improvement and a durability regression.

**INFERENCE on test 33** — it validates the TRUST-10 fix by construction. The current implementation reads the `UserEvents` table before emitting, but `Track` only enqueues, so two concurrent calls both observe "not yet activated." The deterministic-key approach cannot fail this way, and the test proves the difference.

---

# 4. Integration Tests

The full path: `User Action → Event → Storage → Aggregation → API`.

**Provider: SQLite in-memory** (real SQL semantics; fast enough for CI).

## The canonical end-to-end test

| # | Test | Steps | Assertions |
|:--:|---|---|---|
| **37** | Stage funnel, end to end | 1. Create a room (host auto-joined)<br>2. Three users join<br>3. Two raise hands<br>4. One promoted<br>5. That one unmutes then mutes<br>6. Flush the channel<br>7. Run the aggregator<br>8. Call `GET /Analytics/Rooms/StageFunnel` | `DistinctJoiners = 3` (host excluded)<br>`HandRaises = 2`<br>`StagePromotions = 1`<br>`DistinctSpeakers = 1`<br>`SpeakingSeconds` matches the segment<br>Funnel is monotonic<br>`Meta.trustLevel` populated |

**INFERENCE — this single test exercises almost every correction in the programme**: host exclusion (TRUST-01), the new events (TRUST-04), sequential funnel semantics (TRUST-06), aggregation idempotency (INV-4), and trust metadata (INV-6). It is the highest-value test to write first, because a failure anywhere in the chain surfaces here.

## Further integration tests

| # | Test | Assertion |
|:--:|---|---|
| **38** | Onboarding funnel, end to end | Six events → sequential funnel with correct per-step elapsed time |
| **39** | Room spanning UTC midnight | Two `DailyRoomMetrics` rows whose sums equal the room total |
| **40** | Late-arriving `activation_completed` | An event arriving 3 days after the cohort date is picked up by the 45-day trailing recompute |
| **41** | Watermark on failure | A forced aggregation failure leaves `LastProcessedEventId` **unchanged**; the next run reprocesses |
| **42** | Dead-letter path | A permanently failing context routes the batch to `DeadLetterEvents` with **zero** events lost |
| **43** | Graceful shutdown drain | Cancellation drains remaining channel contents before exit |
| **44** | Concurrent insert during purge | Inserts succeed while the batched retention delete runs |
| **45** | Trust metadata present | Every new endpoint returns populated `Meta.metrics` |
| **46** | Not-measured state | A window predating `dataAvailableFromUtc` returns `null`, **never `0`** |

**INFERENCE on test 46** — this is the API-level counterpart to the most important UI rule in `20-`. Returning `0` for an uninstrumented period is a fabricated finding, and it is the default behaviour of most aggregation code (`SUM` over an empty set, `COUNT` of nothing). It must be explicitly tested because the wrong behaviour is the natural one.

**INFERENCE on test 40** — without the trailing recompute, the onboarding funnel is *guaranteed* to be wrong: `activation_completed` arrives only after a human review, so a cohort aggregated once on its cohort date would permanently record a near-zero final step. This test catches a defect that would otherwise look like a catastrophic product problem.

---

# 5. Historical Validation

How backfilled data is proven correct.

| # | Check | Method | Pass criterion |
|:--:|---|---|---|
| **47** | Backfill parity | Compare backfilled rows against rows produced by the live aggregator over the same range | **Byte-identical** |
| **48** | Backfill vs live query | Compare read-model values against a direct live query | Within documented tolerance, differences explained |
| **49** | Control metrics | Registration counts and report counts, old path vs new | **Identical** |
| **50** | Non-backfillable marking | Metrics with no historical source return `dataAvailableFromUtc`, not fabricated values | No fabrication |
| **51** | Additivity | Summing daily counts equals the period count **for additive columns only** | Distinct-user columns asserted **non-additive** |
| **52** | Rate storage | A weekly rate computed from summed numerators/denominators **differs** from the average of daily rates | Proves rates are not stored |
| **53** | Gap detection | Missing snapshot dates are flagged, **never interpolated** | Gaps visible |

**RECOMMENDATION on test 47** — the backfill job must share the same code path as the live aggregator. **INFERENCE** — divergence between backfilled and live-computed rows is a classic and subtle failure: the numbers look plausible, the discontinuity sits at the backfill boundary, and it is usually discovered months later when someone asks why a trend has a step in it. Sharing the code path makes the divergence structurally impossible.

**INFERENCE on tests 51 and 52** — both catch aggregation errors that produce *plausible* wrong numbers. Test 51 catches summing distinct-user counts across days (which double-counts anyone active on multiple days, with the error growing as engagement grows — the worst possible direction). Test 52 catches storing percentages instead of counts, which breaks any period aggregation whenever daily volumes differ. Cocorra's daily volumes differ a great deal between a day with three live rooms and a day with none.

**INFERENCE on test 49** — the control metrics are as important as the changed ones. If registration or report counts differ between the old and new paths, the read-model pipeline itself is broken, and correctness in the changed metrics would not compensate.

---

# 6. Regression Tests

Preventing a metric from silently changing.

## Golden-file tests

**RECOMMENDATION** — for each metric, a fixed input fixture and a committed expected output. Any change to the output must be an explicit, reviewed diff.

```
Fixtures/
  golden-events-2026-08.json          fixed event set, never modified
  expected/
    M-100-wpu.json
    M-101-conversion.json
    M-300-funnel.json
    …
```

| # | Test | Assertion |
|:--:|---|---|
| **54** | Golden file per metric | Output matches the committed expectation exactly |
| **55** | Fixture immutability | The input fixture is unchanged (checksum) |

**INFERENCE — this is the mechanism that prevents the current situation recurring.** `07-metric-verification.md` found three metrics misleading or incorrect. Nobody changed them to be wrong; they were wrong from the start and nothing detected it. A golden file makes any future change to a metric's output visible in a pull request diff, which converts a silent drift into a decision someone has to justify.

## Contract enforcement

| # | Test | Assertion |
|:--:|---|---|
| **56** | Every served metric has a registry entry | No metric reaches the API without a `MetricRegistry` contract |
| **57** | Mandatory fields | Every contract has business purpose, technical definition, formula, **and** validation method |
| **58** | Registry ↔ response consistency | `GET /Analytics/Metrics/Registry` matches the `Meta` embedded in each metric's own response |
| **59** | Host exclusion declared | Every room-participation metric lists host exclusion in `Exclusions` (INV-7) |
| **60** | Deprecated fields absent | Top Speakers, hand-raise count, `AvgDurationHours` appear in **no** response (R-8) |

**INFERENCE on tests 56 and 57** — these make the `08a` mandatory rule executable rather than aspirational. A metric without a contract cannot ship, enforced by a failing build rather than by review discipline. That is the difference between a policy and a guarantee.

**INFERENCE on test 59** — host exclusion is the correction most likely to be silently lost in a future refactor, because it is a `WHERE` clause that looks removable. Asserting it is *declared* in the contract, not just implemented in the query, gives it a second place to fail visibly.

---

# 7. Continuous Validation (Production)

Tests run in CI. These run against live data, permanently.

**INFERENCE** — some correctness properties cannot be tested pre-deployment because they depend on real data volume and real event sequences. These become monitored invariants rather than tests.

| # | Invariant | Detects | Frequency |
|:--:|---|---|---|
| **61** | Cumulative registrations never decrease | **Hard deletes (TRUST-05)** | Daily |
| **62** | Funnel monotonicity holds on live data | Sequential funnel regression | Daily |
| **63** | No user in both speaker and passive sets | **TRUST-01 regression** | Daily |
| **64** | Read models reconcile against live queries | Aggregation drift | Daily |
| **65** | Dead-letter table empty or explained | Silent event loss | Hourly |
| **66** | Aggregation lag within threshold | Pipeline stall | Hourly |
| **67** | Snapshot dates complete | Missed snapshot runs | Daily |
| **68** | `activation_completed` duplicates = 0 | TRUST-10 regression | Weekly |
| **69** | `mic_activated` / `mic_deactivated` orphans only where `wasInitialHostMic` | Segment pairing failure | Daily |
| **70** | FCM token coverage stable | **Token regression of the `dc1c933` class** | Daily |

**RECOMMENDATION** — surface these through `GET /Api/V1/Analytics/System/Health` (`19-` F-3).

**INFERENCE — why this matters more for Cocorra than it would elsewhere.** **FACT** — there is no structured logging sink, no APM, and no metrics export; errors reach `ILogger` → Docker stdout with 10MB/3-file rotation. A failing invariant written only to container logs is a failing invariant nobody sees. Routing these checks through the analytics API is not elegant, but it uses the one observability surface that exists and that people already look at.

**INFERENCE on invariant 61** — this is the cheapest possible detector for the TRUST-05 problem and it works even before soft delete is implemented. A decreasing cumulative registration count is unambiguous evidence of hard deletion, and it quantifies the bias in every retention rate.

---

# Test Coverage by Correction

Traceability from each P0 defect to the tests that prove it fixed.

| Correction | Tests |
|---|---|
| **TRUST-01** Host mic | 2, 3, **4**, 10, 12, 22, 37, 59, **63** |
| **TRUST-02** Growth history | 49, 54, **61** |
| **TRUST-03** Retention | **5**, 48, 54 |
| **TRUST-04** Hand raise | 17, 18, 19, 37 |
| **TRUST-05** Hard deletes | **61**, plus soft-delete tests in `13-` |
| **TRUST-06** Funnel | **6**, 7, 38, **62** |
| **TRUST-07** Pipeline loss | 28, 30, **31**, 42, 43, **65** |
| **TRUST-08** SignalR session | 17, 26 |
| **TRUST-09** Room duration | 24, 39 |
| **TRUST-10** Activation dedup | **33**, **68** |

**Bold** = the decisive test for that correction.

---

# Execution Order

**RECOMMENDATION** — write tests in this order. Each unblocks the next stage of implementation.

| Stage | Tests | Why first |
|:--:|---|---|
| **1** | Provider guard (SQLite rejects duplicate `EventId`) | Without it, every idempotency test below is potentially vacuous |
| **2** | 29–32 (idempotency) | Blocks the flush-retry work |
| **3** | 28, 30, **31**, 42, 43 (pipeline durability) | Blocks P0 event deployment |
| **4** | 1–16 (metric formulas) | Can be written before any implementation; **6** and **4** are the acceptance criteria for TRUST-06 and TRUST-01 |
| **5** | 17–28 (event production) | Alongside each emit site |
| **6** | **37**, 38–46 (integration) | After events and aggregation exist |
| **7** | 47–53 (historical) | Before backfill runs in production |
| **8** | 54–60 (regression) | Before cutover |
| **9** | 61–70 (continuous) | At cutover, permanently |

---

# Summary

| Category | Count | Provider | Purpose |
|---|:--:|---|---|
| Metric unit | 16 | In-memory | Formula correctness |
| Event production | 12 | In-memory | Emit sites and payloads |
| **Idempotency** | 8 | **SQLite / SQL Server** | Duplicate prevention |
| Integration | 10 | SQLite | Action → event → storage → aggregation → API |
| Historical | 7 | SQLite | Backfill correctness |
| Regression | 7 | In-memory | Silent-change prevention |
| Continuous | 10 | Production | Live invariants |
| **Total** | **70** | | |

**Four conclusions (INFERENCE).**

**The provider choice is the highest-risk decision in this document.** `EFCore.InMemory` does not enforce unique constraints, so every idempotency test written against it passes vacuously. The entire durability guarantee rests on a database constraint that the default test provider cannot observe. Adding SQLite is a one-line project change that determines whether eight tests mean anything.

**Two tests are precise acceptance criteria rather than judgement calls.** Test 6 (funnel monotonicity) is *impossible* to satisfy under the current implementation. Test 4 (no user in both speaker and passive sets) fails today by construction. Both must go from red to green, which is exactly what a regression test for a data-trust defect should look like.

**Test 31 is the one that prevents a fix becoming a regression.** A duplicate key in an `AddRange` batch fails all 100 rows. Adding the unique constraint without the per-row fallback would convert a bounded loss problem into a 99-event-wide one.

**The continuous invariants are where this differs from a normal test plan.** With no APM, no error tracking, and no structured logging, correctness has to be asserted against production data and surfaced through the analytics API itself — because that is the only place anyone will see it.
