# 26 — Metric Registry Reconciliation

> **Generated**: 2026-09-02 | **Resolves**: metric-ID drift between `14-metric-contracts.md` and the runtime `MetricRegistry`
> **Outcome**: one authoritative semantic definition per metric ID, held in code.

---

# 1. The problem

Two documents defined metric IDs and they disagreed. Not cosmetically — **six IDs meant entirely different things** in each:

| ID | `14-metric-contracts.md` said | `MetricRegistry.cs` said |
|---|---|---|
| **M-300** | Sequential Onboarding Funnel | Report Insights |
| **M-301** | Admin Review Latency | Report Rate by Room Category |
| **M-302** | Activation to First Room Join | Voice Verification Review Latency |
| **M-500** | Report Rate per 1,000 Room Joins | Platform Summary |
| **M-501** | Report Rate by Room Category | User Registrations |
| **M-502** | Repeat-Reported Users | User Status At Time |

Nine further IDs existed in code and not in the plan (`M-102-LEGACY`, `M-103`, `M-503`…`M-508`, `M-701`, `M-702`), and eight existed in the plan and not in code (`M-203`, `M-204`, `M-400`…`M-403`, `M-600`, `M-602`).

**Why this was not survivable.** The whole point of `AN-012` is that a reader can tell a sound number from a caveated one by looking at `Meta.metrics[].metricKey`. A dashboard engineer implementing "M-501 — Report Rate by Room Category" from the blueprint would have wired a safety chart to an endpoint returning **user registration counts**, and both would have rendered as VERIFIED. The trust framework would have certified a wrong number, which is worse than having no trust framework — it is the original failure with more credibility attached.

---

# 2. The decision: code is authoritative

**The runtime registry wins on every contested ID.**

| Reason | Detail |
|---|---|
| **Code IDs are already on the wire** | Every analytics response carries `Meta.metrics[].metricKey`, and `GET /Analytics/Metrics/Registry` serves the whole set. These are a public API surface. |
| **The plan's IDs were never shipped** | `14-metric-contracts.md` is a planning artefact. No endpoint, test, or client has ever emitted or consumed its numbering. |
| **Renumbering 20 metrics to match a document with no consumers** | would break the only consumers that exist, to satisfy a document. The brief's own instruction applies directly: *prefer preserving existing public identifiers unless there is a compelling reason.* |

**The plan's scheme was arguably better.** Its numbering is tier-based (100 = north star, 200 = supply, 300 = activation, 400 = participation, 500 = safety, 600 = social and reliability), whereas the code's is historical accretion — the 500-block holds the legacy dashboard metrics because they were registered first. That is a real aesthetic loss, recorded here honestly. It does not outweigh breaking the wire format of twenty metrics for zero functional gain.

**One exception, and it went the other way.** `M-400` was unclaimed in code and the plan assigned it to the Stage Funnel. AN-027 adopted `M-400` for exactly that, so both artefacts now agree at no cost. Free IDs are taken from the plan's scheme wherever possible, which limits future divergence without rewriting the past.

---

# 3. The one renumbering, and why it was compelling

**`M-200` was making a false statement about the platform's leading indicator.**

The contract read:

```
M-200  "Rooms Gone Live"
       COUNT(Rooms) WHERE Status != Scheduled AND CreatedAt IN window
```

but the constant was named `ActiveHosts`, the plan called M-200 "Distinct Active Hosts", and it was attached to **two endpoints with incompatible payloads**:

| Endpoint | Headline figure | Does M-200's text describe it? |
|---|---|---|
| `GET /Analytics/Supply/Health` | `ActiveHosts[].DistinctHosts` — a **host** count | **No.** The contract describes rooms. |
| `GET /Analytics/Rooms` | `TotalRooms`, `ActiveRooms`, `EndedRooms` — **no host figure at all** | Yes, but the key is named `ActiveHosts`. |

One key cannot mean both. A reader of the trust envelope on supply health was being told that Cocorra's leading indicator counts rooms, when the series it labels counts hosts. That is exactly the class of defect this programme exists to eliminate, so preserving the identifier would have been preserving the bug.

**Action taken**

| Change | Detail |
|---|---|
| `M-200` **redefined** | Now "Distinct Active Hosts" — `COUNT(DISTINCT Rooms.HostId) GROUP BY period`. Matches the plan, matches the payload, matches the constant name. |
| `M-205` **added** | "Rooms Gone Live", carrying the original formula verbatim. |
| `/Analytics/Rooms` **remapped** | Now reports `M-205` instead of `M-200`. |

**Blast radius: none in practice.** `Response.Meta` was always `null` before this programme, so no client has ever read a `metricKey`. A consumer displaying `Meta.metrics[].name` sees the identical string `"Rooms Gone Live"` on `/Analytics/Rooms` before and after. Only a consumer keying off the literal `M-200` on that endpoint would notice, and none exists.

---

# 4. Full reconciliation table

Legend for **Action Taken**:

- **ALIGNED** — both artefacts already agreed; documentation annotated, no code change
- **UPDATE DOCUMENTATION** — code definition stands; `14-metric-contracts.md` corrected
- **RENUMBER** — the identifier changed in code
- **RESERVED** — ID belongs to a planned metric that is not implemented; nothing may claim it
- **DEPRECATE** — ID retired; a different ID serves the need

| Metric ID | Previous Code Meaning | Previous Documentation Meaning | Final Meaning | Action Taken |
|---|---|---|---|---|
| **M-100** | Weekly Participating Users | Weekly Participating Users (WPU) | Weekly Participating Users (WPU) — **north star** | ALIGNED |
| **M-101** | Speaking Conversion Rate | Speaking Conversion Rate | Speaking Conversion Rate | ALIGNED |
| **M-102** | Weekly Return Rate | Weekly Return Rate | Weekly Return Rate | ALIGNED |
| **M-102-LEGACY** | Legacy Retention Cohort (deprecated) | *(described as a deprecation, unnumbered)* | Legacy Retention Cohort — graded UNRELIABLE, served for continuity only | UPDATE DOCUMENTATION |
| **M-103** | Weekly Cohort Retention Grid | *(absent)* | Weekly Cohort Retention Grid | UPDATE DOCUMENTATION |
| **M-200** | **Rooms Gone Live** *(wrong — see §3)* | Distinct Active Hosts | **Distinct Active Hosts** | **RENUMBER** (definition corrected) |
| **M-201** | Host Second-Room Rate | Host Retention | Host Second-Room Rate | ALIGNED (code name is more precise) |
| **M-202** | Host Concentration | Supply Concentration | Host Concentration | ALIGNED |
| **M-203** | *(absent)* | Distinct Non-Host Speakers per Room | *not implemented* | RESERVED |
| **M-204** | *(absent)* | Audience Return per Host | *not implemented* | RESERVED |
| **M-205** | *(absent)* | *(absent)* | **Rooms Gone Live** | **RENUMBER** (new ID for the old M-200 definition) |
| **M-300** | Report Insights | Sequential Onboarding Funnel | **Report Insights** | UPDATE DOCUMENTATION |
| **M-301** | Report Rate by Room Category | Admin Review Latency | **Report Rate by Room Category** | UPDATE DOCUMENTATION |
| **M-302** | Voice Verification Review Latency | Activation to First Room Join | **Voice Verification Review Latency** | UPDATE DOCUMENTATION |
| **M-303** | Pending Verification Queue Depth | Pending Verification Queue Depth | Pending Verification Queue Depth | ALIGNED |
| **M-400** | *(absent)* | Stage Funnel | **Stage Participation Funnel** | ALIGNED (adopted from the plan by AN-027) |
| **M-401** | *(absent)* | Non-Host Speaking Minutes | *not implemented* | RESERVED |
| **M-402** | *(absent)* | Hand-Raise → Stage Promotion Rate | *not implemented — the value is served as M-400's step-2→step-3 conversion* | RESERVED |
| **M-403** | *(absent)* | Speaking Conversion by Room Configuration | *not implemented* | RESERVED |
| **M-500** | Platform Summary | Report Rate per 1,000 Room Joins | **Platform Summary** | UPDATE DOCUMENTATION |
| **M-501** | User Registrations | Report Rate by Room Category | **User Registrations** | UPDATE DOCUMENTATION |
| **M-502** | User Status At Time | Repeat-Reported Users | **User Status At Time**. The planned repeat-offender metric is *partially* served as `MostReportedUsers` on `/Analytics/Reports` under M-300 — raw counts, not the rate the plan defined | UPDATE DOCUMENTATION |
| **M-503** | Room Participation | *(absent)* | Room Participation | UPDATE DOCUMENTATION |
| **M-504** | Room Analytics | *(absent)* | Room Analytics | UPDATE DOCUMENTATION |
| **M-505** | Most Active Rooms | *(absent)* | Most Active Rooms | UPDATE DOCUMENTATION |
| **M-506** | Peak Active Hours | *(absent)* | Peak Active Hours | UPDATE DOCUMENTATION |
| **M-507** | Sequential Activation Funnel | *(absent)* | Sequential Activation Funnel | UPDATE DOCUMENTATION |
| **M-507-LEGACY** | *(absent — `/Analytics/Funnel` wrongly declared M-507)* | *(absent)* | **Legacy Independent-Step Funnel**, graded UNRELIABLE | **ADDED** during the final audit |
| **M-508** | Voice Verification Drop-off | *(absent)* | Voice Verification Drop-off | UPDATE DOCUMENTATION |
| **M-600** | *(absent)* | Message Reciprocity Rate | *not implemented*. M-701 covers **friend-request** reciprocity (`AcceptanceRatePercent`); message *reply* reciprocity is not computed — `SocialGraphDto` carries `MessagesSent` and `DistinctMessageSenders` but no reply rate | RESERVED |
| **M-601** | Support Volume and Response Time | Technical Problem Ticket Rate | Support Volume and Response Time (a superset of the planned metric) | ALIGNED |
| **M-602** | *(absent)* | Push Send Success Rate | *not implemented — the `push_send_*` events exist (AN-024) but no metric is registered* | RESERVED |
| **M-701** | Social Graph Health | *(absent)* | Social Graph Health | UPDATE DOCUMENTATION |
| **M-702** | MBTI Dichotomy vs Speaking | *(absent)* | MBTI Dichotomy vs Speaking | UPDATE DOCUMENTATION |

**Totals** — **27 IDs served with contracts**, 8 RESERVED, 0 DEPRECATED, 2 RENUMBERED, 1 ADDED, 6 documentation conflicts resolved in favour of code.

**Correction made while compiling this table**, recorded rather than quietly fixed: the first draft marked `M-600` as "superseded by M-701" and `M-502` as "not implemented". Checking the DTOs disproved both. `SocialGraphDto` has no reply-rate field, so message reciprocity is genuinely unbuilt rather than relocated; and `ReportInsightsDto.MostReportedUsers` does exist, so the repeat-offender analysis is partially served. A reconciliation that mis-states two rows is a worse artefact than the drift it documents.

---

# 5. Trust-level vocabulary

The blueprint's trust UX table specifies four display states; the code enum had three. `Experimental` was added:

```
Verified              = 0
ConditionallyReliable = 1
Experimental          = 2   ← added
Unreliable            = 3
```

**The ordering is load-bearing, not alphabetical.** `AnalyticsService.BuildMeta` computes a composite's trust as `Max()` across its components, so the ordinal order must track *descending* trust for "the weakest component governs" to hold. Inserting `Experimental` anywhere after `Unreliable` would have silently rounded composite trust levels up — the exact failure the composite rule exists to prevent. The enum now carries a comment saying so.

**`Deprecated` was deliberately NOT added to the enum, and no ID is graded DEPRECATED.** Deprecation in this system is enforced by *removal from the response*, not by a label: AN-005 deleted `TopSpeakers` and `UsersWhoRaisedHand` from the DTO rather than zeroing or flagging them, on the reasoning that a warned-but-visible wrong number still gets screenshotted into a deck without its warning. A fifth enum value would invite exactly the "render it, but greyed out" treatment that server-side removal exists to prevent. DEPRECATED therefore appears in `29-final-metric-trust-register.md` as a register status for metrics that no longer exist in any response, and nowhere in code.

`M-102-LEGACY` is the one intentional exception: still served, graded `Unreliable`, retained only until the dashboard cuts over. Its contract's `ValidationMethod` says outright *"Do not use for decisions."*

---

# 6. What enforces this from now on

| Guard | What it catches |
|---|---|
| `MetricRegistryContractTests.EveryMetricKeyConstant_HasAContract` | A key declared with no contract — fails the build |
| `MetricRegistryContractTests.EveryContract_HasAllMandatoryFields` | A contract missing name, purpose, definition, formula or validation method |
| `MetricRegistryContractTests.MetricsWithKnownCaveats_AreNotGradedVerified` | A metric claiming VERIFIED while stating a limitation |
| `StageFunnelTests.M400_IsRegisteredAsExperimentalWithTheNotMeasuredLimitation` | M-400 losing its trust grade or its not-backfillable caveat |

**What is still not enforced, stated plainly:** nothing mechanically prevents a *new* endpoint from attaching a semantically wrong `metricKey` to its payload — the M-200 defect would not have been caught by any of the tests above, because M-200 had a complete, well-formed, internally consistent contract. It was simply about a different thing than the endpoint it was attached to.

**RECOMMENDATION** — the reviewable rule that would have caught it: *every `BuildMeta` argument must name a metric whose `TechnicalDefinition` describes a figure actually present in that endpoint's DTO.* That is a code-review check, not a test, and it is cheap to apply because `BuildMeta` call sites are all in one file.

## The rule found two more the moment it was applied

Applying that rule across all 24 `BuildMeta` call sites during the final audit surfaced two further instances of the same defect class. Recorded because a rule that finds nothing on first use is usually a rule nobody ran.

| Finding | Fix |
|---|---|
| **`GET /Analytics/Funnel` declared M-507** — the *corrected, sequential* contract — while computing the non-sequential version. **The trust envelope was certifying defect D-5 as fixed on the one route that still has it.** | Added **M-507-LEGACY**, graded UNRELIABLE, and remapped the route |
| **`GET /Analytics/Rooms` declared M-205 "Rooms Gone Live"**, but `RoomAnalyticsDto` had no such field — only `ActiveRooms` and `EndedRooms`, from which a caller would have to derive it | Added an explicit `RoomsGoneLive` field matching the contract's formula exactly |

**INFERENCE** — three instances of one defect class (M-200, M-507, M-205) is a pattern, not a coincidence. All three passed every test, because a well-formed contract about the wrong thing is indistinguishable from a correct one to any check that does not compare the contract against the payload. The rule above is the only thing that catches it, and it has to be run deliberately.

---

# 7. Authority, going forward

| Artefact | Status |
|---|---|
| **`MetricRegistry.cs`** | **AUTHORITATIVE.** The only place a metric ID acquires meaning. |
| `GET /Analytics/Metrics/Registry` | Authoritative at runtime — serves the registry verbatim. |
| `29-final-metric-trust-register.md` | Authoritative human-readable view, generated from the registry. Must be regenerated when the registry changes. |
| **This document** | Authoritative for the ID *history* — what an ID used to mean and why it changed. |
| `14-metric-contracts.md` | **PLANNING ARTEFACT, SUPERSEDED NUMBERING.** Its metric *rationale* remains valuable and is why it is kept; its IDs must not be used. Annotated in place so nobody reads an ID from it by accident. |
| `20-dashboard-implementation-blueprint.md` | Page and widget structure remains authoritative. **Its metric IDs are the plan's scheme** and must be translated through §4 before implementation. |

**INFERENCE — the last row is the live risk.** The dashboard blueprint is the document a frontend engineer will build from, and it says things like "KPI — reports per 1,000 joins → M-500". Under the final mapping, `M-500` is Platform Summary. The translation table in §4 is not documentation hygiene; it is the thing standing between the blueprint and a safety chart wired to the wrong endpoint. `CLAUDE-DESIGN-PROMPT.md` and the design brief therefore reference metrics **by name and endpoint**, never by the blueprint's ID.
